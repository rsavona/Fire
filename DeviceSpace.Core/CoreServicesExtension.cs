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
    public static LoggingLevelSwitch LevelSwitch = new LoggingLevelSwitch(LogEventLevel.Verbose);

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
                Log.Logger.FireLogTrace("This is a fatal error. An exception will be thrown because the config file was not found");
                Log.Logger.FireLogError("CORE", "ERROR", "CONFIG", "SYSTEM", "No Config File Found");
                throw new Exception("No Config File Found");
            }

            Log.Logger.FireLogTrace(  $"The application is starting. We are in CoreServicesExtensions - AddCoreServices");
            Log.Logger.FireLogInfo("SYSTEM", "STARTUP", "CORE", "FIRE", $"=== {appName} Starting ===");

            Log.Logger.FireLogTrace(  "First we register our Singletons like the MessageBus, DeviceManagerFactory, and DeviceSpaceCore "); 
            builder.Services.AddSingleton(Log.Logger);
            builder.Services.AddSingleton(LevelSwitch);
            builder.Services.AddSingleton<IMessageBus, MessageBus>();
            builder.Services.AddSingleton<DeviceManagerFactory>();
            builder.Services.AddSingleton<WorkflowFactory>();
            builder.Services.AddHostedService<DeviceSpaceCore>();
            
        }
        
        Log.Logger.FireLogTrace(  $"In AddCoreServices - First the reflection information is loaded for Devices and Workflows from the dlls in {baseFolderPath} "); 
        LoadAvailableDeviceManagersFromDll(builder, baseFolderPath, isFire);
        LoadAvailableWorkflowsFromDll(builder, baseFolderPath, isFire);
        if (args != null)
        {
            LoadConfiguredDevices(builder);
            LoadConfiguredWorkflow(builder);
        }
        Log.Logger.FireLogTrace(  "At the end of AddCoreServices returning back to Program.cs"); 
        return builder;
    }


    private static IHostApplicationBuilder LoadConfiguredDevices(IHostApplicationBuilder builder)
    {
        try
        {
            Log.Logger.Verbose("Entered LoadConfigedDevices -  pulled all devices from ConfigurationLoader");
            var allDevices = ConfigurationLoader.GetAllDeviceConfig();

            // Check if configuration returned null before processing
            if (!allDevices.Any())
            {
                Log.Logger.Verbose("No devices were found in the configuration.  Check the JSON for correct formatting");
                Log.Logger.Error("HOSTED", "LOAD", "CONFIG", "NONE", "No device configurations were found.");
                return builder;
            }

            var uniqueDevices = allDevices
                .GroupBy(device => device.Manager)
                .Select(group => group.First())
                .ToList();
             
            Log.Logger.Verbose("Devices were found - entering a loop to register each DeviceManger needed.");
            Log.Logger.Verbose("The loop uses a DeviceManagerFactory and will make the Managers when we tell the builder to build.");
            Log.Logger.Verbose("Here is where we need to make sure dependancy injection can provide each parameter for the DeviceManager's constructor ");
            Log.Logger.Verbose("When the DeviceMangers are instantiated, they will throw an exception because somehting isn't right here or in the factory ");
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
                            Log.Logger.Verbose("I really don't except a exception here, we are just telling the builder how to build the managers..");
                            Log.Logger.Fatal(ex, "HOSTED", "STARTUP", "FACTORY", managerName,
                                "Failed to resolve device manager");
                            throw; // Re-throw to prevent the service from starting in an invalid state
                        }
                    });
                }
                catch (Exception ex)
                {
                    Log.Logger.Verbose("I really don't except a exception here, we are just telling the builder how to build the managers..");
                    // This catch handles failures DURING the registration loop
                    Log.Logger.Error(ex, "HOSTED", "REGISTER", "DI", deviceConfig.Manager?.ToString() ?? "Unknown",
                        "Failed to register manager in DI");
                }
            }
        }
        catch (Exception ex)
        {
            Log.Logger.Verbose("I really don't except a exception here, we are just telling the builder how to build the managers..");
            // This catch handles failures in GetAllDeviceConfig or the GroupBy logic
            Log.Logger.Fatal(ex, "HOSTED", "LOAD", "CRITICAL", "GLOBAL",
                "Critical failure loading device configurations");
            throw; // Usually, you want the service to stop if the config itself is broken
        }
        return builder;
    }

   private static IHostApplicationBuilder LoadConfiguredWorkflow(IHostApplicationBuilder builder)
    {
        Log.Logger.Verbose("Entered LoadConfiguredWorkflow - fetching workflow definitions from configuration.");
        var allWorkflows = ConfigurationLoader.GetAllWorkflowConfig();
        
        foreach (var workflowConfig in allWorkflows)
        {
            var wfConfig = (WorkflowConfig)workflowConfig;
            if (!wfConfig.Enable)
            {
                Log.Logger.FireLogWarning("HOSTED", "SKIP", "WORKFLOW", wfConfig.Name, "Disabled in config");
                continue;
            }

            Log.Logger.FireLogTrace($"Registering workflow '{wfConfig.Name}' into the DI container as a Hosted Service.");
            Log.Logger.FireLogInfo("HOSTED", "CREATE", "WORKFLOW", wfConfig.Name, "Initializing Service");
            
            builder.Services.AddSingleton<IHostedService>(provider =>
            {
                var factory = provider.GetRequiredService<WorkflowFactory>();
                return factory.CreateWorkflow(wfConfig);
            });
        }

        return builder;
    }

    private static IHostApplicationBuilder LoadAvailableWorkflowsFromDll(IHostApplicationBuilder builder,
        string baseFolderPath, bool fire)
    {
        Log.Logger.FireLogTrace("Beginning dynamic discovery of Workflow DLLs to identify available logic modules.");
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
                    Log.Logger.Verbose($"Loading assembly {Path.GetFileName(dllPath)} and scanning for WorkflowBase implementations.");
                    Assembly assembly = Assembly.LoadFrom(dllPath);
                    var foundWorkflows = assembly.GetTypes()
                        .Where(t => t.BaseType == typeof(WorkflowBase) && !t.IsAbstract)
                        .ToList();

                    foreach (var wf in foundWorkflows)
                    {
                        Log.Logger.FireLogInfo("DISCOVERY", "LOAD", "WORKFLOW", wf.Name, "Workflow registered");
                    }

                    workflowList.AddRange(foundWorkflows);
                    if (fire)
                    {
                        Log.Logger.FireLogTrace($"Registering associated plugins found in {Path.GetFileName(dllPath)}");
                        RegisterPlugins(builder, assembly);
                    }
                }
                catch (Exception ex)
                {
                    Log.Logger.FireLogError("DISCOVERY", "FAULT", "DLL", "WORKFLOW", ex.Message);
                }
            }

            Log.Logger.FireLogTrace("Finalizing Workflow discovery; injecting the list of Type definitions into Keyed Services.");
            builder.Services.AddKeyedSingleton<IEnumerable<Type>>("WorkflowTypes", workflowList);
        }

        return builder;
    }

    private static IHostApplicationBuilder LoadAvailableDeviceManagersFromDll(IHostApplicationBuilder builder,
        string baseFolderPath, bool fire)
    {
        var managerList = new List<Type>();
        if (managerList == null) throw new ArgumentNullException(nameof(managerList));
        if (Directory.Exists(baseFolderPath))
        {
            var deviceDlls = Directory.GetFiles(baseFolderPath, "Device.*.dll");
            Log.Logger.FireLogInfo("DISCOVERY", "SCAN", "DLL", "DEVICE", $"Found {deviceDlls.Length} assemblies");

            Log.Logger.FireLogTrace("CoreServicesExtensions - Searching for Device Managers in discovered DLLs.");
            foreach (string dllPath in deviceDlls)
            {
                try
                {
                    Log.Logger.Verbose($"Inspecting {Path.GetFileName(dllPath)} for classes implementing IDeviceManager.");
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
                    if (fire)
                        RegisterPlugins(builder, assembly);
                }
                catch (Exception ex)
                {
                    Log.Logger.FireLogError("DISCOVERY", "FAULT", "DLL", "DEVICE", ex.Message);
                }
            }

            Log.Logger.FireLogTrace("Device Manager discovery complete. Storing metadata in the DeviceManagerTypes service collection.");
            builder.Services.AddKeyedSingleton<IEnumerable<Type>>("DeviceManagerTypes", managerList);
        }

        return builder;
    }

    private static IHostApplicationBuilder RegisterPlugins(IHostApplicationBuilder builder, Assembly assembly)
    {
        Log.Logger.Verbose($"Scanning assembly '{assembly.GetName().Name}' for IDeviceRegistrar implementations to handle custom DI wiring.");
        var registrars = assembly.GetTypes()
            .Where(t => typeof(IDeviceRegistrar).IsAssignableFrom(t) && !t.IsInterface && !t.IsAbstract);

        foreach (var type in registrars)
        {
            try
            {
                Log.Logger.FireLogTrace($"Instantiating registrar '{type.Name}' and executing its service registration logic.");
                if (Activator.CreateInstance(type) is IDeviceRegistrar registrar)
                {
                    registrar.RegisterServices(builder.Services);
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
    /// </summary>
    private static void SetupLogger(this IHostApplicationBuilder builder, string configName)
    {
        Log.Logger.FireLogTrace("Initializing the Serilog multi-pipeline architecture for Audit, Device-Level, and GIN tracking.");
        
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.ControlledBy(LevelSwitch)
            .Enrich.FromLogContext()

            // --- PIPELINE 1: Audit ---
            .WriteTo.Logger(lc => lc
                .Filter.ByIncludingOnly(evt => evt.Properties.ContainsKey("AuditLog"))
                .WriteTo.Async(a => a.File(
                    new CompactJsonFormatter(),
                    $"logs/audit/{configName}_audit_.json",
                    rollingInterval: RollingInterval.Day)))
            
            // --- PIPELINE 2: Device-Specific Log Files ---
            .WriteTo.Logger(lc => lc
                .Filter.ByIncludingOnly(evt => evt.Properties.ContainsKey("DeviceName"))
                .Filter.ByIncludingOnly(LogControl.DynamicFilter)
                
                .WriteTo.Sink(new BufferedLog())
                .WriteTo.Map(
                    keyPropertyName: "DeviceName",
                    defaultKey: "System",
                    configure: (deviceName, wt) =>
                    {
                        wt.Async(a => a.File(
                            path: $"logs/devices/{configName}_{deviceName}_.log",
                            outputTemplate:
                            "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] {Message:lj}{NewLine}{Exception}",
                            rollingInterval: RollingInterval.Day,
                            retainedFileCountLimit: 14));
                    }))

            // --- PIPELINE 3: GIN Tracking ---
            .WriteTo.Logger(lc => lc
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


            // --- PIPELINE 4: General System Logs ---
            .WriteTo.Logger(lc => lc
                .Filter.ByExcluding(evt =>
                    evt.Properties.ContainsKey("AuditLog") ||
                    evt.Properties.ContainsKey("GIN") ||
                    evt.Properties.ContainsKey("DeviceName")) 
                .WriteTo.Async(a => a.File(
                    $"logs/app/{configName}_system_.log",
                    rollingInterval: RollingInterval.Day,
                    retainedFileCountLimit: 30)))
            .CreateLogger();

        Log.Logger.FireLogTrace("Clearing default logging providers and finalizing the Serilog DI registration.");
        builder.Services.AddSingleton(LevelSwitch);
        builder.Logging.ClearProviders();
        builder.Services.AddSerilog();
        builder.Services.AddSingleton<BusAuditLogger>();
    }
    
}