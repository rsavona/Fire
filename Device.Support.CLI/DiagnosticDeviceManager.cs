using DeviceSpace.Common;
using DeviceSpace.Common.BaseClasses;
using DeviceSpace.Common.Contracts;
using Microsoft.Extensions.Logging;

namespace Device.Support.CLI;

public class DiagnosticDeviceManager 
{
    public DiagnosticDeviceManager(string mapExportPath, IMessageBus mb, List<IDeviceConfig> config, ILoggerFactory lf) 
        
    {
        if (!Directory.Exists(mapExportPath)) Directory.CreateDirectory(mapExportPath);
    }

    public void RefreshSystemVisuals()
    {
      
    }

    protected  void RegisterDeviceHandlers(DiagnosticDevice device) { }

    protected  void OnDeviceMessageReceived(object? sender, object messageEnv) { }

    protected  Task HandleBusMessageAsync(DiagnosticDevice device, string routeSource, string dest, MessageEnvelope envelope, CancellationToken ct)
    {
        return Task.CompletedTask;
    }
}