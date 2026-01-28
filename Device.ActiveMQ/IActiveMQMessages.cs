namespace Device.ActiveMQ;

public interface ILabelRequestMessage
{
    public string Type { get; set; }
    public string LineId { get; set; }
}

public interface ILabelDataMessage 
{
    void LogMessage();
    string ToJson();
}