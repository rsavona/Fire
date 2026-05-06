using System;
using DeviceSpace.Common.Enums;

namespace DeviceSpace.Common.Contracts;
/// <summary>
/// A rich, immutable snapshot of a device's current state and health.
/// This is the object raised in the OnStatusUpdated event.
/// </summary>
public interface IDeviceStatus : IDeviceMessage 
{
    // WHO: The unique key of the device
    IDeviceKey DeviceId { get; }

    // WHEN: The exact time this status was generated
    DateTime Timestamp { get; }

    // WHAT: The current state machine state (e.g., "Running")
    string State { get; }

    // HOW: The high-level health (e.g., "Unhealthy")
    DeviceHealth Health { get; } // Renamed from HealthState

    // WHY: A human-readable message
    string Comment { get; }

    int ScreenIndex { get; }
    
    // These are required by DeviceStatusCollection.Summarize
    // The data comes *from* the tracker.
    int CountInbound { get; }
    int CountOutbound { get; }
    int CountConnections { get; }
    int CountDisconnects { get; }
    int CountError { get; }
    
    double InboundRate { get; }
    double OutboundRate { get; }
    
    double AvgProcessTime { get; set; }

    int ResourceTasks { get; }
    int ResourceContainers { get; }
    int ResourceDeepCount { get; }

    char HbVisual { get; }
    
    string GetShortStatusJson();
} 
