using Fusion.Common;
using Xunit;

namespace Fusion.Element.Printer.Suite.Tests;

/// <summary>
/// Verifies PrintJobCommand binding and PrintClientManager.ResolvePrinterData
/// against the payload shapes the JsonNode probing used to accept.
/// </summary>
public class PrintJobCommandTests
{
    private static PrintJobCommand Parse(string json)
    {
        var envelope = new MessageEnvelope("PRINTER1.Print", json);
        Assert.True(envelope.TryGetPayload<PrintJobCommand>(out var job, out var error), error);
        return job!;
    }

    [Fact]
    public void RootPrinterData_AsString_Wins()
    {
        var job = Parse("""{"PrinterData":"^XA^FDROOT^XZ","labels":[{"printerData":"^XA^FDLABEL^XZ"}]}""");

        Assert.Equal("^XA^FDROOT^XZ", PrintClientManager.ResolvePrinterData(job, "SHIPTOP"));
    }

    [Fact]
    public void RootPrinterData_AsArray_PicksFirstNonEmpty()
    {
        var job = Parse("""{"printerData":["","^XA^FDSECOND^XZ"]}""");

        Assert.Equal("^XA^FDSECOND^XZ", PrintClientManager.ResolvePrinterData(job, "SHIPTOP"));
    }

    [Fact]
    public void Labels_MatchingApplicatorType_Wins()
    {
        var job = Parse("""
            {"labels":[
              {"printerData":"^XA^FDSIDE^XZ","applicatorType":"SHIPSIDE"},
              {"printerData":"^XA^FDTOP^XZ","applicatorType":"shiptop"}]}
            """);

        Assert.Equal("^XA^FDTOP^XZ", PrintClientManager.ResolvePrinterData(job, "SHIPTOP"));
    }

    [Fact]
    public void Labels_NoApplicatorMatch_FallsBackToFirstWithData()
    {
        var job = Parse("""
            {"labels":[
              {"applicatorType":"SHIPSIDE"},
              {"printerData":"^XA^FDFALLBACK^XZ","applicatorType":"SHIPSIDE"}]}
            """);

        Assert.Equal("^XA^FDFALLBACK^XZ", PrintClientManager.ResolvePrinterData(job, "SHIPTOP"));
    }

    [Fact]
    public void LabelPrinterData_AsArray_Binds()
    {
        var job = Parse("""{"labels":[{"printerData":["^XA^FDARRAY^XZ"],"applicatorType":"SHIPTOP"}]}""");

        Assert.Equal("^XA^FDARRAY^XZ", PrintClientManager.ResolvePrinterData(job, "SHIPTOP"));
    }

    [Fact]
    public void NoPrinterDataAnywhere_ReturnsNull()
    {
        Assert.Null(PrintClientManager.ResolvePrinterData(Parse("""{"labels":[]}"""), "SHIPTOP"));
        Assert.Null(PrintClientManager.ResolvePrinterData(Parse("""{"other":1}"""), "SHIPTOP"));
    }
}
