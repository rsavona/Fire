


namespace DeviceSpace.Common;


public class RootSettings
{
    // The property name matches the top-level key in the JSON
    // You can use an attribute if you want a different C# property name
    // [JsonPropertyName("AppSettings")] for System.Text.Json
    // [JsonProperty("AppSettings")] for Newtonsoft.Json
    public required AppSettings AppSettings { get; set; }
}

public class AppSettings
{
    public required Configurations.DeviceSpace DeviceSpace { get; set; }
}

 