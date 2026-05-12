using System.Text.Json.Nodes;

namespace DeviceSpace.Common.PayloadParsers;

public class JsonPayloadParser : IPayloadParser
{
    public string Parse(string rawInput)
    {
        try
        {
            // Verify it's valid JSON. If it's already JSON, just return it sanitized if possible.
            var node = JsonNode.Parse(rawInput);
            return node.ToJson();
        }
        catch
        {
            // If it's not valid JSON, wrap it in a "Raw" property
            var json = new JsonObject();
            json["Raw"] = rawInput;
            json["Error"] = "Invalid JSON input";
            return json.ToJson();
        }
    }

    public string Serialize(string jsonInput)
    {
        return jsonInput; // Already JSON
    }
}
