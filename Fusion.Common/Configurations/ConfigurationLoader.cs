using Fusion.Common.Contracts;
using Microsoft.Extensions.Configuration;

namespace Fusion.Common.Configurations;

public static class ConfigurationLoader
{
    private static IConfiguration? _configuration;
    private static string? _loadedFilePath;
    private static bool _initCalled;

    public static event Action? OnConfigurationChanged;

    public static IConfiguration? InitConfig(string[]? args)
    {
        if (!_initCalled)
        {
            _initCalled = true;
            string? fileName = args is { Length: > 0 } ? args[0] : null;

            // If no file was provided via args, try to discover a Fusion file
            if (string.IsNullOrEmpty(fileName))
            {
                var directory = Directory.GetCurrentDirectory();
                var fusionFiles = Directory.GetFiles(directory, "*.fusion");

                if (fusionFiles.Length > 0)
                {
                    // Pick the most recently modified Fusion file
                    fileName = fusionFiles
                        .Select(f => new FileInfo(f))
                        .OrderByDescending(fi => fi.LastWriteTime)
                        .First()
                        .Name;
                }
                else
                {
                    // Fallback to the default .fusion name
                    fileName = ".fusion";
                }
            }

            _loadedFilePath = Path.IsPathRooted(fileName) ? fileName : Path.Combine(Directory.GetCurrentDirectory(), fileName);

            var builder = new ConfigurationBuilder()
                .SetBasePath(Directory.GetCurrentDirectory())
                .AddJsonFile(fileName, optional: false, reloadOnChange: true)
                .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true);
            _configuration = builder.Build();

            // Notify subscribers when configuration is reloaded
            Microsoft.Extensions.Primitives.ChangeToken.OnChange(
                () => _configuration.GetReloadToken(),
                () => OnConfigurationChanged?.Invoke());
        }

        return _configuration;
    }

    public static async Task UpdateDevicePropertyAsync(string deviceName, string propertyName, object value)
    {
        if (string.IsNullOrEmpty(_loadedFilePath) || !File.Exists(_loadedFilePath)) return;

        try
        {
            var json = await File.ReadAllTextAsync(_loadedFilePath);
            var root = System.Text.Json.Nodes.JsonNode.Parse(json);
            
            var cores = root?["AppSettings"]?["SystemBlueprintTemplate"]?["Cores"]?.AsArray();
            if (cores == null) return;

            foreach (var core in cores)
            {
                var elements = core?["Elements"]?.AsArray();
                if (elements == null) continue;

                var device = elements.FirstOrDefault(d => d?["CustomerName"]?.GetValue<string>() == deviceName);
                if (device != null)
                {
                    var properties = device["Properties"]?.AsObject();
                    if (properties == null) continue;

                    properties[propertyName] = System.Text.Json.Nodes.JsonValue.Create(value);

                    var options = new System.Text.Json.JsonSerializerOptions { WriteIndented = true };
                    await File.WriteAllTextAsync(_loadedFilePath, root.ToJsonString(options));
                    return;
                }
            }
        }
        catch (Exception ex)
        {
            Serilog.Log.Error(ex, "Failed to update device property {Prop} for {Dev} in {File}", propertyName, deviceName, _loadedFilePath);
        }
    }

    public static ISystemBlueprintTemplate? GetSpaceConfig()
    {
        var space = _configuration?.GetSection("AppSettings:SystemBlueprintTemplate").Get<SystemBlueprintTemplate>();
        
        if (space is { IsTestEnvironment: true })
        {
            InjectSimulationCore(space);
        }

        return space;
    }

    private static void InjectSimulationCore(SystemBlueprintTemplate space)
    {
        // Don't inject twice if already present
        if (space.Cores.Any(c => c.Name == "SIMULATION_CORE")) return;

        var simCore = new CoreConfig { Name = "SIMULATION_CORE" };

        // 1. Injected Verifier
        simCore.Elements.Add(new DeviceConfig
        {
            Name = "TEST_VERIFIER",
            Manager = "BlueprintVerifierManager",
            Enable = true,
            CoreName = "SIMULATION_CORE",
            Properties = new Dictionary<string, object>
            {
                { "LogPath", "logs/fusion-bugs.md" },
                { "TimeoutMs", 5000 }
            }
        });

        // 2. Injected Virtual PLC (Stimulator)
        // Find existing PLC configuration to mirror its endpoints if possible
        var firstPlc = space.Cores.SelectMany(c => c.Elements).FirstOrDefault(e => e.Manager.Contains("Plc"));
        var decisionPoints = firstPlc?.Properties.ContainsKey("DecisionPoints") == true 
            ? firstPlc.Properties["DecisionPoints"] 
            : "TEST_POINT:0";

        simCore.Elements.Add(new DeviceConfig
        {
            Name = "TEST_STIMULATOR",
            Manager = "VirtualPlcManager",
            Enable = true,
            CoreName = "SIMULATION_CORE",
            Properties = new Dictionary<string, object>
            {
                { "IPAddress", "127.0.0.1" },
                { "Port", 7999 },
                { "DecisionPoints", decisionPoints },
                { "TotalTotes", 999999 },
                { "InductionFreq", 2000 } // Stimulate every 2 seconds
            }
        });

        space.Cores.Add(simCore);
        Serilog.Log.Information("[Configuration] Dynamic SIMULATION_CORE injected into Fusion Blueprint.");
    }

    public static List<IDeviceConfig> GetAllDeviceConfig()
    {
        var deviceSpaceConfig = GetSpaceConfig();
        var allDevices = new List<IDeviceConfig>();
        
        if (deviceSpaceConfig?.Cores != null)
        {
            foreach (var core in deviceSpaceConfig.Cores)
            {
                if (core.Elements == null) continue;
                foreach (var dev in core.Elements)
                {
                    if (dev.Enable)
                    {
                        dev.CoreName = core.Name;
                        allDevices.Add(dev);
                    }
                }
            }
        }

        return allDevices;
    }


