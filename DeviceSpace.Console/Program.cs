using FusionConsole;
using Fusion.Core;
using Fusion.Common;
using Fusion.Common.Contracts;
using Fusion.Common.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Events;
using System.Net;
using System.Text;
using System.Threading;
using Fusion.Common.Configurations;

// Initialize a bootstrap logger to catch very early errors before the full host is built
Log.Logger = new LoggerConfiguration()
    .WriteTo.Console()
    .WriteTo.File(
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "logs", "app", "bootstrap_startup.log")),
        rollingInterval: RollingInterval.Day)
    .CreateBootstrapLogger();

Mutex? singleInstanceMutex = null;

try
{
    Directory.SetCurrentDirectory(AppContext.BaseDirectory);
    var appArgs = StripFireRuntimeArgs(args);

    // Check for Windows Service, systemd (Linux), or explicit --service flag
    bool isService = args.Any(IsServiceFlag) ||
                     !Environment.UserInteractive ||
                     Console.IsInputRedirected ||
                     Environment.GetEnvironmentVariable("INVOCATION_ID") != null ||
                     Environment.GetEnvironmentVariable("JOURNAL_STREAM") != null;

    // Load the configuration early so the process guard can be keyed by the configured system name.
    ConfigurationLoader.InitConfig(appArgs);
    var configurationName = ConfigurationLoader.GetSpaceConfig()?.CustomerName;
    singleInstanceMutex = AcquireSingleInstanceLock(configurationName, out var lockName);

    if (singleInstanceMutex == null)
    {
        var blockedMessage =
            $"Another Fire application instance is already running for configuration '{configurationName ?? "Fusion"}'.";
        Log.Warning("{Message} Lock: {LockName}", blockedMessage, lockName);

        if (!isService)
        {
            Console.WriteLine(blockedMessage);
        }

        Environment.ExitCode = 2;
        return;
    }

    Log.Information("Acquired single-instance lock {LockName} for configuration {ConfigurationName}.",
        lockName, configurationName ?? "Fusion");

    if (!isService)
    {
        // Enforce UTF-8 for console output and input
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        Console.InputEncoding = System.Text.Encoding.UTF8;

        ConsoleHelper.EnableRender();
        ConsoleHelper.SetConsoleWindowSize();
        ConsoleHelper.SetConsoleFont("Consolas", 16);
    }
    else
    {
        LogControl.ConsoleLevelSwitch.MinimumLevel = LogEventLevel.Information;
    }

    // Force secure TLS protocols and disable insecure TLS warnings
    System.Net.ServicePointManager.SecurityProtocol = System.Net.SecurityProtocolType.Tls12 | System.Net.SecurityProtocolType.Tls13;
    AppContext.SetSwitch("System.Net.Security.Tls.DisableInsecureTlsWarnings", true);

    Log.Information("Host starting...");

    // Build and configure the host
    HostApplicationBuilder builder = Host.CreateApplicationBuilder(appArgs);
    
    // Add support for both Windows Services and Linux systemd
    // These are safe to call on any platform; they only activate if the environment matches.
    builder.Services.AddWindowsService(options =>
    {
        var config = ConfigurationLoader.GetSpaceConfig();
        options.ServiceName = GetWindowsServiceName(args, config) ?? config?.CustomerName ?? "FortnaFire";
    });

    builder.Services.AddSystemd();

    if (!isService)
    {
        builder.Services.AddSingleton<ConsoleStatusMonitor>();
    }

    builder = builder.AddCoreServices(appArgs);
    builder.Logging.ClearProviders();
    using IHost host = builder.Build();

    // Check if we should run the interactive console
    if (!isService)
    {
        var config = ConfigurationLoader.GetSpaceConfig();
        bool showSplash = config?.ColorConsole ?? true;

        if (showSplash)
        {
            if (false)
                SplashScreenFusion.Print();
            else
                SplashScreenFire.Print();
        }

        var appLifetime = host.Services.GetRequiredService<IHostApplicationLifetime>();
        Console.CancelKeyPress += (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            Log.Information("Graceful shutdown requested via Ctrl+C.");
            appLifetime.StopApplication();
        };

        var consoleDisplay = host.Services.GetRequiredService<ConsoleStatusMonitor>();
        var messageBus = host.Services.GetRequiredService<IMessageBus>();
        consoleDisplay.Start(appLifetime.ApplicationStopping, showSplash ? SplashScreenFire.Length() : 0);

        await host.StartAsync();
        try
        {
            RunInteractiveConsole(consoleDisplay, messageBus, appLifetime, appLifetime.ApplicationStopping);
        }
        finally
        {
            await host.StopAsync();
        }
    }
    else
    {
        // Run as a normal service (non-interactive)
        await host.RunAsync();
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
    if (singleInstanceMutex != null)
    {
        try
        {
            singleInstanceMutex.ReleaseMutex();
        }
        catch (ApplicationException)
        {
            // The mutex may not be owned if startup failed while creating the guard.
        }
        finally
        {
            singleInstanceMutex.Dispose();
        }
    }

    Log.Information("Host stopped. Flushing logs...");
    await Log.CloseAndFlushAsync();
}

static Mutex? AcquireSingleInstanceLock(string? configurationName, out string lockName)
{
    var normalizedName = string.IsNullOrWhiteSpace(configurationName)
        ? "Fusion"
        : configurationName.Trim();

    lockName = BuildSingleInstanceLockName(normalizedName);
    var mutex = new Mutex(false, lockName);

    try
    {
        if (mutex.WaitOne(0))
        {
            return mutex;
        }
    }
    catch (AbandonedMutexException)
    {
        return mutex;
    }

    mutex.Dispose();
    return null;
}

static string BuildSingleInstanceLockName(string configurationName)
{
    var safeName = new StringBuilder(configurationName.Length);

    foreach (var ch in configurationName)
    {
        safeName.Append(char.IsLetterOrDigit(ch) ? char.ToUpperInvariant(ch) : '_');
    }

    if (safeName.Length == 0)
    {
        safeName.Append("FUSION");
    }

    var prefix = OperatingSystem.IsWindows() ? @"Global\" : string.Empty;
    return $"{prefix}FortnaFire.Configuration.{safeName}";
}

static bool IsServiceFlag(string arg)
{
    return string.Equals(arg, "--service", StringComparison.OrdinalIgnoreCase) ||
           string.Equals(arg, "-service", StringComparison.OrdinalIgnoreCase) ||
           string.Equals(arg, "/service", StringComparison.OrdinalIgnoreCase);
}

static bool IsServiceNameOption(string arg)
{
    return string.Equals(arg, "--service-name", StringComparison.OrdinalIgnoreCase) ||
           string.Equals(arg, "-service-name", StringComparison.OrdinalIgnoreCase) ||
           string.Equals(arg, "/service-name", StringComparison.OrdinalIgnoreCase);
}

static string[] StripFireRuntimeArgs(string[] args)
{
    var filtered = new List<string>(args.Length);

    for (int i = 0; i < args.Length; i++)
    {
        var arg = args[i];

        if (IsServiceFlag(arg))
        {
            continue;
        }

        if (IsServiceNameOption(arg))
        {
            i++;
            continue;
        }

        if (arg.StartsWith("--service-name=", StringComparison.OrdinalIgnoreCase) ||
            arg.StartsWith("-service-name=", StringComparison.OrdinalIgnoreCase) ||
            arg.StartsWith("/service-name=", StringComparison.OrdinalIgnoreCase))
        {
            continue;
        }

        filtered.Add(arg);
    }

    return filtered.ToArray();
}

static string? GetWindowsServiceName(string[] args, ISystemBlueprintTemplate? config)
{
    for (int i = 0; i < args.Length; i++)
    {
        var arg = args[i];

        if (IsServiceNameOption(arg))
        {
            return i + 1 < args.Length ? args[i + 1] : null;
        }

        const string longPrefix = "--service-name=";
        const string shortPrefix = "-service-name=";
        const string slashPrefix = "/service-name=";

        if (arg.StartsWith(longPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return arg[longPrefix.Length..];
        }

        if (arg.StartsWith(shortPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return arg[shortPrefix.Length..];
        }

        if (arg.StartsWith(slashPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return arg[slashPrefix.Length..];
        }
    }

    return string.IsNullOrWhiteSpace(config?.ServiceName) ? null : config.ServiceName;
}

static void RunInteractiveConsole(ConsoleStatusMonitor monitor, IMessageBus bus, IHostApplicationLifetime appLifetime, CancellationToken stoppingToken)
{
    var pluginDir = Path.Combine(AppContext.BaseDirectory, "plugins");
    string unlockSequence = "fortna";
    string currentInput = "";
    DateTime lastKeyPress = DateTime.MinValue;
    TimeSpan lockTimeout = TimeSpan.FromSeconds(30);
    object consoleLock = new object();
 
    while (!stoppingToken.IsCancellationRequested)
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
                WaitForAnyKey(stoppingToken);
                if (stoppingToken.IsCancellationRequested) break;
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
                    
                    // Force all elements and reactions to broadcast their status immediately
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

            if (key.Key == ConsoleKey.C)
            {
                if (!monitor.IsLocked)
                {
                    monitor.TogglePause();
                    int promptRow = Math.Min(Console.WindowHeight - 5, Console.BufferHeight - 5);
                    lock (consoleLock) // Using a local lock for manual console writes
                    {
                        Console.SetCursorPosition(0, promptRow);
                        Console.Write(new string(' ', Console.WindowWidth - 1));
                        Console.SetCursorPosition(0, promptRow + 1);
                        Console.Write(new string(' ', Console.WindowWidth - 1));
                        Console.SetCursorPosition(0, promptRow + 2);
                        Console.Write(new string(' ', Console.WindowWidth - 1));

                        Console.SetCursorPosition(0, promptRow);
                        Console.ForegroundColor = ConsoleColor.Cyan;
                        Console.Write("COMMAND MODE > ");
                        Console.ResetColor();
                    }

                    Console.CursorVisible = true;
                    string? input = ReadConsoleLine(stoppingToken);
                    Console.CursorVisible = false;
                    if (stoppingToken.IsCancellationRequested)
                    {
                        if (monitor.IsActive) monitor.TogglePause();
                        break;
                    }

                    if (!string.IsNullOrWhiteSpace(input))
                    {
                        ProcessConsoleCommand(input, bus, promptRow + 1);
                    }

                    Console.SetCursorPosition(0, promptRow + 3);
                    Console.WriteLine("Press any key to return to dashboard...".PadRight(Console.WindowWidth - 1));
                    WaitForAnyKey(stoppingToken);
                    if (stoppingToken.IsCancellationRequested)
                    {
                        if (monitor.IsActive) monitor.TogglePause();
                        break;
                    }

                    monitor.RequestRedraw();
                    monitor.TogglePause();
                }
                continue;
            }

            if (key.Key == ConsoleKey.U)
            {
                if (!monitor.IsLocked)
                {
                    monitor.TogglePause();
                    int outputRow = Math.Min(Console.WindowHeight - 8, Console.BufferHeight - 8);
                    var process = System.Diagnostics.Process.GetCurrentProcess();
                    var up = DateTime.Now - process.StartTime;
                    var ram = process.WorkingSet64 / 1024 / 1024;
                    string stats = $"--- SYSTEM STATUS ---\n" +
                                   $"Uptime:    {up.Days}d {up.Hours}h {up.Minutes}m {up.Seconds}s\n" +
                                   $"RAM Usage: {ram} MB\n" +
                                   $"Threads:   {process.Threads.Count}\n" +
                                   $"Start:     {process.StartTime:yyyy-MM-dd HH:mm:ss}\n" +
                                   $"---------------------";

                    lock (consoleLock)
                    {
                        var lines = stats.Split('\n');
                        foreach (var line in lines)
                        {
                            Console.SetCursorPosition(0, outputRow++);
                            Console.ForegroundColor = ConsoleColor.Yellow;
                            Console.WriteLine(line.PadRight(Console.WindowWidth - 1));
                        }
                        Console.ResetColor();
                        Console.WriteLine("Press any key to return...".PadRight(Console.WindowWidth - 1));
                    }
                    WaitForAnyKey(stoppingToken);
                    if (stoppingToken.IsCancellationRequested)
                    {
                        if (monitor.IsActive) monitor.TogglePause();
                        break;
                    }
                    monitor.RequestRedraw();
                    monitor.TogglePause();
                }
                continue;
            }

            if (key.Key == ConsoleKey.T)
            {
                var topic = Fusion.Common.MessageBusTopic.ConsoleCommand.ToString();
                _ = bus.PublishAsync(topic, new MessageEnvelope(Fusion.Common.MessageBusTopic.ConsoleCommand, "RELEASE_TOTE"));
                continue;
            }

            if (key.Key == ConsoleKey.H)
            {
                monitor.ToggleHeaders();
                continue;
            }

            if (key.Key == ConsoleKey.R)
            {
                monitor.RequestRedraw();
                continue;
            }

            if (key.Key == ConsoleKey.Q)
            {
                if (!monitor.IsLocked)
                {
                    Log.Information("Graceful shutdown requested via console.");
                    appLifetime.StopApplication();
                    break;
                }
                continue;
            }
        }

        if (stoppingToken.WaitHandle.WaitOne(100)) break;
    }

    Console.CursorVisible = true;
}

static bool WaitForAnyKey(CancellationToken stoppingToken)
{
    while (!stoppingToken.IsCancellationRequested)
    {
        if (Console.KeyAvailable)
        {
            Console.ReadKey(true);
            return true;
        }

        if (stoppingToken.WaitHandle.WaitOne(50)) break;
    }

    return false;
}

static string? ReadConsoleLine(CancellationToken stoppingToken)
{
    var input = new StringBuilder();

    while (!stoppingToken.IsCancellationRequested)
    {
        if (Console.KeyAvailable)
        {
            var key = Console.ReadKey(true);

            if (key.Key == ConsoleKey.Enter)
            {
                Console.WriteLine();
                return input.ToString();
            }

            if (key.Key == ConsoleKey.Escape)
            {
                Console.WriteLine();
                return null;
            }

            if (key.Key == ConsoleKey.Backspace)
            {
                if (input.Length > 0)
                {
                    input.Length--;
                    Console.Write("\b \b");
                }
                continue;
            }

            if (!char.IsControl(key.KeyChar))
            {
                input.Append(key.KeyChar);
                Console.Write(key.KeyChar);
            }
            continue;
        }

        if (stoppingToken.WaitHandle.WaitOne(50)) break;
    }

    return null;
}

static void ProcessConsoleCommand(string input, IMessageBus bus, int outputRow)
{
    var parts = input.Split(' ', StringSplitOptions.RemoveEmptyEntries);
    if (parts.Length == 0) return;

    string verb = parts[0].ToUpper();
    string result = "";

    try
    {
        switch (verb)
        {
            case "LOG":
                if (parts.Length >= 3)
                {
                    string devName = parts[1];
                    if (Enum.TryParse<LogEventLevel>(parts[2], true, out var level))
                    {
                        LogControl.SetElementLevel(devName, level);
                        result = $"SUCCESS: Log level for {devName} set to {level}";
                    }
                    else
                    {
                        result = $"ERROR: Invalid log level: {parts[2]}. Use Verbose, Debug, Information, Warning, Error";
                    }
                }
                else
                {
                    result = "USAGE: LOG [ElementName] [Level]";
                }
                break;

            case "RESTART":
            case "ONLINE":
            case "OFFLINE":
                if (parts.Length >= 2)
                {
                    string targetDev = parts[1].ToUpper();
                    var topic = $"SYS.CONTROL.{targetDev}";
                    var payload = new { Command = verb, Timestamp = DateTime.UtcNow };
                    _ = bus.PublishAsync(topic, new MessageEnvelope(new MessageBusTopic(topic), payload));
                    result = $"SUCCESS: Sent {verb} command for {targetDev}";
                }
                else
                {
                    result = $"USAGE: {verb} [ElementName]";
                }
                break;

            case "PUB":
                if (parts.Length >= 3)
                {
                    string topicStr = parts[1].ToUpper();
                    string payload = string.Join(" ", parts.Skip(2));
                    var topic = new MessageBusTopic(topicStr);
                    _ = bus.PublishAsync(topicStr, new MessageEnvelope(topic, payload));
                    result = $"SUCCESS: Published message to {topicStr}";
                }
                else
                {
                    result = "USAGE: PUB [Topic] [Payload]";
                }
                break;

            case "RELEASE_TOTE":
                var topicT = MessageBusTopic.ConsoleCommand.ToString();
                _ = bus.PublishAsync(topicT, new MessageEnvelope(MessageBusTopic.ConsoleCommand, "RELEASE_TOTE"));
                result = "SUCCESS: Sent RELEASE_TOTE command to bus";
                break;

            case "HELP":
                result = "COMMANDS: LOG, RESTART, ONLINE, OFFLINE, PUB, RELEASE_TOTE";
                break;

            default:
                result = $"ERROR: Unknown command '{verb}'. Type HELP for list.";
                break;
        }
    }
    catch (Exception ex)
    {
        result = $"FAULT: {ex.Message}";
    }

    var lines = result.Split('\n');
    foreach (var line in lines)
    {
        if (outputRow >= Console.BufferHeight) break;
        Console.SetCursorPosition(0, outputRow++);
        Console.ForegroundColor = result.StartsWith("SUCCESS") ? ConsoleColor.Green : (result.StartsWith("ERROR") || result.StartsWith("FAULT") ? ConsoleColor.Red : ConsoleColor.Yellow);
        Console.WriteLine(line.PadRight(Console.WindowWidth - 1));
    }
    Console.ResetColor();
}
