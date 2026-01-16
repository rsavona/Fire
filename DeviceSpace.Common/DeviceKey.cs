using System;
using DeviceSpace.Common.Contracts;
namespace DeviceSpace.Common;

    
public class DeviceKey : IEquatable<DeviceKey>, IDeviceKey
{

    public string ScopeName { get; init; }
    public string DeviceName { get; init; }
    private readonly string _key;

    public DeviceKey(string scope, string name) 
    {
        // basic validation for constructor arguments
        if (string.IsNullOrWhiteSpace(scope))
        {
            scope = "SYSTEM";
        }
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentNullException(nameof(name));
        }

        // --- Initialize the public properties ---
        ScopeName = scope.ToUpperInvariant();
        DeviceName = name.ToUpperInvariant();

        // --- Initialize the readonly key field ---
        _key = $"{ScopeName}-{DeviceName}";
    }

    /// <summary>
    /// Determines whether the specified DeviceKey is equal to the current DeviceKey.
    /// </summary>
    public bool Equals(DeviceKey? other)
    {
        if (other is null)
        {
            return false;
        }

        if (ReferenceEquals(this, other))
        {
            return true;
        }

        return string.Equals(_key, other._key, StringComparison.Ordinal);
    }

    /// <summary>
    /// Determines whether the specified object is equal to the current object.
    /// </summary>
    public override bool Equals(object? obj)
    {
        if (obj is DeviceKey otherKey)
        {
            return Equals(otherKey);
        }
        return false;
    }

    /// <summary>
    /// Serves as the default hash function.
    /// Returns the hash code of the underlying generated key string.
    /// </summary>
    public override int GetHashCode()
    {
        return _key.GetHashCode(StringComparison.Ordinal);
    }

    /// <summary>
    /// Returns the underlying string representation of the key.
    /// </summary>
    public override string ToString()
    {
        return _key;
    }

    // overloading comparison operators 
    public static bool operator ==(DeviceKey? left, DeviceKey? right)
    {
        if (left is null)
        {
            return right is null; // True if both are null
        }
        return left.Equals(right); 
    }

    public static bool operator !=(DeviceKey? left, DeviceKey? right)
    {
        return !(left == right); 
    }
}