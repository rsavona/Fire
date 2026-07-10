using MQTTnet;
using MQTTnet.Client;
using MQTTnet.Protocol;
using Fusion.Common;
using Fusion.Common.BaseClasses;
using Fusion.Common.Configurations;
using Fusion.Common.Contracts;
using Fusion.Common.Enums;
using Fusion.Element.Sparkplug.Protocol;
using Serilog.Core;

namespace Fusion.Element.Sparkplug;

/// <summary>
/// Presents this Fusion instance to an MQTT broker as a Sparkplug B Edge Node,
/// so hosts like Ignition MQTT Engine see Fusion elements as devices with live
/// metrics in their tag browser.
///
/// Identity mapping: Group = site (GroupId), Edge Node = this Fusion instance
/// (EdgeNodeId), Device = a Fusion element (discovered from All_Elements
/// status traffic), Metrics = element status fields plus payloads of any
/// blueprint-selected bus topics (MetricTopics).
///
/// Lifecycle: MQTT CONNECT carries an NDEATH will with the session bdSeq;
/// NBIRTH (seq 0) follows on connect, then a DBIRTH per discovered element.
/// Status changes flow as batched, report-by-exception DDATA every
/// ReportIntervalMs; an element going Critical publishes DDEATH. NCMD
/// "Node Control/Rebirth" replays all births; other NCMD/DCMD metric writes
/// are republished on the local bus at {EdgeNodeId}.SparkplugCmd.{MetricName}.
/// </summary>
public class SparkplugEdgeNodeElement : ClientElementBase
{
    private readonly MqttFactory _factory = new();
    private readonly IMqttClient _client;
    private readonly SparkplugSession _session = new();

    private readonly string _host;
    private readonly int _port;
    private readonly string? _username;
    private readonly string? _password;
    private readonly bool _useTls;
    private readonly int _reportIntervalMs;

    public string GroupId { get; }
    public string EdgeNodeId { get; }
    public bool PublishStatusMetrics { get; }
    public IReadOnlyList<string> MetricTopics { get; }

    // Pending outbound batches, flushed every ReportIntervalMs.
    private readonly object _pendingLock = new();
    private readonly Dictionary<string, Dictionary<string, SparkplugMetric>> _pendingDeviceData = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _pendingDeviceBirths = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, SparkplugMetric> _pendingNodeData = new(StringComparer.OrdinalIgnoreCase);

    private CancellationTokenSource? _flushCts;

