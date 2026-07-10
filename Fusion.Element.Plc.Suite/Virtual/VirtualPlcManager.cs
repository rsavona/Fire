using Fusion.Common;
using Fusion.Common.BaseClasses;
using Fusion.Common.Contracts;
using Microsoft.Extensions.Logging;

namespace Fusion.Element.Plc.Suite.Virtual;

/// <summary>
/// Manages virtual PLC instances for simulation and testing.
/// Standardized for the FIIRE AM product suite.
/// </summary>
public class VirtualPlcManager : ElementManagerBase<VirtualPlcElement>
{
    public VirtualPlcManager(
        IMessageBus bus,
        List<IElementBlueprint> configs,
        IFireLogger<ElementManagerBase<VirtualPlcElement>> logger,
        Func<IElementBlueprint, IFireLogger, VirtualPlcElement> elementFactory,
        string managerName)
        : base(bus, configs, logger, elementFactory, managerName)
    {
    }

    /// <summary>
    /// Simulation-specific setup.
    /// The base class already handles MessageReceived wiring if VirtualPlcElement implements IMessageProvider.
    /// </summary>
    protected override Task RegisterElementSourceBonds(IElement element)
    {
        Logger.LogDebug("[{Dev}] Virtual PLC Manager initialized. Simulation ready.", element.Config.Name);
        
        // Listen for console commands to trigger manual actions
        MessageBus.SubscribeAsync(MessageBusTopic.ConsoleCommand.ToString(), async (envelope, ct) =>
        {
            if (envelope.Payload.ToString() == "RELEASE_TOTE" && element is VirtualPlcElement vElement)
            {
                vElement.TriggerManualRelease();
            }
        });

        return Task.CompletedTask;
    }

    /// <summary>
    /// INBOUND: Data from Virtual PLC -> Bus
    /// Handles simulation data packets (like Heartbeats or Scans) coming from the virtual driver.
    /// </summary>
    protected override Task OnElementMessageToMessageBusAsync(object? sender, object messageEnv)
    {
        if (sender is not VirtualPlcElement element || messageEnv is not string rawData) return Task.CompletedTask;
        var targetLogger = Logger.WithContext("ElementName", element.Key.ElementName);
        // Consistent Product Logging
        targetLogger.Information("[{Dev}] V-PLC-IN >> {Data}", element.Config.Name, rawData.Trim());

        // Example: Wrap the raw simulation string into a Bus Envelope and publish
        var topic = new MessageBusTopic(element.Config.Name, "Sim", "Data");
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
        ElementInstances.TryGetValue(topic.ElementName, out var element);
        if (element?.Key.ElementName == null)
        {
            Logger.Error("No Element name in HandleBusMessage async");
            return;
        }

        var targetLogger = Logger.WithContext("ElementName", element.Key.ElementName);

        try
        {
            var payload = envelope.GetPayloadText();
            if (string.IsNullOrEmpty(payload)) return;

            targetLogger.Information("[{Dev}] V-PLC-OUT << Command: {Payload}",
                element.Config.Name, payload);

            // Forward the command to the virtual hardware to simulate a PLC write
            await element.SendAsync(payload, ct);
        }
        catch (Exception ex)
        {
            targetLogger.LogError(ex, "[{Dev}] Failed to bond message to Virtual PLC.", element.Config.Name);
        }
    }

/*
public IEnumerable<ElementDiagnostic> GetDiagnosticsCommands()
{
    foreach (var element in ManagedElements)
    {
        yield return new ElementDiagnostic
        {
            ElementName = element.Config.CustomerName,
            ManagerType = nameof(VirtualPlcManager),
            Status = element.Machine.State.ToString(),
            IsHealthy = element.Machine.State == State.Connected,

            // DISCOVERY: Find all methods tagged with [DiagnosticCommand]
            AvailableCommands = element.GetType()
                .GetMethods()
                .Where(m => m.GetCustomAttributes(typeof(DiagnosticCommandAttribute), false).Any())
                .Select(m => {
                    var attr = (DiagnosticCommandAttribute)m.GetCustomAttribute(typeof(DiagnosticCommandAttribute))!;
                    return $"{m.CustomerName}|{attr.DisplayName}";
                }).ToList(),

            Metrics = new Dictionary<string, object>
            {
                { "Inbound", element.Tracker.InboundCount },
                { "Outbound", element.Tracker.OutboundCount }
            }
        };
    }
}
public IEnumerable<ElementDiagnostic> GetDiagnostics()
{
    // Iterate through all elements managed by this manager
    foreach (var element in ManagedElements)
    {
        yield return new ElementDiagnostic
        {
            ElementName = element.Config.CustomerName,
            ManagerType = nameof(VirtualPlcManager),

            // Machine State (e.g., Connected, Disconnected, Reconnecting)
            Status = element.Machine.State.ToString(),

            // Pull counters from the internal Tracker
            Metrics = new Dictionary<string, object>
            {
                { "LastHeartbeat", element.Tracker.LastHeartBeat },
                { "OutboundCount", element.Tracker.OutboundCount },
                { "InboundCount", element.Tracker.InboundCount },
                { "Uptime", element.Tracker.Uptime.ToString(@"hh\:mm\:ss") }
            },

            // Health indicator based on Connection State
            IsHealthy = element.Machine.State == State.Connected
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