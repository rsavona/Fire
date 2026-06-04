using System.Reflection;
using System.Diagnostics;
using Fusion.Common.Contracts;
using Fusion.Common.Enums;
using Fusion.Common.Messaging;
using Microsoft.CodeAnalysis.CSharp.Scripting;
using Microsoft.CodeAnalysis.Scripting;
using Microsoft.Extensions.Hosting;
using Serilog; // Use Serilog specifically

namespace Fusion.Common.BaseClasses;

public abstract class ReactionBase : BackgroundService
{
    // --- Enums for Tracker ---
    public enum ReactionState
    {
        Initializing,
        Active,
        ActiveWithErrors,
        Faulted,
        Stopped
    }

    public enum ReactionEvent
    {
        Started,
        MessageProcessed,
        Error,
        Stopped
    }

    protected readonly IMessageBus MessageBus;
    protected readonly IReactionBlueprint Config;

    // Identity and Tracking
    protected readonly ElementKey ReactionKey;
    protected readonly StatusTracker<ReactionState, ReactionEvent> Tracker;

    // Serilog Logger
    protected readonly ILogger Logger;

    // Control State
    protected bool IsSystemStarted { get; private set; } = false;

    // Logic Cache
    private readonly Dictionary<BondKey, Func<MessageEnvelope, CancellationToken, Task<object?>>> _bondExecutors =
        new();


    /// <summary>
    /// Serves as the base class for reactions, providing common functionality and abstractions
    /// for managing reaction state, logging, messaging, and configuration.
    /// </summary>
    /// <remarks>
    /// This abstract class is intended to be extended by concrete reaction implementations.
    /// It initializes core dependencies such as message bus, logger, and status tracker,
    /// and it provides methods for executing the reaction logic and updating status.
    /// </remarks>
    protected ReactionBase(IMessageBus bus, IReactionBlueprint config, ILogger logger)
    {
        MessageBus = bus;
        Config = config;

        // Enrich the logger with the specific reaction name for better filtering in Rider/Logs
        Logger = logger.ForContext("ElementName", Config.Name);
        ReactionKey = new ElementKey("SYSTEM", Config.Name, Config.CoreName);
        Tracker = new StatusTracker<ReactionState, ReactionEvent>(ReactionState.Initializing,
            ReactionEvent.Started);

        Logger.Information("[{Reaction}] Reaction instance created.", ReactionKey.ElementName);
        
        // Subscribe to System Control messages to handle Startup Synchronization
        MessageBus.SubscribeAsync(MessageBusTopic.SystemControl.ToString(), HandleSystemControlMessageAsync);
    }

    private Task HandleSystemControlMessageAsync(MessageEnvelope? envelope, CancellationToken ct)
    {
        if (envelope?.Payload is SystemControlMessage sysMsg)
        {
            switch (sysMsg.Command)
            {
                case SystemCommand.Start:
                    IsSystemStarted = true;
                    Logger.Information("[{Reaction}] System START received. Resuming business logic.", Config.Name);
                    break;
                case SystemCommand.Stop:
                case SystemCommand.Pause:
                    IsSystemStarted = false;
                    Logger.Warning("[{Reaction}] System {Command} received. Business logic suspended.", Config.Name, sysMsg.Command);
                    break;
                case SystemCommand.RefreshStatus:
                    _ = PublishStatusAsync();
                    break;
            }
        }
        return Task.CompletedTask;
    }

    /// <summary>
    /// Executes the core reaction logic in this background service.
    /// </summary>
    /// <param name="stoppingToken">A <see cref="CancellationToken"/> that is triggered when the execution should stop.</param>
    /// <exception cref="OperationCanceledException">Thrown when the operation is canceled through the provided cancellation token.</exception>
    /// <exception cref="Exception">Thrown when an unexpected error occurs during the execution.</exception>
    /// <returns>A task representing the asynchronous execution operation.</returns>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Yield();
        Logger.Information("[{Reaction}] ExecuteAsync started.", "Base-ExecuteAsync");
        UpdateStatus(ReactionState.Initializing, ReactionEvent.Started, ElementHealth.Warning, "Reaction starting...");

