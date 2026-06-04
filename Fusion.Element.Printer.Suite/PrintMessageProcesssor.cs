using System;
using System.Buffers;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Fusion.Common;
using Fusion.Common.Contracts;
using Fusion.Common.Logging;
using Serilog;

namespace Fusion.Element.Virtual.Printer;

public class PrintMessageProcessor : IMessageProcessor
{
    private readonly IFireLogger _logger;

    public event Func<object, Task>? MessageReceived;
    public event Action<string>? OnMessageError;
    public event Action<string> HeartbeatReceived;

    private static readonly Regex ZplDataRegex = new Regex(@"\^FD(.*?)\^FS", RegexOptions.Compiled);

    public PrintMessageProcessor(IFireLogger logger)
    {
        _logger = logger;
    }

    public async Task<bool> ProcessMessageAsync(ReadOnlySequence<byte> buffer, string clientKey,
        Func<object, Task<bool>> sendResponse,
        CancellationToken token)
    {
        try
        {
            // Convert ReadOnlySequence to string efficiently
            string data;
            if (buffer.IsSingleSegment)
            {
                data = Encoding.ASCII.GetString(buffer.First.Span);
            }
            else
            {
                data = Encoding.ASCII.GetString(buffer.ToArray());
            }

            // If Host Status (~HS) is found, notify the system which client sent it
            if (data.Contains("~HS"))
            {
                _logger.Verbose("Heartbeat from Client: {ClientId}", clientKey);

                // Invoke the event with the stored ClientID
                HeartbeatReceived?.Invoke(clientKey);
                return true;

            }

            // If ZPL Start (^XA) or XML-style tags are found
            if (data.Contains("^XA") || data.Contains("<labels>") || data.Contains("</labels>"))
            {
                _logger.Information("Label received from {ClientId}", clientKey);
                if (MessageReceived != null)
                {
                    var envelope = new MessageEnvelope(new MessageBusTopic("VirtualPrinter", "Inbound"), data, 0, clientKey);
                    await MessageReceived.Invoke(envelope);
                }
            }
            return true;
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Label Parsing Error.");
            OnMessageError?.Invoke($"Label Parsing Error Client: {ex.Message}");
            return false;
        }
    }

    public string HandleResponse(string elementName, object payload)
    {
        return "Not Implimented";
    }

    private string ExtractZplData(string zpl)
    {
        var match = ZplDataRegex.Match(zpl);
        return match.Success ? match.Groups[1].Value : "No Data Found";
    }
}