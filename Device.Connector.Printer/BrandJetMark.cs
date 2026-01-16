using System.Net.Sockets;
using System.Threading.Tasks;
using DeviceSpace.Common.BaseClasses;
using DeviceSpace.Common.Contracts;
using Microsoft.Extensions.Logging;
using ILogger = Serilog.ILogger;

namespace Device.Connector.Printer
{
    public class BrandJetMark :  TcpClientDeviceBase,  IMessageProvider, ITcpPrinter
    {
        public BrandJetMark(IDeviceConfig config, ILogger jetLogger) : base(config, jetLogger) { }

        protected Task<(bool IsHealthy, string StatusMessage)> PerformProtocolCheckAsync(NetworkStream stream)
        {
            // JetMark might not have a polling command.
            // Since we are here, the TCP socket is connected.
            // We assume it is healthy.
            return Task.FromResult((true, "Connected"));
        }

        protected override Task HandleReceivedDataAsync(byte[] buffer, int bytesRead, CancellationToken ct)
        {
            throw new NotImplementedException();
        }

        public string Brand { get; init; }
        public PrinterType DestinationType { get; init; }
        public bool PrintError { get; init; }
        public ZplString ErrorLabel { get; init; }
        public Task PrintAsync(LabelToPrintMessage labelData)
        {
            throw new NotImplementedException();
        }

        public event Action<object, object>? MessageReceived;
    }
}