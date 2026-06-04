using System.Text.Json.Nodes;
using Fusion.Common;
using Fusion.Common.BaseClasses;
using Fusion.Common.Contracts;
using Fusion.Common.TcpSocket;
using Fusion.Common.TCP_Classes;
using Fusion.Common.PayloadParsers;
using Serilog.Core;

namespace Fusion.Element.HostComm;

/// <summary>
/// A TCP Server Element that expects CR-terminated messages and parses them 
/// using a configurable strategy (Delimited, FixedLength, JSON, XML).
/// </summary>
public class TcpMessageServerElement : TcpServerElementBase<SocketMessageProcessor>, IMessageProvider
{
    public event Func<object, object, Task>? MessageReceived;
    private readonly IPayloadParser _payloadParser;

    public TcpMessageServerElement(IMessageBus bus, IElementBlueprint config, IFireLogger logger, LoggingLevelSwitch swtch)
        : base(bus, config, logger, 
               new SocketMessageProcessor(config.Name, logger), 
               swtch, 
               GetPort(config), 
               GetTerminationStrategy(config, logger), 
               GetMaxClients(config))
    {
        _payloadParser = PayloadParserFactory.Create(config);

        Processor.MessageReceived += async (msg) => 
        {
            try
            {
                if (msg is MessageEnvelope envelope)
                {
                    Logger.Information("[{Dev}] Processor triggered MessageReceived for client {Client}", Config.Name, envelope.Client);
                    // Fire the state machine event to increment trackers in the base class
                    await Machine.FireAsync(Event.MessageReceived);
                    
                    string rawPayload = envelope.Payload?.ToString() ?? string.Empty;
                    Logger.Information("[{Dev}] Raw Message IN: {Payload}", Config.Name, rawPayload);

                    // Apply parsing logic based on configuration
                    string parsedPayload = _payloadParser.Parse(rawPayload);
                    Logger.Information("[{Dev}] Parsed Message IN: {Payload}", Config.Name, parsedPayload);

                    if (MessageReceived != null)
                    {
                        var parsedEnvelope = envelope with { Payload = parsedPayload };
                        Logger.Debug("[{Dev}] Forwarding parsed message to {Count} subscribers", Config.Name, MessageReceived.GetInvocationList().Length);
                        await MessageReceived.Invoke(this, parsedEnvelope);
                    }
                    else
                    {
                        Logger.Warning("[{Dev}] No subscribers for MessageReceived event", Config.Name);
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "[{Dev}] Critical error in MessageReceived handler", Config.Name);
            }
        };
    }

    private static ITerminationStrategy GetTerminationStrategy(IElementBlueprint config, IFireLogger logger)
    {
        var type = config.Properties.TryGetValue("TerminationType", out var t) ? t.ToString()?.ToUpper() : "DELIMITED";

        if (type == "FIXED")
        {
            var length = config.Properties.TryGetValue("FixedLength", out var l) ? Convert.ToInt32(l) : 0;
            return new FixedLengthTerminationStrategy(length);
        }

        // Default to \n
        var delimiterStr = config.Properties.TryGetValue("Delimiter", out var d) ? d.ToString() : "\\n";
        byte[] delimiters;

        // More robust matching for common escape sequences
        if (delimiterStr == "\\r" || delimiterStr == "\r") delimiters = [(byte)'\r'];
        else if (delimiterStr == "\\n" || delimiterStr == "\n") delimiters = [(byte)'\n'];
        else if (delimiterStr == "\\r\\n" || delimiterStr == "\r\n") return new SequenceTerminationStrategy([(byte)'\r', (byte)'\n']);
        else delimiters = System.Text.Encoding.ASCII.GetBytes(delimiterStr ?? "\n");

        logger.Information("[{Dev}] TCP Delimiter set to: {Bytes}", config.Name, BitConverter.ToString(delimiters));
        return new DelimiterSetStrategy(delimiters);
    }

    private static int GetPort(IElementBlueprint config) => 
        config.Properties.TryGetValue("Port", out var p) ? Convert.ToInt32(p) : 0;
        
    private static int GetMaxClients(IElementBlueprint config) => 
        config.Properties.TryGetValue("MaxClients", out var c) ? Convert.ToInt32(c) : 1;
}
