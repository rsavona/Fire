using System;

namespace DeviceSpace.Common
{
    /// <summary>
    /// A standardized payload for messages sent to the "System.MessageBus.Error" topic.
    /// </summary>
    public class BusErrorMessage
    {
        public string OriginalTopic { get; set; } = string.Empty;
        public string ExceptionMessage { get; set; } = string.Empty;
        public string? StackTrace { get; set; }
        public DateTime Timestamp { get; set; } = DateTime.UtcNow;
        
        // We use 'object' here so the serializer can handle whatever the original payload was.
        public object? OriginalPayload { get; set; }
    }
}