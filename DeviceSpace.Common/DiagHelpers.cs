namespace DeviceSpace.Common;

using System;

/// <summary>
/// Marks a method as an executable command for the Diagnostic Server.
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public class DiagnosticCommandAttribute : Attribute
{
    public string DisplayName { get; }
    public string Description { get; }

    public DiagnosticCommandAttribute(string displayName, string description = "")
    {
        DisplayName = displayName;
        Description = description;
    }
}


public class DiagCommand
{
    // The name of the method to trigger (e.g., "SimulatePaperOut")
    public string CommandName { get; set; } = string.Empty;
    
    public string Description { get; set; } = string.Empty;

    // A dictionary of arguments (e.g., "active" : "true")
    public Dictionary<string, string> Parameters { get; set; } = new();

    // Who requested the command (for audit logs)
    public string RequestedBy { get; set; } = "AdminUI";

    public DiagCommand(string name, string description)
    {
        CommandName = name;
        Description = description; 
    }

}

public class DiagResult
{
    public bool Success { get; set; }
    
    // A message to show the user (e.g., "Paper sensor is now set to LOW")
    public string Message { get; set; } = string.Empty;

    // Optional: Data returned from the device (e.g., current GIN count)
    public object? Data { get; set; }

    // Helper methods for quick creation
    public static DiagResult Ok(string msg) => new() { Success = true, Message = msg };
    public static DiagResult Fail(string msg) => new() { Success = false, Message = msg };
    
}

public class DeviceAnnouncement
{
    public string DeviceName { get; set; }      // e.g., "Printer_Zone_01"
    public string DeviceType { get; set; }      // e.g., "ZebraPrinter"

    public string SoftwareVersion { get; set; } // Assembly Version

    public string Runtime { get; set; }         // e.g., ".NET 8.0.1" or ".NET Standard 2.1"
    
    public int SchemaVersion { get; set; }      // To check message compatibility
    
    // The list of commands this specific device supports
    public List<DiagCommand> AvailableCommands { get; set; } = new();
}

public class DiagCommandInfo
{
    public DiagCommandInfo(string name, string description)
    {
        Name = name;
        Description = description;
    }

    public string Name { get; set; }            // e.g., "SIMULATE_PAPER_OUT"
    public string Description { get; set; }     // e.g., "Toggles paper sensor"
    public List<string> ParameterNames { get; set; } = new(); // e.g., ["active"]
}