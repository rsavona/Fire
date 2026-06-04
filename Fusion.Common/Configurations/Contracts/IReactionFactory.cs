using Blueprints;
using Microsoft.Extensions.Hosting;

namespace Fusion.Common.Contracts;

public interface IReactionFactory
{
    /// <summary>
    /// Creates a hosted service instance for the specified workflow configuration.
    /// </summary>
    /// <param name="config">The specific configuration for the workflow instance.</param>
    /// <returns>An IHostedService ready to run.</returns>
    IHostedService CreateWorkflow(WorkflowConfig config);
}