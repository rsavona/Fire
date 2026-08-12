using System.Text.Json;
using System.Text.Json.Nodes;
using Fusion.Common;
using Fusion.Common.BaseClasses;
using Fusion.Common.Contracts;
using Serilog;

namespace Fusion.Reaction.Tura.HostComm;

/// <summary>
/// Standalone counterpart to a deployed TuraHost: scripts a TcpMessageClientElementManager
/// against it, checks the ROUA echoes come back for every barcode sent, then queries the
/// MessageQueue table to confirm TuraDbLogBond actually persisted each transaction.
/// </summary>
public class TuraVerifyReaction : ReactionBase
{
    private readonly string _clientElement;
    private readonly string _dbElement;
    private readonly int _verifyDelayMs;
    private readonly List<string> _expectedBarcodes;
    private readonly HashSet<string> _echoedBarcodes = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _resultLock = new();
    private bool _verified;
    private bool _dbQueryIssued;
    private CancellationToken _runCt;

    public TuraVerifyReaction(IMessageBus bus, IReactionBlueprint config, ILogger logger)
        : base(bus, config, logger)
    {
        _clientElement = config.Properties.TryGetValue("ClientElement", out var c) ? c.ToString() ?? "TURA_CLIENT" : "TURA_CLIENT";
        _dbElement = config.Properties.TryGetValue("DbElement", out var d) ? d.ToString() ?? "VERIFY_DB" : "VERIFY_DB";
        _verifyDelayMs = config.Properties.TryGetValue("VerifyDelayMs", out var v) ? Convert.ToInt32(v) : 10000;

        _expectedBarcodes = config.Properties.TryGetValue("ExpectedBarcodes", out var b)
            ? (b.ToString() ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList()
            : new List<string>();

        Logger.Information(
            "[{Reaction}] TuraHost verification initialized. Client: {Client}, DB: {Db}, Expecting {Count} barcode(s) within {Delay}ms.",
            Config.Name, _clientElement, _dbElement, _expectedBarcodes.Count, _verifyDelayMs);
    }

    public override async Task StartAsync(CancellationToken ct)
    {
        await base.StartAsync(ct);
        _runCt = ct;
        _ = RunVerificationTimeoutAsync(ct);
    }

    /// <summary>
    /// Fallback ceiling for barcodes that never echo — HandleEcho triggers verification
    /// immediately once every expected barcode is in, so this only fires for stragglers.
    /// </summary>
    private async Task RunVerificationTimeoutAsync(CancellationToken ct)
    {
        try
        {
            await Task.Delay(_verifyDelayMs, ct);
            await TriggerDbVerificationOnceAsync(ct);
        }
        catch (OperationCanceledException)
        {
            // Shutdown before verification ran; nothing to report.
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "[{Reaction}] Failed to issue DB verification query.", Config.Name);
        }
    }

    /// <summary>
    /// Bonded to {ClientElement}.Inbound. Tracks which scripted barcodes actually echoed back.
    /// </summary>
    public Task<object?> HandleEcho(MessageEnvelope envelope, CancellationToken ct)
    {
        string payload = envelope.Payload?.ToString() ?? "";
        if (!payload.Contains("ROUA", StringComparison.OrdinalIgnoreCase))
        {
            return Task.FromResult<object?>(null);
        }

        bool allEchoed;
        foreach (var barcode in _expectedBarcodes)
        {
            if (payload.Contains(barcode, StringComparison.OrdinalIgnoreCase))
            {
                lock (_resultLock)
                {
                    _echoedBarcodes.Add(barcode);
                }

                Logger.Information("[{Reaction}] Echo verified for barcode {Barcode}.", Config.Name, barcode);
            }
        }

        lock (_resultLock)
        {
            allEchoed = _expectedBarcodes.Count > 0 && _expectedBarcodes.All(_echoedBarcodes.Contains);
        }

        if (allEchoed)
        {
            // Fire immediately rather than waiting out the fallback timeout — don't await
            // from inside a bond handler, verification issues its own bus publish.
            _ = TriggerDbVerificationOnceAsync(_runCt);
        }

        return Task.FromResult<object?>(null);
    }

