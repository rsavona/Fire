using libplctag;
using libplctag.DataTypes;
using Fusion.Common;
using Fusion.Common.BaseClasses;
using Fusion.Common.Contracts;
using Fusion.Common.Enums;
using Fusion.Common.Messaging;
using Serilog.Core;
using System.Text.Json;
using System.Text;
using Fusion.Element.Plc.Suite.Messages;
using Fusion.Common.Configurations;

namespace Fusion.Element.Plc.Suite.Connector;

public class AbCipPlcElement : ClientElementBase
{
    private string _ipAddress;
    private string _cpuType;
    private string _path;
    private int _port;
    
    private Tag<StringPlcMapper, string>? _requestTag;
    private Tag<StringPlcMapper, string>? _responseTag;
    private Tag<StringPlcMapper, string>? _updateTag;

    private string _requestTagName;
    private string _responseTagName;
    private string _updateTagName;

    private CancellationTokenSource? _pollingCts;
    private readonly JsonSerializerOptions _jsonOptions = new() { PropertyNameCaseInsensitive = true };

    public AbCipPlcElement(IMessageBus bus, IElementBlueprint config, IFireLogger logger, LoggingLevelSwitch ls)
        : base(bus, config, logger, ls, needsHb: true)
    {
        _ipAddress = config.Properties.TryGetValue("IPAddress", out var ip) ? ip.ToString() ?? "" : "127.0.0.1";
        _cpuType = ConfigurationLoader.GetOptionalConfig(config.Properties, "CpuType", "lgx");
        _path = ConfigurationLoader.GetOptionalConfig(config.Properties, "Path", "1,0");
        _port = ConfigurationLoader.GetOptionalConfig(config.Properties, "Port", 44818);

        _requestTagName = ConfigurationLoader.GetOptionalConfig(config.Properties, "RequestTag", "Fire_Request");
        _responseTagName = ConfigurationLoader.GetOptionalConfig(config.Properties, "ResponseTag", "Fire_Response");
        _updateTagName = ConfigurationLoader.GetOptionalConfig(config.Properties, "UpdateTag", "Fire_Update");
    }

    protected override async Task<bool> ConnectAsync(CancellationToken ct = default)
    {
        try
        {
            Logger.Information("[{Dev}] Connecting to Allen-Bradley PLC at {IP} (Path: {Path})", Config.Name, _ipAddress, _path);

            // Initialize tags
            _requestTag = CreateTag(_requestTagName);
            _responseTag = CreateTag(_responseTagName);
            _updateTag = CreateTag(_updateTagName);

            // Test connection by reading one tag
            await Task.Run(() => _requestTag.Read(), ct);

            Logger.Information("[{Dev}] Successfully connected to PLC via CIP.", Config.Name);
            return true;
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "[{Dev}] Failed to connect to PLC via CIP: {Msg}", Config.Name, ex.Message);
            return false;
        }
    }

    private Tag<StringPlcMapper, string> CreateTag(string tagName)
    {
        return new Tag<StringPlcMapper, string>()
        {
            Name = tagName,
            Gateway = _ipAddress,
            Path = _path,
            PlcType = _cpuType == "lgx" ? PlcType.ControlLogix : PlcType.Slc500, 
            Protocol = Protocol.ab_eip,
            Timeout = TimeSpan.FromSeconds(2)
        };
    }

    protected override Task ElementConnectedAsync()
    {
        _pollingCts = new CancellationTokenSource();
        RegisterTask(Task.Run(() => PollingLoopAsync(_pollingCts.Token)));
        return base.ElementConnectedAsync();
    }

    protected override Task ElementDisconnectedAsync()
    {
        _pollingCts?.Cancel();
        _requestTag?.Dispose();
        _responseTag?.Dispose();
        _updateTag?.Dispose();
        return base.ElementDisconnectedAsync();
    }

    private async Task PollingLoopAsync(CancellationToken ct)
    {
        Logger.Information("[{Dev}] Starting CIP Polling Loop.", Config.Name);
        string lastRequest = "";
        string lastUpdate = "";

        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (_requestTag != null)
                {
                    await Task.Run(() => _requestTag.Read(), ct);
                    string currentRequest = _requestTag.Value?.Trim() ?? "";

                    if (!string.IsNullOrEmpty(currentRequest) && currentRequest != lastRequest)
                    {
                        lastRequest = currentRequest;
                        await ProcessPlcMessageAsync(currentRequest, "DReqM");
                    }
                }

                if (_updateTag != null)
                {
                    await Task.Run(() => _updateTag.Read(), ct);
                    string currentUpdate = _updateTag.Value?.Trim() ?? "";

                    if (!string.IsNullOrEmpty(currentUpdate) && currentUpdate != lastUpdate)
                    {
                        lastUpdate = currentUpdate;
                        await ProcessPlcMessageAsync(currentUpdate, "Update");
                    }
                }

                await Task.Delay(100, ct); // Poll every 100ms
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                Logger.Error(ex, "[{Dev}] CIP Polling Error", Config.Name);
                await Task.Delay(2000, ct);
            }
        }
    }

    private async Task ProcessPlcMessageAsync(string rawJson, string messageType)
    {
        try
        {
            Logger.Verbose("[{Dev}] CIP RX << {Data}", Config.Name, rawJson);
            Tracker.IncrementInbound();
            
            object? payload = null;
            int gin = 0;

            try
            {
                if (messageType == "DReqM")
                {
                    payload = JsonSerializer.Deserialize<DecisionRequestPayload>(rawJson, _jsonOptions);
                    gin = (payload as DecisionRequestPayload)?.Gin ?? 0;
                }
                else if (messageType == "Update")
                {
                    payload = JsonSerializer.Deserialize<DecisionUpdatePayload>(rawJson, _jsonOptions);
                    gin = (payload as DecisionUpdatePayload)?.Gin ?? 0;
                }
            }
            catch
            {
                // Fallback to raw JSON if deserialization fails
                payload = rawJson;
            }

            var topic = new MessageBusTopic(Config.Name, messageType);
            var envelope = new MessageEnvelope(topic, payload ?? rawJson, gin);
            
            // Publish to bus
            await MessageBus.PublishAsync(topic.ToString(), envelope);
            await Machine.FireAsync(Event.MessageReceived);
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "[{Dev}] Failed to process CIP message: {Data}", Config.Name, rawJson);
        }
    }

    public override async Task SendAsync(string message, CancellationToken token, bool fireEvent = true)
    {
        if (Machine.State != State.Connected || _responseTag == null) return;

        try
        {
            _responseTag.Value = message;
            await Task.Run(() => _responseTag.Write(), token);

            if (fireEvent)
            {
                Tracker.IncrementOutbound();
                await Machine.FireAsync(Event.MessageSent);
            }
            Logger.Verbose("[{Dev}] CIP TX >> {Data}", Config.Name, message);
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "[{Dev}] CIP Write Failure", Config.Name);
            throw;
        }
    }

    public override Task SendHeartbeatAsync(CancellationToken token)
    {
        return Task.CompletedTask;
    }

    protected override void OnElementFaultedAsync(CancellationToken token = default)
    {
        Logger.Warning("[{Dev}] CIP Element Faulted.", Config.Name);
    }
}
