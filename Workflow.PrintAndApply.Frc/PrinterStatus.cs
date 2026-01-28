namespace Workflow.PrintAndApplyFrc;

public enum PrintType
{
    Barcode,
    Content
    // Add other types specific to your WCS
}

/// <summary>
/// 
/// </summary>
public class PrinterStatus : object
{
    // The unique identifier for the printer/device.
    public readonly string Name;

    // Barcode or Content
    public readonly string Type;

    public readonly string Induct;

    public readonly int PreferredGroup;

    // Flag indicating if the printer was used for the last print job.
    public bool LastPrinted { get; set; }
    
    // Flag indicating if the printer is currently online and functional.
    public bool IsAvailable { get; set; }

    /// <summary>
    /// constructor
    /// </summary>
    public PrinterStatus(string name,  string type, string induct, int preferredG = 1,  bool isAvailable = false)
    {
        Name = name ?? throw new ArgumentNullException(nameof(name));
        PreferredGroup = preferredG;
        Induct = induct ?? throw new ArgumentNullException(nameof(induct));
        LastPrinted = false;
        Type = type;
        IsAvailable = isAvailable;
    }
    
    public override string ToString() => $"{Name} ({Type})";
}