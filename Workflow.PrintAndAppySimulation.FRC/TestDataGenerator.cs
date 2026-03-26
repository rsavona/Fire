using DeviceSpace.Common;
using Workflow.PrintAndApplyFrc;

namespace Workflow.PrintAndAppySimulation.FRC;

public static class TestDataGenerator
{
    private static readonly Random Rng = new Random();

    // --- EXISTING LABEL GENERATOR (Kept for reference) ---
    public static LabelDataFrcMessage? GenerateMockResponse(object request)
    {
        var random = new Random();
        List<string> bcs = MessageParser.GetBarcodes(request);

        string barcode = bcs.FirstOrDefault() ?? "9999999999";

        string mockExpectedScan = "92384" + random.Next(100000, 999999).ToString() +
                                  random.Next(100000, 999999).ToString();

        // 2. Generate a mock ZPL string for the printerData field
        string mockZpl = $@"<?xml version=""1.0"" encoding=""UTF-8""?>
                                <labels _FORMAT=""PM_DEL.ZPL"" _QUANTITY=""1"">
                                    <label>
                                        <variable name=""LPN"">{barcode}</variable>
                                        <variable name=""printedBarcode"">{mockExpectedScan}</variable>
                                        <variable name=""printerName"">MOCK_PRINTER_{random.Next(1, 5)}</variable>
                                    </label>
                                </labels>";


        var controllerId = MessageParser.GetPart(request, "ControllerId") ?? "";
        var lineId = MessageParser.GetPart(request, "LineId") ?? "";
        var statusCode = "OKAY"; // Using your specific 'OKAY' status
        var statusMessage = $"1 matched label for {barcode}";
        var labels = new List<LabelInfo>
        {
            new LabelInfo(
                ApplicatorType: "SHIPTOP",
                ExpectedScan: mockExpectedScan,
                PrinterData: mockZpl
            )
        };
        var g = Guid.NewGuid();
        return new LabelDataFrcMessage(g, controllerId, lineId, bcs, statusCode, statusMessage, labels);
    }
}