using System.Buffers;
using System.Net.Sockets;
using System.Text;
using Fusion.Common;
using Fusion.Common.BaseClasses;
using Fusion.Common.Configurations;
using Fusion.Common.Contracts;
using Fusion.Common.TcpSocket;
using Fusion.Common.TCP_Classes;
using Serilog.Core;

namespace Fusion.Element.Scanner.Suite;

/// <summary>
/// A Socket Client that acts as a connector to a scanner (or Scanner Server).
/// It reads incoming barcodes and publishes them them to the Fusion Message Bus.
/// </summary>
public class ScannerClientElement : TcpClientElementBase, IMessageProvider
{
    public event Func<object, object, Task>? MessageReceived;
    private readonly string _prefix;
    private readonly string _suffix;
    private readonly string _terminationString;
    private readonly ITerminationStrategy _strategy;

    public ScannerClientElement(IMessageBus bus, IElementBlueprint config, IFireLogger logger, LoggingLevelSwitch swtch)
        : base(bus, config, logger, swtch, false)
    {
        _prefix = ConfigurationLoader.GetOptionalConfig(config.Properties, "Prefix", "");
        _suffix = ConfigurationLoader.GetOptionalConfig(config.Properties, "Suffix", "");
        _terminationString = ConfigurationLoader.GetOptionalConfig(config.Properties, "TerminationChar", "\r");

        // Set the termination strategy based on config
        byte[] delimiter = Encoding.ASCII.GetBytes(_terminationString);
        _strategy = new SequenceTerminationStrategy(delimiter);
    }

    protected override ITerminationStrategy? ReceiveTerminationStrategy => _strategy;

    /// <summary>
    /// Sends a trigger or data to the server if needed.
    /// </summary>
    public async Task SendScanAsync(string barcode, CancellationToken ct = default)
    {
        if (IsConnected)
        {
            Logger.Information("[{Dev}] Sending data to scanner server: {Barcode}", Config.Name, barcode);
            string message = $"{_prefix}{barcode}{_suffix}{_terminationString}";
            await SendAsync(message, ct);
        }
        else
        {
            Logger.Warning("[{Dev}] Cannot send: Not connected.", Config.Name);
        }
    }

    protected override async Task HandleReceivedDataAsync(string incomingData)
    {
        // Clean the incoming data based on prefix/suffix
        string processed = incomingData;
        
        if (!string.IsNullOrEmpty(_suffix) && processed.EndsWith(_suffix))
            processed = processed.Substring(0, processed.Length - _suffix.Length);

        if (!string.IsNullOrEmpty(_prefix) && processed.StartsWith(_prefix))
            processed = processed.Substring(_prefix.Length);

        Logger.Information("[{Dev}] Barcode Received: {Data}", Config.Name, processed);
        
        var topic = new MessageBusTopic(Config.Name, "Scan");
        var envelope = new MessageEnvelope(topic, processed, 0, "Scanner");

        if (MessageReceived != null)
        {
            await MessageReceived.Invoke(this, envelope);
        }
    }

    protected override string GetHeartbeatMessage() => string.Empty;
}
