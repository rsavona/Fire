using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using Device.Plc.Suite.Messages;
using Fusion.Common;
using Fusion.Common.BaseClasses;
using Blueprints;
using Fusion.Common.Contracts;
using Fusion.Common.Logging;
using Microsoft.Extensions.Logging;

namespace Device.Plc.Suite.Connector;

public class  PlcElementManager : ElementManagerBase<PlcServerElement>
{
    public record struct ResponseKey(string DecisionPoint, int Gin);

    public record struct PendingRequest(PlcServerElement MultiClientElement, string Client, object Context);

    private readonly ConcurrentDictionary<ResponseKey, PendingRequest> _pendingResponses = new();
    private readonly HashSet<string> _expectResponseTopics = new();

    public PlcElementManager(IMessageBus bus, List<IElementBlueprint> configs,
        IFireLogger<ElementManagerBase<PlcServerElement>> logger,
        Func<IElementBlueprint, IFireLogger, PlcServerElement> deviceFactory,
        string managerName)
        : base(bus, configs, logger, deviceFactory, managerName)
    {
    }

    /// <summary>
    ///   Send the DecisionRequestPayload and DecisionUpdatePayload to the MessageBus. 
    /// </summary>
    /// <param name="dev"></param>
    /// <param name="messEnv"></param>
    protected override async Task OnDeviceMessageToMessageBusAsync(object? dev, object messEnv)
    {
        if (dev is not PlcServerElement device || messEnv is not MessageEnvelope env) return;
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
            if (node == null || node is not JsonObject obj) return;

            // Helper to get property case-insensitively
            JsonNode? GetProp(JsonObject o, string key) => 
                o.FirstOrDefault(kvp => kvp.Key.Equals(key, StringComparison.OrdinalIgnoreCase)).Value;

            var dp = GetProp(obj, "DecisionPoint")?.GetValue<string>() 
                     ?? GetProp(obj, "decisionPoint")?.GetValue<string>();
            var ginNode = GetProp(obj, "GIN") ?? GetProp(obj, "gin");
            var gin = ginNode?.GetValue<int>();

            if (device == null || dp == null || gin == null) return;
            var key = new ResponseKey(dp, gin.Value);

            // 2. Locate the original PLC requester
            if (_pendingResponses.TryRemove(key, out var request))
            {
                var actionsNode = GetProp(obj, "Actions") ?? GetProp(obj, "actions");
                var decisionPointsNode = GetProp(obj, "DecisionPoints") ?? GetProp(obj, "decisionPoints");

                var responsePayload = new DecisionResponsePayload(dp, gin.Value,
                    actionsNode?.AsArray().Select(a => a?.ToString().Trim('"') ?? "").ToList() ?? 
                    decisionPointsNode?.AsArray().Select(a => a?.ToString().Trim('"') ?? "").ToList() ?? new());
                var responseMsg = PlcMessageParser.FrameResponse(responsePayload,topic.DeviceName );

                Logger.LogConveyableEvent(device.Key.DeviceName,$"Response from {envelope.Destination} to {request.Client}: {responseMsg}",
                    gin.ToString(), responsePayload.DecisionPoints, responsePayload.DecisionPoint);

                var success = await request.MultiClientElement.SendResponseAsync(responseMsg, request.Client);
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