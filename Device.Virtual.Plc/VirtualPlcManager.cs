using Device.Virtual.Plc;
using DeviceSpace.Common;
using DeviceSpace.Common.BaseClasses;
using DeviceSpace.Common.Contracts;
using Microsoft.Extensions.Logging;

namespace Device.Virtual.Plc;

/// <summary>
/// Manages virtual PLC instances for simulation and testing.
/// Standardized for the FIIRE AM product suite.
/// </summary>
public class VirtualPlcManager : DeviceManagerBase<VirtualPlcDevice>
{
    public VirtualPlcManager(
        IMessageBus bus, 
        List<IDeviceConfig> configs, 
        ILogger<DeviceManagerBase<VirtualPlcDevice>> logger, 
            Func<IDeviceConfig, Serilog.ILogger, VirtualPlcDevice> deviceFactory)
        : base(bus, configs, logger, deviceFactory)
    {
    }

    /// <summary>
    /// Simulation-specific setup.
    /// The base class already handles MessageReceived wiring if VirtualPlcDevice implements IMessageProvider.
    /// </summary>
    protected override void RegisterDeviceHandlers(VirtualPlcDevice device)
    {
        Logger.LogDebug("[{Dev}] Virtual PLC Manager initialized. Simulation ready.", device.Config.Name);
    }

    /// <summary>
    /// INBOUND: Data from Virtual PLC -> Bus
    /// Handles simulation data packets (like Heartbeats or Scans) coming from the virtual driver.
    /// </summary>
    protected override void OnDeviceMessageReceivedAsync(object? sender, object messageEnv)
    {
        if (sender is not VirtualPlcDevice device || messageEnv is not string rawData) return;

        // Consistent Product Logging
       Logger.LogInformation("[{Dev}] V-PLC-IN >> {Data}", device.Config.Name, rawData.Trim());

        // Example: Wrap the raw simulation string into a Bus Envelope and publish
        var topic = new MessageBusTopic(device.Config.Name, "Sim", "Data");
        var envelope = new MessageEnvelope(topic, rawData);
        
        _ = MessageBus.PublishAsync(topic.ToString(), envelope);
    }

    /// <summary>
    /// OUTBOUND: Bus -> Virtual PLC
    /// Translates WCS decisions or commands into simulated PLC responses.
    /// </summary>
    protected override async Task HandleBusMessageAsync(
        VirtualPlcDevice device, 
        string routeSource, 
         string dest,
        MessageEnvelope envelope, 
        CancellationToken ct)
    {
        try
        {
            var payload = envelope.Payload?.ToString();
            if (string.IsNullOrEmpty(payload)) return;

            Logger.LogInformation("[{Dev}] V-PLC-OUT << Command from {Source}: {Payload}", 
                device.Config.Name, routeSource, payload);

            // Forward the command to the virtual hardware to simulate a PLC write
            await device.SendAsync(payload);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "[{Dev}] Failed to route message to Virtual PLC.", device.Config.Name);
        }
    }
}