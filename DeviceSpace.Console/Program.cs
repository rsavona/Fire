using DeviceSpaceConsole;
using DeviceSpace.Core;
using DeviceSpace.Common;
using DeviceSpace.Common.Contracts;
using DeviceSpace.Common.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Events;
using System.Net;

// Initialize a bootstrap logger to catch very early errors before the full host is built
Log.Logger = new LoggerConfiguration()
    .WriteTo.Console()
    .WriteTo.File("logs/app/bootstrap_startup.log", rollingInterval: RollingInterval.Day)
    .CreateBootstrapLogger();

try
{
    Directory.SetCurrentDirectory(AppContext.BaseDirectory);
    
    if (Environment.UserInteractive)
    {
        // Enforce UTF-8 for console output and input
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        Console.InputEncoding = System.Text.Encoding.UTF8;

        ConsoleHelper.EnableRender();
        ConsoleHelper.SetConsoleWindowSize();
        ConsoleHelper.SetConsoleFont("Consolas", 16);
        Console.WriteLine("\x1b[31mThis is now RED in cmd and double-click!\x1b[0m");
    }

    // Force secure TLS protocols and disable insecure TLS warnings
    System.Net.ServicePointManager.SecurityProtocol = System.Net.SecurityProtocolType.Tls12 | System.Net.SecurityProtocolType.Tls13;
    AppContext.SetSwitch("System.Net.Security.Tls.DisableInsecureTlsWarnings", true);

    Log.Information("Host starting...");

    // Build and configure the host
    HostApplicationBuilder builder = Host.CreateApplicationBuilder(args);
    
    if (Environment.UserInteractive && !args.Contains("--service"))
    {
        builder.Services.AddSingleton<ConsoleStatusMonitor>();
    }
    else
    {
        builder.Services.AddWindowsService(options =>
        {
            var config = DeviceSpace.Common.Configurations.ConfigurationLoader.GetSpaceConfig();
            options.ServiceName = config?.Name ?? "FortnaFire";
        });
    }

    builder = builder.AddCoreServices(args);
    builder.Logging.ClearProviders();
    IHost host = builder.Build();

    // Check if we should run the interactive console
    if (Environment.UserInteractive && !args.Contains("--service"))
    {
        var config = DeviceSpace.Common.Configurations.ConfigurationLoader.GetSpaceConfig();
        bool showSplash = config?.ColorConsole ?? true;

        if (showSplash)
        {
            if (false)
                SplashScreenFusion.Print();
            else
                SplashScreenFire.Print();
        }

        var appLifetime = host.Services.GetRequiredService<IHostApplicationLifetime>();
        var consoleDisplay = host.Services.GetRequiredService<ConsoleStatusMonitor>();
        consoleDisplay.Start(appLifetime.ApplicationStopping, showSplash ? SplashScreenFire.Length() : 0);

        var hostTask = host.RunAsync();
        RunInteractiveConsole(consoleDisplay, host.Services.GetRequiredService<IMessageBus>());
        await hostTask;
    }
    else
    {
        // Run as a normal service (non-interactive)
        host.Run();
    }
}
catch (Exception ex)
{
    Log.Fatal(ex, "Host terminated unexpectedly during startup.");
    
    Console.ForegroundColor = ConsoleColor.Red;
    Console.WriteLine($"Host failed to start: {ex.Message}");
    Console.ResetColor();
    Console.WriteLine(ex.ToString());
    
    if (Environment.UserInteractive)
    {
        await Task.Delay(15000);
    }
}
finally
{
    Log.Information("Host stopped. Flushing logs...");
    await Log.CloseAndFlushAsync();
}

