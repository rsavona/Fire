using System.Text;
using DeviceSpace.Common;
using DeviceSpace.Common.BaseClasses;
using DeviceSpace.Common.Configurations;
using DeviceSpace.Common.Contracts;
using DeviceSpace.Common.Enums;
using DeviceSpace.Common.Logging;
using DeviceSpace.Common.TCP_Classes;
using Serilog;
using Serilog.Core;

namespace Device.Plc.Suite.Connector;

public class PlcServerDevice : TcpServerDeviceBase<PlcMessageProcessor>, IMessageProvider
{
    public event Func<object, object, Task> MessageReceived;
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
    public PlcServerDevice(IDeviceConfig config, IFireLogger deviceLogger, LoggingLevelSwitch ls)
        : base(
            config,
            deviceLogger,
            new PlcMessageProcessor(new PlcMessageParser(), config.Name, deviceLogger), ls,
            ConfigurationLoader.GetRequiredConfig<int>(config.Properties, "DevicePort"),
            terminalStr: new DelimiterSetStrategy(  new byte[] { 0x03 })
            , 99)
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
    public async Task<bool>  SendResponseAsync(object  jsonPayload, string clientKey)
    {
        if (!ConnectedClients.ContainsKey(clientKey)) { Logger.Error( "Counldn't find client connection"); return false;}
        var ret = await Server.SendResponseAsync(Key.DeviceName, clientKey, jsonPayload);
        if (ret)
        {
            await Machine.FireAsync(Event.MessageSent);
            
        }

        return ret;
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
    private void OnProcessorMessageReceived(object message)
    { 
        if (message is not MessageEnvelope msg) return;
        
        Machine.Fire(Event.MessageReceived);
        Logger.LogInfoData("Message IN :{msg}", new object[] { msg.Payload } , msg.Gin.ToString());
        if (!string.IsNullOrEmpty(msg.Client))
            ConnectedClients.AddOrUpdate(msg.Client, DateTime.UtcNow, (k, v) => DateTime.UtcNow);
        MessageReceived?.Invoke(this, msg);
    }

    private void OnProcessorHBReceived(string client)
    {
        Tracker.HeartBeat();
        UpdateAndNotify();
    }
}

   
  