    public SparkplugEdgeNodeElement(IMessageBus bus, IElementBlueprint config, IFireLogger logger, LoggingLevelSwitch ls)
        : base(bus, config, logger, ls, needsHb: false)
    {
        _host = ConfigurationLoader.GetOptionalConfig(config.Properties, "BrokerHost", "127.0.0.1");
        _port = ConfigurationLoader.GetOptionalConfig(config.Properties, "BrokerPort", 1883);
        _username = GetOptionalString(config, "Username");
        _password = GetOptionalString(config, "Password");
        _useTls = ConfigurationLoader.GetOptionalConfig(config.Properties, "UseTls", false);
        _reportIntervalMs = Math.Max(250, ConfigurationLoader.GetOptionalConfig(config.Properties, "ReportIntervalMs", 5000));
        PublishStatusMetrics = ConfigurationLoader.GetOptionalConfig(config.Properties, "PublishStatusMetrics", true);

        GroupId = GetOptionalString(config, "GroupId") ?? ResolveDefaultGroupId();
        EdgeNodeId = GetOptionalString(config, "EdgeNodeId") ?? ResolveDefaultEdgeNodeId(config);

        MetricTopics = (GetOptionalString(config, "MetricTopics") ?? string.Empty)
            .Split([';', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        _client = _factory.CreateMqttClient();
        _client.ApplicationMessageReceivedAsync += HandleMqttMessageAsync;
        _client.DisconnectedAsync += HandleMqttDisconnectedAsync;
    }

    private static string? GetOptionalString(IElementBlueprint config, string key)
    {
        if (config.Properties.TryGetValue(key, out var raw))
        {
            var text = raw?.ToString();
            if (!string.IsNullOrWhiteSpace(text)) return text;
        }
        return null;
    }

    /// <summary>Sparkplug Group defaults to the site: the space CustomerName.</summary>
    private static string ResolveDefaultGroupId()
    {
        var space = ConfigurationLoader.GetSpaceConfig();
        return string.IsNullOrWhiteSpace(space?.CustomerName) ? "Fusion" : space!.CustomerName;
    }

    /// <summary>
    /// Edge Node defaults to this instance's identity, following the Link suite's
    /// Origin precedent: an explicit Origin property, else the space service name,
    /// else the customer name, else the machine name.
    /// </summary>
    private static string ResolveDefaultEdgeNodeId(IElementBlueprint config)
    {
        var origin = GetOptionalString(config, "Origin");
        if (origin != null) return origin;

        var space = ConfigurationLoader.GetSpaceConfig();
        if (!string.IsNullOrWhiteSpace(space?.ServiceName)) return space!.ServiceName!;
        if (!string.IsNullOrWhiteSpace(space?.CustomerName)) return space!.CustomerName;
        return Environment.MachineName;
    }

    // ------------------------------------------------------------------
    // Connection lifecycle
    // ------------------------------------------------------------------

    protected override async Task<bool> ConnectAsync(CancellationToken ct = default)
    {
        try
        {
            // A new MQTT session gets a new bdSeq; the NDEATH will registered with
            // the CONNECT packet must announce the same bdSeq the NBIRTH will carry.
            ulong bdSeq = _session.OpenSession();
            var willPayload = _session.CreateNodeDeathPayload();

            var builder = new MqttClientOptionsBuilder()
                .WithTcpServer(_host, _port)
                .WithClientId($"{EdgeNodeId}_{Config.Name}")
                .WithCleanSession(true)
                .WithWillTopic(SparkplugTopics.NodeDeath(GroupId, EdgeNodeId))
                .WithWillPayload(willPayload.Encode())
                .WithWillQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
                .WithWillRetain(false);

            if (!string.IsNullOrEmpty(_username))
                builder.WithCredentials(_username, _password ?? string.Empty);
            if (_useTls)
                builder.WithTlsOptions(o => o.UseTls());

            var result = await _client.ConnectAsync(builder.Build(), ct);
            if (result.ResultCode != MqttClientConnectResultCode.Success)
            {
                Logger.Warning("[{Dev}] Sparkplug broker refused connection: {Code}. Will retry.",
                    Config.Name, result.ResultCode);
                return false;
            }

            var subscribeOptions = _factory.CreateSubscribeOptionsBuilder()
                .WithTopicFilter(f => f.WithTopic(SparkplugTopics.NodeCommand(GroupId, EdgeNodeId))
                    .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce))
                .WithTopicFilter(f => f.WithTopic(SparkplugTopics.DeviceCommandFilter(GroupId, EdgeNodeId))
                    .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce))
                .Build();
            await _client.SubscribeAsync(subscribeOptions, ct);

            await PublishBirthSequenceAsync(ct);

            Logger.Information(
                "[{Dev}] Sparkplug session established with {Host}:{Port} as spBv1.0/{Group}/{Node} (bdSeq={BdSeq})",
                Config.Name, _host, _port, GroupId, EdgeNodeId, bdSeq);
            return true;
        }
        catch (Exception ex)
        {
            // Broker unavailable must never fault the element permanently —
            // returning false routes through WaitingToRetry with backoff.
            Logger.Warning("[{Dev}] Sparkplug broker unreachable at {Host}:{Port}: {Message}. Will retry.",
                Config.Name, _host, _port, ex.Message);
            return false;
        }
    }

