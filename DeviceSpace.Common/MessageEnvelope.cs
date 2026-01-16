
using System;

namespace DeviceSpace.Common;

public record MessageEnvelope
{
    public MessageBusTopic Destination;
    public readonly int Gin;
    public readonly string Client;
    public object Payload;

    public MessageEnvelope(MessageBusTopic dest,object payload, int gin = 0, string client = "")
    {
       
        Gin = gin;
        Client = client;
        Payload = payload;
        Destination = dest;
    }

    // Auto-generated timestamp
    public DateTime Created { get; init; } = DateTime.UtcNow;

}