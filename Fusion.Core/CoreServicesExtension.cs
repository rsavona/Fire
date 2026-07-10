using System.Reflection;
using Fusion.Common;
using Fusion.Common.BaseClasses;
using Fusion.Common.Blueprints;
using Fusion.Common.Configurations;
using Fusion.Common.Contracts;
using Fusion.Common.logging;
using Fusion.Common.Logging;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using Serilog.Formatting.Compact;
using Serilog.Enrichers;

namespace Fusion.Core;

public static class CoreServicesExtensions
{
    public static TBuilder AddCoreServices<TBuilder>(this TBuilder builder, string[]? args = null)
        where TBuilder : IHostApplicationBuilder
    {
        // Set default global level to Verbose if you want, but LogControl.LevelSwitch is Information by default.
        // Let's set it to Verbose here if that was the original intent.
        LogControl.LevelSwitch.MinimumLevel = LogEventLevel.Verbose;

        // Suppress insecure TLS warnings globally
        AppContext.SetSwitch("System.Net.Security.Tls.DisableInsecureTlsWarnings", true);

        var baseFolderPath = AppContext.BaseDirectory;
        var isFire = false;
        
        if (args != null)
        {
            isFire = true;
          
            var appconfig = ConfigurationLoader.InitConfig(args);
            var config = appconfig?.GetSection("AppSettings:Fusion").Get<SystemBlueprintTemplate>();
            var appName = config?.CustomerName is { Length: > 0 } ? config.CustomerName : "simulatioin.fusion";

            if (config != null)
            {
                appName ??= "FortnaFire";
                SetupLogger(builder, appName);
                if (Environment.UserInteractive)
                {
                    Console.Title = appName;
                }
            }
            else
            {
                Log.Logger.Error("CORE", "ERROR", "CONFIG", "SYSTEM", "No Config File Found");
                throw new InvalidOperationException("No Config File Found");
            }

            // Fixed: Let Serilog handle the parameter injection
            Log.Logger.Information("SYSTEM", "STARTUP", "CORE", "FIRE", "=== {AppName} Starting ===", appName);

            // Bounded, drop-oldest log firehose (Tier 1 of the two-tier logging design).
            var logBusCapacity = builder.Configuration.GetValue<int?>("Fusion:LoggingBusCapacity")
                                 ?? builder.Configuration.GetValue<int?>("AppSettings:Fusion:LoggingBusCapacity")
                                 ?? LoggingBus.DefaultCapacity;
            builder.Services.AddSingleton(new LoggingBus(logBusCapacity));
            builder.Services.AddSingleton<ILoggingBus>(p => p.GetRequiredService<LoggingBus>());
            builder.Services.AddHostedService(p => p.GetRequiredService<LoggingBus>());

            // When true, FireLogger routes sub-Warning events only through the LoggingBus
            // (persisted by the Logger element). Default false = dual-write (safe increment).
            FireLogger.LoggerElementExclusive =
                builder.Configuration.GetValue<bool?>("Fusion:LoggerElementExclusive")
                ?? builder.Configuration.GetValue<bool?>("AppSettings:Fusion:LoggerElementExclusive")
                ?? false;

            builder.Services.AddSingleton(Log.Logger);
            builder.Services.AddSingleton(LogControl.LevelSwitch);
            builder.Services.AddSingleton<IMessageBus, MessageBus>();
            builder.Services.AddSingleton<ElementManagerFactory>();
            builder.Services.AddSingleton<ReactionFactory>();
            builder.Services.AddSingleton<IReactionFactory>(p => p.GetRequiredService<ReactionFactory>());
            builder.Services.AddHostedService<ReactionOrchestrator>();
            builder.Services.AddHostedService<FusionCore>();
        }
        
        LoadAvailableElementManagersFromDll(builder, baseFolderPath, isFire);
        LoadAvailableReactionsFromDll(builder, baseFolderPath, isFire);
        
        if (args != null)
        {
            LoadConfiguredElements(builder);
        }
       
        return builder;
    }

