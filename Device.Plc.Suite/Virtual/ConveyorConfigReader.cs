using System.Text.Json;

namespace Device.Plc.Suite.Virtual;

public static class ConveyorConfigReader
{
    private static readonly JsonSerializerOptions _options = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public static ConveyorHardwareConfig? LoadFromFile(string filePath)
    {
        try
        {
            if (!File.Exists(filePath))
            {
                throw new FileNotFoundException($"Conveyor config not found at {filePath}");
            }

            string json = File.ReadAllText(filePath);
            return JsonSerializer.Deserialize<ConveyorHardwareConfig>(json, _options);
        }
        catch (Exception ex)
        {
            // Log the error using your Serilog instance if available
            return null;
        }
    }
}