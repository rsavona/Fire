namespace Fusion.Common.Enums;

public enum ElementHealth
{
    Normal,
    Warning,
    Error,
    Critical
}

public static class ElementHealthExtension
{
    /// <summary>
    /// The ANSI escape code to reset the console color.
    /// </summary>
    public const string AnsiReset = "\x1b[0m";

    /// <summary>
    /// Gets the ANSI console color escape code for a severity.
    /// </summary>
    public static string ToAnsiColor(this ElementHealth? severity)
    {
        
        return severity switch
        {
            // \x1b[32m
            ElementHealth.Normal => "\x1b[32m", // Green

            // \x1b[33m
            ElementHealth.Warning => "\x1b[33m", // Yellow

            // \x1b[31m
            ElementHealth.Error => "\x1b[31m", // Red
             
            ElementHealth.Critical => "\x1b[31m", // Red

            // \x1b[37m
            _ => "\u001b[0m"// Default (Gray/White)
        };
    }
}