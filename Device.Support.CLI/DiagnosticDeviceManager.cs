using DeviceSpace.Common;
using DeviceSpace.Common.BaseClasses;
using DeviceSpace.Common.Contracts;

namespace Device.Support.CLI;

/// <summary>
/// Manages the Diagnostic telnet server.
/// </summary>
public class DiagnosticDeviceManager : DeviceManagerBase<DiagnosticDevice>, IDeviceManager
{
    public DiagnosticDeviceManager(IMessageBus bus, List<IDeviceConfig> configs,
        IFireLogger<DeviceManagerBase<DiagnosticDevice>> logger,
        Func<IDeviceConfig, IFireLogger, DiagnosticDevice> deviceFactory)
        : base(bus, configs, logger, deviceFactory)
    {
    }

    protected override async Task OnDeviceMessageToMessageBusAsync(object? dev, object messEnv)
    {
        if (dev is not DiagnosticDevice device || messEnv is not MessageEnvelope env) return;
        await MessageBus.PublishAsync(env.Destination.ToString(), env);
    }

    protected override async Task HandleBusMessageAsync(MessageEnvelope envelope, CancellationToken ct)
    {
        // Handle custom diagnostic commands from the bus if needed
        await Task.CompletedTask;
    }
}
