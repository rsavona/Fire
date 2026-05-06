using DeviceSpace.Common.Configurations;
using Microsoft.Extensions.Hosting;

namespace DeviceSpace.Common.Contracts;

public interface IWorkflowFactory
{
    /// <summary>
    /// Creates a hosted service instance for the specified workflow configuration.
    /// </summary>
    /// <param name="config">The specific configuration for the workflow instance.</param>
    /// <returns>An IHostedService ready to run.</returns>
    IHostedService CreateWorkflow(WorkflowConfig config);
}