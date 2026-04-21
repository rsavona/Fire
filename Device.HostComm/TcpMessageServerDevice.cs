using System.Text.Json.Nodes;
using DeviceSpace.Common;
using DeviceSpace.Common.BaseClasses;
using DeviceSpace.Common.Contracts;
using DeviceSpace.Common.TcpSocket;
using DeviceSpace.Common.TCP_Classes;
using DeviceSpace.Common.PayloadParsers;
using Serilog.Core;

namespace Device.HostComm;

/// <summary>
/// A TCP Server Device that expects ETX-terminated messages and parses them 
/// using a configurable strategy (Delimited, FixedLength, JSON, XML).
/// </summary>
public class TcpMessageServerDevice : TcpServerDeviceBase<SocketMessageProcessor>, IMessageProvider
{
    public event Func<object, object, Task>? MessageReceived;
    private readonly IPayloadParser _payloadParser;

    public TcpMessageServerDevice(IDeviceConfig config, IFireLogger logger, LoggingLevelSwitch swtch)
        : base(config, logger, 
               new SocketMessageProcessor(config.Name, logger), 
               swtch, 
               GetPort(config), 
               new DelimiterSetStrategy([(byte)'\u0003']), // ETX terminator (\u0003)
               GetMaxClients(config))
    {
        _payloadParser = PayloadParserFactory.Create(config);

        Processor.MessageReceived += async (msg) => 
        {
            if (msg is MessageEnvelope envelope)
            {
                var rawPayload = envelope.Payload.ToString() ?? string.Empty;
                var parsedPayload = _payloadParser.Parse(rawPayload);
                
                // Create a new envelope with the parsed JSON payload
                var newEnvelope = envelope with { Payload = parsedPayload };
                
                if (MessageReceived != null)
                {
                    await MessageReceived.Invoke(this, newEnvelope);
                }
            }
        };
    }

    private static int GetPort(IDeviceConfig config) => 
        config.Properties.TryGetValue("Port", out var p) ? Convert.ToInt32(p) : 0;
        
    private static int GetMaxClients(IDeviceConfig config) => 
        config.Properties.TryGetValue("MaxClients", out var c) ? Convert.ToInt32(c) : 1;
}
