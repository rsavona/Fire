using System.Reflection;
using DeviceSpace.Common.Configurations;
using DeviceSpace.Common.Contracts;
using DeviceSpace.Common.Enums;
using Microsoft.CodeAnalysis.CSharp.Scripting;
using Microsoft.CodeAnalysis.Scripting;
using Microsoft.Extensions.Hosting;
using Serilog; // Use Serilog specifically

namespace DeviceSpace.Common.BaseClasses;

public abstract class WorkflowBase : BackgroundService
{
    // --- Enums for Tracker ---
    public enum WorkflowState
    {
        Initializing,
        Active,
        ActiveWithErrors,
        Faulted,
        Stopped
    }

    public enum WorkflowEvent
    {
        Started,
        MessageProcessed,
        Error,
        Stopped
    }

    protected readonly IMessageBus MessageBus;
    protected readonly WorkflowConfig Config;

    // Identity and Tracking
    protected readonly DeviceKey WorkflowKey;
    protected readonly DeviceStatusTracker<WorkflowState, WorkflowEvent> Tracker;

    // Serilog Logger
    protected readonly ILogger Logger;

    // Logic Cache
    private readonly Dictionary<RouteKey, Func<MessageEnvelope, CancellationToken, Task<object?>>> _routeExecutors =
        new();


    /// <summary>
    /// Serves as the base class for workflows, providing common functionality and abstractions
    /// for managing workflow state, logging, messaging, and configuration.
    /// </summary>
    /// <remarks>
    /// This abstract class is intended to be extended by concrete workflow implementations.
    /// It initializes core dependencies such as message bus, logger, and status tracker,
    /// and it provides methods for executing the workflow logic and updating status.
    /// </remarks>
    protected WorkflowBase(IMessageBus bus, WorkflowConfig config, ILogger logger)
    {
        MessageBus = bus;
        Config = config;

        // Enrich the logger with the specific workflow name for better filtering in Rider/Logs
        Logger = logger.ForContext("DeviceName", Config.Name);
        WorkflowKey = new DeviceKey("SYSTEM", Config.Name);
        Tracker = new DeviceStatusTracker<WorkflowState, WorkflowEvent>(WorkflowState.Initializing,
            WorkflowEvent.Started);

        Logger.Information("[{Workflow}] Workflow instance created.", WorkflowKey.DeviceName);
    }

    /// <summary>
    /// Executes the core workflow logic in this background service.
    /// </summary>
    /// <param name="stoppingToken">A <see cref="CancellationToken"/> that is triggered when the execution should stop.</param>
    /// <exception cref="OperationCanceledException">Thrown when the operation is canceled through the provided cancellation token.</exception>
    /// <exception cref="Exception">Thrown when an unexpected error occurs during the execution.</exception>
    /// <returns>A task representing the asynchronous execution operation.</returns>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        Logger.Information("[{Workflow}] ExecuteAsync started.", "Base-ExecuteAsync");
        UpdateStatus(WorkflowState.Initializing, WorkflowEvent.Started, DeviceHealth.Warning, "Workflow starting...");

