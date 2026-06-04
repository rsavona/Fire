using Fusion.Common.Contracts;

namespace Fusion.Common.PayloadParsers;

public static class PayloadParserFactory
{
    public static IPayloadParser Create(IElementBlueprint config)
    {
        string format = config.Properties.TryGetValue("PayloadFormat", out var f) 
            ? f.ToString()?.ToUpper() ?? "DELIMITED" 
            : "DELIMITED";

        return format switch
        {
            "FIXED" => CreateFixedLengthParser(config),
            "JSON" => new JsonPayloadParser(),
            "XML" => new XmlPayloadParser(),
            "DELIMITED" => CreateDelimitedParser(config),
            _ => CreateDelimitedParser(config)
        };
    }

    private static IPayloadParser CreateDelimitedParser(IElementBlueprint config)
    {
        char delimiter = config.Properties.TryGetValue("BodyDelimiter", out var d) 
            ? d.ToString()?[0] ?? ',' 
            : ',';

        var fieldNames = new List<string>();
        if (config.Properties.TryGetValue("FieldNames", out var f))
        {
            if (f is IEnumerable<object> list)
            {
                fieldNames = list.Select(x => x.ToString()!).ToList();
            }
            else if (f is string s)
            {
                fieldNames = s.Split(',').Select(x => x.Trim()).ToList();
            }
        }

        return new DelimitedPayloadParser(delimiter, fieldNames);
    }

    private static IPayloadParser CreateFixedLengthParser(IElementBlueprint config)
    {
        string definition = config.Properties.TryGetValue("FieldDefinitions", out var d) 
            ? d.ToString() ?? string.Empty 
            : string.Empty;

        var defs = FixedLengthPayloadParser.ParseDefinitions(definition);
        return new FixedLengthPayloadParser(defs);
    }
}
