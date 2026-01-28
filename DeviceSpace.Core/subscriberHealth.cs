namespace DeviceSpace.Core;

public class SubscriberHealth
{
    public int FailureCount;
    public DateTime? ResetTime;
    public bool IsBroken => ResetTime.HasValue && DateTime.UtcNow < ResetTime.Value;

    public void RecordFailure(int threshold, TimeSpan timeout)
    {
        Interlocked.Increment(ref FailureCount);
        if (FailureCount >= threshold)
        {
            ResetTime = DateTime.UtcNow.Add(timeout);
        }
    }

    public void RecordSuccess()
    {
        FailureCount = 0;
        ResetTime = null;
    }
}