        try
        {
            await InitializeRoutesAsync();

            if (Tracker.State == WorkflowState.Faulted)
            {
                UpdateStatus(WorkflowState.Faulted, WorkflowEvent.Error, DeviceHealth.Critical);
                Logger.Fatal("[{Workflow}] Workflow entered Faulted state during initialization. Halting.",
                    "Base-ExecuteAsync");
                return;
            }

            UpdateStatus(WorkflowState.Active, WorkflowEvent.Started, DeviceHealth.Normal, "Workflow Active.");
            Logger.Information("[{Workflow}] Workflow is now Running and Active.", "Base-ExecuteAsync");

            // Wait for shutdown signal
            await Task.Delay(Timeout.Infinite, stoppingToken);
        }
        catch (OperationCanceledException)
        {
            Logger.Warning("[{Workflow}] ExecuteAsync cancellation requested.", "Base-ExecuteAsync");
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "[{Workflow}] Fatal startup error: {Message}", "Base-ExecuteAsync", ex.Message);
            UpdateStatus(WorkflowState.Faulted, WorkflowEvent.Error, DeviceHealth.Critical,
                $"Startup Failed: {ex.Message}");
        }
        finally
        {
            UpdateStatus(WorkflowState.Stopped, WorkflowEvent.Stopped, DeviceHealth.Warning, "Workflow terminated.");
            Logger.Information("[{Workflow}] Workflow background service has exited.", "Base-ExecuteAsync");
        }
    }

    /// <summary>
    /// Initializes all configured routes.
    /// </summary>
    /// <exception cref="InvalidOperationException"></exception>
    public async Task InitializeRoutesAsync()
    {
        Logger.Debug("[{Workflow}] Initializing {Count} defined routes.", "Base-InitializeRoutesAsync",
            Config.Routes.Count);
        int successfulRoutes = 0;
        int disabledRoutes = 0;

        foreach (var route in Config.Routes)
        {
            try
            {
                Func<MessageEnvelope, CancellationToken, Task<object?>> executor;

                Logger.Debug("[{Workflow}] Configuring route: {Source} -> {Destination} (Mode: {Mode})",
                    "Base-InitializeRoutesAsync", route.Source, route.Destination, route.Mode);

                switch (route.Mode)
                {
                    case 1: // Method (Reflection)
                        executor = CreateMethodDelegate(route.Handler);
                        break;
                    case 2: // Script (Roslyn)
                        executor = await CreateScriptDelegateAsync(route.Handler);
                        break;
                    case 0: // Disabled
                        Logger.Warning("[{Workflow}] Route {Source} is DISABLED. Skipping.",
                            "Base-InitializeRoutesAsync", route.Source);
                        disabledRoutes++;
                        continue;
                    default:
                        throw new InvalidOperationException($"Unknown route mode: {route.Mode}");
                }

                var key = new RouteKey(route.Source, route.Destination);
                _routeExecutors[key] = executor;

                var topic = route.Source.ToUpper();
                await MessageBus.SubscribeAsync(route.Source, HandleIncomingMessageAsync);
                Logger.Information("[{Workflow}] {Route} Subscribed to topic: {Source} Handler: HandleIncomingMessageAsync",
                    "Base-InitializeRoutesAsync", route.Name, route.Source);

                successfulRoutes++;
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "[{Workflow}] Failed to initialize {route} route '{Source}' with handler '{Handler}'",
                    "Base-InitializeRoutesAsync", route.Name, route.Source, route.Handler);

                Tracker.IncrementError(ex.Message);
                UpdateStatus(WorkflowState.Faulted, WorkflowEvent.Error, DeviceHealth.Critical,
                    $"Route Setup Failed: {ex.Message}");
            }
        }


        if (successfulRoutes + disabledRoutes == Config.Routes.Count)
        {
            Logger.Information("[{Workflow}] {successfulRoutes} initialized routes; {disabledRoutes} disabled.",
                "Base-InitializeRoutesAsync", successfulRoutes, disabledRoutes);
            UpdateStatus(WorkflowState.Active, WorkflowEvent.Started, DeviceHealth.Normal, $"Route Setup Complete");
        }
        else
        {
            Logger.Fatal("[{Workflow}] Not all routes initialized. Successful: {successfulRoutes} Check configuration ",
                "Base-InitializeRoutesAsync", successfulRoutes);
        }
    }

    /// <summary>
    /// Handles incoming messages from the message bus.
    /// </summary>
    /// <param name="message"></param>
    /// <param name="ct"></param>
    private async Task HandleIncomingMessageAsync(MessageEnvelope? message, CancellationToken ct)
    {
        if (message == null)
        {
            Logger.Warning("[{Workflow}] Received null message from bus.", "Base-HandleIncomingMessageAsync");
            Tracker.IncrementError("Received null message");
            return;
        }

        Tracker.IncrementInbound();
        // Trace for high-volume message monitoring
        Logger.Information("[{Workflow}] Message IN : {msg}",
            "Base-HandleIncomingMessageAsync", message);

        try
        {
            var matchingRoutes = _routeExecutors
                .Where(kvp => string.Equals(
                    kvp.Key.Source,
                    message.Destination.ToString(),
                    StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (!matchingRoutes.Any())
            {
                var logmsg = $"No route found for topic: {message.Destination}";
                Tracker.IncrementError(logmsg);
                Logger.Warning("[{Workflow}] No route found for topic: {Topic}", "Base-HandleIncomingMessageAsync",
                    message.Destination);
                return;
            }

            foreach (var rte in matchingRoutes)
            {
                try
                {
                    Logger.Information("[{Workflow}] Executing {handler} for {Src} -> {Dest}",
                        "Base-HandleIncomingMessageAsync", rte.Value.ToString(), rte.Key.Source, rte.Key.Destination);

                    var executor = rte.Value;
                    var result = await executor(message, ct);

                    if (result is MessageEnvelope resultPayload && !string.IsNullOrEmpty(rte.Key.Destination))
                    {
                        Tracker.IncrementOutbound();
                        _ = MessageBus.PublishAsync(rte.Key.Destination, resultPayload, ct);
                        Logger.Information("[{Workflow}] Message Out: {msg}  published to {Destination}",
                            "Base-HandleIncomingMessageAsync", resultPayload, rte.Key.Destination);
                    }
                    else if (result is IDeviceMessage deviceMessage)
                    {
                        var env = deviceMessage.WrapMessage(new MessageBusTopic(rte.Key.Destination));
                        Tracker.IncrementOutbound();
                        _ = MessageBus.PublishAsync(rte.Key.Destination, env, ct);
                        Logger.Information("[{Workflow}] Message Out: {msg}  published to {Destination}",
                            "Base-HandleIncomingMessageAsync", deviceMessage, rte.Key.Destination);
                    }
                    else if (result is string payload && !string.IsNullOrEmpty(rte.Key.Destination))
                    {
                        var env = new MessageEnvelope(new MessageBusTopic(rte.Key.Destination), payload);
                        Tracker.IncrementOutbound();
                        _ = MessageBus.PublishAsync(rte.Key.Destination, env, ct);
                        Logger.Information("[{Workflow}] Message Out: {msg}  published to {Destination}",
                            "Base-HandleIncomingMessageAsync", payload, rte.Key.Destination);
                    }
                    else
                    {
                        Logger.Warning("[{Workflow}] Received unknown result for {Src} -> {Dest}",
                            "Base-HandleIncomingMessageAsync", rte.Key.Source, rte.Key.Destination);
                        Tracker.IncrementError("Received unknown result");
                    }
                }
                catch (OperationCanceledException)
                {
                    Logger.Warning(" {Workflow} Operation Canceled ", "Base-HandleIncomingMessageAsync");
                }
                catch (Exception ex)
                {
                    Logger.Error(ex, "[{Workflow}] Execution failed: {Src} -> {Dest}",
                        "Base-HandleIncomingMessageAsync", rte.Key.Source, rte.Key.Destination);
                    Tracker.IncrementError(ex.Message);
                }
            }
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "[{Workflow}] Handler failure for {Device}", "Base-HandleIncomingMessageAsync");
            Tracker.IncrementError(ex.Message);
        }

        _ = PublishStatusAsync();
        return;
    }

    /// <summary>
    /// Updates the workflow status with the current state, event, health, and an optional comment.
    /// </summary>
    /// <param name="state">The current state of the workflow.</param>
    /// <param name="evt">The event associated with the workflow update.</param>
    /// <param name="health">The health status of the device.</param>
    /// <param name="comment">Optional comment providing additional context for the status update.</param>
    protected void UpdateStatus(WorkflowState state, WorkflowEvent evt, DeviceHealth health, string comment = "")
    {
        Tracker.Update(state, evt, health, comment);
        _ = PublishStatusAsync();
    }

    /// <summary>
    /// Publishes the current workflow status to the message bus.
    /// </summary>
    /// <returns>A task representing the asynchronous operation.</returns>
    /// <exception cref="Exception">Thrown when the status cannot be published.</exception>
    protected async Task PublishStatusAsync()
    {
        try
        {
            var snapshot = Tracker.ToStatusMessage(WorkflowKey);
            await MessageBus.PublishStatusAsync(snapshot, CancellationToken.None);
        }
        catch (Exception ex)
        {
            Logger.Warning("[{Workflow}] Failed to publish status: {Msg}", "Base-UpdateStatus", ex.Message);
        }
    }

    /// <summary>
    /// Creates a delegate that points to a specified method within the current class.
    /// </summary>
    /// <param name="methodName">The name of the method to create the delegate for.
    /// The method should have a signature matching <see>
    ///     <cref>Func{MessageEnvelope, CancellationToken, Task{object?}}</cref>
    /// </see>
    /// .</param>
    /// <returns>
    /// A delegate of type <see>
    ///     <cref>Func{MessageEnvelope, CancellationToken, Task{object?}}</cref>
    /// </see>
    /// that can be used to invoke the specified method.
    /// </returns>
    /// <exception cref="MissingMethodException">
    /// Thrown if the specified method name does not exist in the current class or
    /// its accessibility is not compatible with the required delegate signature.
    /// </exception>
    protected Func<MessageEnvelope, CancellationToken, Task<object?>> CreateMethodDelegate(string methodName)
    {
        var methodInfo = this.GetType().GetMethod(
            methodName,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            null,
            new[] { typeof(MessageEnvelope), typeof(CancellationToken) },
            null);

        if (methodInfo == null)
        {
            throw new MissingMethodException($"Method '{methodName}' not found in '{this.GetType().Name}'");
        }

        return (Func<MessageEnvelope, CancellationToken, Task<object?>>)Delegate.CreateDelegate(
            typeof(Func<MessageEnvelope, CancellationToken, Task<object?>>), this, methodInfo);
    }

    /// <summary>
    /// Creates a delegate for executing a script file as a route handler.
    /// </summary>
    /// <param name="filePath">The file path to the script file that defines the route handler.</param>
    /// <returns>A delegate that can execute the script handler with the provided message envelope and cancellation token.</returns>
    /// <exception cref="FileNotFoundException">Thrown if the script file specified by <paramref name="filePath"/> cannot be found.</exception>
    protected async Task<Func<MessageEnvelope, CancellationToken, Task<object?>>> CreateScriptDelegateAsync(
        string filePath)
    {
        string fullPath = Path.GetFullPath(filePath);
        if (!File.Exists(fullPath)) throw new FileNotFoundException(fullPath);

        string code = await File.ReadAllTextAsync(fullPath);

        var options = ScriptOptions.Default
            .AddReferences(typeof(Console).Assembly)
            .AddReferences(typeof(IMessageBus).Assembly)
            .WithImports("System", "System.Linq");

        var script = CSharpScript.Create<object>(code, options, globalsType: typeof(ScriptGlobals));
        var runner = script.CreateDelegate();

        return async (envelope, ct) =>
        {
            var globals = new ScriptGlobals(envelope.Payload, envelope.Client, MessageBus,
                (s) => Logger.Information(s));
            return await runner(globals, ct);
        };
    }
}