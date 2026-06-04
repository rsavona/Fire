using Fusion.Common;
using Fusion.Common.BaseClasses;
using Fusion.Common.Contracts;
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
            var t = messageEnvelope.Payload?.GetType().Name;
            Logger.Debug("[{Reaction}] Received message from Message Bus {msg}", ReactionKey.ElementName,
                messageEnvelope.Payload);
            
            LabelRequestFrcMessage? labelRequest = messageEnvelope.Payload as LabelRequestFrcMessage;

            if (labelRequest == null && messageEnvelope.Payload is string mstr)
            {
                labelRequest = LabelRequestFrcMessage.FromJson(mstr);
            }

            if (labelRequest == null)
            {
                Logger.Warning("[{Reaction}] Failed to resolve LabelRequestFrcMessage from payload.", ReactionKey.ElementName);
                return null;
            }

            if (labelRequest.Barcodes.FirstOrDefault() == "SIM-0400")
            {
                Logger.Information("[{Reaction}] Simulating delay for {Barcode}", "SIM-0400");
                await Task.Delay(1500, ct);
            }

            var ld = TestDataGenerator.GenerateMockResponse(labelRequest);

            Logger.Debug("[{Reaction}] Generated response {msg}", ReactionKey.ElementName, ld);
            

            return ld; // MessageEnvelope(new MessageBusTopic(bond.Destination), response);
    }
    private async Task<object?>? HandleLabelVerify(MessageEnvelope messageEnvelope, CancellationToken ct)
    {
            var t = messageEnvelope.Payload?.GetType().Name;
            Logger.Debug("[{Reaction}] Received message from Message Bus {msg}", ReactionKey.ElementName,
                messageEnvelope.Payload);
          
            

            Logger.Debug("[{Reaction}] Generated response {msg}", ReactionKey.ElementName, messageEnvelope.Payload);
            

            return messageEnvelope.Payload; // MessageEnvelope(new MessageBusTopic(bond.Destination), response);
    }
}