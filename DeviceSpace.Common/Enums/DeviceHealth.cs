namespace DeviceSpace.Common.Enums;

public enum DeviceHealth
{
    Normal,
    Warning,
    Error,
    Critical
}

public static class DeviceHealthExtension
{
    /// <summary>
    /// The ANSI escape code to reset the console color.
    /// </summary>
    public const string AnsiReset = "\x1b[0m";

    /// <summary>
    /// Gets the ANSI console color escape code for a severity.
    /// </summary>
    public static string ToAnsiColor(this DeviceHealth? severity)
    {
        
        return severity switch
        {
            // \x1b[32m
            DeviceHealth.Normal => "\x1b[32m", // Green

            // \x1b[33m
            DeviceHealth.Warning => "\x1b[33m", // Yellow

            // \x1b[31m
            DeviceHealth.Error => "\x1b[31m", // Red
             
            DeviceHealth.Critical => "\x1b[31m", // Red

            // \x1b[37m
            _ => "\u001b[0m"// Default (Gray/White)
        };
    }
}