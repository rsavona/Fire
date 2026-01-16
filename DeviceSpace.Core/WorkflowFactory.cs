using System;
using System.Collections.Generic;
using System.Linq;
using DeviceSpace.Common;
using DeviceSpace.Common.Configurations;
using DeviceSpace.Common.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace DeviceSpace.Core;

public class WorkflowFactory : IWorkflowFactory
{
    private readonly IServiceProvider _serviceProvider;
    private readonly IEnumerable<Type> _loadedWorkflowTypes;

    // We inject the list of Types that we discovered during the 'AddCoreServices' phase.
    // We use [FromKeyedServices] or just a specific registered IEnumerable if possible, 
    // but simpler is to register a specific wrapper or just IEnumerable<Type> if unique.
    public WorkflowFactory(IServiceProvider serviceProvider, [FromKeyedServices("WorkflowTypes")] IEnumerable<Type> loadedWorkflowTypes)
    {
        _serviceProvider = serviceProvider;
        _loadedWorkflowTypes = loadedWorkflowTypes;
    }

    public IHostedService CreateWorkflow(WorkflowConfig config)
    {
        if (string.IsNullOrEmpty(config.Type))
        {
            throw new ArgumentException($"Workflow configuration '{config.Name}' is missing the 'Type' property.");
        }

        // Find the matching type in our loaded list
        // We match case-insensitive on the Class Name (e.g., "WorkflowPrintAndApplyFrc")
        var workflowType = _loadedWorkflowTypes
            .FirstOrDefault(t => t.Name.Equals(config.Type, StringComparison.OrdinalIgnoreCase) 
                                 && typeof(BackgroundService).IsAssignableFrom(t)
                                 && !t.IsAbstract);

        if (workflowType == null)
        {
            
            Console.WriteLine($"Could not find a workflow implementation for type '{config.Type}'.");
            throw new InvalidOperationException(
                $"Could not find a workflow implementation for type '{config.Type}'. " +
                $"Ensure the DLL is loaded and the class inherits from WorkflowBase.");
        }

    
        try
        {
            // ActivatorUtilities.CreateInstance is powerful:
            // - It pulls dependencies (IMessageBus) from _serviceProvider.
            // - It pushes explicit arguments (config) into the constructor where types match.
            object instance = ActivatorUtilities.CreateInstance(_serviceProvider, workflowType, config);

            if (instance is IHostedService service)
            {
                return service;
            }

            throw new InvalidCastException($"Type '{workflowType.Name}' does not implement IHostedService.");
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"Failed to create workflow '{config.Name}' of type '{config.Type}': {ex.Message}", ex);
        }
    }
}