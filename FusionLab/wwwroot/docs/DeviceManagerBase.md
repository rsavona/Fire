# FIRE AM: Element Manager 

### `ElementManagerBase<TElement>`
The base engine that handles the heavy lifting:
* **Lifecycle Management**: Handles the asynchronous startup and shutdown of all configured elements.
* **Auto-Wiring**: Automatically detects if a element implements `IMessageProvider` and hooks up the `MessageReceived` events.
* **Reaction Subscription**: Scans the global configuration and automatically subscribes the element to any Bus topics where it is listed as a destination.

### `IMessageProvider`
A standard interface (contract) that any element capable of sending data must implement. 

---

## How to Implement a New Module

To add a new Element type (e.g., a "Cognex Scanner" or "Zebra Printer"):

1. **Define the Element Class**: Create your Element class inheriting from `ElementBase`. If it sends data, implement `IMessageProvider`.
2. **Create the Manager**: Inherit from `ElementManagerBase<YourNewElement>`.
3. **Implement `RegisterElementHandlers`**: Add any specific internal logic needed (like pre-calculating bond maps).
4. **Implement `OnElementMessageReceived`**: Define how data coming *from* the Element should be published to the internal Message Bus.
5. **Implement `HandleBusMessageAsync`**: Define how the Element reacts when a message arrives *from* the WCS.

---

Bolierplate code far the Element Manager 
```csharp
using Fusion.Common.Contracts;
using Fusion.Common.BaseClasses;
using Microsoft.Extensions.Logging;

namespace Fusion.Element.NewElement;

/// <summary>
/// Boilerplate Manager for FIRE Productized Modules
/// </summary>
public class NewElementManager : ElementManagerBase<NewElement>
{
    public NewElementManager(
        IMessageBus bus, 
        List<IElementBlueprint> configs, 
        ILoggerFactory loggerFactory) 
        : base(bus, configs, loggerFactory)
    {
    }

    /// <summary>
    /// Step 1: Hook up specialized Element handlers.
    /// Basic Messaging is already auto-wired by the Base Class.
    /// </summary>
    protected override void RegisterElementHandlers(NewElementElement element)
    {
        Logger.Debug("[{Dev}] Initializing specialized Element handlers.", element.Config.Name);
        // Add custom bond caching or correlation logic here
    }

    /// <summary>
    /// Step 2: INBOUND (Element -> Bus)
    /// </summary>
    protected override void OnElementMessageReceived(object? sender, object messageEnv)
    {
        if (sender is not NewElementElement element || messageEnv is not string data) return;

        Logger.Information("[{Dev}] Data Received: {Payload}", element.Config.Name, data);
        
        // Example: Map to Bus
        // _ = MessageBus.PublishAsync("Topic", new MessageEnvelope(...));
    }

    /// <summary>
    /// Step 3: OUTBOUND (Bus -> Element)
    /// </summary>
    protected override async Task HandleBusMessageAsync(
        NewElementElement element, 
        string bondSource, 
        MessageEnvelope envelope, 
        CancellationToken ct)
    {
        try 
        {
            Logger.Information("[{Dev}] Routing command to Element: {Payload}", element.Config.Name, envelope.Payload);
            // await element.ExecuteCommandAsync(envelope.Payload.ToString());
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "[{Dev}] Command execution failed.", element.Config.Name);
        }
    }
}
```