using System.Text.Json.Nodes;
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
            var payload = ResolvePayloadText(envelope.Payload);
            if (string.IsNullOrWhiteSpace(payload))
            {
                Logger.Error("[{Dev}] Manager received empty print payload.", element.Config.Name);
                return;
            }

            if (payload.TrimStart().StartsWith("<") || payload.TrimStart().StartsWith("^"))
            {
                await element.PrintAsync(payload);
                return;
            }

            var node = JsonNode.Parse(payload);
            if (node == null)
            {
                Logger.Error("[{Dev}] ERROR payload not in JSON", element.Config.Name);
                return;
            }

            var jsonObj = node.AsObject();
            var printType = GetConfiguredPrintType(element.Config);
            var labelData = ResolvePrinterData(jsonObj, printType);
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

    private static string ResolvePayloadText(object? payload)
    {
        return payload switch
        {
            null => string.Empty,
            string text => text,
            IElementMessage elementMessage => elementMessage.ToJson(),
            _ => payload.ToJson()
        };
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

    private static string? ResolvePrinterData(JsonObject jsonObj, string printType)
    {
        var rootPrinterData = TryReadPrinterDataNode(jsonObj["PrinterData"])
                              ?? TryReadPrinterDataNode(jsonObj["printerData"]);
        if (!string.IsNullOrWhiteSpace(rootPrinterData))
        {
            return rootPrinterData;
        }

        var labels = jsonObj["labels"]?.AsArray();
        if (labels == null || labels.Count == 0)
        {
            return null;
        }

        string? fallback = null;
        foreach (var labelNode in labels)
        {
            var label = labelNode?.AsObject();
            if (label == null)
            {
                continue;
            }

            var printerData = TryReadPrinterDataNode(label["printerData"])
                              ?? TryReadPrinterDataNode(label["PrinterData"]);
            if (string.IsNullOrWhiteSpace(printerData))
            {
                continue;
            }

            fallback ??= printerData;
            var applicatorType = label["applicatorType"]?.ToString()
                                 ?? label["ApplicatorType"]?.ToString();
            if (string.Equals(applicatorType, printType, StringComparison.OrdinalIgnoreCase))
            {
                return printerData;
            }
        }

        return fallback;
    }

    private static string? TryReadPrinterDataNode(JsonNode? node)
    {
        if (node == null)
        {
            return null;
        }

        if (node is JsonArray array)
        {
            return array
                .Select(item => item?.ToString())
                .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
        }

        return node.ToString();
    }
}