    private static IHostApplicationBuilder LoadConfiguredElements(IHostApplicationBuilder builder)
    {
        try
        {
            var allElements = ConfigurationLoader.GetAllElementConfig();

            if (!allElements.Any())
            {
                Log.Logger.Error("HOSTED", "LOAD", "CONFIG", "NONE", "No element configurations were found in the JSON.");
                return builder;
            }

            var uniqueElements = allElements
                .GroupBy(element => element.Manager)
                .Select(group => group.First())
                .ToList();
             
            // Loop builds the ElementManagerFactory parameters. DI will resolve constructors at runtime.
            foreach (var elementConfig in uniqueElements)
            {
                try
                {
                    string managerName = elementConfig.Manager.ToString();
                    Log.Logger.Information("HOSTED", "CREATE", "MANAGER", managerName, "Registering Service Definition");

                    builder.Services.AddSingleton<IHostedService>(provider =>
                    {
                        try
                        {
                            var factory = provider.GetRequiredService<ElementManagerFactory>();
                            var manager = factory.CreateElementManager(managerName);

                            if (manager == null)
                                throw new InvalidOperationException($"Factory returned null for manager: {managerName}");

                            return (IHostedService)manager;
                        }
                        catch (Exception ex)
                        {
                            Log.Logger.Fatal(ex, "HOSTED", "STARTUP", "FACTORY", managerName, "Failed to resolve element manager at runtime.");
                            throw; 
                        }
                    });
                }
                catch (Exception ex)
                {
                    Log.Logger.Error(ex, "HOSTED", "REGISTER", "DI", elementConfig.Manager?.ToString() ?? "Unknown", "Failed to register manager in DI container.");
                }
            }
        }
        catch (Exception ex)
        {
            Log.Logger.Fatal(ex, "HOSTED", "LOAD", "CRITICAL", "GLOBAL", "Critical failure loading element configurations.");
            throw; 
        }
        return builder;
    }

    private static IHostApplicationBuilder LoadAvailableReactionsFromDll(IHostApplicationBuilder builder, string baseFolderPath, bool fire)
    {
        var reactionList = new List<Type>();
        
        if (Directory.Exists(baseFolderPath))
        {
            var reactionDlls = Directory.GetFiles(baseFolderPath, "Fusion.Reaction.*.dll");
            
            // Fixed: Removed interpolation, added structured property
            Log.Logger.Information("DISCOVERY", "SCAN", "DLL", "REACTION", "Found {AssemblyCount} assemblies", reactionDlls.Length);
            
            foreach (string dllPath in reactionDlls)
            {
                try
                {
                    Assembly assembly = Assembly.LoadFrom(dllPath);
                    var foundReactions = assembly.GetTypes()
                        .Where(t => t.BaseType == typeof(ReactionBase) && !t.IsAbstract)
                        .ToList();

                    foreach (var wf in foundReactions)
                    {
                        Log.Logger.Information("DISCOVERY", "LOAD", "REACTION", wf.Name, "Reaction registered");
                    }

                    reactionList.AddRange(foundReactions);
                    if (fire)
                    {
                        RegisterPlugins(builder, assembly);
                    }
                }
                catch (BadImageFormatException ex)
                {
                    Log.Logger.Warning(
                        "Skipping invalid reaction assembly {DllPath}: {Message}",
                        Path.GetFileName(dllPath),
                        ex.Message);
                }
                catch (Exception ex)
                {
                    // Fixed: Include exception explicitly for stack traces
                    Log.Logger.Error(ex, "Failed to load reaction assembly {DllPath}", Path.GetFileName(dllPath));
                }
            }

            builder.Services.AddKeyedSingleton<IEnumerable<Type>>("ReactionTypes", reactionList);
        }

        return builder;
    }

    private static IHostApplicationBuilder LoadAvailableElementManagersFromDll(IHostApplicationBuilder builder, string baseFolderPath, bool fire)
    {
        var managerList = new List<Type>();
        
        if (Directory.Exists(baseFolderPath))
        {
            var elementDlls = Directory.GetFiles(baseFolderPath, "Fusion.Element.*.dll");
            Log.Logger.Information("DISCOVERY", "SCAN", "DLL", "ELEMENT", "Found {AssemblyCount} assemblies", elementDlls.Length);

            foreach (string dllPath in elementDlls)
            {
                try
                {
                    Assembly assembly = Assembly.LoadFrom(dllPath);
                    var version = assembly.GetName().Version;
                    var managers = assembly.GetTypes()
                        .Where(t => typeof(IElementManager).IsAssignableFrom(t) && !t.IsInterface && !t.IsAbstract)
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
                    Log.Logger.Error(ex, "DISCOVERY", "FAULT", "DLL", "ELEMENT", "Failed to load element assembly: {DllPath}", Path.GetFileName(dllPath));
                }
            }

            builder.Services.AddKeyedSingleton<IEnumerable<Type>>("ElementManagerTypes", managerList);
        }

        return builder;
    }

