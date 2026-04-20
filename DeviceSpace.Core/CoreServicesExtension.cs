using System.Reflection;
using DeviceSpace.Common;
using DeviceSpace.Common.BaseClasses;
using DeviceSpace.Common.Configurations;
using DeviceSpace.Common.Contracts;
using DeviceSpace.Common.logging;
using DeviceSpace.Common.Logging;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using Serilog.Formatting.Compact;
using Serilog.Enrichers;

namespace DeviceSpace.Core;

public static class CoreServicesExtensions
{
    private static readonly LoggingLevelSwitch LevelSwitch = new LoggingLevelSwitch(LogEventLevel.Verbose);

    public static TBuilder AddCoreServices<TBuilder>(this TBuilder builder, string[]? args = null)
        where TBuilder : IHostApplicationBuilder
    {
        var baseFolderPath = AppContext.BaseDirectory;
        var isFire = false;
        
        if (args != null)
        {
            isFire = true;
          
            var appconfig = ConfigurationLoader.InitConfig(args);
            var config = appconfig?.GetSection("AppSettings:DeviceSpace").Get<Common.Configurations.DeviceSpace>();
            var appName = config?.Name is { Length: > 0 } ? config.Name : "DeviceSpace.json";

            if (config != null)
            {
                appName ??= "FortnaFire";
                SetupLogger(builder, appName);
                Console.Title = appName;
            }
            else
            {
                Log.Logger.Error("CORE", "ERROR", "CONFIG", "SYSTEM", "No Config File Found");
                throw new InvalidOperationException("No Config File Found");
            }

            // Fixed: Let Serilog handle the parameter injection
            Log.Logger.Information("SYSTEM", "STARTUP", "CORE", "FIRE", "=== {AppName} Starting ===", appName);

            builder.Services.AddSingleton(Log.Logger);
            builder.Services.AddSingleton(LevelSwitch);
            builder.Services.AddSingleton<IMessageBus, MessageBus>();
            builder.Services.AddSingleton<DeviceManagerFactory>();
            builder.Services.AddSingleton<WorkflowFactory>();
            builder.Services.AddHostedService<DeviceSpaceCore>();
        }
        
        LoadAvailableDeviceManagersFromDll(builder, baseFolderPath, isFire);
        LoadAvailableWorkflowsFromDll(builder, baseFolderPath, isFire);
        
        if (args != null)
        {
            LoadConfiguredDevices(builder);
            LoadConfiguredWorkflow(builder);
        }
       
        return builder;
    }

    private static IHostApplicationBuilder LoadConfiguredDevices(IHostApplicationBuilder builder)
    {
        try
        {
            var allDevices = ConfigurationLoader.GetAllDeviceConfig();

            if (!allDevices.Any())
            {
                Log.Logger.Error("HOSTED", "LOAD", "CONFIG", "NONE", "No device configurations were found in the JSON.");
                return builder;
            }

            var uniqueDevices = allDevices
                .GroupBy(device => device.Manager)
                .Select(group => group.First())
                .ToList();
             
            // Loop builds the DeviceManagerFactory parameters. DI will resolve constructors at runtime.
            foreach (var deviceConfig in uniqueDevices)
            {
                try
                {
                    string managerName = deviceConfig.Manager.ToString();
                    Log.Logger.Information("HOSTED", "CREATE", "MANAGER", managerName, "Registering Service Definition");

                    builder.Services.AddSingleton<IHostedService>(provider =>
                    {
                        try
                        {
                            var factory = provider.GetRequiredService<DeviceManagerFactory>();
                            var manager = factory.CreateDeviceManager(managerName);

                            if (manager == null)
                                throw new InvalidOperationException($"Factory returned null for manager: {managerName}");

                            return (IHostedService)manager;
                        }
                        catch (Exception ex)
                        {
                            Log.Logger.Fatal(ex, "HOSTED", "STARTUP", "FACTORY", managerName, "Failed to resolve device manager at runtime.");
                            throw; 
                        }
                    });
                }
                catch (Exception ex)
                {
                    Log.Logger.Error(ex, "HOSTED", "REGISTER", "DI", deviceConfig.Manager?.ToString() ?? "Unknown", "Failed to register manager in DI container.");
                }
            }
        }
        catch (Exception ex)
        {
            Log.Logger.Fatal(ex, "HOSTED", "LOAD", "CRITICAL", "GLOBAL", "Critical failure loading device configurations.");
            throw; 
        }
        return builder;
    }

