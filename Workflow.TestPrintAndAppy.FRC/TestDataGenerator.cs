using Workflow.PrintAndApplyFrc;

namespace Workflow.TestPrintAndAppyFRC;

public static class TestDataGenerator
{
    private static readonly Random _rng = new Random();

    // --- EXISTING LABEL GENERATOR (Kept for reference) ---
    public static LabelDataFrcMessage GenerateRandomLabelData(LabelRequestFrcMessage request)
    {
        string fakeZpl = $"^XA^FO50,50^ADN,36,20^FDTest Label {request.SessionId}^FS^XZ";
        string verificationBarcode = request.Barcodes.FirstOrDefault() ?? "NO_BARCODE";

        var labels = new List<LabelInfo>
        {
            new LabelInfo("SHIPTOP", verificationBarcode, fakeZpl)
        };

        return new LabelDataFrcMessage(
            request.SessionId.ToString(), request.ControllerId, request.LineId ?? "Default",
            request.Barcodes, "200", "Success", labels
        );
    }

  
}