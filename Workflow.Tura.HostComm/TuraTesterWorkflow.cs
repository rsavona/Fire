using System.Text.Json.Nodes;
using DeviceSpace.Common;
using DeviceSpace.Common.BaseClasses;
using DeviceSpace.Common.Configurations;
using DeviceSpace.Common.Contracts;
using Serilog;

namespace Workflow.Tura.HostComm;

public class TuraTesterWorkflow : WorkflowBase
{
    private readonly string _targetDevice;
    private readonly int _intervalMs;
    private int _messageIndex = 0;
    private readonly char _etx = '\u0003';

    private readonly string[] _barcodes = [
        "1Z59W9A50120530866",
        "1Z59W9A50120532373",
        "1Z59W9A50120533265",
        "1Z59W9A50120534000"
    ];

    public TuraTesterWorkflow(IMessageBus bus, WorkflowConfig config, ILogger logger)
        : base(bus, config, logger)
    {
        _targetDevice = config.Properties.TryGetValue("TargetDevice", out var d) ? d.ToString() ?? "" : "";
        _intervalMs = config.Properties.TryGetValue("IntervalMs", out var i) ? Convert.ToInt32(i) : 5000;
        
        Logger.Information("[{Workflow}] Tura Tester (Simulating Host & Scanner) Initialized. Target: {Target}", 
            Config.Name, _targetDevice);

        MessageBus.SubscribeAsync(MessageBusTopic.DeviceStatus.ToString(), HandleDeviceStatusAsync);
    }

    private async Task HandleDeviceStatusAsync(MessageEnvelope? envelope, CancellationToken ct)
    {
        if (envelope?.Payload is DeviceStatusMessage status && 
            status.DeviceId.DeviceName == _targetDevice && 
            status.State.Equals("Connected", StringComparison.OrdinalIgnoreCase))
        {
            Logger.Information("[{Workflow}] Host Simulator CONNECTED. Starting simulation.", Config.Name);
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Yield();
        await base.ExecuteAsync(stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(_intervalMs, stoppingToken);

                if (IsSystemStarted)
                {
                    // 1. Simulate a PLC Scanner event
                    string barcode = _barcodes[_messageIndex];
                    _messageIndex = (_messageIndex + 1) % _barcodes.Length;

                    Logger.Information("[{Workflow}] SIMULATING SCAN: {Barcode}", Config.Name, barcode);
                    
                    // Publish to a topic that triggers Sorter Assignment
                    var scanTopic = "TURA_SCANNER.Inbound"; 
                    await MessageBus.PublishAsync(scanTopic, new MessageEnvelope(scanTopic, barcode, _messageIndex), stoppingToken);

                    // 2. Occasionally send a TST message from the "Host" to the Server
                    if (_messageIndex % 3 == 0)
                    {
                        string tstMsg = $"                    |TST |{DateTime.Now:yyyy/MM/dd HH:mm:ss.ffffff}|\u0003";
                        var tstTopic = $"{_targetDevice}.Outbound.Test";
                        await MessageBus.PublishAsync(tstTopic, new MessageEnvelope(tstTopic, tstMsg), stoppingToken);
                    }
                }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { Logger.Error(ex, "Error in tester loop"); }
        }
    }

    /// <summary>
    /// Simulates the REAL Host receiving a CSCN and sending back a ROUT.
    /// </summary>
    public async Task<object?> HandleHostReceive(MessageEnvelope envelope, CancellationToken ct)
    {
        string rawPayload = envelope.Payload?.ToString() ?? string.Empty;
        if (string.IsNullOrEmpty(rawPayload)) return null;

        try
        {
            // Manual parsing of the pipe-delimited string sent by Fire
            var parts = rawPayload.Split('|');
            if (parts.Length < 2) return null;

            var msgType = parts[1].Trim();

            if (msgType == "CSCN")
            {
                var barcode = parts[0].Trim();
                Logger.Information("[{Workflow}] HOST SIMULATOR: Received CSCN for {Barcode}. Sending log-formatted ROUT.", Config.Name, barcode);

                // Simulate the specific format seen in the logs: [BOM]Barcode |ROUT|Timestamp|004\r
                string timestamp = DateTime.Now.ToString("yyyy/MM/dd HH:mm:ss.ffffff");
                string routMsg = $"\u00BB\u00BF{barcode}  |ROUT|{timestamp}|004\r\n";
                
                // Return string to be sent back via TURA_CLIENT_TESTER
                return routMsg;
            }
            
            if (msgType == "ROUA")
            {
                Logger.Information("[{Workflow}] HOST SIMULATOR: Received ROUA (Echo) from Fire.", Config.Name);
            }
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Host Simulator failed to process message: {Payload}", rawPayload);
        }

        return null;
    }
}
