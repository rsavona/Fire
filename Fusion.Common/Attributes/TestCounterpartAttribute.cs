using System;

namespace Fusion.Common.Attributes;

/// <summary>
/// Associates a production element or manager with its test/simulation counterpart.
/// This metadata can be used for automated configuration generation, UI mapping, or dynamic replacement.
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = true)]
public class TestCounterpartAttribute : Attribute
{
    /// <summary>
    /// The type of the test counterpart (e.g., typeof(VirtualPlcElement)).
    /// </summary>
    public Type CounterpartType { get; }

    public TestCounterpartAttribute(Type counterpartType)
    {
        CounterpartType = counterpartType;
    }
}