    private static IHostApplicationBuilder RegisterPlugins(IHostApplicationBuilder builder, Assembly assembly)
    {
        var registrars = assembly.GetTypes()
            .Where(t => typeof(IElementRegistrar).IsAssignableFrom(t) && !t.IsInterface && !t.IsAbstract);

        foreach (var type in registrars)
        {
            try
            {
                if (Activator.CreateInstance(type) is IElementRegistrar registrar)
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
        static string LogPath(params string[] parts)
        {
            return Path.GetFullPath(Path.Combine([AppContext.BaseDirectory, "..", "logs", ..parts]));
        }

        builder.Services.AddSingleton<IFireLogger>(provider => 
        {
            var bus = provider.GetService<IMessageBus>();
            var logBus = provider.GetService<ILoggingBus>();
            return new FireLogger(Log.Logger, logBus, bus);
        });
        builder.Services.AddTransient(typeof(IFireLogger<>), typeof(FireLogger<>));

        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.ControlledBy(LogControl.LevelSwitch)
            .Enrich.FromLogContext()
            .WriteTo.Async(a => a.Console(
                levelSwitch: LogControl.ConsoleLevelSwitch,
                outputTemplate: "[{Timestamp:HH:mm:ss.fff}][{Level:u3}][{ElementName}] {MethodTag}{GinTag}{Message:lj}{NewLine}{Exception}"))

            // --- PIPELINE 1: Audit ---
            .WriteTo.Logger(lc => lc
                .Filter.ByIncludingOnly(evt => evt.Properties.ContainsKey("AuditLog"))
                .WriteTo.Async(a => a.File(
                    new CompactJsonFormatter(),
                    LogPath("audit", $"{configName}_audit_.json"),
                    rollingInterval: RollingInterval.Day)))
            
            // --- PIPELINE 2: Element-Specific Log Files ---
            .WriteTo.Logger(lc => lc
                .Filter.ByIncludingOnly(evt => evt.Properties.ContainsKey("ElementName"))
                .Filter.ByIncludingOnly(evt => 
                    evt.Properties.ContainsKey("BufferedDump") || 
                    LogControl.DynamicFilter(evt))
                
                .WriteTo.Sink(new BufferedLog())
                .WriteTo.Async(a => a.File(new CompactJsonFormatter(), LogPath("clef", $"{configName}_elements_.clef")))
                .WriteTo.Map(
                    keyPropertyName: "ElementName",
                    defaultKey: "System",
                    configure: (elementName, wt) =>
                    {
                        wt.Async(a => a.File(
                            path: LogPath("elements", $"{configName}_{elementName}_.log"),
                            outputTemplate: "[{Timestamp:HH:mm:ss:fff}][{Level:u3}] {MethodTag}{GinTag}{Message:lj}{NewLine}{Exception}",
                            rollingInterval: RollingInterval.Day,
                            retainedFileCountLimit: 14));
                    }))

            // --- PIPELINE 3: Tracking ---
            .WriteTo.Logger(lc => lc
                .Filter.ByIncludingOnly(evt =>
                    evt.Properties.ContainsKey("Context") &&
                    evt.Properties["Context"] is ScalarValue { Value: "ConveyableEvents" })
                .WriteTo.File(new CompactJsonFormatter(), LogPath("clef", $"{configName}_tracking_.clef"))
                .WriteTo.Async(a => a.File(
                    path: LogPath("tracking", $"{configName}_tracking_.log"),
                    outputTemplate: "[{Timestamp:HH:mm:ss:fff}][{Level:u3}] {MethodTag}{GinTag}{Message:lj}{NewLine}{Exception}",
                    rollingInterval: RollingInterval.Day,
                    retainedFileCountLimit: 30)))

            // --- PIPELINE 4: General System Logs ---
            .WriteTo.Logger(lc => lc
                .Filter.ByExcluding(evt =>
                    evt.Properties.ContainsKey("AuditLog") ||
                    evt.Properties.ContainsKey("GIN") ||
                    evt.Properties.ContainsKey("ElementName") ||
                    evt.Properties.ContainsKey("Context")) 
                .MinimumLevel.Override("Microsoft.Data.SqlClient", LogEventLevel.Error)
                .WriteTo.File(new CompactJsonFormatter(), LogPath("clef", $"{configName}_system_.clef"))
                .WriteTo.Async(a => a.File(
                    LogPath("app", $"{configName}_system_.log"),
                    rollingInterval: RollingInterval.Day,
                    retainedFileCountLimit: 30)))
            .CreateLogger();

        builder.Logging.ClearProviders();
        builder.Services.AddSerilog();
        builder.Services.AddSingleton<BusAuditLogger>();
    }
}
