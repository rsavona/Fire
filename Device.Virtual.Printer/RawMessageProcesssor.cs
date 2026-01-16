using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using DeviceSpace.Common;
using DeviceSpace.Common.Contracts;

namespace Device.Virtual.Printer;

public class RawMessageProcessor : IMessageProcessor
{
    public event Action<MessageEnvelope>? MessageReceived;
    public event Action<string>? OnMessageError;

    public event Action<string> HeartbeatReceived;

    // Regex to find data between ^FD and ^FS (Zebra Field Data tags)
    private static readonly Regex ZplDataRegex = new Regex(@"\^FD(.*?)\^FS", RegexOptions.Compiled);

    public async Task<bool> ProcessMessageAsync(NetworkStream stream, byte[] buffer, int bytesRead, string clientKey, CancellationToken token)
    {
        try
        {
            string data = Encoding.ASCII.GetString(buffer, 0, bytesRead);

            // Extract barcode/data if it exists in the ZPL
            string extractedData = ExtractZplData(data);

            var envelope = new MessageEnvelope(
                dest: new MessageBusTopic("PrinterInternal"),
                payload: new { RawZpl = data, ExtractedValue = extractedData },
                gin: 0,
                client: clientKey);

            MessageReceived?.Invoke(envelope);
            return true;
        }
        catch (Exception ex)
        {
            OnMessageError?.Invoke($"ZPL Parsing Error: {ex.Message}");
            return false;
        }
    }

    public string HandleResponse(string deviceName, string payload)
    {
        throw new NotImplementedException();
    }

    private string ExtractZplData(string zpl)
    {
        var match = ZplDataRegex.Match(zpl);
        return match.Success ? match.Groups[1].Value : "No Data Found";
    }
}