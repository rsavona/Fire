using System.Text.Json.Nodes;

namespace DeviceSpace.Common.PayloadParsers;

public record FixedFieldDefinition(string Name, int StartIndex, int Length);

public class FixedLengthPayloadParser : IPayloadParser
{
    private readonly List<FixedFieldDefinition> _definitions;

    public FixedLengthPayloadParser(List<FixedFieldDefinition> definitions)
    {
        _definitions = definitions;
    }

    public string Parse(string rawInput)
    {
        var json = new JsonObject();
        
        foreach (var def in _definitions)
        {
            if (rawInput.Length >= def.StartIndex + def.Length)
            {
                json[def.Name] = rawInput.Substring(def.StartIndex, def.Length).Trim();
            }
            else if (rawInput.Length > def.StartIndex)
            {
                json[def.Name] = rawInput.Substring(def.StartIndex).Trim();
            }
            else
            {
                json[def.Name] = string.Empty;
            }
        }
        
        return json.ToJson();
    }

    public string Serialize(string jsonInput)
    {
        try
        {
            var node = JsonNode.Parse(jsonInput);
            int totalLength = _definitions.Count > 0 ? _definitions.Max(d => d.StartIndex + d.Length) : 0;
            char[] buffer = new string(' ', totalLength).ToCharArray();

            foreach (var def in _definitions)
            {
                string val = node?[def.Name]?.ToString() ?? string.Empty;
                if (val.Length > def.Length) val = val.Substring(0, def.Length);
                
                for (int i = 0; i < val.Length; i++)
                {
                    buffer[def.StartIndex + i] = val[i];
                }
            }
            return new string(buffer);
        }
        catch
        {
            return jsonInput;
        }
    }

    /// <summary>
    /// Parses a configuration string like "ID:0:5,Name:5:10,Status:15:1"
    /// </summary>
    public static List<FixedFieldDefinition> ParseDefinitions(string config)
    {
        var defs = new List<FixedFieldDefinition>();
        var parts = config.Split(',', StringSplitOptions.RemoveEmptyEntries);
        
        foreach (var part in parts)
        {
            var segments = part.Split(':');
            if (segments.Length == 3)
            {
                if (int.TryParse(segments[1], out int start) && int.TryParse(segments[2], out int length))
                {
                    defs.Add(new FixedFieldDefinition(segments[0].Trim(), start, length));
                }
            }
            // Support Name:Length format (assuming sequential)
            else if (segments.Length == 2)
            {
                int start = defs.Count > 0 ? defs.Last().StartIndex + defs.Last().Length : 0;
                if (int.TryParse(segments[1], out int length))
                {
                    defs.Add(new FixedFieldDefinition(segments[0].Trim(), start, length));
                }
            }
        }
        
        return defs;
    }
}
