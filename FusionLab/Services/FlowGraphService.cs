using System.Collections.Concurrent;
using Fusion.Common;
using Fusion.Common.Contracts;
using Fusion.Common.Enums;
using Fusion.Common.Messaging;
using Fusion.Core.Replay;

namespace FusionLab.Services;

public enum FlowFeedMode
{
    Live,
    Replay
}

public sealed class FlowNode
{
    public required string Name { get; init; }
    public ElementHealth Health { get; set; } = ElementHealth.Normal;
    public string State { get; set; } = "Unknown";
    public bool HasStatus { get; set; }
    public DateTime LastSeenUtc { get; set; }
    public bool IsReaction { get; set; }
}

public sealed class FlowEdge
{
    public required string Source { get; init; }
    public required string Destination { get; init; }
    public string Force { get; set; } = string.Empty;
    public long TotalCount { get; set; }
    public DateTime LastEventUtc { get; set; }

    // Sliding window of recent event times, for the per-edge rate counter.
    public readonly Queue<DateTime> Recent = new();

    public double RatePerMinute(DateTime nowUtc)
    {
        lock (Recent)
        {
            while (Recent.Count > 0 && (nowUtc - Recent.Peek()).TotalSeconds > 60)
            {
                Recent.Dequeue();
            }
            return Recent.Count;
        }
    }
}

public sealed record FlowPulse(Guid Id, string EdgeKey, DateTime CreatedUtc);

/// <summary>
/// Aggregates bus traffic into a topology graph for the Flow view.
///
/// Live mode consumes the in-process message bus directly:
///   System.DataFlow (FlowEvent), All_Elements.StatusMessage (ElementStatusMessage),
///   System.Topology (SystemTopologyMessage — bond-derived edges, same derivation as
///   the console status monitor).
///
/// Replay mode consumes ONLY the REPLAY.* namespaced mirrors published by
/// <see cref="ReplayService"/> — never live topics — so replaying an audit log can
/// never be confused with (or trigger) live traffic.
/// </summary>
public class FlowGraphService : IDisposable
{
    private readonly IMessageBus _bus;
    private readonly object _gate = new();
    private bool _subscribed;

    private readonly ConcurrentDictionary<string, FlowNode> _nodes = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, FlowEdge> _edges = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<FlowPulse> _pulses = new();

    public FlowFeedMode Mode { get; private set; } = FlowFeedMode.Live;
    public DateTime? LastEventUtc { get; private set; }
    public long TotalFlowEvents { get; private set; }

    public event Action? Changed;

    public FlowGraphService(IMessageBus bus)
    {
        _bus = bus;
    }

    public void EnsureSubscribed()
    {
        lock (_gate)
        {
            if (_subscribed) return;
            _subscribed = true;
        }

        // Live feeds.
        _bus.SubscribeAsync(MessageBusTopic.DataFlow.ToString(), HandleLiveFlowAsync);
        _bus.SubscribeAsync(MessageBusTopic.ElementStatus.ToString(), HandleLiveStatusAsync);
        _bus.SubscribeAsync(MessageBusTopic.SystemTopology.ToString(), HandleTopologyAsync);

        // Replay feeds (REPLAY.* namespace only — see ReplayService guard).
        _bus.SubscribeAsync(ReplayService.TopicPrefix + MessageBusTopic.DataFlow, HandleReplayFlowAsync);
        _bus.SubscribeAsync(ReplayService.TopicPrefix + MessageBusTopic.ElementStatus, HandleReplayStatusAsync);
    }

    public void SetMode(FlowFeedMode mode)
    {
        if (Mode == mode) return;
        Mode = mode;
        Reset();
    }

    public void Reset()
    {
        _nodes.Clear();
        _edges.Clear();
        lock (_pulses) _pulses.Clear();
        TotalFlowEvents = 0;
        LastEventUtc = null;
        Changed?.Invoke();
    }

    public IReadOnlyCollection<FlowNode> Nodes => _nodes.Values.ToList();
    public IReadOnlyCollection<FlowEdge> Edges => _edges.Values.ToList();

    public IReadOnlyList<FlowPulse> ActivePulses(TimeSpan maxAge)
    {
        var cutoff = DateTime.UtcNow - maxAge;
        lock (_pulses)
        {
            _pulses.RemoveAll(p => p.CreatedUtc < cutoff);
            return _pulses.ToList();
        }
    }

    public static string EdgeKey(string source, string destination) =>
        $"{source.ToUpperInvariant()}->{destination.ToUpperInvariant()}";

    // ------------------------------------------------------------------
    //                        LIVE HANDLERS
    // ------------------------------------------------------------------

    private Task HandleLiveFlowAsync(MessageEnvelope envelope, CancellationToken ct)
    {
        if (Mode == FlowFeedMode.Live && envelope.Payload is FlowEvent flow)
        {
            RecordFlow(flow);
        }
        return Task.CompletedTask;
    }

    private Task HandleLiveStatusAsync(MessageEnvelope envelope, CancellationToken ct)
    {
        if (Mode == FlowFeedMode.Live && envelope.Payload is ElementStatusMessage status)
        {
            RecordStatus(status.ElementId?.ElementName ?? string.Empty, status.State, status.Health);
        }
        return Task.CompletedTask;
    }

