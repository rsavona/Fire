using Fusion.Common.Contracts;
using Microsoft.Extensions.DependencyInjection;

namespace Workflow.Tura.HostComm;

public class HostWorkflowRegistrar : IElementRegistrar
{
    public void RegisterServices(IServiceCollection services)
    {
        // Add the workflow types to the collection of available workflows
        services.AddKeyedSingleton("WorkflowTypes", typeof(HostReaction));
        services.AddKeyedSingleton("WorkflowTypes", typeof(TuraTesterReaction));
        services.AddKeyedSingleton("WorkflowTypes", typeof(HostOutputReaction));
    }
}
