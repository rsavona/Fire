using DeviceSpace.Common;
using Workflow.PrintAndApplyFrc;

namespace Workflow.PrintAndAppySimulation.FRC;

public static class TestDataGenerator
{
    private static readonly Random Rng = new Random();

    // --- EXISTING LABEL GENERATOR (Kept for reference) ---
    public static LabelDataFrcMessage? GenerateMockResponse(object request)
    {
       
        List<string> bcs = MessageParser.GetBarcodes(request);

        string barcode = bcs.FirstOrDefault() ?? "9999999999";

        string mockExpectedScan = RandomBarcode(barcode);

        // 2. Generate a mock ZPL string for the printerData field
        string mockZpl = $@"<?xml version=""1.0"" encoding=""UTF-8""?>
                                <labels _FORMAT=""PM_DEL.ZPL"" _QUANTITY=""1"">
                                    <label>
                                        <variable name=""LPN"">{barcode}</variable>
                                        <variable name=""printedBarcode"">{mockExpectedScan}</variable>
                                        <variable name=""printerName"">MOCK_PRINTER</variable>
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