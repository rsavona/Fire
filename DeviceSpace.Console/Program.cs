using DeviceSpaceConsole;
using DeviceSpace.Core;
using Microsoft.Extensions.DependencyInjection; // Your namespace
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

try
{
    // Enforce UTF-8 for console output and input (e.g., for heart symbols, emojis, and special characters)
    Console.OutputEncoding = System.Text.Encoding.UTF8;
    Console.InputEncoding = System.Text.Encoding.UTF8;

    //   Build and configure the host adding the Device CoreServices
    HostApplicationBuilder builder = Host.CreateApplicationBuilder(args);
    if (Environment.UserInteractive && !args.Contains("--service"))
        builder.Services.AddSingleton<ConsoleStatusMonitor>();
    else
    {
        builder.Services.AddWindowsService(options =>
        {
            options.ServiceName = "FortnaFire"; // Set your service name
        });
    }

    builder = builder.AddCoreServices(args);
    builder.Logging.ClearProviders();
    IHost host = builder.Build();


    // Check if we should run the interactive console
    if (Environment.UserInteractive && !args.Contains("--service"))
    {
        int result = new Random().Next(1, 3);
        if (false)
            SplashScreenFusion.Print();
        else
            SplashScreenFire.Print();
        // Run the host in the background

        var appLifetime = host.Services.GetRequiredService<IHostApplicationLifetime>();
        var consoleDisplay = host.Services.GetRequiredService<ConsoleStatusMonitor>();
        consoleDisplay.Start(appLifetime.ApplicationStopping, SplashScreenFire.Length());

        var hostTask = host.RunAsync();
        // Run the interactive console loop on the main thread
        RunInteractiveConsole();

        // When the console closes, the app will exit.
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
    // This block will now catch the hidden exception.
    Console.ForegroundColor = ConsoleColor.Red;
    Console.WriteLine($"Host failed to start: {ex.Message}");
    Console.ResetColor();
    Console.WriteLine(ex.ToString()); // Print the full details
    await Task.Delay(30000);
}

static void RunInteractiveConsole()
{
    var pluginDir = Path.Combine(AppContext.BaseDirectory, "plugins");
 
    while (true)
    {
        var input = Console.ReadLine();
        if (string.IsNullOrWhiteSpace(input)) continue;

        // When you drag-drop a file, it often includes quotes, so we trim them.
        var filePath = input.Trim('"');

        if (File.Exists(filePath) && Path.GetExtension(filePath).Equals(".dll", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var destFile = Path.Combine(pluginDir, Path.GetFileName(filePath));
                File.Copy(filePath, destFile, true); // Overwrite if it exists
                Console.WriteLine("Successfully copied plugin: {0}", Path.GetFileName(filePath));
            }
            catch (Exception ex)
            {
                Console.WriteLine("Failed to copy plugin file. {0}", ex.Message);
            }
        }
        else
        {
            Console.WriteLine("Input is not a valid file path: {0}", input);
        }

        return;
    }
}