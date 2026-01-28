
using System;
using DeviceSpace.Common.Contracts;

namespace DeviceSpace.Common;
public readonly record struct SourceIdentifier(string DeviceKey, string SourcePath);

public record MessageEnvelope
{
    public MessageBusTopic Destination;
    public readonly int Gin;
    public bool IsHighPriority { get; set; }
    public readonly string Client;
    public object  Payload;

    public  MessageEnvelope(string dest,string payload, int gin = 0, string client = "", bool highPriority = true)
    {
       IsHighPriority = highPriority;
        Gin = gin;
        Client = client;
        Payload = payload;
        Destination = new MessageBusTopic(dest);
    }
  public  MessageEnvelope(string dest,IDeviceMessage payload, int gin = 0, string client = "", bool highPriority = true)
    { 
        IsHighPriority = highPriority;
        Gin = gin;
        Client = client;
        Payload = payload;
        Destination = new MessageBusTopic(dest);
    }
  
     public  MessageEnvelope(MessageBusTopic dest,IDeviceMessage payload, int gin = 0, string client = "", bool highPriority = true)
    { 
        IsHighPriority = highPriority;
        Gin = gin;
        Client = client;
        Payload = payload;
        Destination = dest;
    }
     
       public  MessageEnvelope(MessageBusTopic dest,DeviceAnnouncement payload, int gin = 0, string client = "", bool highPriority = true)
    { 
        IsHighPriority = highPriority;
        Gin = gin;
        Client = client;
        Payload = payload;
        Destination = dest;
    }
    public  MessageEnvelope(MessageBusTopic dest,string payload, int gin = 0, string client = "", bool highPriority = true)
    { 
        IsHighPriority = highPriority;
        Gin = gin;
        Client = client;
        Payload = payload;
        Destination = dest;
    }

    public MessageEnvelope(MessageBusTopic dest, object payload,  int gin = 0, string client = "", bool highPriority = true)
    {
          IsHighPriority = highPriority;
        Gin = gin;
        Client = client;
        Payload = payload;
        Destination = dest;
    }

  

    // Auto-generated timestamp
    public DateTime Created { get; init; } = DateTime.UtcNow;

}