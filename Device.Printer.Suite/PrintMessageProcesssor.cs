using System;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using DeviceSpace.Common;
using DeviceSpace.Common.Contracts;
using DeviceSpace.Common.Logging;
using Serilog;

namespace Device.Virtual.Printer;

public class PrintMessageProcessor : IMessageProcessor
{
    private readonly IFireLogger _logger;

    public event Action<object>? MessageReceived;
    public event Action<string>? OnMessageError;
    public event Action<string> HeartbeatReceived;

    private static readonly Regex ZplDataRegex = new Regex(@"\^FD(.*?)\^FS", RegexOptions.Compiled);

    public PrintMessageProcessor(IFireLogger logger)
    {
        _logger = logger;
    }

    public async Task<bool> ProcessMessageAsync(NetworkStream stream, byte[] buffer, int bytesRead, string clientKey,
        CancellationToken token)
    {
        try
        {
            string data = Encoding.ASCII.GetString(buffer, 0, bytesRead);

            // If Host Status (~HS) is found, notify the system which client sent it
            if (data.Contains("~HS"))
            {
                _logger.Verbose("Heartbeat from Client: {ClientId}", clientKey);

                // Invoke the event with the stored ClientID
                HeartbeatReceived?.Invoke(clientKey);

            }

            // If ZPL Start (^XA) is found
            if (data.Contains("^XA"))
            {
                _logger.Information("Label received from {ClientId}", clientKey);
                MessageReceived?.Invoke( data);
            }
            return true;
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "ZPL Parsing Error.");
            OnMessageError?.Invoke($"ZPL Parsing ErrorClient: {ex.Message}");
            return false;
        }
    }

    public string HandleResponse(string deviceName, object payload)
    {
        return "Not Implimented";
    }

    private string ExtractZplData(string zpl)
    {
        var match = ZplDataRegex.Match(zpl);
        return match.Success ? match.Groups[1].Value : "No Data Found";
    }
}