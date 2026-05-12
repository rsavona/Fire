namespace DeviceSpace.Common.Messaging;

public enum SystemCommand
{
    Initialize,
    Start,
    Stop,
    Pause,
    RefreshStatus
}

public class SystemControlMessage
{
    public SystemCommand Command { get; set; }
    public string Sender { get; set; } = "System";
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;

    public SystemControlMessage(SystemCommand command)
    {
        Command = command;
    }
}

public class FlowEvent
{
    public string Source { get; set; } = string.Empty;
    public string Force { get; set; } = string.Empty;
    public string Destination { get; set; } = string.Empty;
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
}
