using System;
using DeviceSpace.Common.Contracts;

namespace DeviceSpace.Common;

public class ScriptGlobals
{
    /// <summary>
    /// The actual data payload from the MessageEnvelope.
    /// typed as 'dynamic' so scripts can access properties like Message.Barcodes
    /// without explicit casting.
    /// </summary>
    public dynamic Message { get; }

    /// <summary>
    /// The Source/Key from the MessageEnvelope (e.g., "Scanner1").
    /// </summary>
    public string Key { get; }

    /// <summary>
    /// Access to the MessageBus so the script can publish new messages.
    /// </summary>
    public IMessageBus Bus { get; }

    /// <summary>
    /// A delegate to allow the script to write to the application logs.
    /// Usage in script: Log("Hello World");
    /// </summary>
    public Action<string> Log { get; }

    public ScriptGlobals(object message, string key, IMessageBus bus, Action<string> logAction)
    {
        Message = message;
        Key = key;
        Bus = bus;
        Log = logAction;
    }
}