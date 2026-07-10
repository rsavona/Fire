using Fusion.Common;
using ILogger = Serilog.ILogger;

namespace Fusion.Element.Link;

/// <summary>
/// Competing-consumer dispatch core for the Link server. Members that join the
/// same (topic pattern, queue group) form a group; each envelope dispatched to
/// the group is delivered to exactly ONE member (round-robin), tracked as a
/// pending delivery until that member acks it. Unacked deliveries are re-sent
/// to the next member on ack timeout or member disconnect, up to a redelivery
/// limit, after which <see cref="DeliveryExhausted"/> fires.
///
/// Deliberately socket-free: all I/O goes through the send delegate so the
/// dispatch logic is unit-testable. All group/pending state is guarded by a
/// single lock because it is touched from bus dispatch threads, frame handling,
/// the watchdog timer, and disconnect callbacks; sends happen outside the lock.
/// </summary>
public class QueueDispatcher
{
    /// <summary>Sends a frame to a connected client. Returns false if the send failed.</summary>
    public delegate Task<bool> SendFrameAsync(string clientKey, LinkFrame frame, CancellationToken ct);

    /// <summary>Raised when a delivery has exhausted its redelivery attempts (or the group emptied).</summary>
    public event Func<MessageEnvelope, string, Task>? DeliveryExhausted;

    private sealed class Group
    {
        public required string Pattern;
        public required string QueueGroup;
        public readonly List<string> Members = [];
        public int Cursor;
    }

    private sealed class PendingDelivery
    {
        public required string DeliveryId;
        public required string GroupKey;
        public required MessageEnvelope Envelope;
        public required string Member;
        public int Attempts;
        public DateTime Deadline;
    }

    private readonly object _sync = new();
    private readonly Dictionary<string, Group> _groups = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, PendingDelivery> _pending = new(StringComparer.OrdinalIgnoreCase);

    private readonly SendFrameAsync _send;
    private readonly string _origin;
    private readonly int _ackTimeoutMs;
    private readonly int _redeliverAttempts;
    private readonly Func<string, bool>? _isMemberHealthy;
    private readonly ILogger? _logger;

    public QueueDispatcher(SendFrameAsync send, string origin,
        int ackTimeoutMs = 30000, int redeliverAttempts = 3,
        Func<string, bool>? isMemberHealthy = null, ILogger? logger = null)
    {
        _send = send;
        _origin = origin;
        _ackTimeoutMs = ackTimeoutMs;
        _redeliverAttempts = redeliverAttempts;
        _isMemberHealthy = isMemberHealthy;
        _logger = logger;
    }

    /// <summary>Number of deliveries awaiting an Ack (diagnostics/tests).</summary>
    public int PendingCount { get { lock (_sync) return _pending.Count; } }

    private static string KeyOf(string pattern, string queueGroup) => $"{pattern}|{queueGroup}";

    /// <summary>
    /// Adds a member to a (pattern, group). Returns true when this call created
    /// the group — the caller must then create the single backing bus subscription.
    /// </summary>
    public bool AddMember(string pattern, string queueGroup, string clientKey)
    {
        lock (_sync)
        {
            string key = KeyOf(pattern, queueGroup);
            if (!_groups.TryGetValue(key, out var group))
            {
                group = new Group { Pattern = pattern, QueueGroup = queueGroup };
                _groups[key] = group;
                group.Members.Add(clientKey);
                return true;
            }

            if (!group.Members.Contains(clientKey, StringComparer.OrdinalIgnoreCase))
            {
                group.Members.Add(clientKey);
            }

            return false;
        }
    }

