
using System;

namespace DeviceSpace.Common;
/// <summary>
/// Represents a composite key for message routing, defined by an origin 
/// (source) and an endpoint (destination).
/// </summary>
/// <remarks>
/// Implemented as an immutable struct for performance and value semantics.
/// </remarks>
public readonly struct RouteKey : IEquatable<RouteKey>
{
    /// <summary>
    /// The starting point or originator of the route/message.
    /// </summary>
    public string Source { get; }

    /// <summary>
    /// The ending point or intended recipient of the route/message.
    /// </summary>
    public string Destination { get; }

    /// <summary>
    /// Initializes a new instance of the RouteKey struct.
    /// </summary>
    /// <param name="source">The source identifier.</param>
    /// <param name="destination">The destination identifier.</param>
    public RouteKey(string source, string destination)
    {
        // Null checks are important for key integrity, though a WCS usually
        // ensures these identifiers are valid strings.
        Source = source ?? throw new ArgumentNullException(nameof(source));
        Destination = destination ?? throw new ArgumentNullException(nameof(destination));
    }

    /// <summary>
    /// Checks if this RouteKey is equal to another RouteKey instance.
    /// </summary>
    public bool Equals(RouteKey other)
    {
        // Use ordinal comparison for string fields for clarity and performance
        return string.Equals(Source, other.Source, StringComparison.Ordinal) &&
               string.Equals(Destination, other.Destination, StringComparison.Ordinal);
    }

    /// <summary>
    /// Checks if this RouteKey is equal to a generic object.
    /// </summary>
    public override bool Equals(object? obj)
    {
        return obj is RouteKey other && Equals(other);
    }

    /// <summary>
    /// Generates a hash code based on the combined hash codes of Source and Destination.
    /// Crucial for use as a key in hash-based collections (like Dictionary).
    /// </summary>
    public override int GetHashCode()
    {
        // Use HashCode.Combine for modern, collision-resistant hash generation.
        return HashCode.Combine(Source, Destination);
    }

    /// <summary>
    /// Provides a string representation of the route key (e.g., "Source->Destination").
    /// </summary>
    public override string ToString()
    {
        return $"{Source}->{Destination}";
    }

    // --- Operator Overloads ---

    public static bool operator ==(RouteKey left, RouteKey right)
    {
        return left.Equals(right);
    }

    public static bool operator !=(RouteKey left, RouteKey right)
    {
        return !(left == right);
    }
}