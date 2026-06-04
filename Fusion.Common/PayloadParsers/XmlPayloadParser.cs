using System.Text.Json.Nodes;
using System.Xml;

namespace Fusion.Common.PayloadParsers;

public class XmlPayloadParser : IPayloadParser
{
    public string Parse(string rawInput)
    {
        try
        {
            XmlDocument doc = new XmlDocument();
            doc.LoadXml(rawInput);
            
            var json = XmlNodeToJson(doc.DocumentElement);
            return json?.ToJson() ?? "{}";
        }
        catch (Exception ex)
        {
            var json = new JsonObject();
            json["Raw"] = rawInput;
            json["Error"] = $"Invalid XML input: {ex.Message}";
            return json.ToJson();
        }
    }

    private JsonNode? XmlNodeToJson(XmlNode? node)
    {
        if (node == null) return null;

        if (node.NodeType == XmlNodeType.Text || node.NodeType == XmlNodeType.CDATA)
        {
            return JsonValue.Create(node.InnerText);
        }

        var jsonObject = new JsonObject();

        // Process Attributes
        if (node.Attributes != null)
        {
            foreach (XmlAttribute attr in node.Attributes)
            {
                jsonObject[$"@{attr.Name}"] = JsonValue.Create(attr.Value);
            }
        }

        // Process Children
        foreach (XmlNode child in node.ChildNodes)
        {
            if (child.NodeType == XmlNodeType.Text || child.NodeType == XmlNodeType.CDATA)
            {
                // If it's a mix of text and elements, this might need more complex logic
                // For now, if there is text, put it in a #text property if there are other properties
                if (jsonObject.Count > 0)
                    jsonObject["#text"] = JsonValue.Create(child.InnerText);
                else
                    return JsonValue.Create(child.InnerText);
            }
            else if (child.NodeType == XmlNodeType.Element)
            {
                var convertedChild = XmlNodeToJson(child);
                if (convertedChild != null)
                {
                    // Handle multiple children with same name (arrays)
                    if (jsonObject.ContainsKey(child.Name))
                    {
                        var existing = jsonObject[child.Name];
                        if (existing is JsonArray array)
                        {
                            array.Add(convertedChild);
                        }
                        else
                        {
                            // Convert existing object to array
                            jsonObject.Remove(child.Name);
                            var newArray = new JsonArray { existing, convertedChild };
                            jsonObject[child.Name] = newArray;
                        }
                    }
                    else
                    {
                        jsonObject[child.Name] = convertedChild;
                    }
                }
            }
        }

        return jsonObject;
    }

    public string Serialize(string jsonInput)
    {
        // For now, return as-is. Full JSON-to-XML serialization is a complex topic 
        // that can be added if specific output structure is required.
        return jsonInput; 
    }
}
