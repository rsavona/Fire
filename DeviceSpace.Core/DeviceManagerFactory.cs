using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.DependencyInjection;
using DeviceSpace.Common;
using DeviceSpace.Common.Configurations;
using DeviceSpace.Common.Contracts;
using DeviceSpace.Common.Logging;
using Microsoft.Extensions.Configuration;
using Serilog;


namespace DeviceSpace.Core;

public class DeviceManagerFactory : IDeviceManagerFactory
{

    private readonly IServiceProvider _serviceProvider;
    private readonly Dictionary<string, Type> _deviceTypes;
    private readonly IConfiguration _configuration;

    /// <summary>
    /// 
    /// </summary>
    /// <param name="serviceProvider"></param>
    /// <param name="discoveredDeviceTypes"></param>
    /// <param name="configuration"></param>
    public DeviceManagerFactory(IServiceProvider serviceProvider,
        [FromKeyedServices("DeviceManagerTypes")]
        IEnumerable<Type> discoveredDeviceTypes,
        IConfiguration configuration)
    {
        _serviceProvider = serviceProvider;
        _deviceTypes = new Dictionary<string, Type>();
        _configuration = configuration;

        // get all the IDeviceManagers
        var managerTypes = discoveredDeviceTypes
            .Where(t => typeof(IDeviceManager).IsAssignableFrom(t) && !t.IsInterface && !t.IsAbstract);

        // Now, loop through the *filtered* list
        foreach (var type in managerTypes)
        {
            // Use the simple class name as the key
            var typeName = type.Name;

            if (!string.IsNullOrEmpty(typeName))
            {
                _deviceTypes[typeName.ToUpper()] = type;
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
    public IDeviceManager? CreateDeviceManager(string managerType)
    {
        if (!_deviceTypes.TryGetValue(managerType.ToUpper(), out var deviceType))
        {
            throw new ArgumentException($"Device type '{managerType}' is not registered or found.");
        }

        var config = ConfigurationLoader.GetSpaceConfig();

        List<IDeviceConfig> deviceList = ConfigurationLoader.GetDeviceConfig(managerType);
        //  Use ActivatorUtilities to create an instance. It can inject services
        //    from the DI container and also pass your 'config' section as a parameter
        //    to the device's constructor.
        try
        {
            var manager = (IDeviceManager)ActivatorUtilities.CreateInstance(
                _serviceProvider,
                deviceType, deviceList);

            return manager;
        }
        catch (Exception ex)
        {
            Log.Logger.Debug(  ex.Message, ex.StackTrace); 
            Log.Logger.Error("DeviceManagerFactory", "exception", managerType, deviceType.Name, ex.Message);
        }

        return null;
    }
}
