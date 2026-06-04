# FIRE AM: Device Manager 

### `DeviceManagerBase<TDevice>`
The base engine that handles the heavy lifting:
* **Lifecycle Management**: Handles the asynchronous startup and shutdown of all configured devices.
* **Auto-Wiring**: Automatically detects if a device implements `IMessageProvider` and hooks up the `MessageReceived` events.
* **Workflow Subscription**: Scans the global configuration and automatically subscribes the device to any Bus topics where it is listed as a destination.

### `IMessageProvider`
A standard interface (contract) that any device capable of sending data must implement. 

---

## How to Implement a New Module

To add a new Device type (e.g., a "Cognex Scanner" or "Zebra Printer"):

1. **Define the Device Class**: Create your Device class inheriting from `DeviceBase`. If it sends data, implement `IMessageProvider`.
2. **Create the Manager**: Inherit from `DeviceManagerBase<YourNewDevice>`.
3. **Implement `RegisterDeviceHandlers`**: Add any specific internal logic needed (like pre-calculating route maps).
4. **Implement `OnDeviceMessageReceived`**: Define how data coming *from* the Device should be published to the internal Message Bus.
5. **Implement `HandleBusMessageAsync`**: Define how the Device reacts when a message arrives *from* the WCS.

---

Bolierplate code far the Device Manager 
```csharp
using DeviceSpace.Common.Contracts;
using DeviceSpace.Common.BaseClasses;
using Microsoft.Extensions.Logging;

namespace Device.NewDevice;

/// <summary>
/// Boilerplate Manager for FIRE Productized Modules
/// </summary>
public class NewDeviceManager : DeviceManagerBase<NewDevice>
{
    public NewDeviceManager(
        IMessageBus bus, 
        List<IDeviceConfig> configs, 
        ILoggerFactory loggerFactory) 
        : base(bus, configs, loggerFactory)
    {
    }

    /// <summary>
    /// Step 1: Hook up specialized Device handlers.
    /// Basic Messaging is already auto-wired by the Base Class.
    /// </summary>
    protected override void RegisterDeviceHandlers(NewDeviceDevice device)
    {
        Logger.Debug("[{Dev}] Initializing specialized Device handlers.", device.Config.Name);
        // Add custom route caching or correlation logic here
    }

    /// <summary>
    /// Step 2: INBOUND (Device -> Bus)
    /// </summary>
    protected override void OnDeviceMessageReceived(object? sender, object messageEnv)
    {
        if (sender is not NewDeviceDevice device || messageEnv is not string data) return;

        Logger.Information("[{Dev}] Data Received: {Payload}", device.Config.Name, data);
        
        // Example: Map to Bus
        // _ = MessageBus.PublishAsync("Topic", new MessageEnvelope(...));
    }

    /// <summary>
    /// Step 3: OUTBOUND (Bus -> Device)
    /// </summary>
    protected override async Task HandleBusMessageAsync(
        NewDeviceDevice device, 
        string routeSource, 
        MessageEnvelope envelope, 
        CancellationToken ct)
    {
        try 
        {
            Logger.Information("[{Dev}] Routing command to Device: {Payload}", device.Config.Name, envelope.Payload);
            // await device.ExecuteCommandAsync(envelope.Payload.ToString());
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "[{Dev}] Command execution failed.", device.Config.Name);
        }
    }
}
```