   private static IHostApplicationBuilder LoadConfiguredWorkflow(IHostApplicationBuilder builder)
    {
        var allWorkflows = ConfigurationLoader.GetAllWorkflowConfig();
        
        foreach (var workflowConfig in allWorkflows)
        {
            var wfConfig = (WorkflowConfig)workflowConfig;
            if (!wfConfig.Enable)
            {
                Log.Logger.Warning("HOSTED", "SKIP", "WORKFLOW", wfConfig.Name, "Disabled in config");
                continue;
            }

            Log.Logger.Information("HOSTED", "CREATE", "WORKFLOW", wfConfig.Name, "Registering Workflow Service Definition");
            
            builder.Services.AddSingleton<IHostedService>(provider =>
            {
                var factory = provider.GetRequiredService<WorkflowFactory>();
                return factory.CreateWorkflow(wfConfig);
            });
        }

        return builder;
    }

    private static IHostApplicationBuilder LoadAvailableWorkflowsFromDll(IHostApplicationBuilder builder, string baseFolderPath, bool fire)
    {
        var workflowList = new List<Type>();
        
        if (Directory.Exists(baseFolderPath))
        {
            var workflowDlls = Directory.GetFiles(baseFolderPath, "Workflow.*.dll");
            
            // Fixed: Removed interpolation, added structured property
            Log.Logger.Information("DISCOVERY", "SCAN", "DLL", "WORKFLOW", "Found {AssemblyCount} assemblies", workflowDlls.Length);
            
            foreach (string dllPath in workflowDlls)
            {
                try
                {
                    Assembly assembly = Assembly.LoadFrom(dllPath);
                    var foundWorkflows = assembly.GetTypes()
                        .Where(t => t.BaseType == typeof(WorkflowBase) && !t.IsAbstract)
                        .ToList();

                    foreach (var wf in foundWorkflows)
                    {
                        Log.Logger.Information("DISCOVERY", "LOAD", "WORKFLOW", wf.Name, "Workflow registered");
                    }

                    workflowList.AddRange(foundWorkflows);
                    if (fire)
                    {
                        RegisterPlugins(builder, assembly);
                    }
                }
                catch (Exception ex)
                {
                    // Fixed: Include exception explicitly for stack traces
                    Log.Logger.Error(ex, "DISCOVERY", "FAULT", "DLL", "WORKFLOW", "Failed to load workflow assembly: {DllPath}", Path.GetFileName(dllPath));
                }
            }

            builder.Services.AddKeyedSingleton<IEnumerable<Type>>("WorkflowTypes", workflowList);
        }

        return builder;
    }

    private static IHostApplicationBuilder LoadAvailableDeviceManagersFromDll(IHostApplicationBuilder builder, string baseFolderPath, bool fire)
    {
        var managerList = new List<Type>();
        
        if (Directory.Exists(baseFolderPath))
        {
            var deviceDlls = Directory.GetFiles(baseFolderPath, "Device.*.dll");
            Log.Logger.Information("DISCOVERY", "SCAN", "DLL", "DEVICE", "Found {AssemblyCount} assemblies", deviceDlls.Length);

            foreach (string dllPath in deviceDlls)
            {
                try
                {
                    Assembly assembly = Assembly.LoadFrom(dllPath);
                    var version = assembly.GetName().Version;
                    var managers = assembly.GetTypes()
                        .Where(t => typeof(IDeviceManager).IsAssignableFrom(t) && !t.IsInterface && !t.IsAbstract)
                        .ToList();

                    foreach (var m in managers)
                    {
                        // Fixed: Removed interpolation, properly structured variables
                        Log.Logger.Information("DISCOVERY", "LOAD", "MANAGER", m.Name, "v{Version} loaded from {DllName}", version, Path.GetFileName(dllPath));
                    }

                    managerList.AddRange(managers);
                    if (fire)
                    {
                        RegisterPlugins(builder, assembly);
                    }
                }
                catch (Exception ex)
                {
                    Log.Logger.Error(ex, "DISCOVERY", "FAULT", "DLL", "DEVICE", "Failed to load device assembly: {DllPath}", Path.GetFileName(dllPath));
                }
            }

            builder.Services.AddKeyedSingleton<IEnumerable<Type>>("DeviceManagerTypes", managerList);
        }

        return builder;
    }

