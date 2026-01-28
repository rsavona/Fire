using System.ComponentModel;
using DeviceSpace.Common.BaseClasses;
using DeviceSpace.Common.Contracts;
using Microsoft.Extensions.Configuration;
using Serilog;

namespace Device.Plc.Suite.Virtual
{
    public class VirtualPlcDevice : TcpClientDeviceBase
    {
        private readonly System.Timers.Timer _shiftTimer;
        private ConveyorHardwareConfig? _hwConfig;
        private CancellationTokenSource _simCts;
        private Task _simulationTask;
        private Container?[] _trackingArray = new Container?[240];

        private List<string> _inductPoints;
        
        public VirtualPlcDevice(
            IDeviceConfig config,
            ILogger logger
        )
            : base(config, logger, true)
        {
            _shiftTimer = new System.Timers.Timer(100);
            _shiftTimer.Elapsed += (s, e) => ShiftTracking();
            
            // Initialize the timer (e.g., 100ms pulse for the warehouse conveyor simulation)
        }


        public override async Task StartAsync(CancellationToken cancellationToken)
        {
            Logger.Information("[{Dev}] WCS Service Starting...", Config.Name);

            // 1. Trigger the 'Start' event in your State Machine
            await Machine.FireAsync(Event.Start);

            // 2. Start the simulation loop in the background, bound to the service lifetime
            _ = Task.Run(() => RunDecisionRequestSimulationAsync(0, 500, cancellationToken), cancellationToken);
        }

        protected override Task<bool> HandleReceivedDataAsync(byte[] buffer, int bytesRead, CancellationToken ct)
        {
            return Task.FromResult(true);
        }


        public void InitializeHardware(string configPath)
        {
            _hwConfig = ConveyorConfigReader.LoadFromFile(configPath);

            if (_hwConfig != null)
            {
                Logger.Information("[{Dev}] Hardware Emulation Loaded: {Desc}",
                    Config.Name, _hwConfig.Configuration.Description);

                // Example: Access the verification scanner index
                var verifyScanner = _hwConfig.Configuration.Nodes
                    .FirstOrDefault(n => n.Id == "SCAN_VERIFY");

                if (verifyScanner != null)
                {
                    Logger.Debug("[{Dev}] Verification Scanner active at cell {Index}",
                        Config.Name, verifyScanner.CellIndex);
                }
            }
        }

        private void ShiftTracking()
        {
            // Shift logic: Move everything forward one slot
            for (int i = _trackingArray.Length - 1; i > 0; i--)
            {
                _trackingArray[i] = _trackingArray[i - 1];
                CheckStationTriggers(i);
            }

            _trackingArray[0] = null;
        }


        private void CheckStationTriggers(int currentIndex)
        {
            var gin = _trackingArray[currentIndex];
            if (gin == null) return;

            // Based on your 240-cell config (5 inches per cell):

            // Induct Scanner (at 2 feet / cell 5)
            if (currentIndex == 5)
            {
                Logger.Information("[{Dev}] Tote {Gin} arrived at Induct Scanner.", Config.Name, gin);
                // Trigger a Decision Request message here if needed
            }

            // Printer 1 (at 20 feet / cell 48)
            if (currentIndex == 48)
            {
                Logger.Information("[{Dev}] Tote {Gin} passing Printer 1.", Config.Name, gin);
            }

            // Verification Scanner (at 85 feet / cell 204)
            if (currentIndex == 204)
            {
                Logger.Information("[{Dev}] Tote {Gin} arrived at Verification.", Config.Name, gin);
                // This is where you would typically send a DReqM (Decision Request)
            }
        }

        /// <summary>
        /// Continuously sends Decision Requests (DReqM) to simulate a high-volume induct process.
        /// </summary>
        /// <param name="count">Number of requests to send (set to 0 for infinite).</param>
        /// <param name="delayMs">Delay between each request in milliseconds.</param>
        /// <param name="token">Cancellation token to stop the loop.</param>
        public async Task RunDecisionRequestSimulationAsync(int count, int delayMs, CancellationToken token)
        {
            int sentCount = 0;
            int globalGin = 0;

            // Use a try-catch outside the loop or inside depending on if you want the 
            // whole simulation to die on a TaskCanceledException. 
            // Usually, for simulations, we want a clean exit.
            try
            {
                while (!token.IsCancellationRequested)
                {
                    // 1. Wait for the specified delay first
                    await Task.Delay(delayMs, token);

                    // 2. Check if the machine is in a valid state to send messages
                    if (Machine.State == State.Connected || Machine.State == State.Processing)
                    {
                        globalGin++;
                        // Create the message using your factory
                        var request = PlcMessageFactory.CreateDecisionRequest(
                            device: Config.Name,
                            decisionPoint: "PNA_Induct", globalGin);

                        string framedMessage = PlcMessageFactory.BuildFrameFromMessage(Config.Name, request);

                        try
                        {
                            await SendAsync(framedMessage, token);

                            sentCount++;

                            // Extract GIN for specific logging - assuming cast is safe based on factory method
                            var gin = ((DecisionRequestPayload)request.Payload).Gin;
                            Logger.Information("[{Dev}] Simulation: Sent Decision Request #{Count} (GIN: {Gin})",
                                Config.Name, sentCount, gin);
                        }
                        catch (Exception ex)
                        {
                            Logger.Error(ex, "[{Dev}] Simulation send failure.", Config.Name);
                            await Machine.FireAsync(Event.ConnectionLost);
                            break; // Exit loop on connection failure
                        }

                        // 3. Exit if we reached the target count (0 = infinite)
                        if (count > 0 && sentCount >= count)
                        {
                            Logger.Information("[{Dev}] Simulation completed. {Count} requests sent.", Config.Name,
                                sentCount);
                            break;
                        }
                    }
                    else
                    {
                        // Optional: Log that we are skipping because the machine is offline/error
                        Logger.Warning("[{Dev}] Simulation: Skipping pulse, Machine State is {State}", Config.Name,
                            Machine.State);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                Logger.Information("[{Dev}] Simulation cancelled via token.", Config.Name);
            }
            catch (Exception ex)
            {
                Logger.Fatal(ex, "[{Dev}] Unexpected error in simulation loop.", Config.Name);
            }
        }

        protected override Task HandleReceivedDataAsync(string incomingData)
        {
            Logger.Information("[{Dev}] Received : {Data}", Config.Name, incomingData);
            return Task.CompletedTask;
        }

        protected override bool IsHeartbeat(string incomingData)
        {
            return PlcMessageFactory.IsHeartbeatAck(incomingData);
        }

        protected override string GetHeartbeatMessage()
        {
            return PlcMessageFactory.CreateRawHeartbeat(Key.DeviceName);
        }
    }
}