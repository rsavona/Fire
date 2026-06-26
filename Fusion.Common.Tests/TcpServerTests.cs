using System.Buffers;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Fusion.Common.Contracts;
using Fusion.Common.Logging;
using Fusion.Common.TcpSocket;
using Fusion.Common.TCP_Classes;
using Serilog;

namespace Fusion.Common.Tests;

public class TcpServerTests
{
    [Fact]
    public async Task TcpServer_RemovesClientWhenRemoteDisconnects()
    {
        int port = GetFreeTcpPort();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var logger = new FireLogger(new LoggerConfiguration().MinimumLevel.Fatal().CreateLogger());
        var processor = new RecordingMessageProcessor();
        using var server = new TcpServer(port, processor, logger, new DelimiterSetStrategy((byte)'\n'), timeoutMs: 1000);

        var listening = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var disconnected = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        server.ListenerStateChanged += state =>
        {
            if (state == TcpListenerState.Listening)
            {
                listening.TrySetResult(true);
            }
        };

        server.ClientConnectionChanged += (_, connected, _) =>
        {
            if (!connected)
            {
                disconnected.TrySetResult(true);
            }
        };

        var serverTask = Task.Run(() => server.StartAsync(timeout.Token));
        await listening.Task.WaitAsync(timeout.Token);

        using (var client = new TcpClient())
        {
            await client.ConnectAsync(IPAddress.Loopback, port, timeout.Token);
            byte[] payload = Encoding.ASCII.GetBytes("hello\n");
            await client.GetStream().WriteAsync(payload.AsMemory(), timeout.Token);
        }

        await disconnected.Task.WaitAsync(TimeSpan.FromSeconds(3));

        Assert.Equal(1, processor.MessagesProcessed);
        Assert.Equal(0, server.ConnectedClientCount);

        await server.StopAsync();
        timeout.Cancel();

        try
        {
            await serverTask.WaitAsync(TimeSpan.FromSeconds(3));
        }
        catch (OperationCanceledException)
        {
        }
    }

    private static int GetFreeTcpPort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private sealed class RecordingMessageProcessor : IMessageProcessor
    {
        public event Func<object, Task>? MessageReceived;
        public event Action<string>? OnMessageError;
        public event Action<string>? HeartbeatReceived;

        public int MessagesProcessed { get; private set; }

        public Task<bool> ProcessMessageAsync(
            ReadOnlySequence<byte> buffer,
            string clientKey,
            Func<object, Task<bool>> sendResponse,
            CancellationToken token)
        {
            if (buffer.IsEmpty)
            {
                OnMessageError?.Invoke("Empty message");
                return Task.FromResult(false);
            }

            MessagesProcessed++;
            HeartbeatReceived?.Invoke(clientKey);
            MessageReceived?.Invoke(Encoding.ASCII.GetString(buffer.ToArray()));
            return Task.FromResult(true);
        }

        public string HandleResponse(string elementName, object payload) => payload.ToString() ?? string.Empty;
    }
}
