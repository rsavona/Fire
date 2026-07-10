using Fusion.Common;
using Fusion.Common.BaseClasses;
using Fusion.Common.Contracts;

namespace Fusion.Element.Ai.Suite;

/// <summary>
/// Manages one or more AiElement instances and bridges them to the Fusion Message Bus.
/// </summary>
public class AiElementManager : ElementManagerBase<AiElement>
{
    public AiElementManager(IMessageBus bus, List<IElementBlueprint> configs,
        IFireLogger<ElementManagerBase<AiElement>> logger,
        Func<IElementBlueprint, IFireLogger, AiElement> elementFactory,
        string managerName)
        : base(bus, configs, logger, elementFactory, managerName)
    {
    }

    /// <summary>
    /// Forwards AI responses from the element directly to the message bus.
    /// </summary>
    protected override async Task OnElementMessageToMessageBusAsync(object? dev, object messEnv)
    {
        if (dev is not AiElement element || messEnv is not MessageEnvelope env) return;

        Logger.WithContext("ElementName", element.Config.Name)
              .Verbose("[{Dev}] Forwarding AI response to bus: {Topic}", element.Config.Name, env.Destination);

        await MessageBus.PublishAsync(env.Destination.ToString(), env);
    }

    /// <summary>
    /// Handles requests from the message bus and sends them to the AI element for processing.
    /// </summary>
    protected override async Task HandleBusMessageAsync(MessageEnvelope envelope, CancellationToken ct)
    {
        var topic = envelope.Destination;
        if (!ElementInstances.TryGetValue(topic.ElementName, out var element))
        {
            Logger.Warning("[{Dev}] Received bus message but element instance not found.", topic.ElementName);
            return;
        }

        try
        {
            ct.ThrowIfCancellationRequested();

            string prompt = envelope.GetPayloadText();
            if (string.IsNullOrWhiteSpace(prompt))
            {
                Logger.Warning("[{Dev}] Received AI request with empty prompt. Topic: {Topic}", topic.ElementName, envelope.Destination);
                return;
            }

            // Process the prompt via AI
            await element.ChatAsync(prompt, ct);
        }
        catch (Exception ex)
        {
            element.GetLogger().Error(ex, "[{Dev}] Error processing AI request", element.Config.Name);
        }
    }
}
