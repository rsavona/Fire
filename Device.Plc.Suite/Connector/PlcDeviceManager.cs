using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using DeviceSpace.Common;
using DeviceSpace.Common.BaseClasses;
using DeviceSpace.Common.Configurations;
using DeviceSpace.Common.Contracts;
using DeviceSpace.Common.Logging;
using Microsoft.Extensions.Logging;

namespace Device.Plc.Suite.Connector;

public class 
    
    
    
    
    
    
    
    
    
    
    
    
    
    
    
    
    
    
    
    
    
    
    
    
    
    
    
    
    
    
    
    
    
    
    
    
    
    
    
    
    PlcDeviceManager : DeviceManagerBase<PlcServerDevice>
{
    public record struct ResponseKey(string DecisionPoint, int Gin);

    public record struct PendingRequest(PlcServerDevice MultiClientDevice, string Client, object Context);

    private readonly ConcurrentDictionary<ResponseKey, PendingRequest> _pendingResponses = new();
    private readonly HashSet<string> _expectResponseTopics = new();

    public PlcDeviceManager(IMessageBus bus, List<IDeviceConfig> configs,
        ILogger<DeviceManagerBase<PlcServerDevice>> logger,
        Func<IDeviceConfig, Serilog.ILogger, PlcServerDevice> deviceFactory)
        : base(bus, configs, logger, deviceFactory)
    {
    }

    /// <summary>
    ///   Send the DecisionRequestPayload and DecisionUpdatePayload to the MessageBus. 
    /// </summary>
    /// <param name="dev"></param>
    /// <param name="messEnv"></param>
    protected override void OnDeviceMessageReceivedAsync(object? dev, object messEnv)
    {
        if (dev is not PlcServerDevice device || messEnv is not MessageEnvelope env) return;
        Logger.LogTrace("The message from the PLC Processor should have bubbled up to here.{messEnv}", messEnv);
        if (env.Payload is DecisionRequestPayload req)
        {
            MessageBusTopic messageBusTopic = new MessageBusTopic(device.Config.Name, "DReqM", req.DecisionPoint);
            Logger.LogInformation("[{Dev}] PLC-REQ >> GIN: {Gin} at {DP}", device.Config.Name, req.Gin,
                req.DecisionPoint);

            List<string> subList = MessageBus.GetSubscriptionList(messageBusTopic);
            var logMessage = $"[{device.Config.Name}]";
            if (subList.Count > 0) logMessage += $" Subscribed to {subList.Count} topics.";
            Logger.LogTrace(logMessage);
            if (_expectResponseTopics.Contains(messageBusTopic.ToString()))
            {
                _pendingResponses.TryAdd(new ResponseKey(req.DecisionPoint, req.Gin),
                    new PendingRequest(device, env.Client, req));
            }

            var json = JsonSerializer.Serialize(env.Payload);
            
            _ = MessageBus.PublishAsync(messageBusTopic.ToString(),
                new MessageEnvelope(messageBusTopic, json, env.Gin, env.Client));
        }
        else if (env.Payload is DecisionUpdatePayload upd)
        {
            MessageBusTopic messageBusTopic = new MessageBusTopic(device.Config.Name, "DUM", upd.DecisionPoint);
            Logger.LogInformation("[{Dev}] PLC-UPD >> GIN: {Gin} at {DP}", device.Config.Name, upd.Gin,
                upd.DecisionPoint);
            _ = MessageBus.PublishAsync(messageBusTopic.ToString(), new MessageEnvelope(messageBusTopic, env.Payload));
        }
    }

    /// <summary>
    /// Get the response from the message bus and send it back to the PLC.
    /// This is registerd as a handler for the message bus by the DeviceManager Base
    /// </summary>
    /// <param name="multiClientDevice"></param>
    /// <param name="routeSource"></param>
    /// <param name="envelope"></param>
    /// <param name="ct"></param>
    protected override async Task HandleBusMessageAsync(IDevice multiClientDevice, string routeSource,
        string dest, MessageEnvelope envelope, CancellationToken ct)
    {
        try
        {
            var node = JsonNode.Parse(envelope.Payload?.ToString() ?? "{}");
            var dp = node?["DecisionPoint"]?.GetValue<string>();
            var gin = node?["GIN"]?.GetValue<int>();

            if (dp == null || gin == null) return;
            var key = new ResponseKey(dp, gin.Value);

            if (_pendingResponses.TryRemove(key, out var request))
            {
                Logger.LogInformation("[{Dev}] BUS-RESP >> Sending Response to plc {Client} (GIN: {Gin})",
                    multiClientDevice.Config.Name, request.Client, gin);

                var responseMsg = new DecisionResponsePayload(dp, gin.Value,
                    node!["Actions"]?.AsArray().Select(a => a?.ToString() ?? "").ToList() ?? new());
                await request.MultiClientDevice.SendResponseAsync(JsonSerializer.Serialize(responseMsg),
                    request.Client);
            }
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "[{Dev}] Error processing Bus Response", multiClientDevice.Config.Name);
        }
    }
}