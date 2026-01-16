using System.Reflection;
using DeviceSpace.Common;
using DeviceSpace.Common.BaseClasses;
using DeviceSpace.Common.Configurations;
using DeviceSpace.Common.Contracts;
using DeviceSpace.Common.Logging;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using Serilog.Formatting.Compact;

namespace DeviceSpace.Core;

public static class CoreServicesExtensions
{
    public static  LoggingLevelSwitch LevelSwitch = new LoggingLevelSwitch(LogEventLevel.Verbose);

    public static TBuilder AddCoreServices<TBuilder>(this TBuilder builder, string[]? args = null)
    where TBuilder : IHostApplicationBuilder
    {
        var baseFolderPath = AppContext.BaseDirectory;
        if (args != null) 
        {
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
                Log.Logger.FireLogError("CORE", "ERROR", "CONFIG", "SYSTEM", "No Config File Found");
                throw new Exception("No Config File Found");
            }

            Log.Logger.FireLogInfo("SYSTEM", "STARTUP", "CORE", "FIRE", $"=== {appName} Starting ===");


            // Core Infrastructure
            builder.Services.AddSingleton(Log.Logger);
            builder.Services.AddSingleton(LevelSwitch);
            builder.Services.AddSingleton<IMessageBus, MessageBus>();
            builder.Services.AddSingleton<DeviceManagerFactory>();
            builder.Services.AddSingleton<WorkflowFactory>();
            builder.Services.AddHostedService<DeviceSpaceCore>();

        }    
        LoadAvailableDeviceManagersFromDll(builder, baseFolderPath);
        LoadAvailableWorkflowsFromDll(builder, baseFolderPath);
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

            // Check if configuration returned null before processing
            if (!allDevices.Any())
            {
                Log.Logger.Warning("HOSTED", "LOAD", "CONFIG", "NONE", "No device configurations were found.");
                return builder;
            }

            var uniqueDevices = allDevices
                .GroupBy(device => device.Manager)
                .Select(group => group.First())
                .ToList();

