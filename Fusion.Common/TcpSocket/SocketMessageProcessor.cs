using System.Net.Sockets;
using System.Text;
using Fusion.Common.Contracts;

namespace Fusion.Common.TcpSocket;

public class SocketMessageProcessor : IMessageProcessor
{
    private readonly string _deviceName;
    private readonly IFireLogger _logger;

    public event Action<string>? HeartbeatReceived;
    public event Func<object, Task>? MessageReceived;
    public event Action<string>? OnMessageError;

    public SocketMessageProcessor(string deviceName, IFireLogger logger)
    {
        _deviceName = deviceName;
        _logger = logger;
    }

    public async Task<bool> ProcessMessageAsync(NetworkStream stream, byte[] rawMessage, int len, string clientKey,
        CancellationToken token)
    {
        try
        {
            int offset = 0;
            // Strip UTF-8 BOM if present (0xEF, 0xBB, 0xBF)
            if (len >= 3 && rawMessage[0] == 0xEF && rawMessage[1] == 0xBB && rawMessage[2] == 0xBF)
            {
                offset = 3;
            }
            // Also handle cases where only the last two bytes of the BOM might be present in the buffer
            else if (len >= 2 && rawMessage[0] == 0xBB && rawMessage[1] == 0xBF)
            {
                offset = 2;
            }

            string strMessage = Encoding.UTF8.GetString(rawMessage, offset, len - offset).TrimEnd('\r', '\n', '\0');
            _logger.Information("[{Client}] Raw Message Received: {Msg}", clientKey, strMessage);

            // Wrap in an envelope so we can pass the client key up the stack
            var topic = new MessageBusTopic(_deviceName, "Inbound");
            var envelope = new MessageEnvelope(topic, strMessage, 0, clientKey);

            if (MessageReceived != null)
            {
                await MessageReceived.Invoke(envelope);
            }
            return true;
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "[{Client}] Error processing socket message.", clientKey);
            OnMessageError?.Invoke(ex.Message);
            return false;
        }
    }

    public string HandleResponse(string deviceName, object payload)
    {
        return payload?.ToString() ?? string.Empty;
    }
}
