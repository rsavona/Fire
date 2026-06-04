using System;
using Fusion.Common.Contracts;

namespace Fusion.Common;

    
public class ElementKey : IEquatable<ElementKey>, IElementKey
{

    public string ScopeName { get; init; }
    public string ElementName { get; init; }
    public string CoreName { get; init; }
    private readonly string _key;

    public ElementKey(string scope, string name, string core = "Fusion") 
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
        ElementName = name.ToUpperInvariant();
        CoreName = (string.IsNullOrEmpty(core) ? "Fusion" : core).ToUpperInvariant();

        // --- Initialize the readonly key field ---
        _key = $"{CoreName}-{ScopeName}-{ElementName}";
    }

    /// <summary>
    /// Determines whether the specified ElementKey is equal to the current ElementKey.
    /// </summary>
    public bool Equals(ElementKey? other)
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
        if (obj is ElementKey otherKey)
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
    public static bool operator ==(ElementKey? left, ElementKey? right)
    {
        if (left is null)
        {
            return right is null; // True if both are null
        }
        return left.Equals(right); 
    }

    public static bool operator !=(ElementKey? left, ElementKey? right)
    {
        return !(left == right); 
    }
}