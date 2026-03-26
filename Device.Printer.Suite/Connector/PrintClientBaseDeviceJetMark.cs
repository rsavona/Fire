using System.Net.Sockets;
using DeviceSpace.Common.BaseClasses;
using DeviceSpace.Common.Contracts;
using Serilog.Core;
using ILogger = Serilog.ILogger;

namespace Device.Printer.Suite.Connector
{
    public class PrintClientBaseDeviceJetMark :  TcpClientDeviceBase,  IMessageProvider, ITcpPrintClientBase
    {
        /// <summary>
        /// Constructor
        /// </summary>
        /// <param name="config"></param>
        /// <param name="zebraLogger"></param>
        /// <param name="ls"></param>
        public PrintClientBaseDeviceJetMark(IDeviceConfig config, IFireLogger zebraLogger, LoggingLevelSwitch ls) : base(
            config, zebraLogger, ls)
        {
        }
        
        /// <summary>
        /// JetMark does not need to start.
        /// </summary>
        /// <param name="ct"></param>
        /// <returns></returns>
         protected override  Task OnStartAsync(CancellationToken ct)
         {
             if (Task.CompletedTask != null) return null;
             return null;
         }

         protected Task<(bool IsHealthy, string StatusMessage)> PerformProtocolCheckAsync(NetworkStream stream)
        {
            // JetMark might not have a polling command.
            // Since we are here, the TCP socket is connected.
            // We assume it is healthy.
            return Task.FromResult((true, "Connected"));
        }

        protected override Task HandleReceivedDataAsync(string incomingData)
    {
         Logger.Debug("[{Dev}] Received from printer: {Data}", Config.Name, incomingData);
        return Task.CompletedTask;
    }
      

        protected override string GetHeartbeatMessage()
        {
            return "";
        }

        public override Task SendHeartbeatAsync( CancellationToken token = default)
        {
            throw new NotImplementedException();
        }
        
        public string Brand { get; init; }
        public PrintDestination DestinationType { get; init; }
        public bool PrintError { get; init; }
        public ZplString ErrorLabel { get; init; }
        public Task PrintAsync(LabelToPrintMessage labelData)
        {
            throw new NotImplementedException();
        }

        public event Func<object, object, Task> MessageReceived;
    }
}