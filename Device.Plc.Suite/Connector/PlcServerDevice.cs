using System.Text;
using System.Text.Json.Nodes;
using Device.Plc.Suite.Messages;
using DeviceSpace.Common;
using DeviceSpace.Common.BaseClasses;
using DeviceSpace.Common.Configurations;
using DeviceSpace.Common.Contracts;
using DeviceSpace.Common.Enums;
using DeviceSpace.Common.Logging;
using DeviceSpace.Common.TCP_Classes;
using Serilog;
using Serilog.Core;
using Serilog.Events;

namespace Device.Plc.Suite.Connector;

public class PlcServerDevice : TcpServerDeviceBase<PlcMessageProcessor>, IMessageProvider
{
    public event Func<object, object, Task> MessageReceived;
    public event Action<object, object>? OnMessageError;

    private readonly GinSequenceLearner _ginLearner;

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
            terminalStr: new DelimiterSetStrategy(new byte[] { 0x03 })
            , 99)
    {
        _ginLearner = new GinSequenceLearner(config.Name, deviceLogger);
        Processor.HeartbeatReceived += OnProcessorHBReceived;
        Processor.MessageReceived += OnProcessorMessageReceived;
        Processor.OnMessageError += OnProcessorMessageError;
        LogControl.SetDeviceLevel(Key.DeviceName, LogEventLevel.Verbose);
    }

    protected override async Task OnEnterStoppingAsync()
    {
        _ginLearner.SaveData();
        await base.OnEnterStoppingAsync();
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
    public async Task<bool> SendResponseAsync(object jsonPayload, string clientKey)
    {
        if (!ConnectedClients.ContainsKey(clientKey))
        {
            Logger.Error("Counldn't find client connection");
            return false;
        }

        // 1. Ensure the payload is framed if it's a raw object
        object finalPayload = jsonPayload;
        if (jsonPayload is not string)
        {
            finalPayload = PlcMessageParser.FrameResponse(jsonPayload, Key.DeviceName);
        }

        var ret = await Server.SendResponseAsync(Key.DeviceName, clientKey, finalPayload);
        if (ret)
        {
            string decisionPoint = "UNKNOWN";
            int gin = 0;

            // 2. Extract IDs for the transaction timer
            if (jsonPayload is DecisionResponseMessage respMsg && respMsg.Payload is DecisionResponsePayload drp)
            {
                decisionPoint = drp.DecisionPoint;
                gin = drp.Gin;
            }
            else if (jsonPayload is DecisionResponsePayload payload)
            {
                decisionPoint = payload.DecisionPoint;
                gin = payload.Gin;
            }
            else if (jsonPayload is string jsonStr)
            {
                // If it's a string, it might be framed or raw JSON
                if (Processor.GetParser().TryParseToPlcMessage(jsonStr, out var parsed) &&
                    parsed?.Payload is DecisionResponsePayload d)
                {
                    decisionPoint = d.DecisionPoint;
                    gin = d.Gin;
                }
                else
                {
                    try
                    {
                        var node = JsonNode.Parse(jsonStr);
                        decisionPoint = node?["DecisionPoint"]?.GetValue<string>() ?? "UNKNOWN";
                        gin = node?["GIN"]?.GetValue<int>() ?? 0;
                    }
                    catch { Logger.LogError("Unable to parse outgoing message"); }
                }
            }

            var ptime = StopTransaction(decisionPoint, gin);
            await Machine.FireAsync(Event.MessageSent);
            Logger.Verbose("Message OUT {ptime}ms: {msg}", ptime, finalPayload);
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

        string gin = msg.Gin.ToString();
        string barcodes = "";
        string dp = "";

        if (msg.Payload is DecisionRequestPayload drp)
        {
            barcodes = string.Join(",", drp.Barcodes);
            dp = drp.DecisionPoint;
            gin = drp.Gin.ToString();
            StartTransaction(drp.DecisionPoint, drp.Gin);
            _ginLearner.ProcessGin(dp, drp.Gin);
        }
        else if (msg.Payload is DecisionUpdatePayload dup)
        {
            barcodes = string.Join(",", dup.Barcodes);
            dp = dup.DecisionPoint;
            gin = dup.Gin.ToString();
            _ginLearner.ProcessGin(dp, dup.Gin);
        }

        Logger.LogDebug($"Message IN: {msg.Payload}", gin, barcodes, dp);

        if (!string.IsNullOrEmpty(msg.Client))
            ConnectedClients.AddOrUpdate(msg.Client, DateTime.UtcNow, (k, v) => DateTime.UtcNow);
        MessageReceived?.Invoke(this, msg);
    }

    /// <summary>
    /// Handles the heartbeat received event from the processor, updating the device status tracker,
    /// </summary>
    /// <param name="client"></param>
    private void OnProcessorHBReceived(string client)
    {
        Tracker.HeartBeat();
        UpdateAndNotify();
    }
}