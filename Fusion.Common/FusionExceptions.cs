namespace Fusion.Common;

public class QueueReadException : Exception
{
    public QueueReadException(string queueName, string elementName) 
        : base($"[CRITICAL] Unable to read from queue '{queueName}' on element '{elementName}'.") { }
}