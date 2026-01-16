using System.Data;
using System.Text;
using DeviceSpace.Common.BaseClasses;
using DeviceSpace.Common.Configurations;
using DeviceSpace.Common.Contracts;
using DeviceSpace.Common.Enums;
using ILogger = Serilog.ILogger;

namespace Device.Virtual.Printer;

/// <summary>
/// A Virtual Zebra Printer acting as a Single-Client TCP Server.
/// This implementation allows only one active connection at a time.
/// </summary>
public class VirtualPrinterSingleClientDevice : TcpServerSingleClientDeviceBase<RawMessageProcessor>
{
    private bool _isPaperOut = false;
    private bool _isPaused = false;
    private bool _isHeadOpen = false;

    public VirtualPrinterSingleClientDevice(IDeviceConfig config, ILogger logger)
        : base(config, logger, new RawMessageProcessor(),
            ConfigurationLoader.GetRequiredConfig<int>(config.Properties, "Port"))
    {
        Logger.Information("[{Dev}] Initializing Single-Client Virtual Printer on Port {Port}",
            Config.Name, ConfigurationLoader.GetRequiredConfig<int>(config.Properties, "Port"));

        Watchdog.Start();
    }

    /// <summary>
    /// Processes incoming data from the single connected client.
    /// </summary>
    protected override async Task HandleReceivedDataAsync(byte[] buffer, int bytesRead, CancellationToken ct)
    {
        string hex = BitConverter.ToString(buffer, 0, bytesRead);
        Logger.Verbose("[{Dev}] RX RAW >> {Hex}", Config.Name, hex);

        string data = Encoding.ASCII.GetString(buffer, 0, bytesRead);

        // 1. Handle Status Request (~HS)
        if (data.Contains("~HS", StringComparison.OrdinalIgnoreCase))
        {
            Logger.Debug("[{Dev}] Status request (~HS) received.", Config.Name);
            string status = GenerateZebraStatus();
            byte[] statusBytes = Encoding.ASCII.GetBytes(status);

            // Send status back to the client using the base Send method
            await SendAsync(status);
            Logger.Verbose("[{Dev}] TX Status Response Sent.", Config.Name);
        }

        // 2. Handle Label Format (^XA ... ^XZ)
        if (data.Contains("^XA", StringComparison.OrdinalIgnoreCase))
        {
            if (_isPaused || _isPaperOut || _isHeadOpen)
            {
                Logger.Warning("[{Dev}] Print Rejected: PaperOut={Paper}, HeadOpen={Head}, Paused={Paused}",
                    Config.Name, _isPaperOut, _isHeadOpen, _isPaused);

                // Fire Error to move to Faulted/Warning state
                await Machine.FireAsync(Event.Error);
            }
            else
            {
                Logger.Information("[{Dev}] Printing ZPL label ({Len} chars)", Config.Name, data.Length);


                // Simulate physical print time
                await Task.Delay(250, ct);

                Tracker.IncrementInbound(); // Track processed label

                // Return to Connected state
                await Machine.FireAsync(Event.MessageSent);
                Logger.Debug("[{Dev}] Print Complete.", Config.Name);
            }
        }
    }

    public async Task SendStatusAsync(string zplStatus)
    {
        // 1. Get the stream from the base class
        var stream = GetStream();

        // 2. Validate the stream is active and writable
        if (stream != null && stream.CanWrite)
        {
            byte[] data = Encoding.ASCII.GetBytes(zplStatus);

            // 3. Perform the write
            await stream.WriteAsync(data, 0, data.Length);
            await stream.FlushAsync();

            Logger.Debug("[{Dev}] Successfully wrote {Len} bytes to client.", Config.Name, data.Length);
        }
        else
        {
            Logger.Warning("[{Dev}] GetStream() returned null or unwritable stream. Is a client connected?",
                Config.Name);
        }
    }

    #region Simulation Controls

    public void SimulatePaperOut(bool active)
    {
        _isPaperOut = active;
        _isPaused = active;

        // Use the explicit Enum paths and provide the required State and Event
        UpdateStatus(
            Machine.State,
            active ? Event.Error : Event.ProcessingComplete,
            active ? DeviceSpace.Common.Enums.DeviceHealth.Warning : DeviceSpace.Common.Enums.DeviceHealth.Normal,
            active ? "Paper Out Simulation Active" : "Paper Reloaded"
        );

        Logger.Information("[{Dev}] Status Updated: PaperOut={Active}", Config.Name, active);

        // 2. Update the Tracker (If your framework requires an error count increment)
        if (active)
        {
            Tracker.IncrementError("Paper Out"); // Optional: Track how many times the printer errored
        }

        // 3. Notify UI/WCS of the change
        UpdateAndNotify();
    }

    public void SimulateHeadOpen(bool active)
    {
        _isHeadOpen = active;
        _isPaused = active;

        UpdateStatus(
            Machine.State,
            active ? Event.Error : Event.ProcessingComplete,
            active ? DeviceSpace.Common.Enums.DeviceHealth.Warning : DeviceSpace.Common.Enums.DeviceHealth.Normal,
            active ? "Print Head Open" : "Print Head Closed"
        );

        Logger.Information("[{Dev}] Status Updated: HeadOpen={Active}", Config.Name, active);

        // 2. Update Tracker
        if (active)
        {
            Tracker.IncrementError("Head Open");
        }

        // 3. Notify UI/WCS
        UpdateAndNotify();
    }

    #endregion

    private string GenerateZebraStatus()
    {
        char paperOut = _isPaperOut ? '1' : '0';
        char headOpen = _isHeadOpen ? '1' : '0';
        char paused = _isPaused ? '1' : '0';

        // Standard Zebra ~HS format: 3 lines of comma-separated values
        var sb = new StringBuilder();
        sb.Append("123,0,0,1234,000,0,0,0,000,0,0,0\r\n");
        sb.Append($"001,0,0,{paused},{headOpen},{paperOut},0,0,00000000,1,000\r\n");
        sb.Append("1234,0,0000,00000,00,0,0,0,000,000,000\r\n");

        return sb.ToString();
    }

    protected override DeviceHealth MapStateToHealth(State state)
    {
        // Custom override: Even if Connected, if sensors are tripped, show Warning
        if (_isPaperOut || _isHeadOpen)
            return DeviceHealth.Warning;

        return base.MapStateToHealth(state);
    }
}