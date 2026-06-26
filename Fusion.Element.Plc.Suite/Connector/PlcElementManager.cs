using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using Fusion.Common.Attributes;
using Fusion.Element.Plc.Suite.Messages;
using Fusion.Element.Plc.Suite.Virtual;
using Fusion.Common;
using Fusion.Common.BaseClasses;
using Fusion.Common.Configurations;
using Fusion.Common.Contracts;
using Fusion.Common.Logging;
using Microsoft.Extensions.Logging;

namespace Fusion.Element.Plc.Suite.Connector;

[TestCounterpart(typeof(VirtualPlcManager))]
public class  PlcElementManager : ElementManagerBase<PlcServerElement>
{
    private static readonly TimeSpan PendingResponseTtl = TimeSpan.FromMinutes(2);
    private static readonly HashSet<string> ResponsePublishingHandlers = new(StringComparer.OrdinalIgnoreCase)
    {
        "HandleBarcodeToPrinterAsync",
        "HandleContentToPrinterAsync"
    };

    public record struct ResponseKey(string DecisionPoint, int Gin);

    public record struct PendingRequest(
        PlcServerElement MultiClientElement,
        string Client,
        object Context,
        DateTime CreatedUtc);

    private readonly ConcurrentDictionary<ResponseKey, PendingRequest> _pendingResponses = new();
    private readonly ConcurrentDictionary<string, byte> _expectResponseTopics = new(StringComparer.OrdinalIgnoreCase);

    public PlcElementManager(IMessageBus bus, List<IElementBlueprint> configs,
        IFireLogger<ElementManagerBase<PlcServerElement>> logger,
        Func<IElementBlueprint, IFireLogger, PlcServerElement> elementFactory,
        string managerName)
        : base(bus, configs, logger, elementFactory, managerName)
    {
    }

    protected override void PrepareForBondDestinations(IElement element)
    {
        var requestPrefix = $"{element.Config.Name}.DReqM.";
        foreach (var topic in _expectResponseTopics.Keys.Where(topic =>
                     topic.StartsWith(requestPrefix, StringComparison.OrdinalIgnoreCase)))
        {
            _expectResponseTopics.TryRemove(topic, out _);
        }

        var responseTopics = ConfigurationLoader.GetAllReactionConfig()
            .Where(reaction => reaction.Enable)
            .SelectMany(reaction => reaction.Bonds)
            .Where(bond => BondProducesDecisionResponse(element.Config.Name, bond.Source, bond.Destination, bond.Handler))
            .Select(bond => bond.Source)
            .Distinct(StringComparer.OrdinalIgnoreCase);

        foreach (var topic in responseTopics)
        {
            _expectResponseTopics.TryAdd(topic, 0);
        }
    }

    /// <summary>
    ///   Send the DecisionRequestPayload and DecisionUpdatePayload to the MessageBus. 
    /// </summary>
    /// <param name="dev"></param>
    /// <param name="messEnv"></param>
    protected override async Task OnElementMessageToMessageBusAsync(object? dev, object messEnv)
    {
        if (dev is not PlcServerElement element || messEnv is not MessageEnvelope env) return;
        var targetLogger = Logger.WithContext("ElementName", element.Key.ElementName);
        if (env.Payload is DecisionRequestPayload req)
        {
            RemoveStalePendingResponses(DateTime.UtcNow);

            MessageBusTopic messageBusTopic = new MessageBusTopic(element.Config.Name, "DReqM", req.DecisionPoint);
            
            Logger.LogConveyableEvent( element.Key.ElementName, $"Request to {messageBusTopic}", req.Gin.ToString(), req.Barcodes, req.DecisionPoint);

            List<string> subList = MessageBus.GetSubscriptionList(messageBusTopic);
            var logMessage = $"[{element.Config.Name}]";
            if (subList.Count > 0) logMessage += $" Subscribed to {subList.Count} topics.";
            targetLogger.Verbose(logMessage);
            if (_expectResponseTopics.ContainsKey(messageBusTopic.ToString()))
            {
                _pendingResponses[new ResponseKey(req.DecisionPoint, req.Gin)] =
                    new PendingRequest(element, env.Client, req, DateTime.UtcNow);
            }

            var json = JsonSerializer.Serialize(env.Payload);
            
            await MessageBus.PublishAsync(messageBusTopic.ToString(),
                new MessageEnvelope(messageBusTopic, json, env.Gin, env.Client));
        }
        else if (env.Payload is DecisionUpdatePayload upd)
        {
            MessageBusTopic messageBusTopic = new MessageBusTopic(element.Config.Name, "Update", upd.DecisionPoint);
            
            Logger.LogConveyableEvent(element.Key.ElementName, upd.ToString(),  upd.Gin.ToString(),upd.Barcodes, upd.DecisionPoint);

            targetLogger.Information("[{Dev}] PLC-UPD >> GIN: {Gin} at {DP}", element.Config.Name, upd.Gin,
                upd.DecisionPoint);
            await MessageBus.PublishAsync(messageBusTopic.ToString(), new MessageEnvelope(messageBusTopic, env.Payload));
        }
        else if (env.Payload is TestMessagePayload test)
        {
            var messageType = env.Destination.MessageType;
            var messageBusTopic = new MessageBusTopic(element.Config.Name, messageType, test.TestName);

            targetLogger.Information("[{Dev}] PLC-TEST >> {Event}: {TestName}",
                element.Config.Name, test.Event, test.TestName);

            await MessageBus.PublishAsync(
                messageBusTopic.ToString(),
                new MessageEnvelope(messageBusTopic, env.Payload, env.Gin, env.Client));
        }
    }

