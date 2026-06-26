using Xunit;
using Fusion.Element.Plc.Suite;
using Fusion.Element.Plc.Suite.Messages;

namespace Fusion.Element.Plc.Suite.Tests;

public class PlcParserTests
{
    [Fact]
    public void TestParseFailingMessage()
    {
        var parser = new PlcMessageParser();
        // This is the message from the error log, but I'll add the control characters to see if it works with them.
        char stx = '\x02';
        char etx = '\x03';
        char gs = '\x1D';
        
        string rawMessage = $"{stx}CP1{gs}00000001{gs}DUM{gs}{{\"DecisionPoint\":\"SCN302\",\"GIN\":19,\"Barcodes\":[\"1Z59W9A50391821432\"],\"ActionTaken\":\"5\",\"ReasonCode\":1,\"Timestamp\":\"2026-05-07 14:14:01\"}}{etx}";
        
        // Simulating PlcMessageProcessor.ProcessMessageAsync extraction
        string payload = rawMessage.Trim(stx, etx);
        
        bool success = parser.TryParseToPlcMessage(payload, out var plcMessage);
        
        Assert.True(success);
        Assert.NotNull(plcMessage);
        Assert.Equal("CP1", plcMessage.ClientKey);
        Assert.Equal(1, plcMessage.SequenceNumber);
        Assert.Equal(PlcMessageHeaders.DUM, plcMessage.Header);
    }

    [Fact]
    public void TestParseLiteralMessageFromError()
    {
        var parser = new PlcMessageParser();
        // This is exactly what was in the error log (assuming GS were missing or removed)
        string payload = "CP100000001DUM{\"DecisionPoint\":\"SCN302\",\"GIN\":19,\"Barcodes\":[\"1Z59W9A50391821432\"],\"ActionTaken\":\"5\",\"ReasonCode\":1,\"Timestamp\":\"2026-05-07 14:14:01\"}";
        
        bool success = parser.TryParseToPlcMessage(payload, out var plcMessage);
        
        Assert.False(success);
    }

    [Fact]
    public void TestParseTestSummaryMessage()
    {
        var parser = new PlcMessageParser();
        var payload = new TestMessagePayload(
            TestName: "print-and-apply-real-world",
            Description: "summary",
            ScriptPath: "print-and-apply-real-world.json",
            GeneratedAtUtc: "2026-06-10T12:00:00.000Z",
            StartsAtUtc: "2026-06-10T12:00:05.000Z",
            StartsInSeconds: 5,
            StageCount: 1,
            ToteCount: 2,
            DecisionChain: "PNALINE-2:0;PNA2_151:3000|PNA2_152:3000;PNA2_Verify:3000",
            Printer1: "PNA2_151",
            Printer2: "PNA2_152",
            Stages:
            [
                new TestStageSummary(
                    Stage: 1,
                    Name: "single-box-baseline",
                    ToteCount: 2,
                    FirstGin: 1,
                    LastGin: 2,
                    InductionSpacingMs: 6000,
                    ExpectedOutcomes: ["ship"],
                    ExpectedFlow: "baseline")
            ]);

        var rawMessage = PlcMessageParser.CreateTestMessage("CP1", payload).ToString();

        bool success = parser.TryParseToPlcMessage(rawMessage, out var plcMessage);

        Assert.True(success);
        Assert.NotNull(plcMessage);
        Assert.Equal(PlcMessageHeaders.TEST, plcMessage.Header);
        var testPayload = Assert.IsType<TestMessagePayload>(plcMessage.Payload);
        Assert.Equal("print-and-apply-real-world", testPayload.TestName);
        Assert.Equal(5, testPayload.StartsInSeconds);
        Assert.Single(testPayload.Stages);
    }
}
