using Xunit;
using Device.Plc.Suite;
using Device.Plc.Suite.Messages;

namespace Device.Plc.Suite.Tests;

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
        
        string rawMessage = $"{stx}CP1{gs}00000001{gs}DUM{gs}{{\"DecisionPoint\":\"SCN302\",\"GIN\":19,\"Barcodes\":[\"1Z59W9A50391821432\"],\"ActionTaken\":[\"5\"],\"ReasonCode\":1,\"Timestamp\":\"2026-05-07 14:14:01\"}}{etx}";
        
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
        string payload = "CP100000001DUM{\"DecisionPoint\":\"SCN302\",\"GIN\":19,\"Barcodes\":[\"1Z59W9A50391821432\"],\"ActionTaken\":[\"5\"],\"ReasonCode\":1,\"Timestamp\":\"2026-05-07 14:14:01\"}";
        
        bool success = parser.TryParseToPlcMessage(payload, out var plcMessage);
        
        Assert.False(success);
    }
}
