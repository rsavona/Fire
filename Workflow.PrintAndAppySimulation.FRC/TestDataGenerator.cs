using DeviceSpace.Common;
using Workflow.PrintAndApplyFrc;

namespace Workflow.PrintAndAppySimulation.FRC;


public static class TestDataGenerator
{
    private static readonly Random Rng = new Random();

    // --- EXISTING LABEL GENERATOR (Kept for reference) ---
    public static LabelDataFrcMessage? GenerateMockResponse(MessageEnvelope request)
    {

        if (request.Payload is LabelRequestFrcMessage req)
        {
            var random = new Random();
            string barcode = req.Barcodes.FirstOrDefault() ?? "9999999999";

            // 1. Create a random expected scan (usually longer than the LPN)
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

            // 3. Construct the response
            return new LabelDataFrcMessage(
                SessionId: req.SessionId,
                ControllerId: req.ControllerId,
                LineId: req.LineId ?? "UNKNOWN_LINE",
                Barcodes: req.Barcodes,
                StatusCode: "OKAY", // Using your specific 'OKAY' status
                StatusMessage: $"1 matched label for {barcode}",
                Labels: new List<LabelInfo>
                {
                    new LabelInfo(
                        ApplicatorType: "SHIPTOP",
                        ExpectedScan: mockExpectedScan,
                        PrinterData: mockZpl
                    )
                });
        }
        else
        {
            return null;
        }
    }

}