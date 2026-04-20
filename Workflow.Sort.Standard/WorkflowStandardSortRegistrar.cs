using DeviceSpace.Common.Configurations;
using DeviceSpace.Common.Contracts;
using Microsoft.Extensions.DependencyInjection;

namespace Workflow.Sort.Standard;


/// <summary>
/// Registrar for the Print and Apply simulation workflow.
/// </summary>
public class PrintAndApplyFrcSimulationRegistrar : IDeviceRegistrar // Or IWorkflowRegistrar if your framework separates them
{
    public void RegisterServices(IServiceCollection services)
    {
        // 1. Register the concrete workflow type as Transient
        // This allows the factory to create a new instance for each workflow configuration
        services.AddTransient<StandardSort>();
        // 2. Register the Factory Delegate
        // This is what the WorkflowManager will invoke when it needs to spin up a new instance.
        // ActivatorUtilities handles the "IMessageBus" injection from the container automatically.
        services.AddTransient<Func<WorkflowConfig, IFireLogger, StandardSort>>(provider => 
            (config, logger) => 
            {
                return ActivatorUtilities.CreateInstance<StandardSort>(provider, config, logger);
            });
    }
}