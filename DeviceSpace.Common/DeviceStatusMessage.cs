using System.Collections.ObjectModel;
using System.Text.Json;
using DeviceSpace.Common.BaseClasses;
using DeviceSpace.Common.Contracts;
using DeviceSpace.Common.Enums;

namespace DeviceSpace.Common;

public record DeviceStatusMessage : DeviceMessageBase, IDeviceStatus
{
    public IDeviceKey DeviceId { get; init; }
    public DateTime Timestamp { get; init; }
    public string State { get; init; }
    public DeviceHealth Health { get; init; }
    public string Comment { get; init; }
    public int ScreenIndex { get; init; }
    
    // --- Standard Metrics (Keep for UI Compatibility) ---
    public int CountInbound { get; init; }
    public int CountOutbound { get; init; }
    public int CountConnections { get; init; }
    public int CountDisconnects { get; init; }
    public int CountError { get; init; }
    public double InboundRate { get; init; }
    public double OutboundRate { get; init; }
    public double AvgProcessTime { get; set; }
    public char HbVisual { get; init; }

    public int ResourceTasks { get; init; }
    public int ResourceContainers { get; init; }
    public int ResourceDeepCount { get; init; }

    // Dynamic Metrics Dictionary
    public IReadOnlyDictionary<string, long> Metrics { get; init; }

    /// <summary>
    /// Primary constructor used by the DeviceStatusTracker.
    /// </summary>
    public DeviceStatusMessage(
        IDeviceKey deviceId, 
        string state, 
        DeviceHealth health, 
        string comment,
        int screenIndex,
        int countInbound, 
        int countOutbound, 
        int countConnections, 
        int countDisconnects,
        int countError, 
        double inboundRate,
        double outboundRate,
        double avgProcessTime, 
        char hb,
        int resTasks = 0,
        int resContainers = 0,
        int resDeepCount = 0,
        IDictionary<string, long>? extraMetrics = null) 
    {
        DeviceId = deviceId;
        Timestamp = DateTime.UtcNow;
        State = state;
        Health = health;
        Comment = comment;
        ScreenIndex = screenIndex;
        CountInbound = countInbound;
        CountOutbound = countOutbound;
        CountConnections = countConnections;
        CountDisconnects = countDisconnects;
        CountError = countError;
        InboundRate = inboundRate;
        OutboundRate = outboundRate;
        AvgProcessTime = avgProcessTime;
        HbVisual = hb;
        ResourceTasks = resTasks;
        ResourceContainers = resContainers;
        ResourceDeepCount = resDeepCount;

        // Wrap the dictionary in a ReadOnly collection for the record
        Metrics = new ReadOnlyDictionary<string, long>(extraMetrics ?? new Dictionary<string, long>());
    }

    public string GetShortStatusJson()
    {
        // Now includes dynamic metrics in the JSON output
        var options = new JsonSerializerOptions { WriteIndented = true };
        var shortObj = new
        {
            deviceId = DeviceId.ToString(),
            health = Health.ToString(),
            state = State,
            mainMetrics = new { In = CountInbound, InRate = InboundRate, Out = CountOutbound, OutRate = OutboundRate, Err = CountError, ScreenIndex = ScreenIndex },
            resources = new { Tasks = ResourceTasks, Containers = ResourceContainers, DeepCount = ResourceDeepCount },
            customMetrics = Metrics // All your Enum-based metrics appear here!
        };

        return JsonSerializer.Serialize(shortObj, options);
    }
}
