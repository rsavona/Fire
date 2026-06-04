using System.Buffers;

namespace Fusion.Common.Contracts;

public interface IMessageProcessor
{
    
    public event Func<object, Task>? MessageReceived;
    
     public event Action<string>? OnMessageError;
     Task<bool> ProcessMessageAsync(
        ReadOnlySequence<byte> buffer, 
        string clientKey, 
        Func<object, Task<bool>> sendResponse,
        CancellationToken token);

     public event Action<string> HeartbeatReceived;
     public string HandleResponse(string elementName, object payload);
}