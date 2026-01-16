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
    public enum WorkflowState { Initializing, Active, ActiveWithErrors, Faulted, Stopped }
    public enum WorkflowEvent { Started, MessageProcessed, Error, Stopped }

    protected readonly IMessageBus MessageBus;
    protected readonly WorkflowConfig Config;
    
    // Identity and Tracking
    protected readonly DeviceKey WorkflowKey;
    protected readonly DeviceStatusTracker<WorkflowState, WorkflowEvent> Tracker;
    
    // Serilog Logger
    protected readonly ILogger Logger;
  
    // Logic Cache
    private readonly Dictionary<RouteKey, Func<MessageEnvelope, CancellationToken, Task<object?>>> _routeExecutors = new();
    private readonly List<string> _subscribedSources = new (); 

    protected WorkflowBase(IMessageBus bus, WorkflowConfig config, ILogger logger)
    {
        MessageBus = bus;
        Config = config;
        
        // Enrich the logger with the specific workflow name for better filtering in Rider/Logs
        Logger = logger.ForContext("WorkflowName", Config.Name); 
        
        WorkflowKey = new DeviceKey("SYSTEM", Config.Name);
        Tracker = new DeviceStatusTracker<WorkflowState, WorkflowEvent>(WorkflowState.Initializing, WorkflowEvent.Started);

        Logger.Information("[{Workflow}] Workflow instance created.", WorkflowKey.DeviceName);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        Logger.Information("[{Workflow}] ExecuteAsync started.", WorkflowKey.DeviceName);
        UpdateStatus(WorkflowState.Initializing, WorkflowEvent.Started, DeviceHealth.Warning, "Workflow starting...");

        try 
        {
            await InitializeRoutesAsync(); 

            if (Tracker.State == WorkflowState.Faulted)
            {
                Logger.Fatal("[{Workflow}] Workflow entered Faulted state during initialization. Halting.", WorkflowKey.DeviceName);
                return;
            }
            
            UpdateStatus(WorkflowState.Active, WorkflowEvent.Started, DeviceHealth.Normal, "Workflow Active.");
            Logger.Information("[{Workflow}] Workflow is now Running and Active.", WorkflowKey.DeviceName);

            // Wait for shutdown signal
            await Task.Delay(Timeout.Infinite, stoppingToken);
        }
        catch (OperationCanceledException)
        {
            Logger.Warning("[{Workflow}] ExecuteAsync cancellation requested.", WorkflowKey.DeviceName);
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "[{Workflow}] Fatal startup error: {Message}", WorkflowKey.DeviceName, ex.Message);
            UpdateStatus(WorkflowState.Faulted, WorkflowEvent.Error, DeviceHealth.Critical, $"Startup Failed: {ex.Message}");
        }
        finally
        {
            UpdateStatus(WorkflowState.Stopped, WorkflowEvent.Stopped, DeviceHealth.Warning, "Workflow terminated.");
            Logger.Information("[{Workflow}] Workflow background service has exited.", WorkflowKey.DeviceName);
        }
    }
    
    public async Task InitializeRoutesAsync()
    {
        Logger.Debug("[{Workflow}] Initializing {Count} defined routes.", WorkflowKey.DeviceName, Config.Routes.Count);
        int successfulRoutes = 0;
        
        foreach (var route in Config.Routes)
        {
            try
            {
                Func<MessageEnvelope, CancellationToken, Task<object?>> executor;

                Logger.Debug("[{Workflow}] Configuring route: {Source} -> {Destination} (Mode: {Mode})", 
                    WorkflowKey.DeviceName, route.Source, route.Destination, route.Mode);

                switch (route.Mode)
                {
                    case 1: // Method (Reflection)
                        executor = CreateMethodDelegate(route.Handler);
                        break;
                    case 2: // Script (Roslyn)
                        executor = await CreateScriptDelegateAsync(route.Handler);
                        break;
                    case 0: // Disabled
                        Logger.Warning("[{Workflow}] Route {Source} is DISABLED. Skipping.", WorkflowKey.DeviceName, route.Source);
                        continue; 
                    default:
                        throw new InvalidOperationException($"Unknown route mode: {route.Mode}");
                }
                
                var key = new RouteKey(route.Source, route.Destination);
                _routeExecutors[key] = executor;

                if (!_subscribedSources.Contains(route.Source))
                {
                    _subscribedSources.Add(route.Source);
                    await MessageBus.SubscribeAsync(route.Source, HandleIncomingMessageAsync);
                    Logger.Information("[{Workflow}] Subscribed to topic: {Source}", WorkflowKey.DeviceName, route.Source);
                }
                successfulRoutes++;
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "[{Workflow}] Failed to initialize route '{Source}' with handler '{Handler}'", 
                    WorkflowKey.DeviceName, route.Source, route.Handler);
                
                Tracker.IncrementError(ex.Message);
                UpdateStatus(WorkflowState.Faulted, WorkflowEvent.Error, DeviceHealth.Critical, $"Route Setup Failed: {ex.Message}");
            }
        }

        if (successfulRoutes == Config.Routes.Count )
        {
             Logger.Information( "[{Workflow}] All routes Initialized", 
                    WorkflowKey.DeviceName);
             UpdateStatus(WorkflowState.Active, WorkflowEvent.Started, DeviceHealth.Normal, $"Route Setup Complete");
        }
        else
        {
             Logger.Fatal("[{Workflow}] Some routes could be initialized. Workflow is effectively dead.", WorkflowKey.DeviceName);
        }
    }

    private async Task HandleIncomingMessageAsync(MessageEnvelope? message, CancellationToken ct)
    {
        if (message == null)
        {
            Logger.Warning("[{Workflow}] Received null message from bus.", WorkflowKey.DeviceName);
            return;
        }

        // Trace for high-volume message monitoring
        Logger.Verbose("[{Workflow}] Inbound: Source={Source}, Dest={Topic}", 
            WorkflowKey.DeviceName, message.Client, message.Destination); 

        try
        {
            Tracker.IncrementInbound();
             _ = PublishStatusAsync();

             var matchingRoutes = _routeExecutors
                 .Where(kvp => kvp.Key.Source == message.Destination.ToString())
                 .ToList();

             if (!matchingRoutes.Any())
             {
                 Logger.Debug("[{Workflow}] No route found for topic: {Topic}", WorkflowKey.DeviceName, message.Destination);
                 return;
             }

             foreach (var rte in matchingRoutes)
             {
                try
                {
                    Logger.Verbose("[{Workflow}] Executing handler for {Src} -> {Dest}", 
                        WorkflowKey.DeviceName, rte.Key.Source, rte.Key.Destination);

                    var executor = rte.Value;
                    var result = await executor(message, ct);

                    if (result is MessageEnvelope resultPayload && !string.IsNullOrEmpty(rte.Key.Destination))
                    {
                        await MessageBus.PublishAsync(rte.Key.Destination, resultPayload, ct);
                        Tracker.IncrementOutbound(); 
                         _ = PublishStatusAsync();
                        Logger.Debug("[{Workflow}] Result published to {Destination}", WorkflowKey.DeviceName, rte.Key.Destination);
                    }
                }
                catch (OperationCanceledException) { }
                catch (Exception ex)
                {
                    Logger.Error(ex, "[{Workflow}] Execution failed: {Src} -> {Dest}", 
                        WorkflowKey.DeviceName, rte.Key.Source, rte.Key.Destination);
                    
                    UpdateStatus(WorkflowState.ActiveWithErrors, WorkflowEvent.Error, DeviceHealth.Error, ex.Message);
                }
            }
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "[{Workflow}] Handler failure for {Device}", WorkflowKey.DeviceName);
            Tracker.IncrementError(ex.Message);
        }
    }

    protected void UpdateStatus(WorkflowState state, WorkflowEvent evt, DeviceHealth health, string comment)
    {
        Tracker.Update(state, evt, health, -1, comment);
        _ = PublishStatusAsync();
    }

    private async Task PublishStatusAsync()
    {
        try
        {
            var snapshot = Tracker.ToStatusMessage(WorkflowKey);
            await MessageBus.PublishStatusAsync(WorkflowKey.DeviceName, snapshot);
        }
        catch(Exception ex)
        {
             Logger.Warning("[{Workflow}] Failed to publish status: {Msg}", WorkflowKey.DeviceName, ex.Message);
        }
    }

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

    protected async Task<Func<MessageEnvelope, CancellationToken, Task<object?>>> CreateScriptDelegateAsync(string filePath)
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
            var globals = new ScriptGlobals(envelope.Payload, envelope.Client, MessageBus, (s) => Logger.Information(s));
            return await runner(globals, ct); 
        };
    }
}