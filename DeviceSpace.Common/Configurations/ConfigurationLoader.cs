using DeviceSpace.Common.Contracts;
using Microsoft.Extensions.Configuration;

namespace DeviceSpace.Common.Configurations;

public static class ConfigurationLoader
{
    private static IConfiguration? _configuration;
    private static bool _initCalled;

    public static IConfiguration? InitConfig(string[]? args)
    {
        if (! _initCalled)
        {
            _initCalled = true;
            var fileName = args is { Length: > 0 } ? args[0] : "DeviceSpace.json";

            var builder = new ConfigurationBuilder()
                .SetBasePath(Directory.GetCurrentDirectory())
                .AddJsonFile(fileName, optional: false, reloadOnChange: false)
                .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true);
            _configuration = builder.Build();
        }
        return _configuration;
    }

    public static IDeviceSpace? GetSpaceConfig()
    {
        return _configuration?.GetSection("AppSettings:DeviceSpace").Get<DeviceSpace>();
    }

    public static List<IDeviceConfig> GetAllDeviceConfig()
    {
        var deviceSpaceConfig = _configuration?.GetSection("AppSettings:DeviceSpace").Get<DeviceSpace>();
        var allDevices = new List<IDeviceConfig>();
        // Iterate the configuration file getting all devices
        bool? any = false;
        if (deviceSpaceConfig?.DeviceList != null)
        {
            foreach (var unused in deviceSpaceConfig.DeviceList)
            {
                any = true;
                break;
            }

            if (any != true) return allDevices;
            foreach (var dev in deviceSpaceConfig.DeviceList)
            {
                if (dev.Enable)
                {
                    allDevices.Add(dev);
                }
            }
        }

        return allDevices;
    }

    /// <summary>
    /// 
    /// </summary>
    /// <param name="properties"></param>
    /// <param name="key"></param>
    /// <typeparam name="T"></typeparam>
    /// <returns></returns>
    /// <exception cref="ArgumentException"></exception>
    public static T GetRequiredConfig<T>(Dictionary<string, object> properties, string key)
    {
        if (properties.TryGetValue(key, out object? value) && Convert.ChangeType(value, typeof(T)) is T typedValue)
        {
            return typedValue;
        }

        throw new ArgumentException($"Configuration is missing or has an invalid type for required key: '{key}'.");
    }

    /// <summary>
    /// 
    /// </summary>
    /// <param name="properties"></param>
    /// <param name="key"></param>
    /// <param name="defaultValue"></param>
    /// <typeparam name="T"></typeparam>
    /// <returns></returns>
    public static T GetOptionalConfig<T>(Dictionary<string, object> properties, string key, T defaultValue )
    {
        try
        {
            if (properties.TryGetValue(key, out object? value) &&
                Convert.ChangeType(value, typeof(T)) is T typedValue)
            {
                return typedValue;
            }
        }
        catch (Exception)
        {
            // Ignore conversion errors, just use default
        }

        return defaultValue;
    }
    
    /// <summary>
    /// Retrieves all enabled workflows from the configuration.
    /// </summary>
    public static List<WorkflowConfig> GetAllWorkflowConfig()
    {
        // Get the root object
        var deviceSpaceConfig = _configuration?.GetSection("AppSettings:DeviceSpace").Get<DeviceSpace>();
        
        var activeWorkflows = new List<WorkflowConfig>();

        
        if (deviceSpaceConfig?.WorkflowList.Any() == true)
        {
            foreach (var wf in deviceSpaceConfig.WorkflowList)
            {
                // Only return workflows that are explicitly enabled
                if (wf.Enable) 
                {
                    activeWorkflows.Add(wf);
                }
            }
        }

        return activeWorkflows;
    }/// <summary>
    /// Retrieves configuration for a specific type of Manager (e.g. "PlcManager").
    /// </summary>
    public static List<IDeviceConfig> GetDeviceConfig(string type)
    {
        // Get the current snapshot of the config
        var deviceSpaceConfig = _configuration?.GetSection("AppSettings:DeviceSpace").Get<DeviceSpace>();
        var matchingDevices = new List<IDeviceConfig>();

        if (deviceSpaceConfig?.DeviceList.Any() == true)
        {
            foreach (var dev in deviceSpaceConfig.DeviceList)
            {
                // Check if the Manager matches the requested type
                // We use OrdinalIgnoreCase to be safe (e.g. "plcmanager" vs "PlcManager")
                if (string.Equals(dev.Manager, type, StringComparison.OrdinalIgnoreCase))
                {
                    // Ensure Scope is set, consistent with GetAllDeviceConfig
                 
                    // Optional: You might want to check 'if (dev.Enable)' here too 
                    // if you only want enabled devices for this specific manager.
                    matchingDevices.Add(dev);
                }
            }
        }

        return matchingDevices;
    }
  
}

