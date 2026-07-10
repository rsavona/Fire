using Fusion.Common;
using Fusion.Common.BaseClasses;
using Fusion.Common.Contracts;
using Microsoft.Extensions.Logging;

namespace Fusion.Element.Mqtt;

public class MqttManager : ElementManagerBase<MqttElement>
{
    public MqttManager(IMessageBus bus, List<IElementBlueprint> configs,
        IFireLogger<MqttManager> logger,
        Func<IElementBlueprint, IFireLogger, MqttElement> elementFactory,
        string managerName)
        : base(bus, configs, logger, elementFactory, managerName)
    {
    }

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
            string payload = envelope.GetPayloadText();
            await element.SendAsync(payload, ct);
            Logger.Information("[{Dev}] Message published to MQTT: {Payload}", element.Config.Name, payload);
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "[{Dev}] Failed to publish message to MQTT", element.Config.Name);
        }
    }
}
