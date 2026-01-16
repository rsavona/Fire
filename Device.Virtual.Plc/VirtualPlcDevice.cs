using Device.Connector.Plc;
using DeviceSpace.Common.BaseClasses;
using DeviceSpace.Common.Contracts;
using DeviceSpace.Common.Enums; // Added for State/Event access
using ILogger = Serilog.ILogger;

namespace Device.Virtual.Plc;

public class VirtualPlcDevice : TcpClientDeviceBase
{
    private readonly PlcTestMessageFactory _messageFactory;
    private readonly ConveyorTrackingPipeline _pipeline;

    public VirtualPlcDevice(IDeviceConfig config, ILogger logger) : base(config, logger)
    {
        Logger.Information("[{Dev}] Initializing Virtual PLC Device...", Config.Name);
        
        _messageFactory = new PlcTestMessageFactory(logger, config.Name);
        _pipeline = new ConveyorTrackingPipeline(logger);

        // Wire event: When pipeline hits a station, send a DReqM
        _pipeline.StationTriggered += async (idx, carton, station) => await HandleStationTrigger(carton, station);
        
        // Start conveyor only after first heartbeat success
        _messageFactory.HeartbeatReceived += () => 
        {
           
            Tracker.HeartBeat();
            UpdateAndNotify(false);
            if (!_pipeline.Started)
            {
                Logger.Information("[{Dev}] Initial Heartbeat RX. Starting conveyor pipeline.", Config.Name);
                _pipeline.Start();
            }
        };
    }

    public override async Task StartAsync(CancellationToken token)
    {
        Logger.Information("[{Dev}] Starting Background Service...", Config.Name);
        await base.StartAsync(token);
        _ = Task.Run(() => HeartbeatLoopAsync(token), token);
    }

    private async Task HandleStationTrigger(ContainerInfo carton, string station)
    {
        // Monitor state specifically during station triggers
        if (Machine.State != State.Connected)
        {
            Logger.Warning("[{Dev}] Station {Station} triggered for GIN {Gin}, but device is {State}. Message dropped.", 
                Config.Name, station, carton.Gin, Machine.State);
            return;
        }

        try
        {
            var request = _messageFactory.CreateTestDecisionRequest(station, carton.Gin);
            string rawFrame = _messageFactory.BuildFrameFromMessage(request);
            
            Logger.Debug("[{Dev}] Station Trigger: GIN {Gin} @ {Station}. Sending {Len} bytes.", 
                Config.Name, carton.Gin, station, rawFrame.Length);

            await SendAsync(rawFrame);
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "[{Dev}] Failed to send Decision Request for GIN {Gin}", Config.Name, carton.Gin);
        }
    }

    private async Task HeartbeatLoopAsync(CancellationToken token)
    {
        Logger.Debug("[{Dev}] Heartbeat loop started (5000ms interval).", Config.Name);
        
        while (!token.IsCancellationRequested)
        {
            try
            {
                if (Machine.State == State.Connected)
                {
                    Logger.Verbose("[{Dev}] Sending Heartbeat pulse...", Config.Name);
                    var hb = _messageFactory.CreateRawHeartbeat(this.Key.DeviceName);
                    await SendAsync(hb);
                }
                else
                {
                    Logger.Verbose("[{Dev}] Heartbeat suppressed: State is {State}", Config.Name, Machine.State);
                }
            }
            catch (Exception ex)
            {
                Logger.Warning("[{Dev}] Heartbeat transmission failed: {Msg}", Config.Name, ex.Message);
                // The base class usually handles the actual disconnect/reconnect logic
            }

            await Task.Delay(5000, token);
        }
        
        Logger.Information("[{Dev}] Heartbeat loop terminated.", Config.Name);
    }

    protected override async Task HandleReceivedDataAsync(byte[] buffer, int bytesRead, CancellationToken ct)
    {
        Logger.Verbose("[{Dev}] RX Data: {Bytes} bytes received.", Config.Name, bytesRead);
        
        try
        {
            // Factory parses ACK/DRespM and triggers appropriate logic
            await _messageFactory.HandleReceivedDataAsync(buffer, bytesRead, ct);
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "[{Dev}] Error parsing incoming PLC data stream.", Config.Name);
        }
    }
}