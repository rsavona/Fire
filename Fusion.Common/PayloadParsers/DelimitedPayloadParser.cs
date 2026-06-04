using System.Text.Json.Nodes;

namespace Fusion.Common.PayloadParsers;

public class DelimitedPayloadParser : IPayloadParser
{
    private readonly char _delimiter;
    private readonly List<string> _fieldNames;

    public DelimitedPayloadParser(char delimiter, List<string> fieldNames)
    {
        _delimiter = delimiter;
        _fieldNames = fieldNames;
    }

    public string Parse(string rawInput)
    {
        var parts = rawInput.Split(_delimiter);
        var json = new JsonObject();
        
        for (int i = 0; i < _fieldNames.Count; i++)
        {
            json[_fieldNames[i]] = i < parts.Length ? parts[i] : string.Empty;
        }
        
        return json.ToJson();
    }

    public string Serialize(string jsonInput)
    {
        try
        {
            var node = JsonNode.Parse(jsonInput);
            var values = new List<string>();
            foreach (var field in _fieldNames)
            {
                values.Add(node?[field]?.ToString() ?? string.Empty);
            }
            return string.Join(_delimiter, values);
        }
        catch
        {
            return jsonInput;
        }
    }
}
