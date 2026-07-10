using Fusion.Element.Sparkplug.Protocol;

namespace Fusion.Element.Sparkplug;

/// <summary>
/// The Sparkplug B session state machine, independent of any MQTT transport so
/// it can be unit tested without a broker. Owns:
/// <list type="bullet">
/// <item>bdSeq — increments (wrapping at 255) on every new MQTT session; the
/// NDEATH will registered at CONNECT must carry the same bdSeq as the NBIRTH.</item>
/// <item>seq — the 0-255 wrapping payload sequence number; NBIRTH is always 0.</item>
/// <item>The device cache — last known metric values per device, used for
/// DBIRTH construction, report-by-exception filtering, and rebirth replay.</item>
/// </list>
/// </summary>
public sealed class SparkplugSession
{
    public const string BdSeqMetricName = "bdSeq";
    public const string RebirthMetricName = "Node Control/Rebirth";

    private readonly object _lock = new();
    private long _bdSeq = -1; // -1 = no session opened yet; first session announces 0
    private int _seq;

    private sealed class DeviceState
    {
        public Dictionary<string, SparkplugMetric> Metrics { get; } = new(StringComparer.OrdinalIgnoreCase);
        public bool BirthPublished { get; set; }
        public bool IsDead { get; set; }
    }

    private readonly Dictionary<string, DeviceState> _devices = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The bdSeq announced by the current session's NBIRTH/NDEATH pair.</summary>
    public ulong CurrentBdSeq
    {
        get { lock (_lock) return (ulong)Math.Max(_bdSeq, 0); }
    }

    /// <summary>
    /// Opens a new Sparkplug session for a new MQTT connect attempt: increments
    /// bdSeq (0 on the very first session, wrapping at 255), resets seq to 0, and
    /// marks every cached device as needing a new DBIRTH. Returns the session bdSeq.
    /// </summary>
    public ulong OpenSession()
    {
        lock (_lock)
        {
            _bdSeq = (_bdSeq + 1) & 0xFF;
            _seq = 0;
            foreach (var device in _devices.Values)
                device.BirthPublished = false;
            return (ulong)_bdSeq;
        }
    }

    /// <summary>
    /// Returns the next payload sequence number and advances the counter,
    /// wrapping 255 back to 0.
    /// </summary>
    public ulong NextSeq()
    {
        lock (_lock)
        {
            ulong current = (ulong)_seq;
            _seq = (_seq + 1) & 0xFF;
            return current;
        }
    }

    /// <summary>
    /// Resets the sequence counter and birth flags in response to an NCMD
    /// "Node Control/Rebirth" so the caller can republish NBIRTH (seq 0)
    /// followed by all DBIRTHs. bdSeq is unchanged — the MQTT session survives.
    /// </summary>
    public void ResetForRebirth()
    {
        lock (_lock)
        {
            _seq = 0;
            foreach (var device in _devices.Values)
                device.BirthPublished = false;
        }
    }

    // ------------------------------------------------------------------
    // Node payloads
    // ------------------------------------------------------------------

    /// <summary>NDEATH payload for the MQTT will. Carries only the bdSeq metric — no seq.</summary>
    public SparkplugPayload CreateNodeDeathPayload()
    {
        return new SparkplugPayload
        {
            Seq = null,
            Metrics = [SparkplugMetric.Create(BdSeqMetricName, (long)CurrentBdSeq)]
        };
    }

    /// <summary>
    /// NBIRTH payload: seq (0 immediately after OpenSession/ResetForRebirth),
    /// the bdSeq metric, the writable Node Control/Rebirth metric, plus any
    /// caller-supplied node-level metrics.
    /// </summary>
    public SparkplugPayload CreateNodeBirthPayload(IEnumerable<SparkplugMetric>? nodeMetrics = null)
    {
        var metrics = new List<SparkplugMetric>
        {
            SparkplugMetric.Create(BdSeqMetricName, (long)CurrentBdSeq),
            SparkplugMetric.Create(RebirthMetricName, false)
        };
        if (nodeMetrics != null) metrics.AddRange(nodeMetrics);

        return new SparkplugPayload { Seq = NextSeq(), Metrics = metrics };
    }

