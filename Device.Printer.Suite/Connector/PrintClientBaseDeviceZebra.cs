using System.Data;
using System.Net.Sockets;
using System.Text;
using DeviceSpace.Common.BaseClasses;
using DeviceSpace.Common.Contracts;
using DeviceSpace.Common.Enums;
using ILogger = Serilog.ILogger;

namespace Device.Printer.Suite;

public class PrintClientBaseDeviceZebra : TcpClientDeviceBase, IMessageProvider, ITcpPrintClientBase
{
    public string Brand { get; init; } = "Zebra";
    public PrintDestination DestinationType { get; init; }
    public bool PrintError { get; init; }
    public ZplString ErrorLabel { get; init; }

    public PrintClientBaseDeviceZebra(IDeviceConfig config, ILogger zebraLogger) : base(config, zebraLogger) 
    {
        // Initialize ZplString default if needed
        ErrorLabel = ZplString.CreateErrorLabel("Zebra Connection Error");
    }

    /// <summary>
    /// Sends ZPL data over the persistent TCP stream.
    /// </summary>
    public async Task PrintAsync(LabelToPrintMessage labelData)
    {
        // Ensure we are in a connected state before attempting to write
        if (Machine.State is not  State.Connected)
        {
            Logger.Warning("[{Dev}] Print ignored: Device is in state {State}", Config.Name, Machine.State);
            return;
        }

        try
        {
            // Validate the ZPL structure using your ZplString class
            var validatedZpl = new ZplString(labelData.ZplData);
            byte[] data = Encoding.ASCII.GetBytes(validatedZpl.Value);

            // Get the stream from the base TcpClientDeviceBase
            NetworkStream? stream = GetStream(); 
            if (stream != null && stream.CanWrite)
            {
                Logger.Information("[{Dev}] Sending Print Job. Session: {SessionId}", Config.Name, labelData.SessionId);
                
                await stream.WriteAsync(data, 0, data.Length);
                await stream.FlushAsync();

                Tracker.IncrementOutbound();
                Logger.Verbose("[{Dev}] ZPL Sent: {Zpl}", Config.Name, validatedZpl.Value);
            }
            else
            {
                Logger.Error("[{Dev}] Stream is unavailable or unwritable.", Config.Name);
                await Machine.FireAsync(Event.Error); // Trigger reconnection logic in base
            }
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "[{Dev}] Critical error during PrintAsync for Session {Id}", Config.Name, labelData.SessionId);
  
        }
    }

    protected override Task HandleReceivedDataAsync(string incomingData)
    {
        throw new NotImplementedException();
    }


    protected override string GetHeartbeatMessage()
    {
        return "";
    }

    public override Task SendHeartbeatAsync(CancellationToken token = default)
    {
        throw new NotImplementedException();
    }

    protected override Task HandleReceivedDataAsync(byte[] buffer, int bytesRead, CancellationToken ct)
    {
        // Zebra HS responses or status updates would be parsed here
        var response = Encoding.ASCII.GetString(buffer, 0, bytesRead);
        Logger.Debug("[{Dev}] Received from printer: {Data}", Config.Name, response);
        return Task.CompletedTask;
    }

    public event Action<object, object>? MessageReceived;
}