namespace DeviceSpace.Common.Contracts;

 public interface IHeartbeatProvider
    {
        int IntervalMs { get; }
        int TimeoutMs { get; }
        Task SendHeartbeatAsync(CancellationToken ct);
        bool IsResponseValid(object response);
    }
