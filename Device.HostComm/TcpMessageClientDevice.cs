using System.Text.Json.Nodes;
using DeviceSpace.Common;
using DeviceSpace.Common.BaseClasses;
using DeviceSpace.Common.Contracts;
using DeviceSpace.Common.Configurations;
using DeviceSpace.Common.PayloadParsers;
using Serilog.Core;

namespace Device.HostComm;

/// <summary>
/// A TCP Client Device that expects CR-terminated messages and parses them 
/// using a configurable strategy (Delimited, FixedLength, JSON, XML).
/// </summary>
public class TcpMessageClientDevice : TcpClientDeviceBase, IMessageProvider
{
    public event Func<object, object, Task>? MessageReceived;
    private readonly IPayloadParser _payloadParser;

    public TcpMessageClientDevice(IMessageBus bus, IDeviceConfig config, IFireLogger logger, LoggingLevelSwitch swtch)
        : base(bus, config, logger, swtch, config.Properties.ContainsKey("HeartbeatIntervalMs"))
    {
        _payloadParser = PayloadParserFactory.Create(config);
    }

    protected override async Task HandleReceivedDataAsync(string incomingData)
    {
        // Strip CR if present at the end
        string sanitized = incomingData.TrimEnd('\r');
        
        // Use a generic topic that matches the chamber route source
        var topic = new MessageBusTopic(Config.Name, "Inbound");
        var envelope = new MessageEnvelope(topic, sanitized, 0, "Server");

        if (MessageReceived != null)
        {
            await MessageReceived.Invoke(this, envelope);
        }
    }

    protected override string GetHeartbeatMessage()
    {
        return Config.Properties.TryGetValue("HeartbeatMessage", out var m) 
            ? m.ToString() ?? string.Empty 
            : string.Empty;
    }

    protected override bool IsHeartbeat(string incomingData)
    {
        if (Config.Properties.TryGetValue("HeartbeatAck", out var ack))
        {
            return incomingData.Contains(ack.ToString() ?? "HB_ACK");
        }
        return false;
    }
}
