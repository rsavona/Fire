using System;
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
    public int CountInbound { get; init; }
    public int CountOutbound { get; init; }
    public int CountConnections { get; init; }
    public int CountError { get; init; }
    public double AvgProcessTime { get; set; }
    
    public char HbVisual { get; set;}

    public DeviceStatusMessage(IDeviceKey deviceId, string state, DeviceHealth health, string comment,
        int countInbound, int countOutbound, int countWarning, int countError, int avgProcessTime = 0, char hb = ' ')
    {
        DeviceId = deviceId;
        Timestamp = DateTime.UtcNow;
        State = state;
        Health = health;
        Comment = comment;
        CountInbound = countInbound;
        CountOutbound = countOutbound;
        CountConnections = countWarning;
        CountError = countError;
        AvgProcessTime = avgProcessTime;
        HbVisual = hb;
    }

  
    public DeviceStatusMessage(DeviceKey managerKey, string state, DeviceHealth health, string comment, 
        int countInbound, int countOutbound, int countWarning, int countError, double avgProcessTime, char hb = ' ')
    {
        
        DeviceId = managerKey;
        Timestamp = DateTime.UtcNow;
        State = state;
        Health = health;
        Comment = comment;
        CountInbound = countInbound;
        CountOutbound = countOutbound;
        CountConnections = countWarning;
        CountError = countError;
        AvgProcessTime = (int)avgProcessTime;
        HbVisual = hb;
    }

    public string GetShortStatusJson()
    {
        int totalMessages = CountInbound + CountOutbound;
        bool showComment = Health == DeviceHealth.Error || Health == DeviceHealth.Critical;

        return $$"""
        {
            "deviceId": "{{DeviceId}}",
            "health": "{{Health}}",
            "messageCount": {{totalMessages}}{{ (showComment ? $",\n    \"comment\": \"{Comment}\"" : "") }}
        }
        """;
    }
}