    /// <summary>
    /// Removes a member from a (pattern, group). Returns true when the group is
    /// now empty — the caller must then remove the backing bus subscription.
    /// Pending deliveries held by the departing member are redelivered immediately.
    /// </summary>
    public async Task<bool> RemoveMemberAsync(string pattern, string queueGroup, string clientKey)
    {
        string key = KeyOf(pattern, queueGroup);
        List<PendingDelivery> orphaned;
        bool emptied;

        lock (_sync)
        {
            if (!_groups.TryGetValue(key, out var group)) return false;

            group.Members.RemoveAll(m => string.Equals(m, clientKey, StringComparison.OrdinalIgnoreCase));
            emptied = group.Members.Count == 0;
            if (emptied) _groups.Remove(key);

            orphaned = _pending.Values
                .Where(p => string.Equals(p.GroupKey, key, StringComparison.OrdinalIgnoreCase) &&
                            string.Equals(p.Member, clientKey, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }

        foreach (var delivery in orphaned)
        {
            await RedeliverAsync(delivery, "member left group");
        }

        return emptied;
    }

    /// <summary>
    /// Removes a disconnected client from every group it belongs to. Returns the
    /// (pattern, group) pairs that became empty so the caller can drop their bus
    /// subscriptions. The client's pending deliveries are redelivered immediately.
    /// </summary>
    public async Task<IReadOnlyList<(string Pattern, string QueueGroup)>> RemoveClientAsync(string clientKey)
    {
        List<(string, string)> emptied = [];
        List<PendingDelivery> orphaned;

        lock (_sync)
        {
            foreach (var (key, group) in _groups.ToList())
            {
                if (group.Members.RemoveAll(m => string.Equals(m, clientKey, StringComparison.OrdinalIgnoreCase)) > 0 &&
                    group.Members.Count == 0)
                {
                    _groups.Remove(key);
                    emptied.Add((group.Pattern, group.QueueGroup));
                }
            }

            orphaned = _pending.Values
                .Where(p => string.Equals(p.Member, clientKey, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }

        foreach (var delivery in orphaned)
        {
            await RedeliverAsync(delivery, "member disconnected");
        }

        return emptied;
    }

    /// <summary>
    /// Delivers an envelope to exactly one member of the (pattern, group),
    /// selected round-robin (skipping members reported unhealthy). Envelopes
    /// that themselves arrived over a link are never queue-dispatched.
    /// </summary>
    public async Task DispatchAsync(string pattern, string queueGroup, MessageEnvelope envelope, CancellationToken ct = default)
    {
        // Loop guard: same single-hop rule as pub/sub forwarding.
        if (LinkConventions.IsFromLink(envelope)) return;

        PendingDelivery delivery;
        lock (_sync)
        {
            if (!_groups.TryGetValue(KeyOf(pattern, queueGroup), out var group) || group.Members.Count == 0)
            {
                return;
            }

            delivery = new PendingDelivery
            {
                DeliveryId = Guid.NewGuid().ToString("N"),
                GroupKey = KeyOf(pattern, queueGroup),
                Envelope = envelope,
                Member = SelectNextMember(group, exclude: null),
                Attempts = 1,
                Deadline = DateTime.UtcNow.AddMilliseconds(_ackTimeoutMs)
            };
            _pending[delivery.DeliveryId] = delivery;
        }

        await SendDeliveryAsync(delivery, ct);
    }

    /// <summary>Clears the pending delivery for an Ack. Returns false for unknown/late acks.</summary>
    public bool Acknowledge(string? deliveryId)
    {
        if (string.IsNullOrEmpty(deliveryId)) return false;
        lock (_sync)
        {
            return _pending.Remove(deliveryId);
        }
    }

    /// <summary>Redelivers every pending delivery whose ack deadline has passed. Timer-driven.</summary>
    public async Task ScanTimeoutsAsync(DateTime utcNow)
    {
        List<PendingDelivery> expired;
        lock (_sync)
        {
            expired = _pending.Values.Where(p => p.Deadline <= utcNow).ToList();
        }

        foreach (var delivery in expired)
        {
            await RedeliverAsync(delivery, "ack timeout");
        }
    }

    private async Task SendDeliveryAsync(PendingDelivery delivery, CancellationToken ct)
    {
        var frame = LinkConventions.ToQueuePublishFrame(delivery.Envelope, _origin, delivery.DeliveryId);
        bool sent;
        try
        {
            sent = await _send(delivery.Member, frame, ct);
        }
        catch (Exception ex)
        {
            _logger?.Warning(ex, "Queue delivery {DeliveryId} send to {Member} failed", delivery.DeliveryId, delivery.Member);
            sent = false;
        }

        if (!sent)
        {
            await RedeliverAsync(delivery, "send failed");
        }
    }

    private async Task RedeliverAsync(PendingDelivery delivery, string reason)
    {
        bool resend = false;
        string? exhaustedReason = null;

        lock (_sync)
        {
            // Acked (or already exhausted) while we were deciding — nothing to do.
            if (!_pending.ContainsKey(delivery.DeliveryId)) return;

            if (delivery.Attempts - 1 >= _redeliverAttempts)
            {
                _pending.Remove(delivery.DeliveryId);
                exhaustedReason = $"{reason}; {_redeliverAttempts} redelivery attempt(s) exhausted";
            }
            else if (!_groups.TryGetValue(delivery.GroupKey, out var group) || group.Members.Count == 0)
            {
                _pending.Remove(delivery.DeliveryId);
                exhaustedReason = $"{reason}; no members remain in group";
            }
            else
            {
                delivery.Member = SelectNextMember(group, exclude: delivery.Member);
                delivery.Attempts++;
                delivery.Deadline = DateTime.UtcNow.AddMilliseconds(_ackTimeoutMs);
                resend = true;
            }
        }

        if (exhaustedReason != null)
        {
            _logger?.Warning("Queue delivery {DeliveryId} for {Topic} abandoned: {Reason}",
                delivery.DeliveryId, delivery.Envelope.Destination, exhaustedReason);

            if (DeliveryExhausted != null)
            {
                await DeliveryExhausted.Invoke(delivery.Envelope, exhaustedReason);
            }
            return;
        }

        if (resend)
        {
            _logger?.Debug("Redelivering {DeliveryId} to {Member} (attempt {Attempt}): {Reason}",
                delivery.DeliveryId, delivery.Member, delivery.Attempts, reason);
            await SendDeliveryAsync(delivery, CancellationToken.None);
        }
    }

    /// <summary>
    /// Round-robin over the member list, preferring healthy members and (on
    /// redelivery) members other than the one that just failed. Falls back to
    /// plain round-robin when every member is filtered out.
    /// </summary>
    private string SelectNextMember(Group group, string? exclude)
    {
        int count = group.Members.Count;
        for (int pass = 0; pass < 2; pass++)
        {
            for (int i = 0; i < count; i++)
            {
                string candidate = group.Members[(group.Cursor + i) % count];
                if (pass == 0)
                {
                    if (exclude != null && count > 1 &&
                        string.Equals(candidate, exclude, StringComparison.OrdinalIgnoreCase)) continue;
                    if (_isMemberHealthy != null && !_isMemberHealthy(candidate)) continue;
                }

                group.Cursor = (group.Cursor + i + 1) % count;
                return candidate;
            }
        }

        // Unreachable: pass 1 always returns for a non-empty member list.
        return group.Members[0];
    }
}
