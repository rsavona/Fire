using System.Net.Sockets;

namespace DeviceSpace.Common.Contracts;

public interface IMessageProcessor
{
    
    public event Action<object>? MessageReceived;
    
     public event Action<string>? OnMessageError;
     Task<bool> ProcessMessageAsync(
        NetworkStream stream, 
        byte[] buffer, 
        int bytesRead, 
        string clientKey, 
        CancellationToken token);

     public event Action<string> HeartbeatReceived;
     public string HandleResponse(string deviceName, object payload);
}