        try
        {
            await InitializeBondsAsync();

            if (Tracker.State == ReactionState.Faulted)
            {
                UpdateStatus(ReactionState.Faulted, ReactionEvent.Error, ElementHealth.Critical);
                Logger.Fatal("[{Reaction}] Reaction entered Faulted state during initialization. Halting.",
                    "Base-ExecuteAsync");
                return;
            }

            UpdateStatus(ReactionState.Active, ReactionEvent.Started, ElementHealth.Normal, "Reaction Active.");
            Logger.Information("[{Reaction}] Reaction is now Running and Active.", "Base-ExecuteAsync");

            // Wait for shutdown signal
            await Task.Delay(Timeout.Infinite, stoppingToken);
        }
        catch (OperationCanceledException)
        {
            Logger.Warning("[{Reaction}] ExecuteAsync cancellation requested.", "Base-ExecuteAsync");
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "[{Reaction}] Fatal startup error: {Message}", "Base-ExecuteAsync", ex.Message);
            UpdateStatus(ReactionState.Faulted, ReactionEvent.Error, ElementHealth.Critical,
                $"Startup Failed: {ex.Message}");
        }
        finally
        {
            UpdateStatus(ReactionState.Stopped, ReactionEvent.Stopped, ElementHealth.Warning, "Reaction terminated.");
            Logger.Information("[{Reaction}] Reaction background service has exited.", "Base-ExecuteAsync");
        }
    }

    /// <summary>
    /// Initializes all configured bonds.
    /// </summary>
    /// <exception cref="InvalidOperationException"></exception>
    public async Task InitializeBondsAsync()
    {
        Logger.Debug("[{Reaction}] Initializing {Count} defined bonds.", "Base-InitializeBondsAsync",
            Config.Bonds.Count);
        int successfulBonds = 0;
        int disabledBonds = 0;

        foreach (var bond in Config.Bonds)
        {
            try
            {
                Func<MessageEnvelope, CancellationToken, Task<object?>> executor;

                Logger.Debug("[{Reaction}] Configuring bond: {Source} -> {Destination} (Mode: {Mode})",
                    "Base-InitializeBondsAsync", bond.Source, bond.Destination, bond.Mode);

                switch (bond.Mode)
                {
                    case 1: // Method (Reflection)
                        executor = CreateMethodDelegate(bond.Handler);
                        break;
                    case 2: // Script (Roslyn)
                        executor = await CreateScriptDelegateAsync(bond.Handler);
                        break;
                    case 0: // Disabled
                        Logger.Warning("[{Reaction}] Bond {Source} is DISABLED. Skipping.",
                            "Base-InitializeBondsAsync", bond.Source);
                        disabledBonds++;
                        continue;
                    default:
                        throw new InvalidOperationException($"Unknown bond mode: {bond.Mode}");
                }

                var key = new BondKey(bond.Source, bond.Destination);
                _bondExecutors[key] = executor;

                var topic = bond.Source.ToUpper();
                await MessageBus.SubscribeAsync(bond.Source, HandleIncomingMessageAsync);
                Logger.Information("[{Reaction}] {Bond} Subscribed to topic: {Source} Handler:{handler} dest: {dest}",
                    "Base-InitializeBondsAsync", bond.Name, bond.Source, bond.Handler, bond.Destination);

                Tracker.IncrementConnections();
                successfulBonds++;
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "[{Reaction}] Failed to initialize {bond} bond '{Source}' with handler '{Handler}'",
                    "Base-InitializeBondsAsync", bond.Name, bond.Source, bond.Handler);

                Tracker.IncrementError(ex.Message);
                UpdateStatus(ReactionState.Faulted, ReactionEvent.Error, ElementHealth.Critical,
                    $"Bond Setup Failed: {ex.Message}");
            }
        }


        if (successfulBonds + disabledBonds == Config.Bonds.Count)
        {
            Logger.Information("[{Reaction}] {successfulBonds} initialized bonds; {disabledBonds} disabled.",
                "Base-InitializeBondsAsync", successfulBonds, disabledBonds);
            UpdateStatus(ReactionState.Active, ReactionEvent.Started, ElementHealth.Normal, $"Bond Setup Complete");
        }
        else
        {
            Logger.Fatal("[{Reaction}] Not all bonds initialized. Successful: {successfulBonds} Check configuration ",
                "Base-InitializeBondsAsync", successfulBonds);
        }
    }

    /// <summary>
    /// Handles incoming messages from the message bus.
    /// </summary>
    /// <param name="message"></param>
    /// <param name="ct"></param>
    private async Task HandleIncomingMessageAsync(MessageEnvelope? message, CancellationToken ct)
    {
        await Task.Yield(); // Break synchronous recursion to prevent stack overflow

        if (message == null)
        {
            Logger.Warning("[{Reaction}] Received null message from bus.", "Base-HandleIncomingMessageAsync");
            Tracker.IncrementError("Received null message");
            return;
        }

        if (false) //!IsSystemStarted)
        {
            Logger.Warning("[{Reaction}] System not started. Dropping message from {Source}.", Config.Name, message.Destination);
            return;
        }

        long startMemory = GC.GetAllocatedBytesForCurrentThread();

        Tracker.IncrementInbound();
        // Trace for high-volume message monitoring
        Logger.Information("[{Reaction}] Message IN : {msg}",
            "Base-HandleIncomingMessageAsync", message);

        try
        {
            var matchingBonds = _bondExecutors
                .Where(kvp => string.Equals(
                    kvp.Key.Source,
                    message.Destination.ToString(),
                    StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (!matchingBonds.Any())
            {
                var logmsg = $"No bond found for topic: {message.Destination}";
                Tracker.IncrementError(logmsg);
                Logger.Warning("[{Reaction}] No bond found for topic: {Topic}", "Base-HandleIncomingMessageAsync",
                    message.Destination);
                return;
            }

            foreach (var rte in matchingBonds)
            {
                try
                {
                    Logger.Information("[{Reaction}] Executing {handler} for {Src} -> {Dest}",
                        "Base-HandleIncomingMessageAsync", rte.Value.ToString(), rte.Key.Source, rte.Key.Destination);
                    var tmr = Environment.TickCount64;
                    Tracker.StartTransaction(tmr);
                    var executor = rte.Value;
                 
                    var result = await executor(message, ct);
                    Tracker.StopTransaction(tmr);

                    if (result == null)
                    {
                        continue;
                    }

                    if (result is MessageEnvelope resultPayload && !string.IsNullOrEmpty(rte.Key.Destination))
                    {
                        Tracker.IncrementOutbound();
                        await MessageBus.PublishAsync(rte.Key.Destination, resultPayload, ct);
                        
                        // Publish Flow Event
                        _ = MessageBus.PublishAsync(MessageBusTopic.DataFlow.ToString(), new MessageEnvelope(MessageBusTopic.DataFlow, new FlowEvent { 
                            Source = new MessageBusTopic(rte.Key.Source).ElementName, 
                            Force = Config.Name, 
                            Destination = new MessageBusTopic(rte.Key.Destination).ElementName 
                        }));
                        Logger.Information("[{Reaction}] Message Out: {msg}  published to {Destination}",
                            "Base-HandleIncomingMessageAsync", resultPayload, rte.Key.Destination);
                    }
                    else if (result is IElementMessage elementMessage)
                    {
                        var env = elementMessage.WrapMessage(new MessageBusTopic(rte.Key.Destination));
                        Tracker.IncrementOutbound();
                        await MessageBus.PublishAsync(rte.Key.Destination, env, ct);
                        Logger.Information("[{Reaction}] Message Out: {msg}  published to {Destination}",
                            "Base-HandleIncomingMessageAsync", elementMessage, rte.Key.Destination);
                    }
                    else if (result is string payload)
                    {
                        if (!string.IsNullOrEmpty(rte.Key.Destination))
                        {
                            var env = new MessageEnvelope(new MessageBusTopic(rte.Key.Destination), payload, message.Gin, message.Client);
                            Tracker.IncrementOutbound();
                            await MessageBus.PublishAsync(rte.Key.Destination, env, ct);
                            Logger.Information("[{Reaction}] Message Out: {msg}  published to {Destination}",
                                "Base-HandleIncomingMessageAsync", payload, rte.Key.Destination);
                        }
                        else
                        {
                            Logger.Information("[{Reaction}] Received {msg}  No bond to forward ",
                                "Base-HandleIncomingMessageAsync", rte.Key.Source, rte.Key.Destination);
                        }
                    }
                    else
                    {
                        Logger.Warning("[{Reaction}] Received unknown result for {Src} -> {Dest}",
                            "Base-HandleIncomingMessageAsync", rte.Key.Source, rte.Key.Destination);
                        Tracker.IncrementError("Received unknown result");
                    }
                }
                catch (OperationCanceledException)
                {
                    Logger.Warning(" {Reaction} Operation Canceled ", "Base-HandleIncomingMessageAsync");
                }
                catch (Exception ex)
                {
                    Logger.Error(ex, "[{Reaction}] Execution failed: {Src} -> {Dest}",
                        "Base-HandleIncomingMessageAsync", rte.Key.Source, rte.Key.Destination);
                    Tracker.IncrementError(ex.Message);
                }
            }
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "[{Reaction}] Handler failure for {Element}", "Base-HandleIncomingMessageAsync");
            Tracker.IncrementError(ex.Message);
        }
        
       
        long endMemory = GC.GetAllocatedBytesForCurrentThread();
        long bytesUsed = endMemory - startMemory;

        _ = PublishStatusAsync();

        return;
    }

    /// <summary>
    /// Updates the reaction status with the current state, event, health, and an optional comment.
    /// </summary>
    /// <param name="state">The current state of the reaction.</param>
    /// <param name="evt">The event associated with the reaction update.</param>
    /// <param name="health">The health status of the element.</param>
    /// <param name="comment">Optional comment providing additional context for the status update.</param>
    protected void UpdateStatus(ReactionState state, ReactionEvent evt, ElementHealth health, string comment = "")
    {
        Tracker.Update(state, evt, health, comment);
        _ = PublishStatusAsync();
    }

    /// <summary>
    /// Publishes the current reaction status to the message bus.
    /// </summary>
    /// <returns>A task representing the asynchronous operation.</returns>
    /// <exception cref="Exception">Thrown when the status cannot be published.</exception>
    protected async Task PublishStatusAsync()
    {
        try
        {
            var snapshot = Tracker.ToStatusMessage(ReactionKey);
            await MessageBus.PublishStatusAsync(snapshot, CancellationToken.None);
        }
        catch (Exception ex)
        {
            Logger.Warning("[{Reaction}] Failed to publish status: {Msg}", "Base-UpdateStatus", ex.Message);
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
    /// Creates a delegate for executing a script file as a bond handler.
    /// </summary>
    /// <param name="filePath">The file path to the script file that defines the bond handler.</param>
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

        var script = CSharpScript.Create<object>(code, options, globalsType: typeof(SystemGlobals));
        var runner = script.CreateDelegate();

        return async (envelope, ct) =>
        {
            var globals = new SystemGlobals(envelope.Payload, envelope.Client, MessageBus,
                (s) => Logger.Information(s));
            return await runner(globals, ct);
        };
    }
}