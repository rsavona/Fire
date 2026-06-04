using System.Data;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Fusion.Common;
using Fusion.Common.BaseClasses;
using Fusion.Common.Configurations;
using Fusion.Common.Contracts;
using Fusion.Common.TcpSocket;
using Fusion.Common.TCP_Classes;
using Serilog.Core;

namespace Fusion.Element.Scanner.Suite;

/// <summary>
/// A Socket Server that simulates a physical scanner by generating and sending barcodes to connected clients.
/// </summary>
public class ScannerSimulatorElement : TcpServerElementBase<SocketMessageProcessor>, IMessageProvider
{
    public event Func<object, object, Task>? MessageReceived;

    private readonly string _barcodeSource;
    private readonly int _minLen;
    private readonly int _maxLen;
    private readonly int _minInterval;
    private readonly int _maxInterval;
    private readonly List<string> _exceptionBarcodes;
    private readonly int _exceptionPercent;
    private readonly string _prefix;
    private readonly string _suffix;
    private readonly string _terminationChar;
    
    private readonly List<string> _fileBarcodes = new();
    private readonly List<string> _sqlBarcodes = new();
    private readonly string? _sqlElementName;
    private readonly string? _sqlQuery;

    private Task? _simulationTask;
    private CancellationTokenSource? _simCts;

    public ScannerSimulatorElement(IMessageBus bus, IElementBlueprint config, IFireLogger logger, LoggingLevelSwitch swtch)
        : base(bus, config, logger, 
               new SocketMessageProcessor(config.Name, logger), 
               swtch, 
               ConfigurationLoader.GetOptionalConfig(config.Properties, "Port", 5000), 
               new DelimiterSetStrategy([(byte)'\r']), 
               ConfigurationLoader.GetOptionalConfig(config.Properties, "MaxClients", 10))
    {
        _barcodeSource = ConfigurationLoader.GetOptionalConfig(config.Properties, "BarcodeSource", "Random");
        
        var lenRange = ConfigurationLoader.GetOptionalConfig(config.Properties, "BarcodeLengthRange", "10-20");
        ParseRange(lenRange, 10, 20, out _minLen, out _maxLen);

        var intervalRange = ConfigurationLoader.GetOptionalConfig(config.Properties, "ScanIntervalRangeMs", "1000-5000");
        ParseRange(intervalRange, 1000, 5000, out _minInterval, out _maxInterval);

        _exceptionBarcodes = ConfigurationLoader.GetOptionalConfig(config.Properties, "ExceptionBarcodes", new List<string>());
        _exceptionPercent = ConfigurationLoader.GetOptionalConfig(config.Properties, "ExceptionPercentage", 0);
        
        _prefix = ConfigurationLoader.GetOptionalConfig(config.Properties, "Prefix", "");
        _suffix = ConfigurationLoader.GetOptionalConfig(config.Properties, "Suffix", "");
        _terminationChar = ConfigurationLoader.GetOptionalConfig(config.Properties, "TerminationChar", "\r");

        if (_barcodeSource.Equals("File", StringComparison.OrdinalIgnoreCase))
        {
            var filePath = ConfigurationLoader.GetOptionalConfig(config.Properties, "SourceFile", "");
            if (!string.IsNullOrEmpty(filePath) && File.Exists(filePath))
            {
                _fileBarcodes.AddRange(File.ReadAllLines(filePath).Where(l => !string.IsNullOrWhiteSpace(l)));
            }
        }
        else if (_barcodeSource.Equals("Sql", StringComparison.OrdinalIgnoreCase))
        {
            _sqlElementName = ConfigurationLoader.GetOptionalConfig(config.Properties, "SqlElementName", "");
            _sqlQuery = ConfigurationLoader.GetOptionalConfig(config.Properties, "SqlQuery", "");
        }
    }

