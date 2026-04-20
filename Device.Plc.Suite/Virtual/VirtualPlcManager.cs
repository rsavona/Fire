using DeviceSpace.Common;
using DeviceSpace.Common.BaseClasses;
using DeviceSpace.Common.Contracts;
using Microsoft.Extensions.Logging;

namespace Device.Plc.Suite.Virtual;

/// <summary>
/// Manages virtual PLC instances for simulation and testing.
/// Standardized for the FIIRE AM product suite.
/// </summary>
public class VirtualPlcManager : DeviceManagerBase<VirtualPlcDevice>
{
    public VirtualPlcManager(
        IMessageBus bus,
        List<IDeviceConfig> configs,
        IFireLogger<DeviceManagerBase<VirtualPlcDevice>> logger,
        Func<IDeviceConfig, IFireLogger, VirtualPlcDevice> deviceFactory)
        : base(bus, configs, logger, deviceFactory)
    {
    }

    /// <summary>
    /// Simulation-specific setup.
    /// The base class already handles MessageReceived wiring if VirtualPlcDevice implements IMessageProvider.
    /// </summary>
    protected override Task RegisterDeviceSourceRoutes(IDevice device)
    {
        Logger.LogDebug("[{Dev}] Virtual PLC Manager initialized. Simulation ready.", device.Config.Name);
        return Task.CompletedTask;
    }

    /// <summary>
    /// INBOUND: Data from Virtual PLC -> Bus
    /// Handles simulation data packets (like Heartbeats or Scans) coming from the virtual driver.
    /// </summary>
    protected override Task OnDeviceMessageToMessageBusAsync(object? sender, object messageEnv)
    {
        if (sender is not VirtualPlcDevice device || messageEnv is not string rawData) return Task.CompletedTask;
        var targetLogger = Logger.WithContext("DeviceName", device.Key.DeviceName);
        // Consistent Product Logging
        targetLogger.Information("[{Dev}] V-PLC-IN >> {Data}", device.Config.Name, rawData.Trim());

        // Example: Wrap the raw simulation string into a Bus Envelope and publish
        var topic = new MessageBusTopic(device.Config.Name, "Sim", "Data");
        var envelope = new MessageEnvelope(topic, rawData);

        _ = MessageBus.PublishAsync(topic.ToString(), envelope);
        return Task.CompletedTask;
    }

    /// <summary>
    /// OUTBOUND: Bus -> Virtual PLC
    /// Translates WCS decisions or commands into simulated PLC responses.
    /// </summary>
    protected override async Task HandleBusMessageAsync(
        MessageEnvelope envelope,
        CancellationToken ct)
    {
        var topic = envelope.Destination;
        DeviceInstances.TryGetValue(topic.DeviceName, out var device);
        if (device?.Key.DeviceName == null)
        {
            Logger.Error("No Device name in HandleBusMessage async");
            return;
        }

        var targetLogger = Logger.WithContext("DeviceName", device.Key.DeviceName);

        try
        {
            var payload = envelope.Payload?.ToString();
            if (string.IsNullOrEmpty(payload)) return;

            targetLogger.Information("[{Dev}] V-PLC-OUT << Command: {Payload}",
                device.Config.Name, payload);


            if (device is { } dev)
            {
                // Forward the command to the virtual hardware to simulate a PLC write
                await dev.SendAsync(payload, ct);
            }
            else
            {
                targetLogger.Error("[{Dev}] Could not send HandleBusMessageAsync", device.Config.Name);
            }
        }
        catch (Exception ex)
        {
            targetLogger.LogError(ex, "[{Dev}] Failed to route message to Virtual PLC.", device.Config.Name);
        }
    }

/*
public IEnumerable<DeviceDiagnostic> GetDiagnosticsCommands()
{
    foreach (var device in ManagedDevices)
    {
        yield return new DeviceDiagnostic
        {
            DeviceName = device.Config.Name,
            ManagerType = nameof(VirtualPlcManager),
            Status = device.Machine.State.ToString(),
            IsHealthy = device.Machine.State == State.Connected,

            // DISCOVERY: Find all methods tagged with [DiagnosticCommand]
            AvailableCommands = device.GetType()
                .GetMethods()
                .Where(m => m.GetCustomAttributes(typeof(DiagnosticCommandAttribute), false).Any())
                .Select(m => {
                    var attr = (DiagnosticCommandAttribute)m.GetCustomAttribute(typeof(DiagnosticCommandAttribute))!;
                    return $"{m.Name}|{attr.DisplayName}";
                }).ToList(),

            Metrics = new Dictionary<string, object>
            {
                { "Inbound", device.Tracker.InboundCount },
                { "Outbound", device.Tracker.OutboundCount }
            }
        };
    }
}
public IEnumerable<DeviceDiagnostic> GetDiagnostics()
{
    // Iterate through all devices managed by this manager
    foreach (var device in ManagedDevices)
    {
        yield return new DeviceDiagnostic
        {
            DeviceName = device.Config.Name,
            ManagerType = nameof(VirtualPlcManager),

            // Machine State (e.g., Connected, Disconnected, Reconnecting)
            Status = device.Machine.State.ToString(),

            // Pull counters from the internal Tracker
            Metrics = new Dictionary<string, object>
            {
                { "LastHeartbeat", device.Tracker.LastHeartBeat },
                { "OutboundCount", device.Tracker.OutboundCount },
                { "InboundCount", device.Tracker.InboundCount },
                { "Uptime", device.Tracker.Uptime.ToString(@"hh\:mm\:ss") }
            },

            // Health indicator based on Connection State
            IsHealthy = device.Machine.State == State.Connected
        };
    }
} */
    public IEnumerable<DiagCommand> GetAvailableCommands()
    {
        throw new NotImplementedException();
    }

    public Task<DiagResult> ExecuteCommandAsync(string commandName, Dictionary<string, string> parameters)
    {
        throw new NotImplementedException();
    }
}