using System.Net.Sockets;
using System.Text;
using DeviceSpace.Common.Contracts;
using DeviceSpace.Common.TcpSocket;
using DeviceSpace.Common.TCP_Classes;
using Serilog.Core;

namespace DeviceSpace.Common.BaseClasses;

public class SocketServerDevice : TcpServerDeviceBase<SocketMessageProcessor>, IMessageProvider
{
    public event Func<object, object, Task>? MessageReceived;

    public SocketServerDevice(IMessageBus bus, IDeviceConfig config, IFireLogger logger, LoggingLevelSwitch swtch)
        : base(bus, config, logger, 
               new SocketMessageProcessor(config.Name, logger), 
               swtch, 
               GetPort(config), 
               GetTerminationStrategy(config), 
               GetMaxClients(config))
    {
        Processor.MessageReceived += async (msg) => 
        {
            if (MessageReceived != null)
            {
                await MessageReceived.Invoke(this, msg);
            }
        };
    }

    private static int GetPort(IDeviceConfig config)
    {
        return config.Properties.TryGetValue("Port", out var p) ? Convert.ToInt32(p) : 0;
    }

    private static int GetMaxClients(IDeviceConfig config)
    {
        return config.Properties.TryGetValue("MaxClients", out var c) ? Convert.ToInt32(c) : 1;
    }

    private static ITerminationStrategy GetTerminationStrategy(IDeviceConfig config)
    {
        var type = config.Properties.TryGetValue("TerminationType", out var t) ? t.ToString()?.ToUpper() : "DELIMITED";

        if (type == "FIXED")
        {
            var length = config.Properties.TryGetValue("FixedLength", out var l) ? Convert.ToInt32(l) : 0;
            return new FixedLengthTerminationStrategy(length);
        }

        // Default to delimited (e.g. CR, LF, or ETX)
        var delimiterStr = config.Properties.TryGetValue("Delimiter", out var d) ? d.ToString() : "\u0003";
        byte[] delimiters;
        
        if (delimiterStr == "\\r") delimiters = [(byte)'\r'];
        else if (delimiterStr == "\\n") delimiters = [(byte)'\n'];
        else if (delimiterStr == "\\r\\n") return new SequenceTerminationStrategy([(byte)'\r', (byte)'\n']);
        else delimiters = Encoding.ASCII.GetBytes(delimiterStr ?? "\u0003");

        return new DelimiterSetStrategy(delimiters);
    }

    public override async Task StartAsync(CancellationToken token)
    {
        await base.StartAsync(token);
    }

    public override async Task StopAsync(CancellationToken token)
    {
        await base.StopAsync(token);
    }
}
