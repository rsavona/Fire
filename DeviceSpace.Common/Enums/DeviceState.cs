
namespace DeviceSpace.Common.Enums;

public abstract class DeviceState
{
    // The "Global" states every device has
    public static readonly DeviceState Offline = new BaseState(0, "Offline", DeviceHealth.Warning);
    public static readonly DeviceState Starting = new BaseState(1, "Starting", DeviceHealth.Warning);
    public static readonly DeviceState Faulted = new BaseState(2, "Faulted", DeviceHealth.Critical);
    public static readonly DeviceState Stopping = new BaseState(3, "Stopping", DeviceHealth.Warning);

    public int Value { get; }
    public string Name { get; }
    public DeviceHealth Health { get; }

    protected DeviceState(int value, string name, DeviceHealth health)
    {
        Value = value;
        Name = name;
        Health = health;
    }

    public override string ToString() => Name;

    // Implementation for the core base states
    private sealed class BaseState : DeviceState
    {
        public BaseState(int value, string name, DeviceHealth health) : base(value, name, health) { }
    }
}