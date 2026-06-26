using System.Collections.Concurrent;
using System.Text;
using Fusion.Common;
using Fusion.Common.BaseClasses;
using Fusion.Common.Configurations;
using Fusion.Common.Contracts;
using Fusion.Common.Enums;
using Fusion.Common.Messaging;
using Serilog;
using Serilog.Core;

namespace Fusion.Element.Support.CLI;

// Simple ConcurrentHashSet helper
public class ConcurrentHashSet<T> where T : notnull
{
    private readonly ConcurrentDictionary<T, byte> _dict = new();
    public bool Add(T item) => _dict.TryAdd(item, 0);
    public bool Contains(T item) => _dict.ContainsKey(item);
}

public class BlueprintVerifierElement : ElementBase<BlueprintVerifierElement.State, BlueprintVerifierElement.Event, BlueprintVerifierElement.VerifierMetric>
{
    public enum State { Idle, Monitoring, BugDetected }
    public enum Event { Start, Stop, ErrorFound, Reset }
    public enum VerifierMetric { BugsFound, Timeouts, InflightMessages }

    private readonly string _logPath;
    private readonly int _timeoutMs;
    private readonly ConcurrentDictionary<string, DateTime> _inflightMessages = new();
    private readonly ConcurrentHashSet<string> _executedBonds = new();
    private int _bugCount = 0;
    private DateTime _startTime;
    private CancellationTokenSource? _monitorCts;

    private static string DefaultLogPath =>
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "logs", "fusion-bugs.md"));

    public BlueprintVerifierElement(IMessageBus bus, IElementBlueprint config, IFireLogger logger) 
        : base(bus, config, logger, new LoggingLevelSwitch(), State.Idle, Event.Start)
    {
        _logPath = ConfigurationLoader.GetOptionalConfig(config.Properties, "LogPath", DefaultLogPath);
        _timeoutMs = ConfigurationLoader.GetOptionalConfig(config.Properties, "TimeoutMs", 5000);
        _startTime = DateTime.Now;
    }

    protected override void ConfigureStateMachine()
    {
        Machine.Configure(State.Idle)
            .Permit(Event.Start, State.Monitoring);

        Machine.Configure(State.Monitoring)
            .Permit(Event.ErrorFound, State.BugDetected)
            .Permit(Event.Stop, State.Idle);

        Machine.Configure(State.BugDetected)
            .Permit(Event.Reset, State.Monitoring)
            .Permit(Event.Stop, State.Idle);
    }

    public override async Task StartAsync(CancellationToken token)
    {
        _monitorCts = CancellationTokenSource.CreateLinkedTokenSource(token);
        _startTime = DateTime.Now;
        
        Machine.Fire(Event.Start);
        UpdateStatus(State.Monitoring, Event.Start, ElementHealth.Normal, "Verifier Active");

        // 1. Monitor Element Health
        await MessageBus.SubscribeAsync(MessageBusTopic.ElementStatus.ToString(), async (envelope, ct) =>
        {
            if (envelope.Payload is IElementStatus msg && msg.Health is ElementHealth.Warning or ElementHealth.Critical)
            {
                await LogBugAsync("Health Degradation", $"Element '{msg.ElementId.ElementName}' reported {msg.Health} state. Comment: {msg.Comment}");
            }
        });

        // 2. Monitor Data Flow & Timeouts
        await MessageBus.SubscribeAsync(MessageBusTopic.DataFlow.ToString(), async (envelope, ct) =>
        {
            if (envelope.Payload is FlowEvent flow)
            {
                _executedBonds.Add($"{flow.Source}->{flow.Force}->{flow.Destination}");
                _inflightMessages.TryRemove($"{flow.Source}:{envelope.Gin}", out _);
            }
        });

        // Start background monitor loop
        _ = Task.Run(() => MonitorLoopAsync(_monitorCts.Token), _monitorCts.Token);
    }

    public override async Task StopAsync(CancellationToken token)
    {
        _monitorCts?.Cancel();
        if (Machine.CanFire(Event.Stop))
            Machine.Fire(Event.Stop);
        await Task.CompletedTask;
    }

    private async Task MonitorLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(1000, ct);
                
                // Check for timeouts
                var now = DateTime.Now;
                foreach (var msg in _inflightMessages)
                {
                    if ((now - msg.Value).TotalMilliseconds > _timeoutMs)
                    {
                        if (_inflightMessages.TryRemove(msg.Key, out _))
                        {
                            await LogBugAsync("Message Timeout", $"Message {msg.Key} failed to reach destination within {_timeoutMs}ms.");
                        }
                    }
                }

                // Update Timers in Blueprint
                var runtime = DateTime.Now - _startTime;
                _ = ConfigurationLoader.UpdateElementPropertyAsync("SYSTEM", "SimulationRuntime", runtime);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                Logger.Error(ex, "[Verifier] Monitor Loop Error");
            }
        }
    }

    private async Task LogBugAsync(string type, string description)
    {
        _bugCount++;
        if (Machine.CanFire(Event.ErrorFound))
            Machine.Fire(Event.ErrorFound);
            
        UpdateStatus(State.BugDetected, Event.ErrorFound, ElementHealth.Warning, $"Last Bug: {type}");
        
        // Reset Stability timer in blueprint
        _ = ConfigurationLoader.UpdateElementPropertyAsync("SYSTEM", "StabilityDuration", TimeSpan.Zero);

        var sb = new StringBuilder();
        sb.AppendLine($"## BUG #{_bugCount} - {type}");
        sb.AppendLine($"- **Timestamp**: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"- **Description**: {description}");
        sb.AppendLine("- **Status**: Reported to AI");
        sb.AppendLine();

        try
        {
            var dir = Path.GetDirectoryName(_logPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            await File.AppendAllTextAsync(_logPath, sb.ToString());
            Logger.Warning("[Verifier] BUG LOGGED: {Type} - {Desc}", type, description);
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "[Verifier] Failed to write to bug log at {Path}", _logPath);
        }
    }

    protected override ElementHealth MapStateToHealth(State state) => state switch
    {
        State.Monitoring => ElementHealth.Normal,
        State.BugDetected => ElementHealth.Warning,
        _ => ElementHealth.Normal
    };
}