    private static IHostApplicationBuilder RegisterPlugins(IHostApplicationBuilder builder, Assembly assembly)
    {
        var registrars = assembly.GetTypes()
            .Where(t => typeof(IDeviceRegistrar).IsAssignableFrom(t) && !t.IsInterface && !t.IsAbstract);

        foreach (var type in registrars)
        {
            try
            {
                if (Activator.CreateInstance(type) is IDeviceRegistrar registrar)
                {
                    registrar.RegisterServices(builder.Services);
                }
            }
            catch (Exception ex)
            {
                Log.Logger.Error(ex, "PLUGINS", "ERROR", "REGISTRAR", type.Name, "Failed to execute registrar logic.");
            }
        }

        return builder;
    }

    /// <summary>
    /// Configures Serilog and adds the Audit Logger.
    /// </summary>
    private static void SetupLogger(this IHostApplicationBuilder builder, string configName)
    {
       ;
        builder.Services.AddSingleton<IFireLogger>(provider => 
        {
            var bus = provider.GetService<IMessageBus>();
            return new FireLogger(Log.Logger, bus);
        });
        builder.Services.AddTransient(typeof(IFireLogger<>), typeof(FireLogger<>));

        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.ControlledBy(LevelSwitch)
            .Enrich.FromLogContext()

            // --- PIPELINE 1: Audit ---
            .WriteTo.Logger(lc => lc
                .Filter.ByIncludingOnly(evt => evt.Properties.ContainsKey("AuditLog"))
                .WriteTo.Async(a => a.File(
                    new CompactJsonFormatter(),
                    $"../../logs/audit/{configName}_audit_.json",
                    rollingInterval: RollingInterval.Day)))
            
            // --- PIPELINE 2: Device-Specific Log Files ---
            .WriteTo.Logger(lc => lc
                .Filter.ByIncludingOnly(evt => evt.Properties.ContainsKey("DeviceName"))
                .Filter.ByIncludingOnly(evt => 
                    evt.Properties.ContainsKey("BufferedDump") || 
                    LogControl.DynamicFilter(evt))
                
                .WriteTo.Sink(new BufferedLog())
                .WriteTo.File(new CompactJsonFormatter(), $"../../logs/clef/{configName}_devices_.clef")
                .WriteTo.Map(
                    keyPropertyName: "DeviceName",
                    defaultKey: "System",
                    configure: (deviceName, wt) =>
                    {
                        wt.Async(a => a.File(
                            path: $"../../logs/devices/{configName}_{deviceName}_.log",
                            outputTemplate: "[{Timestamp:HH:mm:ss:fff}][{Level:u3}] {MethodTag}{GinTag}{Message:lj}{NewLine}{Exception}",
                            rollingInterval: RollingInterval.Day,
                            retainedFileCountLimit: 14));
                    }))

            // --- PIPELINE 3: Tracking ---
            .WriteTo.Logger(lc => lc
                .Filter.ByIncludingOnly(evt =>
                    evt.Properties.ContainsKey("Context") &&
                    evt.Properties["Context"] is ScalarValue { Value: "ConveyableEvents" })
                .WriteTo.File(new CompactJsonFormatter(), $"../../logs/clef/{configName}_tracking_.clef")
                .WriteTo.Async(a => a.File(
                    path: $"../../logs/tracking/{configName}_tracking_.log",
                    outputTemplate: "[{Timestamp:HH:mm:ss:fff}][{Level:u3}] {MethodTag}{GinTag}{Message:lj}{NewLine}{Exception}",
                    rollingInterval: RollingInterval.Day,
                    retainedFileCountLimit: 30)))

            // --- PIPELINE 4: General System Logs ---
            .WriteTo.Logger(lc => lc
                .Filter.ByExcluding(evt =>
                    evt.Properties.ContainsKey("AuditLog") ||
                    evt.Properties.ContainsKey("GIN") ||
                    evt.Properties.ContainsKey("DeviceName") ||
                    evt.Properties.ContainsKey("Context")) 
                .MinimumLevel.Override("Microsoft.Data.SqlClient", LogEventLevel.Error)
                .WriteTo.File(new CompactJsonFormatter(), $"../../logs/clef/{configName}_system_.clef")
                .WriteTo.Async(a => a.File(
                    $"../../logs/app/{configName}_system_.log",
                    rollingInterval: RollingInterval.Day,
                    retainedFileCountLimit: 30)))
            .CreateLogger();

        builder.Logging.ClearProviders();
        builder.Services.AddSerilog();
        builder.Services.AddSingleton<BusAuditLogger>();
    }
}