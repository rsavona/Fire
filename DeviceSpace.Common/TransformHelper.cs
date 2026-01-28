using System.Text.Json.Nodes;

namespace DeviceSpace.Common.BaseClasses;

public static class TransformHelper
{

    public static JsonObject TransformJson(string sourceJson, Dictionary<string, Func<JsonObject, object>> mappers)
    {
        // 1. Parse source into a flexible node
        var sourceNode = JsonNode.Parse(sourceJson)?.AsObject()
                         ?? throw new ArgumentException("Invalid Source JSON");

        var targetObject = new JsonObject();

        // 2. Iterate through your mapping rules
        foreach (var rule in mappers)
        {
            string targetField = rule.Key;
            var getValueFunc = rule.Value;

            // Execute the mapping logic
            object result = getValueFunc(sourceNode);

            // 3. Add to the new message
            targetObject[targetField] = JsonValue.Create(result);
        }

        return targetObject;
    }
}