    public SparkplugPayload CreateNodeDataPayload(IEnumerable<SparkplugMetric> metrics) =>
        new() { Seq = NextSeq(), Metrics = metrics.ToList() };

    // ------------------------------------------------------------------
    // Device cache / payloads
    // ------------------------------------------------------------------

    public sealed record DeviceUpdate(bool NeedsBirth, IReadOnlyList<SparkplugMetric> ChangedMetrics);

    /// <summary>
    /// Merges the latest metric readings for a device into the cache and reports
    /// what needs publishing: a DBIRTH if the device is new, was dead, or has not
    /// been born in this session; otherwise the report-by-exception subset of
    /// metrics whose values actually changed.
    /// </summary>
    public DeviceUpdate RecordDevice(string deviceId, IEnumerable<SparkplugMetric> metrics)
    {
        lock (_lock)
        {
            if (!_devices.TryGetValue(deviceId, out var device))
            {
                device = new DeviceState();
                _devices[deviceId] = device;
            }

            var changed = new List<SparkplugMetric>();
            foreach (var metric in metrics)
            {
                if (device.Metrics.TryGetValue(metric.Name, out var previous) && previous.ValueEquals(metric))
                    continue;
                device.Metrics[metric.Name] = metric;
                changed.Add(metric);
            }

            bool needsBirth = !device.BirthPublished || device.IsDead;
            if (device.IsDead) device.IsDead = false;

            return new DeviceUpdate(needsBirth, changed);
        }
    }

    public bool IsDeviceBorn(string deviceId)
    {
        lock (_lock)
            return _devices.TryGetValue(deviceId, out var device) && device.BirthPublished && !device.IsDead;
    }

    /// <summary>Device ids with cached metrics that are not marked dead — replayed as DBIRTHs on (re)connect and rebirth.</summary>
    public IReadOnlyList<string> GetKnownDevices()
    {
        lock (_lock)
            return _devices.Where(kv => !kv.Value.IsDead && kv.Value.Metrics.Count > 0)
                           .Select(kv => kv.Key).ToList();
    }

    /// <summary>DBIRTH payload with the full cached metric set for the device. Marks the device born.</summary>
    public SparkplugPayload CreateDeviceBirthPayload(string deviceId)
    {
        lock (_lock)
        {
            if (!_devices.TryGetValue(deviceId, out var device))
            {
                device = new DeviceState();
                _devices[deviceId] = device;
            }

            device.BirthPublished = true;
            device.IsDead = false;
            return new SparkplugPayload { Seq = NextSeq(), Metrics = device.Metrics.Values.ToList() };
        }
    }

    /// <summary>DDATA payload for a batch of changed metrics.</summary>
    public SparkplugPayload CreateDeviceDataPayload(IEnumerable<SparkplugMetric> changedMetrics) =>
        new() { Seq = NextSeq(), Metrics = changedMetrics.ToList() };

    /// <summary>DDEATH payload. Marks the device dead so it re-births when it recovers.</summary>
    public SparkplugPayload CreateDeviceDeathPayload(string deviceId)
    {
        lock (_lock)
        {
            if (_devices.TryGetValue(deviceId, out var device))
            {
                device.IsDead = true;
                device.BirthPublished = false;
            }

            return new SparkplugPayload { Seq = NextSeq(), Metrics = [] };
        }
    }

    // ------------------------------------------------------------------
    // Inbound command helpers
    // ------------------------------------------------------------------

    /// <summary>True when the payload contains a Node Control/Rebirth metric set to true.</summary>
    public static bool IsRebirthRequest(SparkplugPayload payload) =>
        payload.Metrics.Any(m =>
            string.Equals(m.Name, RebirthMetricName, StringComparison.OrdinalIgnoreCase) &&
            m.Value is bool and true);
}