public static T? GetRequiredConfig<T>(Dictionary<string, object> properties, string key)
{
    if (!properties.TryGetValue(key, out object? value) || value == null)
    {
        throw new KeyNotFoundException($"Key '{key}' missing.");
    }

    // 1. If it's already the type we want
    if (value is T typedValue) return typedValue;

    // 2. Base Type Conversion (int, bool, string)
    try
    {
        Type t = typeof(T);
        Type u = Nullable.GetUnderlyingType(t) ?? t;
        
        // Convert.ChangeType works perfectly for string -> int, string -> bool, etc.
        return (T)Convert.ChangeType(value.ToString(), u);
    }
    catch (Exception ex)
    {
        throw new InvalidCastException($"Key '{key}' could not be converted to {typeof(T).Name}.", ex);
    }
}
    /// <summary>
    /// 
    /// </summary>
    /// <param name="properties"></param>
    /// <param name="key"></param>
    /// <param name="defaultValue"></param>
    /// <typeparam name="T"></typeparam>
    /// <returns></returns>
    public static T GetOptionalConfig<T>(Dictionary<string, object> properties, string key, T defaultValue)
    {
        if (properties == null || !properties.TryGetValue(key, out object? value) || value == null)
        {
            return defaultValue;
        }

        // 1. Try direct casting (Works for List<string>, custom classes, etc.)
        if (value is T typedValue)
        {
            return typedValue;
        }

        // 2. Try conversion for base types (int, bool, double, etc.)
        try
        {
            Type targetType = typeof(T);

            // Handle Nullable types (e.g., int?)
            Type underlyingType = Nullable.GetUnderlyingType(targetType) ?? targetType;

            return (T)Convert.ChangeType(value, underlyingType);
        }
        catch (Exception)
        {
            // 3. Last resort: If it's a string and we want a complex type, 
            // you could add JSON deserialization here if needed.
            return defaultValue;
        }
    }

    /// <summary>
    /// Retrieves all enabled workflows from the configuration.
    /// </summary>
    public static List<WorkflowConfig> GetAllWorkflowConfig()
    {
        var deviceSpaceConfig = GetSpaceConfig();
        var activeWorkflows = new List<WorkflowConfig>();

        if (deviceSpaceConfig?.Cores != null)
        {
            foreach (var core in deviceSpaceConfig.Cores)
            {
                if (core.Forces == null) continue;
                foreach (var wf in core.Forces)
                {
                    if (wf.Enable && wf is WorkflowConfig wfc)
                    {
                        wfc.CoreName = core.Name;
                        activeWorkflows.Add(wfc);
                    }
                }
            }
        }

        return activeWorkflows;
    }

    /// <summary>
    /// Retrieves configuration for a specific type of Manager (e.g. "PlcManager").
    /// </summary>
    public static List<IDeviceConfig> GetDeviceConfig(string type)
    {
        var deviceSpaceConfig = GetSpaceConfig();
        var matchingDevices = new List<IDeviceConfig>();

        if (deviceSpaceConfig?.Cores != null)
        {
            foreach (var core in deviceSpaceConfig.Cores)
            {
                if (core.Elements == null) continue;
                foreach (var dev in core.Elements)
                {
                    if (string.Equals(dev.Manager, type, StringComparison.OrdinalIgnoreCase))
                    {
                        dev.CoreName = core.Name;
                        matchingDevices.Add(dev);
                    }
                }
            }
        }

        return matchingDevices;
    }
}