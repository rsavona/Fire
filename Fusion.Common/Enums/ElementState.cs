
namespace Fusion.Common.Enums;

public abstract class ElementState
{
    // The "Global" states every element has
    public static readonly ElementState Offline = new BaseState(0, "Offline", ElementHealth.Warning);
    public static readonly ElementState Starting = new BaseState(1, "Starting", ElementHealth.Warning);
    public static readonly ElementState Faulted = new BaseState(2, "Faulted", ElementHealth.Critical);
    public static readonly ElementState Stopping = new BaseState(3, "Stopping", ElementHealth.Warning);

    public int Value { get; }
    public string Name { get; }
    public ElementHealth Health { get; }

    protected ElementState(int value, string name, ElementHealth health)
    {
        Value = value;
        Name = name;
        Health = health;
    }

    public override string ToString() => Name;

    // Implementation for the core base states
    private sealed class BaseState : ElementState
    {
        public BaseState(int value, string name, ElementHealth health) : base(value, name, health) { }
    }
}