    protected override Task OnStartAsync(CancellationToken ct)
    {
        _simCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _simulationTask = Task.Run(() => RunSimulationAsync(_simCts.Token), _simCts.Token);
        
        if (_barcodeSource.Equals("Sql", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(_sqlElementName))
        {
            _ = Task.Run(() => RequestSqlBarcodesAsync(_simCts.Token), _simCts.Token);
        }

        return base.OnStartAsync(ct);
    }

    protected override async Task OnEnterStoppingAsync()
    {
        if (_simCts != null)
        {
            await _simCts.CancelAsync();
        }

        if (_simulationTask != null)
        {
            try { await _simulationTask; } catch { }
        }

        await base.OnEnterStoppingAsync();
    }

    private async Task RunSimulationAsync(CancellationToken ct)
    {
        Logger.Information("[{Dev}] Scanner simulation started. Source: {Source}", Config.Name, _barcodeSource);

        while (!ct.IsCancellationRequested)
        {
            try
            {
                // Wait for a random interval
                int interval = Random.Shared.Next(_minInterval, _maxInterval + 1);
                await Task.Delay(interval, ct);

                // Only send if there are connected clients
                if (ConnectedClients.IsEmpty) continue;

                string barcode = GenerateBarcode();
                string framedBarcode = $"{_prefix}{barcode}{_suffix}{_terminationChar}";
                
                Logger.Debug("[{Dev}] Simulated Scan: {Barcode}", Config.Name, barcode);

                foreach (var client in ConnectedClients.Keys)
                {
                    await SendAsync(client, framedBarcode);
                }

                // Also notify the bus that WE sent a barcode (simulating a scanner element)
                var topic = new MessageBusTopic(Config.Name, "Scan");
                var envelope = new MessageEnvelope(topic, barcode, 0, "Simulator");
                if (MessageReceived != null)
                {
                    await MessageReceived.Invoke(this, envelope);
                }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                Logger.Error(ex, "[{Dev}] Error in simulation loop", Config.Name);
                await Task.Delay(5000, ct);
            }
        }
    }

    private string GenerateBarcode()
    {
        // 1. Check for exceptions
        if (_exceptionBarcodes.Count > 0 && Random.Shared.Next(100) < _exceptionPercent)
        {
            return _exceptionBarcodes[Random.Shared.Next(_exceptionBarcodes.Count)];
        }

        // 2. Generate from source
        if (_barcodeSource.Equals("File", StringComparison.OrdinalIgnoreCase) && _fileBarcodes.Count > 0)
        {
            return _fileBarcodes[Random.Shared.Next(_fileBarcodes.Count)];
        }
        
        if (_barcodeSource.Equals("Sql", StringComparison.OrdinalIgnoreCase) && _sqlBarcodes.Count > 0)
        {
            return _sqlBarcodes[Random.Shared.Next(_sqlBarcodes.Count)];
        }

        // Default to Random
        int length = Random.Shared.Next(_minLen, _maxLen + 1);
        const string chars = "ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";
        return new string(Enumerable.Repeat(chars, length)
            .Select(s => s[Random.Shared.Next(s.Length)]).ToArray());
    }

    private async Task RequestSqlBarcodesAsync(CancellationToken ct)
    {
        if (string.IsNullOrEmpty(_sqlElementName) || string.IsNullOrEmpty(_sqlQuery)) return;

        Logger.Information("[{Dev}] Requesting barcodes from SQL element: {SqlElement}", Config.Name, _sqlElementName);

        // Standard pattern for requesting from DB element
        var requestTopic = $"{_sqlElementName}.Command.Query";
        var payload = new 
        {
            Sql = _sqlQuery,
            Operation = "QUERY",
            Discriminator = "Barcodes"
        };

        // Subscribe to results
        var responseTopic = $"{_sqlElementName}.QueryResult.Barcodes";
        await MessageBus.SubscribeAsync(responseTopic, (env, _) => 
            {
                try
                {
                    var node = JsonNode.Parse(env.Payload?.ToString() ?? "{}");
                    var results = node?["Results"]?.AsArray();
                    if (results != null)
                    {
                        lock (_sqlBarcodes)
                        {
                            _sqlBarcodes.Clear();
                            foreach (var row in results)
                            {
                                var barcode = row?["Barcode"]?.GetValue<string>() ?? row?.AsObject().FirstOrDefault().Value?.GetValue<string>();
                                if (!string.IsNullOrEmpty(barcode)) _sqlBarcodes.Add(barcode);
                            }
                        }
                        Logger.Information("[{Dev}] Loaded {Count} barcodes from SQL", Config.Name, _sqlBarcodes.Count);
                    }
                }
                catch (Exception ex)
                {
                    Logger.Error(ex, "[{Dev}] Failed to parse SQL barcodes response", Config.Name);
                }
                return Task.CompletedTask;
            });

            while (!ct.IsCancellationRequested)
            {
                await MessageBus.PublishAsync(requestTopic, new MessageEnvelope(requestTopic, JsonSerializer.Serialize(payload)));
            // Refresh every 5 minutes
            await Task.Delay(TimeSpan.FromMinutes(5), ct);
        }
    }

    private void ParseRange(string range, int defaultMin, int defaultMax, out int min, out int max)
    {
        min = defaultMin;
        max = defaultMax;
        if (string.IsNullOrEmpty(range)) return;

        var parts = range.Split(new[] { '-', ':', ',' }, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length >= 2)
        {
            if (int.TryParse(parts[0], out int p1) && int.TryParse(parts[1], out int p2))
            {
                min = Math.Min(p1, p2);
                max = Math.Max(p1, p2);
            }
        }
        else if (parts.Length == 1 && int.TryParse(parts[0], out int val))
        {
            min = max = val;
        }
    }
}
