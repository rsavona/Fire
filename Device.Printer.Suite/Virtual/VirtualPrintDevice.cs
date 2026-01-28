using System.Text;
using DeviceSpace.Common.BaseClasses;
using DeviceSpace.Common.Contracts;
using DeviceSpace.Common.Enums;
using Serilog.Core;

namespace Device.Virtual.Printer;

public class VirtualPrintDevice : TcpServerDeviceBase<RawMessageProcessor>
{
    private bool _isPaperOut = false;
    private bool _isPaused = false;
    private bool _isHeadOpen = false;

    public VirtualPrintDevice(IDeviceConfig config, Serilog.ILogger logger, LoggingLevelSwitch ls)
        : base(config, logger,  new RawMessageProcessor(), ls, 
            config.Properties.TryGetValue("Port", out var p) ? Convert.ToInt32(p) : 9100, 
            maxClients: 1) // Forced to single client
    { }

    // Override the base event to handle ZPL logic
    protected  void OnServerMessageReceived(string clientKey, string payload, DateTime timestamp)
    {

        // 2. Handle Zebra specific commands
        if (payload.Contains("~HS", StringComparison.OrdinalIgnoreCase))
        {
            _ = SendAsync(clientKey, GenerateZebraStatus());
        }

        if (payload.Contains("^XA", StringComparison.OrdinalIgnoreCase))
        {
            ProcessLabelJob(payload);
        }
    }

    private void ProcessLabelJob(string zpl)
    {
        if (_isPaused || _isPaperOut || _isHeadOpen)
        {
            Logger.Warning("[{Dev}] Print Failed: Hardware error state active.", Config.Name);
            Machine.Fire(Event.RecoverableError);
        }
        else
        {
            Logger.Information("[{Dev}] Processing Label Job...", Config.Name);
            // Simulate physical printer delay
            Task.Delay(300).ContinueWith(_ => Logger.Debug("[{Dev}] Job Printed Successfully.", Config.Name));
        }
    }

    private string GenerateZebraStatus()
    {
        char paper = _isPaperOut ? '1' : '0';
        char head = _isHeadOpen ? '1' : '0';
        char pause = _isPaused ? '1' : '0';

        var sb = new StringBuilder();
        sb.Append("123,0,0,1234,000,0,0,0,000,0,0,0\r\n");
        sb.Append($"001,0,0,{pause},{head},{paper},0,0,00000000,1,000\r\n");
        sb.Append("1234,0,0000,00000,00,0,0,0,000,000,000\r\n");
        return sb.ToString();
    }

    public void SimulateError(string errorType, bool active)
    {
        if (errorType == "Paper") _isPaperOut = active;
        if (errorType == "Head") _isHeadOpen = active;
        _isPaused = active;

        Machine.Fire(Event.RecoverableError);
        Logger.Information("[{Dev}] Sensor Simulation: {Type} is {Status}", Config.Name, errorType, active ? "Active" : "Cleared");
    }

    protected override DeviceHealth MapStateToHealth(State state)
    {
        if (_isPaperOut || _isHeadOpen) return DeviceHealth.Warning;
        return base.MapStateToHealth(state);
    }
}