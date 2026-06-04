using System.Collections.Concurrent;
using System.Text;
using Fusion.Common;
using Fusion.Common.BaseClasses;
using Fusion.Common.Contracts;
using Fusion.Common.Enums;
using Serilog.Core;

namespace Fusion.Element.Support.CLI;

public class SimulationValidationElement : ElementBase<SimulationValidationElement.State, SimulationValidationElement.Event, ElementMetric>
{
    public enum State { Idle, Monitoring, Validating, Finished }
    public enum Event { Start, Stop, Validate, Reset }

    private readonly ConcurrentDictionary<string, IElementStatus> _latestStatuses = new();
    private readonly ConcurrentDictionary<string, int> _initialResourceCounts = new();

    public SimulationValidationElement(IMessageBus bus, IElementBlueprint config, IFireLogger logger, LoggingLevelSwitch ls)
        : base(bus, config, logger, ls, State.Idle, Event.Start)
    {
        SimulationCoordinator.PhaseChanged += OnSimulationPhaseChanged;
        MessageBus.SubscribeAsync(MessageBusTopic.ElementStatus.ToString(), HandleStatusUpdateAsync);
        
        ConfigureStateMachine();
    }

    protected override void ConfigureStateMachine()
    {
        Machine.Configure(State.Idle)
            .Permit(Event.Start, State.Monitoring);

        Machine.Configure(State.Monitoring)
            .OnEntry(() => Logger.Information("[{Dev}] Simulation monitoring started.", Config.Name))
            .Permit(Event.Validate, State.Validating)
            .Permit(Event.Stop, State.Idle);

        Machine.Configure(State.Validating)
            .OnEntry(PerformValidation)
            .Permit(Event.Reset, State.Idle);
    }

    private Task HandleStatusUpdateAsync(MessageEnvelope? envelope, CancellationToken ct)
    {
        if (envelope?.Payload is IElementStatus status)
        {
            string name = status.ElementId.ElementName;
            _latestStatuses[name] = status;

            // Capture baseline resources if this is the first time we see this element
            if (SimulationCoordinator.CurrentPhase == SimulationPhase.SteadyState)
            {
                _initialResourceCounts.TryAdd(name, status.ResourceDeepCount);
            }
        }
        return Task.CompletedTask;
    }

    private void OnSimulationPhaseChanged(SimulationPhase phase)
    {
        if (phase == SimulationPhase.Completed)
        {
            Machine.Fire(Event.Validate);
        }
    }

    private void PerformValidation()
    {
        Logger.Information("[{Dev}] === SIMULATION VALIDATION RESULTS ===", Config.Name);

        var report = new StringBuilder();
        int totalErrors = 0;
        int leakCount = 0;

        foreach (var kvp in _latestStatuses)
        {
            var name = kvp.Key;
            var status = kvp.Value;

            // 1. Check Metrics
            if (status.CountError > 0)
            {
                totalErrors += status.CountError;
                Logger.Warning("[{Dev}] Element {Name} reported {Count} errors.", Config.Name, name, status.CountError);
            }

            // 2. Check for Memory Leaks (ResourceDeepCount)
            if (_initialResourceCounts.TryGetValue(name, out int initialCount))
            {
                int currentCount = status.ResourceDeepCount;
                int growth = currentCount - initialCount;

                if (growth > 0)
                {
                    leakCount++;
                    Logger.Error("[{Dev}] POTENTIAL LEAK DETECTED in {Name}: Growth of {Growth} items ({Initial} -> {Current})",
                        Config.Name, name, growth, initialCount, currentCount);
                }
            }

            // 3. Summarize key metrics
            Logger.Information("[{Dev}] {Name,-15} | IN: {In,4} | OUT: {Out,4} | ERR: {Err,2} | RES: {Res,5}",
                Config.Name, name, status.CountInbound, status.CountOutbound, status.CountError, status.ResourceDeepCount);
        }

        Logger.Information("[{Dev}] Validation Summary: {Errors} Total Errors, {Leaks} Potential Leaks Found.",
            Config.Name, totalErrors, leakCount);
        
        Logger.Information("[{Dev}] === END OF VALIDATION ===", Config.Name);
    }

    public override Task StartAsync(CancellationToken token)
    {
        Machine.Fire(Event.Start);
        return Task.CompletedTask;
    }

    public override Task StopAsync(CancellationToken token)
    {
        Machine.Fire(Event.Stop);
        return Task.CompletedTask;
    }

    protected override ElementHealth MapStateToHealth(State state) => ElementHealth.Normal;
}