    /// <summary>Publishes NBIRTH (seq 0) followed by a DBIRTH for every known device.</summary>
    private async Task PublishBirthSequenceAsync(CancellationToken ct)
    {
        var nodeMetrics = new List<SparkplugMetric>
        {
            SparkplugMetric.Create("Node Info/Platform", "Fortna Fusion"),
            SparkplugMetric.Create("Node Info/Version", GetElementVersion()),
            SparkplugMetric.Create("Node Info/Machine", Environment.MachineName)
        };

        var birth = _session.CreateNodeBirthPayload(nodeMetrics);
        await PublishSparkplugAsync(SparkplugTopics.NodeBirth(GroupId, EdgeNodeId), birth, ct);

        foreach (var deviceId in _session.GetKnownDevices())
        {
            var deviceBirth = _session.CreateDeviceBirthPayload(deviceId);
            await PublishSparkplugAsync(SparkplugTopics.DeviceBirth(GroupId, EdgeNodeId, deviceId), deviceBirth, ct);
        }

        // Any queued deltas are superseded by the full births just published.
        lock (_pendingLock)
        {
            _pendingDeviceData.Clear();
            _pendingDeviceBirths.Clear();
        }
    }

    protected override Task ElementConnectedAsync()
    {
        _flushCts?.Cancel();
        _flushCts = new CancellationTokenSource();
        RegisterTask(FlushLoopAsync(_flushCts.Token));
        return Task.CompletedTask;
    }

    protected override async Task ElementDisconnectedAsync()
    {
        _flushCts?.Cancel();
        try
        {
            if (_client.IsConnected)
                await _client.DisconnectAsync();
        }
        catch (Exception ex)
        {
            Logger.Debug("[{Dev}] Error during MQTT disconnect: {Message}", Config.Name, ex.Message);
        }

        await base.ElementDisconnectedAsync();
    }

    private async Task HandleMqttDisconnectedAsync(MqttClientDisconnectedEventArgs e)
    {
        if (Machine.State == State.Connected)
        {
            Logger.Warning("[{Dev}] Sparkplug broker connection lost: {Reason}. Reconnecting...",
                Config.Name, e.Reason);
            if (Machine.CanFire(Event.ConnectionLost))
                await Machine.FireAsync(Event.ConnectionLost);
        }
    }

    // ------------------------------------------------------------------
    // Outbound: element status → device births/data/deaths
    // ------------------------------------------------------------------

    /// <summary>
    /// Bus handler for All_Elements.StatusMessage. Discovers Fusion elements as
    /// Sparkplug devices, converts their status fields to metrics, and queues
    /// DBIRTH/DDATA/DDEATH work for the report loop.
    /// </summary>
    public async Task HandleStatusEnvelopeAsync(MessageEnvelope envelope, CancellationToken ct)
    {
        if (envelope.Payload is not ElementStatusMessage status) return;

        string deviceId = status.ElementId.ElementName;

        // Never model this element as a device of itself — its own publish
        // activity would feed back into new DDATA forever.
        if (deviceId.Equals(Config.Name, StringComparison.OrdinalIgnoreCase)) return;

        if (status.Health == ElementHealth.Critical)
        {
            await PublishDeviceDeathAsync(deviceId, ct);
            return;
        }

        var metrics = SparkplugMetricMapper.FromStatus(status);
        var update = _session.RecordDevice(deviceId, metrics);

        if (!update.NeedsBirth && update.ChangedMetrics.Count == 0) return;

        lock (_pendingLock)
        {
            if (update.NeedsBirth)
            {
                _pendingDeviceBirths.Add(deviceId);
                _pendingDeviceData.Remove(deviceId);
            }
            else
            {
                if (!_pendingDeviceData.TryGetValue(deviceId, out var pending))
                {
                    pending = new Dictionary<string, SparkplugMetric>(StringComparer.OrdinalIgnoreCase);
                    _pendingDeviceData[deviceId] = pending;
                }
                foreach (var metric in update.ChangedMetrics)
                    pending[metric.Name] = metric;
            }
        }
    }

