using DeviceSpace.Common.Contracts;
using Microsoft.Extensions.DependencyInjection;

namespace Device.Support.CLI;

public class DiagnosticDeviceRegistrar : IDeviceRegistrar
{
    public void RegisterServices(IServiceCollection services)
    {
        services.AddTransient<DiagnosticDevice>();
        services.AddTransient<BlueprintVerifierDevice>();

        services.AddTransient<Func<IDeviceConfig, IFireLogger, DiagnosticDevice>>(provider => 
            (config, logger) => ActivatorUtilities.CreateInstance<DiagnosticDevice>(provider, config, logger));

        services.AddTransient<Func<IDeviceConfig, IFireLogger, BlueprintVerifierDevice>>(provider => 
            (config, logger) => ActivatorUtilities.CreateInstance<BlueprintVerifierDevice>(provider, config, logger));
    }
}
