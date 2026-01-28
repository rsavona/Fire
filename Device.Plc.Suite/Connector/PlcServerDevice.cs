using DeviceSpace.Common;
using DeviceSpace.Common.BaseClasses;
using DeviceSpace.Common.Configurations;
using DeviceSpace.Common.Contracts;
using DeviceSpace.Common.Enums;
using DeviceSpace.Common.Logging;
using Serilog;
using Serilog.Core;

namespace Device.Plc.Suite.Connector;

public class PlcServerDevice : TcpServerDeviceBase<PlcMessageProcessor>, IMessageProvider
{
    public event Action<object, object>? MessageReceived;
    public event Action<object, object>? OnMessageError;

    /// <summary>
    /// Represents a multi-client device implementation for connecting to PLCs (Programmable Logic Controllers).
    /// This class leverages a TCP-based server infrastructure to handle multiple concurrent client connections,
    /// processes incoming messages using a PLC-specific message processor, and emits appropriate events.
    /// </summary>
    /// <remarks>
    /// The implementation is based on the TcpServerMultiClientDeviceBase class, providing core support for client-management
    /// and state transitions. It integrates with a PlcMessageProcessor instance for handling PLC message parsing
    /// and processing. Events from the processor, such as MessageReceived and OnMessageError, are subscribed to internally.
    /// </remarks>
    /// <param name="config">
    /// An instance of IDeviceConfig providing configuration for the device, including settings such as device properties.
    /// </param>
    /// <param name="deviceLogger">
    /// An instance of ILogger used for logging messages and diagnostic information for the device.
    /// </param>
    public PlcServerDevice(IDeviceConfig config, ILogger deviceLogger, LoggingLevelSwitch ls)
        : base(
            config,
            deviceLogger,
            new PlcMessageProcessor(new PlcMessageParser(), config.Name),ls,
            ConfigurationLoader.GetRequiredConfig<int>(config.Properties, "DevicePort"), 99
        )
    {
        Processor.HeartbeatReceived += OnProcessorHBReceived;
        Processor.MessageReceived += OnProcessorMessageReceived;
        Processor.OnMessageError += OnProcessorMessageError;
    }

    /// <summary>
    /// Handles an error event raised by the message processor, logs the error details,
    /// updates the device status tracker, notifies clients about the status change,
    /// and triggers the state machine to transition to a faulted state if needed.
    /// </summary>
    /// <param name="errorMessage">
    /// A string representing the error message that describes the issue encountered by the message processor.
    /// </param>
    private void OnProcessorMessageError(string errorMessage)
    {
        OnError("Protocol", new Exception(errorMessage));
        OnMessageError?.Invoke(this, errorMessage);
    }

    /// <summary>
    /// Sends a framed response message to the specified client, updates the machine state,
    /// and notifies connected clients while incrementing the outbound message tracker.
    /// </summary>
    /// <param name="jsonPayload">
    /// A JSON-formatted string containing the payload of the message to be sent to the client.
    /// </param>
    /// <param name="clientKey">
    /// A unique identifier representing the target client to which the message will be sent.
    /// The identifier must correspond to a currently connected client.
    /// </param>
    /// <returns>
    /// A task that represents the asynchronous message sending operation.
    /// </returns>
    public async Task SendResponseAsync(string jsonPayload, string clientKey)
    {
        if (string.IsNullOrEmpty(clientKey) || !ConnectedClients.ContainsKey(clientKey)) return;

        await Server.SendResponseAsync(Key.DeviceName, clientKey, jsonPayload);
        await Machine.FireAsync(Event.MessageSent);
    }

    /// <summary>
    /// Handles the message received event from the processor, updating connected clients,
    /// incrementing inbound message count, transitioning the state machine, and triggering
    /// a notification for subscribers of the MessageReceived event.
    /// </summary>
    /// <param name="message">
    /// An instance of <see cref="MessageEnvelope"/> containing details about the received
    /// message, including client information, payload, and destination.
    /// </param>
    private void OnProcessorMessageReceived(MessageEnvelope message)
    { 
        Machine.Fire(Event.MessageReceived);
        Logger.Verbose("The Plc Message Processor received a message and sent it here the PlcServerDevice");
        if (!string.IsNullOrEmpty(message.Client))
            ConnectedClients.AddOrUpdate(message.Client, DateTime.UtcNow, (k, v) => DateTime.UtcNow);
        Logger.Verbose(
            "Here the Event is fired for Message received and it makes sure that the client is in the msintained client list. ");
        Logger.Verbose(" Next the MessageReceived is invoked to send the  message to the PLCDeviceManager");
        MessageReceived?.Invoke(this, message);
    }

    private void OnProcessorHBReceived(string client)
    {
        Tracker.HeartBeat();
        UpdateAndNotify();
    }
}

   
  