static void RunInteractiveConsole(ConsoleStatusMonitor monitor, IMessageBus bus)
{
    var pluginDir = Path.Combine(AppContext.BaseDirectory, "plugins");
    string unlockSequence = "fortna";
    string currentInput = "";
    DateTime lastKeyPress = DateTime.MinValue;
    TimeSpan lockTimeout = TimeSpan.FromSeconds(30);
 
    while (true)
    {
        // Auto-lock check
        if (!monitor.IsLocked && (DateTime.Now - lastKeyPress) > lockTimeout)
        {
            monitor.IsLocked = true;
            currentInput = "";
            Log.Warning("Console locked due to inactivity.");
        }

        if (Console.KeyAvailable)
        {
            var key = Console.ReadKey(true);
            lastKeyPress = DateTime.Now;

            if (monitor.IsLocked)
            {
                // In locked mode, only listen for the unlock sequence
                currentInput += key.KeyChar.ToString().ToLower();
                if (currentInput.Length > unlockSequence.Length)
                {
                    currentInput = currentInput.Substring(currentInput.Length - unlockSequence.Length);
                }

                if (currentInput == unlockSequence)
                {
                    monitor.IsLocked = false;
                    currentInput = "";
                    Log.Information("Console UNLOCKED.");
                }
                continue;
            }

            // --- Unlocked Commands ---
            if (key.Key == ConsoleKey.P || key.Key == ConsoleKey.Spacebar || key.Key == ConsoleKey.Pause)
            {
                monitor.TogglePause();
                // After toggling, wait for another key to resume if it was just paused
                Console.ReadKey(true);
                monitor.TogglePause();
                continue;
            }
            if (key.Key == ConsoleKey.L)
            {
                if (monitor.IsActive)
                {
                    monitor.IsActive = false;
                    LogControl.ConsoleLevelSwitch.MinimumLevel = LogControl.LevelSwitch.MinimumLevel;
                }
                else
                {
                    monitor.IsActive = true;
                    LogControl.ConsoleLevelSwitch.MinimumLevel = LogEventLevel.Fatal + 1;
                    
                    // Force all devices and workflows to broadcast their status immediately
                    var topic = MessageBusTopic.SystemControl.ToString();
                    var msg = new SystemControlMessage(SystemCommand.RefreshStatus);
                    _ = bus.PublishAsync(topic, new MessageEnvelope(MessageBusTopic.SystemControl, msg));
                }
                continue;
            }

            if (key.Key == ConsoleKey.D1 || key.Key == ConsoleKey.NumPad1)
            {
                LogControl.LevelSwitch.MinimumLevel = LogEventLevel.Information;
                if (!monitor.IsActive) LogControl.ConsoleLevelSwitch.MinimumLevel = LogEventLevel.Information;
                Log.Information("SYSTEM", "LEVEL", "CHANGE", "INFO", "Global Log Level set to INFORMATION");
                continue;
            }
            if (key.Key == ConsoleKey.D2 || key.Key == ConsoleKey.NumPad2)
            {
                LogControl.LevelSwitch.MinimumLevel = LogEventLevel.Debug;
                if (!monitor.IsActive) LogControl.ConsoleLevelSwitch.MinimumLevel = LogEventLevel.Debug;
                Log.Information("SYSTEM", "LEVEL", "CHANGE", "DEBUG", "Global Log Level set to DEBUG");
                continue;
            }
            if (key.Key == ConsoleKey.D3 || key.Key == ConsoleKey.NumPad3)
            {
                LogControl.LevelSwitch.MinimumLevel = LogEventLevel.Verbose;
                if (!monitor.IsActive) LogControl.ConsoleLevelSwitch.MinimumLevel = LogEventLevel.Verbose;
                Log.Information("SYSTEM", "LEVEL", "CHANGE", "TRACE", "Global Log Level set to VERBOSE");
                continue;
            }

            if (key.Key == ConsoleKey.T)
            {
                var topic = DeviceSpace.Common.MessageBusTopic.ConsoleCommand.ToString();
                _ = bus.PublishAsync(topic, new MessageEnvelope(DeviceSpace.Common.MessageBusTopic.ConsoleCommand, "RELEASE_TOTE"));
                continue;
            }
        }

        Thread.Sleep(100);
    }
}