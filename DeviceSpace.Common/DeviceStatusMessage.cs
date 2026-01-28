using System.Collections.ObjectModel;
using System.Text.Json;
using DeviceSpace.Common.BaseClasses;
using DeviceSpace.Common.Contracts;
using DeviceSpace.Common.Enums;

public record DeviceStatusMessage : DeviceMessageBase, IDeviceStatus
{
    public IDeviceKey DeviceId { get; init; }
    public DateTime Timestamp { get; init; }
    public string State { get; init; }
    public DeviceHealth Health { get; init; }
    public string Comment { get; init; }
    
    // --- Standard Metrics (Keep for UI Compatibility) ---
    public int CountInbound { get; init; }
    public int CountOutbound { get; init; }
    public int CountConnections { get; init; }
    public int CountDisconnects { get; init; }
    public int CountError { get; init; }
    public double AvgProcessTime { get; set; } // Changed to init and double
    public char HbVisual { get; init; }

    // --- NEW: Dynamic Metrics Dictionary ---
    // This allows the UI to display any new enum-based metrics automatically
    public IReadOnlyDictionary<string, long> Metrics { get; init; }

    /// <summary>
    /// Primary constructor used by the DeviceStatusTracker.
    /// </summary>
    public DeviceStatusMessage(
        IDeviceKey deviceId, 
        string state, 
        DeviceHealth health, 
        string comment,
        int countInbound, 
        int countOutbound, 
        int countConnections, 
        int countDisconnects, 
        int countError, 
        double avgProcessTime, 
        char hb,
        IDictionary<string, long>? extraMetrics = null) 
    {
        DeviceId = deviceId;
        Timestamp = DateTime.UtcNow;
        State = state;
        Health = health;
        Comment = comment;
        CountInbound = countInbound;
        CountOutbound = countOutbound;
        CountConnections = countConnections;
        CountDisconnects = countDisconnects;
        CountError = countError;
        AvgProcessTime = avgProcessTime;
        HbVisual = hb;
        
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
            mainMetrics = new { In = CountInbound, Out = CountOutbound, Err = CountError },
            customMetrics = Metrics // All your Enum-based metrics appear here!
        };

        return JsonSerializer.Serialize(shortObj, options);
    }
}