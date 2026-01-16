namespace DeviceSpace.Common;

public class QueueReadException : Exception
{
    public QueueReadException(string queueName, string deviceName) 
        : base($"[CRITICAL] Unable to read from queue '{queueName}' on device '{deviceName}'.") { }
}