    private async Task TriggerDbVerificationOnceAsync(CancellationToken ct)
    {
        lock (_resultLock)
        {
            if (_dbQueryIssued)
            {
                return;
            }
            _dbQueryIssued = true;
        }

        if (_expectedBarcodes.Count == 0)
        {
            Logger.Warning("[{Reaction}] No ExpectedBarcodes configured; skipping DB verification.", Config.Name);
            return;
        }

        // Publishing straight to "{DbElement}.Query.Verify" would go unheard: ElementManagerBase
        // only subscribes a DB element to its own bare name plus any bond Destination that targets
        // it (see RegisterElementDestBonds). So this fires the TuraDbQueryBond's Source instead —
        // its Handler (BuildDbVerifyQuery) builds the query and ReactionBase publishes the return
        // value to that bond's Destination, which is what actually gets VERIFY_DB subscribed.
        const string triggerTopic = "TURA_VERIFY.TriggerDbCheck";
        await MessageBus.PublishAsync(triggerTopic, new MessageEnvelope(triggerTopic, "check"), ct);
    }

    /// <summary>
    /// Bonded to TURA_VERIFY.TriggerDbCheck -> {DbElement}.Query.Verify. Builds the parameterized
    /// query; ReactionBase publishes the returned string to the bond's Destination.
    /// </summary>
    public Task<object?> BuildDbVerifyQuery(MessageEnvelope envelope, CancellationToken ct)
    {
        var parameters = new Dictionary<string, object>();
        var clauses = new List<string>();
        for (int i = 0; i < _expectedBarcodes.Count; i++)
        {
            string paramName = $"B{i}";
            clauses.Add($"RawData LIKE '%' + @{paramName} + '%'");
            parameters[paramName] = _expectedBarcodes[i];
        }

        var queryRequest = new
        {
            Sql = $"SELECT RawData, Direction, Type FROM MessageQueue WHERE Direction = 'IN' AND ({string.Join(" OR ", clauses)})",
            Operation = "QUERY",
            Parameters = parameters
        };

        Logger.Information("[{Reaction}] DB verification query issued for {Count} barcode(s).", Config.Name, _expectedBarcodes.Count);

        return Task.FromResult<object?>(JsonSerializer.Serialize(queryRequest));
    }

    /// <summary>
    /// Bonded to {DbElement}.QueryResult.Verify. Reports the final PASS/FAIL summary.
    /// </summary>
    public Task<object?> HandleDbVerifyResults(MessageEnvelope envelope, CancellationToken ct)
    {
        lock (_resultLock)
        {
            if (_verified)
            {
                return Task.FromResult<object?>(null);
            }
            _verified = true;
        }

        var loggedBarcodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            string payload = envelope.Payload?.ToString() ?? "{}";
            var node = JsonNode.Parse(payload);
            var results = node?["Results"]?.AsArray();

            if (results != null)
            {
                foreach (var row in results)
                {
                    string rawData = row?["RawData"]?.GetValue<string>() ?? "";
                    foreach (var barcode in _expectedBarcodes)
                    {
                        if (rawData.Contains(barcode, StringComparison.OrdinalIgnoreCase))
                        {
                            loggedBarcodes.Add(barcode);
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "[{Reaction}] Failed to parse DB verification results.", Config.Name);
        }

        Logger.Information("[{Reaction}] === TURAHOST VERIFICATION RESULTS ===", Config.Name);

        bool allPassed = true;
        foreach (var barcode in _expectedBarcodes)
        {
            bool echoed;
            lock (_resultLock)
            {
                echoed = _echoedBarcodes.Contains(barcode);
            }
            bool logged = loggedBarcodes.Contains(barcode);
            bool passed = echoed && logged;
            allPassed &= passed;

            Logger.Information("[{Reaction}] {Barcode,-20} | Echo: {Echo,-4} | DB Logged: {Logged,-4} | {Result}",
                Config.Name, barcode, echoed, logged, passed ? "PASS" : "FAIL");
        }

        Logger.Information("[{Reaction}] === {Result}: {Passed}/{Total} barcode(s) round-tripped and logged. ===",
            Config.Name, allPassed ? "OVERALL PASS" : "OVERALL FAIL", _expectedBarcodes.Count(b => _echoedBarcodes.Contains(b) && loggedBarcodes.Contains(b)), _expectedBarcodes.Count);

        return Task.FromResult<object?>(null);
    }
}
