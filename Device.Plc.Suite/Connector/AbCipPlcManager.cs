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

public class AbCipPlcManager : DeviceManagerBase<AbCipPlcDevice>
{
    public AbCipPlcManager(IMessageBus bus, List<IDeviceConfig> configs,
        IFireLogger<AbCipPlcManager> logger,
        Func<IDeviceConfig, IFireLogger, AbCipPlcDevice> deviceFactory,
        string managerName)
        : base(bus, configs, logger, deviceFactory, managerName)
    {
    }

    protected override async Task HandleBusMessageAsync(MessageEnvelope envelope, CancellationToken ct)
    {
        var topic = envelope.Destination;
        if (!DeviceInstances.TryGetValue(topic.DeviceName, out var device)) return;

        try
        {
            ct.ThrowIfCancellationRequested();

            // Extract relevant data for logging
            var node = JsonNode.Parse(envelope.Payload?.ToString() ?? "{}");
            if (node == null || node is not JsonObject obj) return;

            string payload = envelope.Payload?.ToString() ?? "";
            await device.SendAsync(payload, ct);
            
            // Log the event using the structured logging helper
            Logger.LogConveyableEvent(device.Key.DeviceName, $"CIP Response written to PLC: {payload}", envelope.Gin.ToString(), new List<string>(), "");
            Logger.Information("[{Dev}] CIP Response written to PLC for GIN {Gin}", device.Config.Name, envelope.Gin);
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "[{Dev}] Failed to write CIP response to PLC", device.Config.Name);
        }
    }
}
