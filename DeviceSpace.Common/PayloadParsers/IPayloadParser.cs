using System.Text.Json.Nodes;

namespace DeviceSpace.Common.PayloadParsers;

/// <summary>
/// Defines a strategy for parsing raw string data into a JSON object.
/// </summary>
public interface IPayloadParser
{
    /// <summary>
    /// Parses the raw input string into a JSON string.
    /// </summary>
    string Parse(string rawInput);

    /// <summary>
    /// Serializes a JSON string or object back into the raw format.
    /// </summary>
    string Serialize(string jsonInput);
}
