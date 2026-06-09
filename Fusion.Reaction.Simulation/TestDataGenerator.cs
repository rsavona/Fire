using Fusion.Common;

namespace Fusion.Reaction.Simulation;

public static class TestDataGenerator
{
    private static readonly Random Rng = new Random();

    // --- EXISTING LABEL GENERATOR (Kept for reference) ---
    public static LabelDataFrcMessage? GenerateMockResponse(LabelRequestFrcMessage request)
    {
        List<string> bcs = request.Barcodes;
        string barcode = bcs.FirstOrDefault() ?? string.Empty;

        if (barcode.StartsWith("PNA-NOLABEL-", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        if (string.IsNullOrEmpty(barcode) || barcode.Contains('?') || barcode.Contains('*'))
        {
            return BuildResponse(
                request,
                "ERROR",
                $"No label found for '{barcode}' (Invalid or Unknown)",
                new List<LabelInfo>());
        }

        return barcode switch
        {
            "SIM-0030" => BuildResponse(
                request,
                "OKAY",
                "Anomaly: OK status with no labels",
                new List<LabelInfo>()),

            "SIM-0031" => BuildResponse(
                request,
                "OKAY",
                "Anomaly: missing expected scan",
                new List<LabelInfo>
                {
                    BuildLabel(barcode, string.Empty, BuildPrinterData(barcode, string.Empty))
                }),

            "SIM-0032" => BuildResponse(
                request,
                "OKAY",
                "Anomaly: malformed printer data",
                new List<LabelInfo>
                {
                    BuildLabel(barcode, barcode + "123", "<labels><label><variable name=\"LPN\">")
                }),

            "SIM-0033" => BuildResponse(
                request,
                "ERROR",
                "Anomaly: error status with label payload present",
                new List<LabelInfo>
                {
                    BuildLabel(barcode, barcode + "123", BuildPrinterData(barcode, barcode + "123"))
                }),

            "SIM-0034" => BuildResponse(
                request,
                "OKAY",
                "Anomaly: expected scan does not match barcode convention",
                new List<LabelInfo>
                {
                    BuildLabel(barcode, "WRONG-" + barcode, BuildPrinterData(barcode, "WRONG-" + barcode))
                }),

            _ => BuildResponse(
                request,
                "OKAY",
                $"1 matched label for {barcode}",
                new List<LabelInfo>
                {
                    BuildLabel(barcode, barcode + "123", BuildPrinterData(barcode, barcode + "123"))
                })
        };
    }

    private static LabelDataFrcMessage BuildResponse(
        LabelRequestFrcMessage request,
        string statusCode,
        string statusMessage,
        List<LabelInfo> labels)
    {
        return new LabelDataFrcMessage(
            request.SessionId,
            request.ControllerId,
            request.LineId ?? "L1",
            request.Barcodes,
            statusCode,
            statusMessage,
            labels);
    }

    private static LabelInfo BuildLabel(string barcode, string expectedScan, string printerData)
    {
        return new LabelInfo(
            ApplicatorType: "SHIPTOP",
            ExpectedScan: expectedScan,
            PrinterData: printerData);
    }

    private static string BuildPrinterData(string barcode, string expectedScan)
    {
        return $@"<?xml version=""1.0"" encoding=""UTF-8""?>
                                <labels _FORMAT=""PM_DEL.ZPL"" _QUANTITY=""1"">
                                    <label>
                                        <variable name=""LPN"">{barcode}</variable>
                                        <variable name=""printedBarcode"">{expectedScan}</variable>
                                        <variable name=""printerName"">MOCK_PRINTER</variable>
                                    </label>
                                </labels>";
    }

    public static string RandomBarcode(string barcode)
    {
        var random = new Random();
        // .Next(10) generates a random integer from 0 up to 9.
        int roll = random.Next(10); 

        // 1 out of 10 chance (10%)
        if (roll == 0) 
        {
            return "999" + barcode;
        }
        // 9 out of 10 chance (90%)
        else 
        {
            return "123" + barcode;
        }
    }
}
