using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using Device.Plc.Suite.Messages;
using DeviceSpace.Common;
using DeviceSpace.Common.BaseClasses;
using DeviceSpace.Common.Configurations;
using DeviceSpace.Common.Contracts;
using DeviceSpace.Common.Logging;
using Microsoft.Extensions.Logging;

namespace Device.Plc.Suite.Connector;

public class  PlcDeviceManager : DeviceManagerBase<PlcServerDevice>
{
    public record struct ResponseKey(string DecisionPoint, int Gin);

    public record struct PendingRequest(PlcServerDevice MultiClientDevice, string Client, object Context);

    private readonly ConcurrentDictionary<ResponseKey, PendingRequest> _pendingResponses = new();
    private readonly HashSet<string> _expectResponseTopics = new();

    public PlcDeviceManager(IMessageBus bus, List<IDeviceConfig> configs,
        IFireLogger<DeviceManagerBase<PlcServerDevice>> logger,
        Func<IDeviceConfig, IFireLogger, PlcServerDevice> deviceFactory)
        : base(bus, configs, logger, deviceFactory)
    {
    }

    /// <summary>
    ///   Send the DecisionRequestPayload and DecisionUpdatePayload to the MessageBus. 
    /// </summary>
    /// <param name="dev"></param>
    /// <param name="messEnv"></param>
    protected override async Task OnDeviceMessageToMessageBusAsync(object? dev, object messEnv)
    {
        if (dev is not PlcServerDevice device || messEnv is not MessageEnvelope env) return;
        var targetLogger = Logger.WithContext("DeviceName", device.Key.DeviceName);
        if (env.Payload is DecisionRequestPayload req)
        {
            _pendingResponses[new ResponseKey(req.DecisionPoint, req.Gin)] = new PendingRequest(device, env.Client, req);
            MessageBusTopic messageBusTopic = new MessageBusTopic(device.Config.Name, "DReqM", req.DecisionPoint);
            
            Logger.LogConveyableEvent( device.Key.DeviceName, $"Request to {messageBusTopic}", req.Gin.ToString(), req.Barcodes, req.DecisionPoint);

            List<string> subList = MessageBus.GetSubscriptionList(messageBusTopic);
            var logMessage = $"[{device.Config.Name}]";
            if (subList.Count > 0) logMessage += $" Subscribed to {subList.Count} topics.";
            targetLogger.Verbose(logMessage);
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
            MessageBusTopic messageBusTopic = new MessageBusTopic(device.Config.Name, "Update", upd.DecisionPoint);
            
            Logger.LogConveyableEvent(device.Key.DeviceName, upd.ToString(),  upd.Gin.ToString(),upd.Barcodes, upd.DecisionPoint);

            targetLogger.Information("[{Dev}] PLC-UPD >> GIN: {Gin} at {DP}", device.Config.Name, upd.Gin,
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
    protected override async Task HandleBusMessageAsync( MessageEnvelope envelope, CancellationToken ct)
    {
        var topic = envelope.Destination; 
        DeviceInstances.TryGetValue(topic.DeviceName, out var device);
        try
        {
            // 1. Check for cancellation before starting work
            ct.ThrowIfCancellationRequested();
           
            var node = JsonNode.Parse(envelope.Payload?.ToString() ?? "{}");
            var dp = node?["DecisionPoint"]?.GetValue<string>();
            var gin = node?["GIN"]?.GetValue<int>();

            if (device == null || dp == null || gin == null) return;
            var key = new ResponseKey(dp, gin.Value);

            // 2. Locate the original PLC requester
            if (_pendingResponses.TryRemove(key, out var request))
            {
                var responsePayload = new DecisionResponsePayload(dp, gin.Value,
                    node!["Actions"]?.AsArray().Select(a => a?.ToString() ?? "").ToList() ?? new());
                var responseMsg = PlcMessageParser.FrameResponse(responsePayload,topic.DeviceName );
                
                Logger.LogConveyableEvent(device.Key.DeviceName,$"Response from {envelope.Destination} to {request.Client}: {responseMsg}", 
                    gin.ToString(), responsePayload.Actions, responsePayload.DecisionPoint);
                    
                var success = await request.MultiClientDevice.SendResponseAsync(responseMsg, request.Client); 
                
                
            }else
            { device.GetLogger().LogWarning("UNEXPECTED Message from the Message bus");}
        }
        catch (OperationCanceledException)
        {
            device?.GetLogger().Information("[{Dev}] PLC Response dispatch was cancelled.", device.Config.Name);
        }
        catch (Exception ex)
        {
            device?.GetLogger().Error(ex, "[{Dev}] Error processing Bus Response for GIN {Gin}", device.Config.Name, envelope.Gin);
        }
    }
}