    /// <summary>
    /// Bus handler for blueprint-selected MetricTopics patterns: the envelope's
    /// payload becomes a device metric named after the full bus topic, attached
    /// to the device matching the topic's element segment.
    /// </summary>
    public Task HandleMetricEnvelopeAsync(MessageEnvelope envelope, CancellationToken ct)
    {
        // Loop guard: skip anything this element itself published (SparkplugCmd echoes).
        if (envelope.Client.Equals(Config.Name, StringComparison.OrdinalIgnoreCase)) return Task.CompletedTask;

        string deviceId = envelope.Destination.ElementName;
        if (deviceId.Equals(Config.Name, StringComparison.OrdinalIgnoreCase)) return Task.CompletedTask;

        string metricName = envelope.Destination.ToString();
        var metric = SparkplugMetric.FromText(metricName, envelope.GetPayloadText());
        var update = _session.RecordDevice(deviceId, [metric]);

        if (!update.NeedsBirth && update.ChangedMetrics.Count == 0) return Task.CompletedTask;

        lock (_pendingLock)
        {
            if (update.NeedsBirth)
            {
                _pendingDeviceBirths.Add(deviceId);
                _pendingDeviceData.Remove(deviceId);
            }
            else
            {
                if (!_pendingDeviceData.TryGetValue(deviceId, out var pending))
                {
                    pending = new Dictionary<string, SparkplugMetric>(StringComparer.OrdinalIgnoreCase);
                    _pendingDeviceData[deviceId] = pending;
                }
                foreach (var changed in update.ChangedMetrics)
                    pending[changed.Name] = changed;
            }
        }

        return Task.CompletedTask;
    }

    private async Task PublishDeviceDeathAsync(string deviceId, CancellationToken ct)
    {
        bool wasBorn = _session.IsDeviceBorn(deviceId);
        var death = _session.CreateDeviceDeathPayload(deviceId);

        lock (_pendingLock)
        {
            _pendingDeviceData.Remove(deviceId);
            _pendingDeviceBirths.Remove(deviceId);
        }

        if (!wasBorn || !_client.IsConnected) return;

        Logger.Warning("[{Dev}] Element {Device} went Critical — publishing DDEATH.", Config.Name, deviceId);
        await PublishSparkplugAsync(SparkplugTopics.DeviceDeath(GroupId, EdgeNodeId, deviceId), death, ct);
    }

    /// <summary>Batches pending births and RBE deltas into DBIRTH/DDATA/NDATA every ReportIntervalMs.</summary>
    private async Task FlushLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(_reportIntervalMs, ct);
                if (!_client.IsConnected) continue;

                List<string> births;
                List<(string DeviceId, List<SparkplugMetric> Metrics)> dataBatches;
                List<SparkplugMetric> nodeBatch;

                lock (_pendingLock)
                {
                    births = _pendingDeviceBirths.ToList();
                    _pendingDeviceBirths.Clear();

                    dataBatches = _pendingDeviceData
                        .Select(kv => (kv.Key, kv.Value.Values.ToList()))
                        .ToList();
                    _pendingDeviceData.Clear();

                    nodeBatch = _pendingNodeData.Values.ToList();
                    _pendingNodeData.Clear();
                }

                try
                {
                    foreach (var deviceId in births)
                    {
                        var birth = _session.CreateDeviceBirthPayload(deviceId);
                        await PublishSparkplugAsync(SparkplugTopics.DeviceBirth(GroupId, EdgeNodeId, deviceId), birth, ct);
                        Logger.Information("[{Dev}] DBIRTH published for device {Device} ({Count} metrics)",
                            Config.Name, deviceId, birth.Metrics.Count);
                    }

                    foreach (var (deviceId, metrics) in dataBatches)
                    {
                        var data = _session.CreateDeviceDataPayload(metrics);
                        await PublishSparkplugAsync(SparkplugTopics.DeviceData(GroupId, EdgeNodeId, deviceId), data, ct);
                    }

                    if (nodeBatch.Count > 0)
                    {
                        var nodeData = _session.CreateNodeDataPayload(nodeBatch);
                        await PublishSparkplugAsync(SparkplugTopics.NodeData(GroupId, EdgeNodeId), nodeData, ct);
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    Logger.Warning("[{Dev}] Sparkplug flush failed (broker hiccup?): {Message}", Config.Name, ex.Message);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown/disconnect.
        }
    }

