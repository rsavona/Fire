using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.DependencyInjection;
using Fusion.Common;
using Fusion.Common.Configurations;
using Fusion.Common.Logging;
using Fusion.Common.Contracts;
using Microsoft.Extensions.Configuration;
using Serilog;


namespace Fusion.Core;

public class ElementManagerFactory : IElementManagerFactory
{

    private readonly IServiceProvider _serviceProvider;
    private readonly Dictionary<string, Type> _elementTypes;
    private readonly IConfiguration _configuration;

    /// <summary>
    /// 
    /// </summary>
    /// <param name="serviceProvider"></param>
    /// <param name="discoveredElementTypes"></param>
    /// <param name="configuration"></param>
    public ElementManagerFactory(IServiceProvider serviceProvider,
        [FromKeyedServices("ElementManagerTypes")]
        IEnumerable<Type> discoveredElementTypes,
        IConfiguration configuration)
    {
        _serviceProvider = serviceProvider;
        _elementTypes = new Dictionary<string, Type>();
        _configuration = configuration;

        // get all the IElementManagers
        var managerTypes = discoveredElementTypes
            .Where(t => typeof(IElementManager).IsAssignableFrom(t) && !t.IsInterface && !t.IsAbstract);

        // Now, loop through the *filtered* list
        foreach (var type in managerTypes)
        {
            // Use the simple class name as the key
            var typeName = type.Name;

            if (!string.IsNullOrEmpty(typeName))
            {
                _elementTypes[typeName.ToUpper()] = type;
            }
        }
    }

    /// <summary>
    /// 
    /// </summary>
    /// <param name="managerType"></param>
    /// <returns></returns>
    /// <exception cref="ArgumentException"></exception>
    /// <exception cref="InvalidOperationException"></exception>
    public IElementManager? CreateElementManager(string managerType)
    {
        if (!_elementTypes.TryGetValue(managerType.ToUpper(), out var elementType))
        {
            throw new ArgumentException($"Element type '{managerType}' is not registered or found.");
        }

        var config = ConfigurationLoader.GetSpaceConfig();

        List<IElementBlueprint> elementList = ConfigurationLoader.GetElementConfig(managerType);
        //  Use ActivatorUtilities to create an instance. It can inject services
        //    from the DI container and also pass your 'config' section as a parameter
        //    to the element's constructor.
        try
        {
            var manager = (IElementManager)ActivatorUtilities.CreateInstance(
                _serviceProvider,
                elementType, elementList, managerType);

            return manager;
        }
        catch (Exception ex)
        {
            Log.Logger.Debug(  ex.Message, ex.StackTrace); 
            Log.Logger.Error("ElementManagerFactory", "exception", managerType, elementType.Name, ex.Message);
        }

        return null;
    }
}