            foreach (var deviceConfig in uniqueDevices)
            {
                try
                {
                    // Safeguard against null Manager property

                    string managerName = deviceConfig.Manager.ToString();

                    Log.Logger.FireLogInfo("HOSTED", "CREATE", "MANAGER", managerName, "Initializing Service");

                    builder.Services.AddSingleton<IHostedService>(provider =>
                    {
                        try
                        {
                            var factory = provider.GetRequiredService<DeviceManagerFactory>();
                            var manager = factory.CreateDeviceManager(managerName);

                            if (manager == null)
                                throw new Exception($"Factory returned null for manager: {managerName}");

                            return (IHostedService)manager;
                        }
                        catch (Exception ex)
                        {
                            // This catch handles failures DURING service resolution (at runtime)
                            Log.Logger.Fatal(ex, "HOSTED", "STARTUP", "FACTORY", managerName,
                                "Failed to resolve device manager");
                            throw; // Re-throw to prevent the service from starting in an invalid state
                        }
                    });
                }
                catch (Exception ex)
                {
                    // This catch handles failures DURING the registration loop
                    Log.Logger.Error(ex, "HOSTED", "REGISTER", "DI", deviceConfig.Manager?.ToString() ?? "Unknown",
                        "Failed to register manager in DI");
                }
            }
        }
        catch (Exception ex)
        {
            // This catch handles failures in GetAllDeviceConfig or the GroupBy logic
            Log.Logger.Fatal(ex, "HOSTED", "LOAD", "CRITICAL", "GLOBAL",
                "Critical failure loading device configurations");
            throw; // Usually, you want the service to stop if the config itself is broken
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
                Log.Logger.FireLogWarning("HOSTED", "SKIP", "WORKFLOW", wfConfig.Name, "Disabled in config");
                continue;
            }

            Log.Logger.FireLogInfo("HOSTED", "CREATE", "WORKFLOW", wfConfig.Name, "Initializing Service");
            builder.Services.AddSingleton<IHostedService>(provider =>
            {
                var factory = provider.GetRequiredService<WorkflowFactory>();
                return factory.CreateWorkflow(wfConfig);
            });
        }

        return builder;
    }

    private static IHostApplicationBuilder LoadAvailableWorkflowsFromDll(IHostApplicationBuilder builder, string baseFolderPath)
    {
        var workflowList = new List<Type>();
        if (workflowList == null) throw new ArgumentNullException(nameof(workflowList));
        if (Directory.Exists(baseFolderPath))
        {
            var workflowDlls = Directory.GetFiles(baseFolderPath, "Workflow.*.dll");
            Log.Logger.FireLogInfo("DISCOVERY", "SCAN", "DLL", "WORKFLOW", $"Found {workflowDlls.Length} assemblies");
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
                        Log.Logger.FireLogInfo("DISCOVERY", "LOAD", "WORKFLOW", wf.Name, "Workflow registered");
                    }

                    workflowList.AddRange(foundWorkflows);
                    //RegisterPlugins(builder, assembly);
                }
                catch (Exception ex)
                {
                    Log.Logger.FireLogError("DISCOVERY", "FAULT", "DLL", "WORKFLOW", ex.Message);
                }
            }

            builder.Services.AddKeyedSingleton<IEnumerable<Type>>("WorkflowTypes", workflowList);
        }

        return builder;
    }

    private static IHostApplicationBuilder LoadAvailableDeviceManagersFromDll(IHostApplicationBuilder builder, string baseFolderPath)
    {
        var managerList = new List<Type>();
        if (managerList == null) throw new ArgumentNullException(nameof(managerList));
        if (Directory.Exists(baseFolderPath))
        {
            var deviceDlls = Directory.GetFiles(baseFolderPath, "Device.*.dll");
            Log.Logger.FireLogInfo("DISCOVERY", "SCAN", "DLL", "DEVICE", $"Found {deviceDlls.Length} assemblies");

            Log.Logger.FireLogTrace(" CoreServicesExtensions  - (1 of 4) Searching for Device Managers");
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
                        Log.Logger.FireLogInfo("DISCOVERY", "LOAD", "MANAGER", m.Name,
                            $"v{version} from {Path.GetFileName(dllPath)}");
                    }

                    managerList.AddRange(managers);
                    //RegisterPlugins(builder, assembly);
                }
                catch (Exception ex)
                {
                    Log.Logger.FireLogError("DISCOVERY", "FAULT", "DLL", "DEVICE", ex.Message);
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
                    // Using FireLogTrace style for internal DI discovery
             
                }
            }
            catch (Exception ex)
            {
                Log.Logger.FireLogError("PLUGINS", "ERROR", "REGISTRAR", type.Name, ex.Message);
            }
        }
        return builder;
    }

    /// <summary>
    /// Configures Serilog and adds the Audit Logger.
    /// TODO -- add a variety of logging capabilities
    /// </summary>
    private static void SetupLogger(this IHostApplicationBuilder builder, string configName)
    {
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.ControlledBy(LevelSwitch)
            .Enrich.FromLogContext()

            // --- PIPELINE 1: Audit (Unchanged) ---
            .WriteTo.Logger(lc => lc
                .Filter.ByIncludingOnly(evt => evt.Properties.ContainsKey("AuditLog"))
                .WriteTo.Async(a => a.File(
                    new CompactJsonFormatter(),
                    $"logs/audit/{configName}_audit_.json",
                    rollingInterval: RollingInterval.Day)))

            // --- PIPELINE 2: GIN-Specific Logging (ONLY if GIN exists) ---
            .WriteTo.Logger(lc => lc
                // This is the key: Only proceed if the "GIN" property is present and has a value
                .Filter.ByIncludingOnly(evt =>
                    evt.Properties.ContainsKey("GIN") &&
                    evt.Properties["GIN"] is ScalarValue { Value: string s } &&
                    !string.IsNullOrWhiteSpace(s))
                .WriteTo.Map("GIN", "NoGIN", (ginValue, wt) =>
                {
                    wt.Async(a => a.File(
                        path: $"logs/gin/{configName}_GIN_{ginValue}_.log",
                        outputTemplate:
                        "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] {Message:lj}{NewLine}{Exception}",
                        rollingInterval: RollingInterval.Day,
                        retainedFileCountLimit: 7));
                }))

            // --- PIPELINE 3: General System Logs (Excluding Audit) ---
            .WriteTo.Logger(lc => lc
                .Filter.ByExcluding(evt => evt.Properties.ContainsKey("AuditLog"))
                .WriteTo.Async(a => a.File(
                    $"logs/app/{configName}_system_.log",
                    rollingInterval: RollingInterval.Day,
                    retainedFileCountLimit: 30)))
            .CreateLogger();

        builder.Services.AddSingleton(LevelSwitch);
        builder.Logging.ClearProviders();
        builder.Services.AddSerilog();
        builder.Services.AddSingleton<BusAuditLogger>();
    }
}