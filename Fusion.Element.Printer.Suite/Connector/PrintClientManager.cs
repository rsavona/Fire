using System.Text.Json;
using Fusion.Common;
using Fusion.Common.BaseClasses;
using Fusion.Common.Contracts;
using Microsoft.Extensions.Logging;


using Fusion.Common.Attributes;
using Fusion.Element.Printer.Suite.Virtual;

namespace Fusion.Element.Printer.Suite;

/// <summary>
/// A manager that creates and manages printer elements.
/// </summary>
[TestCounterpart(typeof(VirtualPrinterManager))]
public class PrintClientManager : ElementManagerBase<ITcpPrintClientBase>
{
    /// <summary>
    /// Constructor must be public for Dependency Injection to access it.
    /// </summary>
    public PrintClientManager(
        IMessageBus bus,
        List<IElementBlueprint> configs,
        IFireLogger<PrintClientManager> logger, // Updated to match this specific manager class
        Func<IElementBlueprint, IFireLogger, ITcpPrintClientBase> elementFactory,
        string managerName)
        : base(bus, configs, logger, elementFactory, managerName)
    {
        ElementFactory = elementFactory;
    }

    /// <summary>
    /// Create a printer element using the injected factory.
    /// </summary>
    protected override Task<ITcpPrintClientBase> CreateElementAsync(IElementBlueprint config)
    {
        // Create a specific logger for this element instance
        var elementLogger = Logger.WithContext("ElementName", config.Name);

        var printer = ElementFactory(config, elementLogger);
        return Task.FromResult(printer);
    }

    protected override async Task RegisterElementSourceBonds(IElement element)

    {
        /* Printers are passive listeners */
        return;
    }

    protected override Task OnElementMessageToMessageBusAsync(object? sender, object messageEnv)
    {
        /* No inbound from printer */
        return Task.CompletedTask;
    }

    protected override async Task HandleBusMessageAsync(
        MessageEnvelope envelope, CancellationToken ct)
    {
        var topic = envelope.Destination;
        ElementInstances.TryGetValue(topic.ElementName, out var element);
        if (element == null)
        {
            Logger.Error("[{Dev}] Manager received print job for unknown printer.", topic.ElementName);
            return;
        }

        try
        {
            var payload = envelope.GetPayloadText();
            if (string.IsNullOrWhiteSpace(payload))
            {
                Logger.Error("[{Dev}] Manager received empty print payload.", element.Config.Name);
                return;
            }

            // Raw label data (XML or ZPL) prints directly without a job wrapper.
            if (payload.TrimStart().StartsWith("<") || payload.TrimStart().StartsWith("^"))
            {
                await element.PrintAsync(payload);
                return;
            }

            if (!TryParseCommand<PrintJobCommand>(envelope, out var job) || job == null) return;

            var printType = GetConfiguredPrintType(element.Config);
            var labelData = ResolvePrinterData(job, printType);
            if (string.IsNullOrWhiteSpace(labelData))
            {
                Logger.Error("[{Dev}] No {PrintType} printer data found in payload.", element.Config.Name, printType);
                return;
            }

            await element.PrintAsync(labelData);

        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "[{Dev}] Manager failed to bond print job.", element.Config.Name);
        }
    }

    private static string GetConfiguredPrintType(IElementBlueprint config)
    {
        var legacyPrintType = TryGetProperty(config.Properties, "aSPrintType");
        return TryGetProperty(config.Properties, "PrintType")
               ?? legacyPrintType
               ?? "SHIPTOP";
    }

    private static string? TryGetProperty(Dictionary<string, object> properties, string key)
    {
        return properties.TryGetValue(key, out var value)
            ? value?.ToString()
            : null;
    }

    internal static string? ResolvePrinterData(PrintJobCommand job, string printType)
    {
        var rootPrinterData = TryReadPrinterData(job.PrinterData);
        if (!string.IsNullOrWhiteSpace(rootPrinterData))
        {
            return rootPrinterData;
        }

        if (job.Labels == null || job.Labels.Count == 0)
        {
            return null;
        }

        string? fallback = null;
        foreach (var label in job.Labels)
        {
            var printerData = TryReadPrinterData(label.PrinterData);
            if (string.IsNullOrWhiteSpace(printerData))
            {
                continue;
            }

            fallback ??= printerData;
            if (string.Equals(label.ApplicatorType, printType, StringComparison.OrdinalIgnoreCase))
            {
                return printerData;
            }
        }

        return fallback;
    }

    /// <summary>PrinterData arrives as either a JSON string or an array of strings.</summary>
    private static string? TryReadPrinterData(JsonElement? element)
    {
        if (element == null) return null;

        var value = element.Value;
        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Array => value.EnumerateArray()
                .Select(item => item.ValueKind == JsonValueKind.String ? item.GetString() : item.ToString())
                .FirstOrDefault(text => !string.IsNullOrWhiteSpace(text)),
            JsonValueKind.Null or JsonValueKind.Undefined => null,
            _ => value.ToString()
        };
    }
}