    private static bool BondProducesDecisionResponse(
        string elementName,
        string source,
        string destination,
        string handler)
    {
        var requestPrefix = $"{elementName}.DReqM.";
        if (!source.StartsWith(requestPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var responsePrefix = $"{elementName}.DRespM.";
        return destination.StartsWith(responsePrefix, StringComparison.OrdinalIgnoreCase) ||
               ResponsePublishingHandlers.Contains(handler);
    }

    private void RemoveStalePendingResponses(DateTime nowUtc)
    {
        foreach (var item in _pendingResponses)
        {
            if (nowUtc - item.Value.CreatedUtc > PendingResponseTtl)
            {
                _pendingResponses.TryRemove(item.Key, out _);
            }
        }
    }

    /// <summary>
    /// Get the response from the message bus and send it back to the PLC.
    /// This is registerd as a handler for the message bus by the ElementManager Base
    /// </summary>
    /// <param name="multiClientElement"></param>
    /// <param name="bondSource"></param>
    /// <param name="envelope"></param>
    /// <param name="ct"></param>
    protected override async Task HandleBusMessageAsync( MessageEnvelope envelope, CancellationToken ct)
    {
        var topic = envelope.Destination; 
        ElementInstances.TryGetValue(topic.ElementName, out var element);
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

            if (element == null || dp == null || gin == null) return;
            var key = new ResponseKey(dp, gin.Value);

            // 2. Locate the original PLC requester
            if (_pendingResponses.TryRemove(key, out var request))
            {
                var actionsNode = GetProp(obj, "Actions") ?? GetProp(obj, "actions");
                var decisionPointsNode = GetProp(obj, "DecisionPoints") ?? GetProp(obj, "decisionPoints");

                var responsePayload = new DecisionResponsePayload(dp, gin.Value,
                    actionsNode?.AsArray().Select(a => a?.ToString().Trim('"') ?? "").ToList() ?? 
                    decisionPointsNode?.AsArray().Select(a => a?.ToString().Trim('"') ?? "").ToList() ?? new());
                var responseMsg = PlcMessageParser.FrameResponse(responsePayload,topic.ElementName );

                Logger.LogConveyableEvent(element.Key.ElementName,$"Response from {envelope.Destination} to {request.Client}: {responseMsg}",
                    gin.ToString(), responsePayload.DecisionPoints, responsePayload.DecisionPoint);

                var success = await request.MultiClientElement.SendResponseAsync(responseMsg, request.Client);
            }else
            { element.GetLogger().LogWarning("UNEXPECTED Message from the Message bus");}
        }
        catch (OperationCanceledException)
        {
            element?.GetLogger().Information("[{Dev}] PLC Response dispatch was cancelled.", element.Config.Name);
        }
        catch (Exception ex)
        {
            element?.GetLogger().Error(ex, "[{Dev}] Error processing Bus Response for GIN {Gin}", element.Config.Name, envelope.Gin);
        }
    }
}
