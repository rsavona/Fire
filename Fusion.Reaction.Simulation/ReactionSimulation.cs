using System.Text.Json.Nodes;
using Fusion.Common;
using Fusion.Common.BaseClasses;
using Fusion.Common.Contracts;
using Fusion.Element.Plc.Suite.Messages;
using Serilog;

namespace Fusion.Reaction.Simulation;

public class ReactionSimulation : ReactionBase
{
    public ReactionSimulation(IMessageBus bus, IReactionBlueprint config, ILogger logger)
        : base(bus, config, logger)
    {
        Logger.Information("[{Reaction}] Simulation Physics & Logic Initialized.", ReactionKey.ElementName);
    }

    /// <summary>
    /// Transform Label request into Label Data 
    /// </summary>
    private async Task<object?>? HandleLabelRequest(MessageEnvelope messageEnvelope, CancellationToken ct)
    {
            Logger.Debug("[{Reaction}] Received {PayloadType} from Message Bus {Payload}",
                ReactionKey.ElementName,
                messageEnvelope.Payload?.GetType().FullName ?? "null",
                messageEnvelope.Payload);
            
            LabelRequestFrcMessage? labelRequest = ResolveLabelRequest(messageEnvelope.Payload);

            if (labelRequest == null)
            {
                Logger.Warning("[{Reaction}] Failed to resolve LabelRequestFrcMessage from payload.", ReactionKey.ElementName);
                return null;
            }

            var ld = TestDataGenerator.GenerateMockResponse(labelRequest);

            if (ld == null)
            {
                Logger.Warning("[{Reaction}] Suppressing LabelData response for barcode {Barcode}.",
                    ReactionKey.ElementName, labelRequest.Barcodes.FirstOrDefault() ?? string.Empty);
                return null;
            }

            Logger.Debug("[{Reaction}] Generated response {msg}", ReactionKey.ElementName, ld);
            

            return await Task.FromResult<object?>(ld); // MessageEnvelope(new MessageBusTopic(bond.Destination), response);
    }

    private static LabelRequestFrcMessage? ResolveLabelRequest(object? payload)
    {
        return payload switch
        {
            null => null,
            LabelRequestFrcMessage request => request,
            string json => TryDeserializeLabelRequest(json),
            _ => TryDeserializeLabelRequest(payload.ToJson())
        };
    }

    private static LabelRequestFrcMessage? TryDeserializeLabelRequest(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            return LabelRequestFrcMessage.FromJson(json);
        }
        catch
        {
            return null;
        }
    }

    private async Task<object?>? HandleLabelVerify(MessageEnvelope messageEnvelope, CancellationToken ct)
    {
            var t = messageEnvelope.Payload?.GetType().Name;
            Logger.Debug("[{Reaction}] Received message from Message Bus {msg}", ReactionKey.ElementName,
                messageEnvelope.Payload);

            Logger.Debug("[{Reaction}] Generated response {msg}", ReactionKey.ElementName, messageEnvelope.Payload);
            
            return messageEnvelope.Payload; // MessageEnvelope(new MessageBusTopic(bond.Destination), response);
    }
    private async Task<object?>? HandleRoberQueue(MessageEnvelope messageEnvelope, CancellationToken ct)
    {
        Logger.Debug("[{Reaction}] HandleRoberQueue received: {Payload}", ReactionKey.ElementName, messageEnvelope.Payload);

        if (messageEnvelope.Payload is DecisionRequestPayload drp)
        {
            return drp.Metadata;
        }

        if (messageEnvelope.Payload is LabelDataFrcMessage ldfm)
        {
                // If it's LabelData, we might not have metadata directly,
            // but the user mentioned "mdata goes back into the configured queue".
            // If LabelData is expected here, we return it as is or handle accordingly.
            return ldfm;
        }

        return messageEnvelope.Payload;
    }
}