    private Task HandleTopologyAsync(MessageEnvelope envelope, CancellationToken ct)
    {
        // Topology (bond configuration) is accepted in any mode: it describes the wiring,
        // not traffic. Same bond -> element derivation as ConsoleStatusMonitor.
        if (envelope.Payload is not SystemTopologyMessage topology) return Task.CompletedTask;

        foreach (var element in topology.Elements)
        {
            GetOrAddNode(element.Name);
        }

        foreach (var reaction in topology.Reactions)
        {
            foreach (var bond in reaction.Bonds)
            {
                var src = SafeElementName(bond.Source);
                var dst = SafeElementName(bond.Destination);
                if (src.Length == 0 || dst.Length == 0) continue;

                GetOrAddNode(src);
                GetOrAddNode(dst);
                var edge = _edges.GetOrAdd(EdgeKey(src, dst), _ => new FlowEdge { Source = src, Destination = dst });
                if (string.IsNullOrEmpty(edge.Force)) edge.Force = reaction.Name;
            }
        }

        Changed?.Invoke();
        return Task.CompletedTask;
    }

    // ------------------------------------------------------------------
    //                       REPLAY HANDLERS
    // ------------------------------------------------------------------

    private Task HandleReplayFlowAsync(MessageEnvelope envelope, CancellationToken ct)
    {
        if (Mode == FlowFeedMode.Replay && envelope.Payload is FlowEvent flow)
        {
            RecordFlow(flow);
        }
        return Task.CompletedTask;
    }

    private Task HandleReplayStatusAsync(MessageEnvelope envelope, CancellationToken ct)
    {
        if (Mode != FlowFeedMode.Replay) return Task.CompletedTask;

        if (envelope.Payload is ReplayedElementStatus status)
        {
            // Replayed ids are full element keys ("FUSION-SYS-HOST_A"); reduce to the element name.
            RecordStatus(ReduceElementId(status.ElementId), status.State, status.Health);
        }
        return Task.CompletedTask;
    }

    // ------------------------------------------------------------------
    //                          AGGREGATION
    // ------------------------------------------------------------------

    private void RecordFlow(FlowEvent flow)
    {
        if (string.IsNullOrWhiteSpace(flow.Source) || string.IsNullOrWhiteSpace(flow.Destination)) return;

        var now = DateTime.UtcNow;
        GetOrAddNode(flow.Source).LastSeenUtc = now;
        GetOrAddNode(flow.Destination).LastSeenUtc = now;

        var edge = _edges.GetOrAdd(EdgeKey(flow.Source, flow.Destination),
            _ => new FlowEdge { Source = flow.Source, Destination = flow.Destination });
        if (!string.IsNullOrWhiteSpace(flow.Force)) edge.Force = flow.Force;
        edge.TotalCount++;
        edge.LastEventUtc = now;
        lock (edge.Recent)
        {
            edge.Recent.Enqueue(now);
            while (edge.Recent.Count > 600) edge.Recent.Dequeue();
        }

        lock (_pulses)
        {
            _pulses.Add(new FlowPulse(Guid.NewGuid(), EdgeKey(flow.Source, flow.Destination), now));
            if (_pulses.Count > 200) _pulses.RemoveRange(0, _pulses.Count - 200);
        }

        TotalFlowEvents++;
        LastEventUtc = now;
        Changed?.Invoke();
    }

    private void RecordStatus(string elementName, string state, ElementHealth health)
    {
        if (string.IsNullOrWhiteSpace(elementName)) return;

        // Only color nodes that participate in the flow graph; also try suffix matching
        // for ids that arrive as full element keys.
        var node = _nodes.TryGetValue(elementName, out var exact)
            ? exact
            : _nodes.Values.FirstOrDefault(n =>
                elementName.EndsWith("-" + n.Name, StringComparison.OrdinalIgnoreCase));

        node ??= GetOrAddNode(elementName);

        node.Health = health;
        node.State = state;
        node.HasStatus = true;
        node.LastSeenUtc = DateTime.UtcNow;
        Changed?.Invoke();
    }

    private FlowNode GetOrAddNode(string name)
    {
        var trimmed = name.Trim();
        return _nodes.GetOrAdd(trimmed, _ => new FlowNode { Name = trimmed.ToUpperInvariant() });
    }

    private static string ReduceElementId(string elementId)
    {
        // Element keys render as "{CORE}-{SCOPE}-{NAME}"; keep everything after the
        // second dash so element names containing dashes survive.
        var parts = elementId.Split('-', 3);
        return parts.Length == 3 ? parts[2] : elementId;
    }

    private static string SafeElementName(string topic)
    {
        try
        {
            return string.IsNullOrWhiteSpace(topic) ? string.Empty : new MessageBusTopic(topic).ElementName;
        }
        catch
        {
            return string.Empty;
        }
    }

    public void Dispose()
    {
        // Singleton lifetime — subscriptions live for the app. Nothing to release.
    }
}
