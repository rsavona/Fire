using System.Collections.Concurrent;
using Fusion.Common;
using Fusion.Common.BaseClasses;
using Fusion.Common.Contracts;
using Fusion.Common.Enums;
using Fusion.Common.Messaging;
using Serilog.Core;

namespace Fusion.Element.Enterprise.Suite.Inventory;

public class ContainerState
{
    public int Gin { get; set; }
    public string CurrentLocation { get; set; } = string.Empty;
    public string LastForce { get; set; } = string.Empty;
    public DateTime LastSeen { get; set; } = DateTime.UtcNow;
    public Dictionary<string, string> Attributes { get; set; } = new();
}

public class InventoryElement : ElementBase<InventoryElement.State, InventoryElement.Event, InventoryElement.Metric>
{
    public enum State { Idle, Tracking }
    public enum Event { Start, Stop, ContainerUpdated }
    public enum Metric { ActiveContainers, TotalMovements }

    private readonly ConcurrentDictionary<int, ContainerState> _inventory = new();

    public InventoryElement(IMessageBus bus, IElementBlueprint config, IFireLogger logger) 
        : base(bus, config, logger, new LoggingLevelSwitch(), State.Idle, Event.Start)
    {
    }

    protected override void ConfigureStateMachine()
    {
        Machine.Configure(State.Idle)
            .Permit(Event.Start, State.Tracking);
        
        Machine.Configure(State.Tracking)
            .Permit(Event.Stop, State.Idle);
    }

    public override async Task StartAsync(CancellationToken token)
    {
        UpdateStatus(State.Tracking, Event.Start, ElementHealth.Normal, "Tracking Active");
        Logger.Information("[{Dev}] Digital Twin Inventory tracking started.", Config.Name);
        await Task.CompletedTask;
    }

    public override async Task StopAsync(CancellationToken token)
    {
        UpdateStatus(State.Idle, Event.Stop, ElementHealth.Normal, "Tracking Stopped");
        await Task.CompletedTask;
    }

    public void UpdateContainer(int gin, string location, string force, Dictionary<string, string>? attrs = null)
    {
        var state = _inventory.GetOrAdd(gin, new ContainerState { Gin = gin });
        lock (state)
        {
            state.CurrentLocation = location;
            state.LastForce = force;
            state.LastSeen = DateTime.UtcNow;
            if (attrs != null)
            {
                foreach (var attr in attrs) state.Attributes[attr.Key] = attr.Value;
            }
        }
        
        Tracker.IncrementInbound(); // Treat updates as inbound data
        Logger.Debug("[{Dev}] Container {Gin} seen at {Loc} via {Force}", Config.Name, gin, location, force);
    }

    public ContainerState? GetContainer(int gin)
    {
        _inventory.TryGetValue(gin, out var state);
        return state;
    }

    protected override ElementHealth MapStateToHealth(State state) => ElementHealth.Normal;
}
