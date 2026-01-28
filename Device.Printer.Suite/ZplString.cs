using System;

namespace Device.Printer.Suite;



/// <summary>
/// Represents a ZPL (Zebra Programming Language) string that has passed
/// light structural validation.
///
/// This class ensures that the raw string provided:
/// 1. Is not null or empty.
/// 2. Starts with the ZPL start command (^XA).
/// 3. Ends with the ZPL end command (^XZ).
///
/// This pattern is useful to prevent passing unformed ZPL strings
/// through your system.
/// </summary>
public class ZplString
{
    /// <summary>
    /// Gets the validated, raw ZPL string.
    /// </summary>
    public string Value { get; }

    /// <summary>
    /// Initializes a new instance of the <see cref="ZplString"/> class
    /// and performs validation on the raw ZPL.
    /// </summary>
    /// <param name="rawZpl">The raw ZPL string to validate.</param>
    /// <exception cref="ArgumentNullException">Thrown if rawZpl is null.</exception>
    /// <exception cref="ArgumentException">Thrown if ZPL is empty or malformed.</exception>
    public ZplString(string rawZpl)
    {
        if (rawZpl == null)
        {
            throw new ArgumentNullException(nameof(rawZpl));
        }

        // Trim any surrounding whitespace (like newlines)
        string trimmedZpl = rawZpl.Trim();

        if (string.IsNullOrWhiteSpace(trimmedZpl))
        {
            throw new ArgumentException("ZPL string cannot be empty or whitespace.", nameof(rawZpl));
        }

        // 1. Light Validation: Check for Start command
        if (!trimmedZpl.StartsWith("^XA", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Invalid ZPL: String must start with ^XA.", nameof(rawZpl));
        }

        // 2. Light Validation: Check for End command
        if (!trimmedZpl.EndsWith("^XZ", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Invalid ZPL: String must end with ^XZ.", nameof(rawZpl));
        }
        this.Value = trimmedZpl;
    }

    /// <summary>
    /// Returns the raw, validated ZPL string.
    /// </summary>
    public override string ToString()
    {
        return this.Value;
    }

    /// <summary>
    /// Creates a default, validated ZPL string for printing an error label.
    /// </summary>
    /// <param name="errorMessage">An optional short message to include on the label.</param>
    /// <returns>A new ZplString object containing the error label ZPL.</returns>
    public static ZplString CreateErrorLabel(string errorMessage = "Check WCS")
    {
        // A simple ZPL label that prints "PRINT ERROR" and a custom message
        string errorZpl = $@"
^XA
^FO50,40^A0N,40,40^FDPRINT ERROR^FS
^FO50,90^A0N,30,30^FD{errorMessage}^FS
^FO40,30^GB350,100,3^FS
^XZ
";
        // The constructor will validate this ZPL string
        return new ZplString(errorZpl);
    }
}

