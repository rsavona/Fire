using System.Net.Sockets;
using DeviceSpace.Common.BaseClasses;
using DeviceSpace.Common.Contracts;
using ILogger = Serilog.ILogger;

namespace Device.Printer.Suite.Connector
{
    public class PrintClientBaseDeviceJetMark :  TcpClientDeviceBase,  IMessageProvider, ITcpPrintClientBase
    {
        public PrintClientBaseDeviceJetMark(IDeviceConfig config, ILogger jetLogger) : base(config, jetLogger) { }

        protected Task<(bool IsHealthy, string StatusMessage)> PerformProtocolCheckAsync(NetworkStream stream)
        {
            // JetMark might not have a polling command.
            // Since we are here, the TCP socket is connected.
            // We assume it is healthy.
            return Task.FromResult((true, "Connected"));
        }

        protected override Task HandleReceivedDataAsync(string incomingData)
        {
            throw new NotImplementedException();
        }

      

        protected override string GetHeartbeatMessage()
        {
            return "";
        }

        public override Task SendHeartbeatAsync( CancellationToken token = default)
        {
            throw new NotImplementedException();
        }

        protected override Task HandleReceivedDataAsync(byte[] buffer, int bytesRead, CancellationToken ct)
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

        public event Action<object, object>? MessageReceived;
    }
}