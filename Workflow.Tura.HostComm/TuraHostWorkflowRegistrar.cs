using DeviceSpace.Common.Contracts;
using Microsoft.Extensions.DependencyInjection;

namespace Workflow.Tura.HostComm;

public class TuraHostWorkflowRegistrar : IDeviceRegistrar
{
    public void RegisterServices(IServiceCollection services)
    {
        // Add the workflow types to the collection of available workflows
        services.AddKeyedSingleton("WorkflowTypes", typeof(TuraHostWorkflow));
        services.AddKeyedSingleton("WorkflowTypes", typeof(TuraTesterWorkflow));
        services.AddKeyedSingleton("WorkflowTypes", typeof(TuraHostOutputWorkflow));
    }
}
