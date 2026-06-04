using Fusion.Common;
using Fusion.Common.BaseClasses;
using Fusion.Common.Contracts;
using Fusion.Common.TcpSocket;
using Fusion.Common.TCP_Classes;
using Serilog.Core;

namespace Fusion.Element.Scanner.Suite;

/// <summary>
/// A Socket Server that acts as the receiver for scanner data.
/// It listens for incoming connections from scanners (clients).
/// </summary>
public class ScannerServerElement : TcpServerElementBase<SocketMessageProcessor>, IMessageProvider
{
    public event Func<object, object, Task>? MessageReceived;

    public ScannerServerElement(IMessageBus bus, IElementBlueprint config, IFireLogger logger, LoggingLevelSwitch swtch)
        : base(bus, config, logger, 
               new SocketMessageProcessor(config.Name, logger), 
               swtch, 
               GetPort(config), 
               new DelimiterSetStrategy([(byte)'\r']), // Standard industrial scanners often terminate with CR
               GetMaxClients(config))
    {
        Processor.MessageReceived += async (msg) => 
        {
            if (msg is MessageEnvelope envelope)
            {
                Logger.Information("[{Dev}] Scanner Data Received from {Client}: {Data}", Config.Name, envelope.Client, envelope.Payload);
                
                // Fire the state machine event to increment trackers
                await Machine.FireAsync(Event.MessageReceived);

                if (MessageReceived != null)
                {
                    await MessageReceived.Invoke(this, envelope);
                }
            }
        };
    }

    private static int GetPort(IElementBlueprint config) => 
        config.Properties.TryGetValue("Port", out var p) ? Convert.ToInt32(p) : 0;
        
    private static int GetMaxClients(IElementBlueprint config) => 
        config.Properties.TryGetValue("MaxClients", out var c) ? Convert.ToInt32(c) : 1;
}
