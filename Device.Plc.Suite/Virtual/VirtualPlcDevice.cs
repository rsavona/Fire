using System.ComponentModel;
using System.Text;
using DeviceSpace.Common.BaseClasses;
using DeviceSpace.Common.Configurations;
using DeviceSpace.Common.Contracts;
using Microsoft.Extensions.Configuration;
using Serilog;
using Serilog.Core;

namespace Device.Plc.Suite.Virtual
{
    public class DecisionStep
    {
        public string DecisionPoint { get; set; }
        public int DelayMsAfterPreviousStep { get; set; }
    }


    public class VirtualPlcDevice : TcpClientDeviceBase
    {
  
        private readonly int _inductionFeq;
        private readonly List<DecisionStep> _myChain;
        private readonly int _totalTotes;

        public VirtualPlcDevice(
            IDeviceConfig config,
            ILogger logger,
           LoggingLevelSwitch levelSwitch)
            : base(config, logger, levelSwitch,  true)
        {
            string? rawChain = ConfigurationLoader.GetRequiredConfig<string>(Config.Properties, "DecisionPoints");
            string[]? segments = rawChain?.Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Select(s => s.Trim())
                .ToArray();
            _totalTotes = ConfigurationLoader.GetRequiredConfig<int>(Config.Properties, "TotalTotes");
            _myChain = new List<DecisionStep>();

            if (segments is { Length: >= 2 })
            {
                // 1. The first segment is your Induct Decision Point
                string firstDp = segments[0];

                // 2. The second segment is the Induction Frequency
                int.TryParse(segments[1], out _inductionFeq);

                // 3. Add the first step to the chain with 0 delay
                _myChain.Add(new DecisionStep
                {
                    DecisionPoint = firstDp,
                    DelayMsAfterPreviousStep = 0
                });

                // 4. Loop through the rest (Starting at Index 2)
                // We expect: [Name], [Delay], [Name], [Delay]
                for (int i = 2; i < segments.Length; i += 2)
                {
                    string dpName = segments[i];
                    int.TryParse(i + 1 < segments.Length ? segments[i + 1] : "0", out int delay);

                    _myChain.Add(new DecisionStep
                    {
                        DecisionPoint = dpName,
                        DelayMsAfterPreviousStep = delay
                    });
                }
            }
        }

        protected override Task HandleReceivedDataAsync(byte[] buffer, int bytesRead, CancellationToken ct)
        {
            throw new NotImplementedException();
        }

        /// <summary>
        /// Starts the device.
        /// </summary>
        /// <param name="ct"></param>
        public override async Task StartAsync(CancellationToken ct)
        {
            Logger.Information("[{Dev}] WCS Service Starting...", Config.Name);

            // 1. Trigger the 'Start' event in your State Machine
            await Machine.FireAsync(Event.Start);

            while (!ct.IsCancellationRequested)
            {
                while (Machine.State != State.Connected && Machine.State != State.Processing)
                {
                    if (ct.IsCancellationRequested) return;

                    await Task.Delay(1000, ct);

                    Logger.Debug("[{Dev}] Still waiting for connection... Current State: {State}",
                        Config.Name, Machine.State);
                }

                Logger.Information("[{Dev}] Connection established. Launching simulation chain.", Config.Name);

                await RunChainSimulationAsync(_totalTotes, _inductionFeq, _myChain, ct);
                
                await Task.Delay(10000, ct);
            }
        }

        protected override Task HandleReceivedDataAsync(string incomingData)
        {
            Logger.Information("[{Dev}] Received : {Data}", Config.Name, incomingData);
            return Task.CompletedTask;
        }

        protected override bool IsHeartbeat(string incomingData)
        {
            return PlcMessageParser.IsHeartbeatAck(incomingData);
        }

        protected override string GetHeartbeatMessage()
        {
            return PlcMessageParser.CreateRawHeartbeat(Key.DeviceName);
        }

        public async Task RunChainSimulationAsync(
            int totalTotes,
            int inductIntervalMs,
            List<DecisionStep> workflowChain,
            CancellationToken token)
        {
            int currentGin = 0;

            try
            {
                while (!token.IsCancellationRequested && (totalTotes == 0 || currentGin < totalTotes))
                {
                    currentGin++;

                    // Fire and forget the lifecycle of THIS specific tote
                    // This allows multiple totes to be "on the wire" at once
                    _ = Task.Run(() => ProcessToteLifecycleAsync(currentGin, workflowChain, token), token);

                    // Wait for the next induction interval
                    await Task.Delay(inductIntervalMs, token);
                }
            }
            catch (OperationCanceledException)
            {
                /* Clean exit */
            }
        }

        private async Task ProcessToteLifecycleAsync(int gin, List<DecisionStep> chain, CancellationToken token)
        {
            foreach (var step in chain)
            {
                // Wait for the tote to "travel" to the next decision point
                await Task.Delay(step.DelayMsAfterPreviousStep, token);

                if (Machine.State != State.Connected && Machine.State != State.Processing)
                    continue;

                try
                {
                    // 1. Create the Message
                    var request = PlcMessageParser.CreateDecisionRequest(Config.Name, step.DecisionPoint, gin);
                    string framedMessage = PlcMessageParser.BuildFrameFromMessage(Config.Name, request);

                    // 2. Send via the Virtual PLC base
                    await SendAsync(framedMessage, token);

                    Logger.Information("[{Dev}] GIN:{Gin} reached {DP}", Config.Name, gin, step.DecisionPoint);
                }
                catch (Exception ex)
                {
                    Logger.Error(ex, "[{Dev}] Failed sending DP {DP} for GIN {Gin}", Config.Name, step.DecisionPoint,
                        gin);
                }
            }
        }
    }
}