using System.Text.Json.Nodes;
using DeviceSpace.Common;
using DeviceSpace.Common.BaseClasses;
using DeviceSpace.Common.Contracts;
using DeviceSpace.Common.Configurations;
using DeviceSpace.Common.PayloadParsers;
using Serilog.Core;

namespace Device.HostComm;

/// <summary>
/// A TCP Client Device that expects ETX-terminated messages and parses them 
/// using a configurable strategy (Delimited, FixedLength, JSON, XML).
/// </summary>
public class TcpMessageClientDevice : TcpClientDeviceBase, IMessageProvider
{
    public event Func<object, object, Task>? MessageReceived;
    private readonly IPayloadParser _payloadParser;

    public TcpMessageClientDevice(IDeviceConfig config, IFireLogger logger, LoggingLevelSwitch swtch)
        : base(config, logger, swtch, config.Properties.ContainsKey("HeartbeatIntervalMs"))
    {
        _payloadParser = PayloadParserFactory.Create(config);
    }

    protected override async Task HandleReceivedDataAsync(string incomingData)
    {
        // Strip ETX if present at the end
        string sanitized = incomingData.TrimEnd('\u0003');
        
        var parsedPayload = _payloadParser.Parse(sanitized);
        
        var topic = new MessageBusTopic(Config.Name, "Inbound", "Server");
        var envelope = new MessageEnvelope(topic, parsedPayload, 0, "Server");

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
