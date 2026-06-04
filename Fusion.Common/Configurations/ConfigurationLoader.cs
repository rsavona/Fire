using Fusion.Common.Blueprints;
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
                var fusionFiles = Directory.GetFiles(directory, "fbp_*.json");

                if (fusionFiles.Length > 0)
                {
                    // Pick the most recently modified Blueprint file
                    fileName = fusionFiles
                        .Select(f => new FileInfo(f))
                        .OrderByDescending(fi => fi.LastWriteTime)
                        .First()
                        .Name;
                }
                else
                {
                    // Fallback to the default name
                    fileName = "fbp_default.json";
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

    public static async Task UpdateElementPropertyAsync(string elementName, string propertyName, object value)
    {
        if (string.IsNullOrEmpty(_loadedFilePath) || !File.Exists(_loadedFilePath)) return;

        try
        {
            var json = await File.ReadAllTextAsync(_loadedFilePath);
            var root = System.Text.Json.Nodes.JsonNode.Parse(json);
            
            var cores = root?["AppSettings"]?["Fusion"]?["Cores"]?.AsArray();
            if (cores == null) return;

            foreach (var core in cores)
            {
                var elements = core?["Elements"]?.AsArray();
                if (elements == null) continue;

                var element = elements.FirstOrDefault(d => d?["Name"]?.GetValue<string>() == elementName);
                if (element != null)
                {
                    var properties = element["Properties"]?.AsObject();
                    if (properties == null) continue;

                    properties[propertyName] = System.Text.Json.Nodes.JsonValue.Create(value);

                    var options = new System.Text.Json.JsonSerializerOptions { WriteIndented = true };
                    await File.WriteAllTextAsync(_loadedFilePath, root?.ToJsonString(options));
                    return;
                }
            }
        }
        catch (Exception ex)
        {
            Serilog.Log.Error(ex, "Failed to update element property {Prop} for {Dev} in {File}", propertyName, elementName, _loadedFilePath);
        }
    }

    public static ISystemBlueprintTemplate? GetSpaceConfig()
    {
        var space = _configuration?.GetSection("AppSettings:Fusion").Get<SystemBlueprintTemplate>();
        
        return space;
    }

    public static List<IElementBlueprint> GetAllElementConfig()
    {
        var elementSpaceConfig = GetSpaceConfig();
        var allElementsMap = new Dictionary<string, IElementBlueprint>(StringComparer.OrdinalIgnoreCase);
        
        // 1. Load from Cores (Compound structure) - Priority
        if (elementSpaceConfig?.Cores != null)
        {
            foreach (var core in elementSpaceConfig.Cores)
            {
                foreach (var dev in core.Elements)
                {
                    if (dev.Enable)
                    {
                        dev.CoreName = core.Name;
                        allElementsMap[dev.Name] = dev;
                    }
                }
            }
        }

        // 2. Load from Legacy ElementList (Flat structure) - Only if not already present
        if (elementSpaceConfig?.ElementList != null)
        {
            foreach (var dev in elementSpaceConfig.ElementList)
            {
                if (dev.Enable && !allElementsMap.ContainsKey(dev.Name))
                {
                    dev.CoreName ??= "System";
                    allElementsMap[dev.Name] = dev;
                }
            }
        }

        return allElementsMap.Values.ToList();
    }


public static T GetRequiredConfig<T>(Dictionary<string, object> properties, string key)
{
    if (!properties.TryGetValue(key, out object? value))
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
        return (T)Convert.ChangeType(value.ToString(), u)!;
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
        if (!properties.TryGetValue(key, out object? value))
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
    /// Retrieves all enabled reactions from the configuration.
    /// </summary>
    public static List<ReactionBlueprint> GetAllReactionConfig()
    {
        var elementSpaceConfig = GetSpaceConfig();
        var activeReactionsMap = new Dictionary<string, ReactionBlueprint>(StringComparer.OrdinalIgnoreCase);

        // 1. Load from Cores
        if (elementSpaceConfig?.Cores != null)
        {
            foreach (var core in elementSpaceConfig.Cores)
            {
                foreach (var wf in core.Reactions)
                {
                    if (wf.Enable && wf is ReactionBlueprint wfc)
                    {
                        wfc.CoreName = core.Name;
                        activeReactionsMap[wfc.Name] = wfc;
                    }
                }
            }
        }

        // 2. Load from Legacy ReactionList
        if (elementSpaceConfig?.ReactionList != null)
        {
            foreach (var wf in elementSpaceConfig.ReactionList)
            {
                if (wf.Enable && wf is ReactionBlueprint wfc && !activeReactionsMap.ContainsKey(wfc.Name))
                {
                    wfc.CoreName ??= "System";
                    activeReactionsMap[wfc.Name] = wfc;
                }
            }
        }

        return activeReactionsMap.Values.ToList();
    }

    /// <summary>
    /// Retrieves configuration for a specific type of Manager (e.g. "PlcManager").
    /// </summary>
    public static List<IElementBlueprint> GetElementConfig(string type)
    {
        var elementSpaceConfig = GetSpaceConfig();
        var matchingElementsMap = new Dictionary<string, IElementBlueprint>(StringComparer.OrdinalIgnoreCase);

        // 1. Search in Cores
        if (elementSpaceConfig?.Cores != null)
        {
            foreach (var core in elementSpaceConfig.Cores)
            {
                foreach (var dev in core.Elements)
                {
                    if (string.Equals(dev.Manager, type, StringComparison.OrdinalIgnoreCase))
                    {
                        dev.CoreName = core.Name;
                        matchingElementsMap[dev.Name] = dev;
                    }
                }
            }
        }

        // 2. Search in Legacy ElementList
        if (elementSpaceConfig?.ElementList != null)
        {
            foreach (var dev in elementSpaceConfig.ElementList)
            {
                if (string.Equals(dev.Manager, type, StringComparison.OrdinalIgnoreCase) && !matchingElementsMap.ContainsKey(dev.Name))
                {
                    dev.CoreName ??= "System";
                    matchingElementsMap[dev.Name] = dev;
                }
            }
        }

        return matchingElementsMap.Values.ToList();
    }
}