    // ------------------------------------------------------------------
    // Inbound: NCMD / DCMD
    // ------------------------------------------------------------------

    private async Task HandleMqttMessageAsync(MqttApplicationMessageReceivedEventArgs e)
    {
        try
        {
            var parsed = SparkplugTopics.Parse(e.ApplicationMessage.Topic);
            if (parsed == null) return;
            if (parsed.MessageType != "NCMD" && parsed.MessageType != "DCMD") return;

            var payload = SparkplugPayload.Decode(e.ApplicationMessage.PayloadSegment.ToArray());
            Tracker.IncrementInbound();
            if (Machine.CanFire(Event.MessageReceived))
                await Machine.FireAsync(Event.MessageReceived);

            foreach (var metric in payload.Metrics)
            {
                if (parsed.MessageType == "NCMD" &&
                    string.Equals(metric.Name, SparkplugSession.RebirthMetricName, StringComparison.OrdinalIgnoreCase) &&
                    metric.Value is bool and true)
                {
                    Logger.Information("[{Dev}] NCMD Node Control/Rebirth received — republishing births.", Config.Name);
                    _session.ResetForRebirth();
                    await PublishBirthSequenceAsync(CancellationToken.None);
                    continue;
                }

                await RepublishCommandAsync(parsed.DeviceId, metric);
            }
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "[{Dev}] Error handling inbound Sparkplug command", Config.Name);
        }
    }

    /// <summary>
    /// Republishes an inbound NCMD/DCMD metric write onto the local bus at
    /// {EdgeNodeId}.SparkplugCmd.{MetricName} so reactions can act on it.
    /// </summary>
    private async Task RepublishCommandAsync(string? deviceId, SparkplugMetric metric)
    {
        var command = new SparkplugCommandMessage
        {
            DeviceId = deviceId,
            MetricName = metric.Name,
            DataType = metric.DataType.ToString(),
            Value = metric.Value?.ToString()
        };

        string topic = $"{EdgeNodeId}.SparkplugCmd.{metric.Name}";
        Logger.Information("[{Dev}] Sparkplug command {Type} '{Metric}'={Value} (device: {Device}) → bus topic {Topic}",
            Config.Name, deviceId == null ? "NCMD" : "DCMD", metric.Name, command.Value, deviceId ?? "-", topic);

        await MessageBus.PublishAsync(topic, new MessageEnvelope(topic, command, client: Config.Name));
    }

    // ------------------------------------------------------------------
    // ClientElementBase plumbing
    // ------------------------------------------------------------------

    private async Task PublishSparkplugAsync(string topic, SparkplugPayload payload, CancellationToken ct)
    {
        var message = new MqttApplicationMessageBuilder()
            .WithTopic(topic)
            .WithPayload(payload.Encode())
            .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtMostOnce)
            .Build();

        await _client.PublishAsync(message, ct);
        Tracker.IncrementOutbound();
    }

    /// <summary>Free-form text sent to this element becomes a node-level String metric in the next NDATA.</summary>
    public override Task SendAsync(string message, CancellationToken token, bool fireEvent = true)
    {
        var metric = SparkplugMetric.FromText("Fusion/Message", message);
        lock (_pendingLock)
        {
            _pendingNodeData[metric.Name] = metric;
        }
        return Task.CompletedTask;
    }

    public override Task SendHeartbeatAsync(CancellationToken token) => Task.CompletedTask;

    protected override void OnElementFaultedAsync(CancellationToken token = default)
    {
        Logger.Warning("[{Dev}] Sparkplug element faulted.", Config.Name);
    }

    protected override async ValueTask DisposeAsyncCore()
    {
        _flushCts?.Cancel();
        try
        {
            if (_client.IsConnected) await _client.DisconnectAsync();
        }
        catch
        {
            // Best effort on dispose.
        }
        _client.Dispose();
        await base.DisposeAsyncCore();
    }
}
