using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

public static class MessageParser
{
    /// <summary>
    /// Extract a specific string value from a raw JSON payload.
    /// </summary>
    public static string? GetPart(object? payload, string propertyName)
    {
        if (payload is not string json) return null;

        try
        {
            var node = JsonNode.Parse(json);
            // Returns the value of the property (e.g., "sessionId" or "type")
            return node?[propertyName]?.ToString();
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Specifically extracts the GIN as an integer.
    /// </summary>
    public static int GetGin(object? payload)
    {
        string? ginStr = GetPart(payload, "GIN") ?? GetPart(payload, "gin");
        return int.TryParse(ginStr, out int gin) ? gin : 0;
    }
    public static string? GetSession(object? payload)
    {
        string? sessionStr = GetPart(payload, "Session") ?? GetPart(payload, "session");
        return sessionStr;
    }
    
    
    public static string GetDecisionPoint(object? payload)
    {
        if (payload is not string json) return "Unknown";
        try
        {
            var node = JsonNode.Parse(json);
            // Check both common casings used in WCS
            return node?["decisionPoint"]?.ToString() 
                ?? node?["DecisionPoint"]?.ToString() 
                ?? "Unknown";
        }
        catch { return "Error"; }
    }

    // 2. Get Barcodes (List of Strings)
    public static List<string> GetBarcodes(object? payload)
    {
        if (payload is not string json) return new List<string>();
        try
        {
            var node = JsonNode.Parse(json);
            var barcodeArray = node?["barcodes"]?.AsArray() ?? node?["Barcodes"]?.AsArray();
            
            return barcodeArray?
                .Select(x => x?.ToString() ?? string.Empty)
                .Where(s => !string.IsNullOrEmpty(s))
                .ToList() ?? new List<string>();
        }
        catch { return new List<string>(); }
    }

    // 3. Get Timestamp (DateTime)
    public static DateTime GetTimestamp(object? payload)
    {
        if (payload is not string json) return DateTime.UtcNow;
        try
        {
            var node = JsonNode.Parse(json);
            var tsStr = node?["timestamp"]?.ToString() ?? node?["Created"]?.ToString();
            
            return DateTime.TryParse(tsStr, out var dt) ? dt : DateTime.UtcNow;
        }
        catch { return DateTime.UtcNow; }
    }
}



public static class JsonExtensions
{
    private static readonly JsonSerializerOptions _options = new()
    {
        // This prevents the \u0022 escaping you saw earlier
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = false
    };

    /// <summary>
    /// Safely converts an object to a JSON string. 
    /// If the object is already a string, it returns it as-is.
    /// </summary>
    public static string ToJson(this object? obj)
    {
        if (obj == null) return "{}";
        if (obj is string s) return s; // Don't double-serialize!

        return JsonSerializer.Serialize(obj, _options);
    }
}