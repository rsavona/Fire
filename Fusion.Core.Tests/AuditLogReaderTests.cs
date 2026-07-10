using Fusion.Common.Enums;
using Fusion.Core.Replay;

namespace Fusion.Core.Tests;

public class AuditLogReaderTests
{
    // Real-shape legacy line (whole message rendered into @mt).
    private const string LegacyStatusLine =
        """{"@t":"2026-06-25T14:54:15.2489828Z","@mt":"[All_Elements.StatusMessage              ] [ElementStatusMessage]  ElementStatusMessage { ElementId = FUSION-SYS-PNA2_151, Timestamp = 6/25/2026 2:54:15 PM, State = Connected, Health = Normal, Comment = Connected: 127.0.0.1:62595, CountInbound = 3 }","SourceContext":"Fusion.Core.BusAuditLogger","AuditLog":true,"CorrelationId":"cabd7e5e-6b95-4dbd-b37b-98a1553bb3f7","MessageId":"3b81089f-d632-4fcc-996a-aa2b99cdd522"}""";

    // Structured line as produced by the current BusAuditLogger template.
    private const string StructuredFlowLine =
        """{"@t":"2026-07-10T08:00:01.5000000Z","@mt":"[{Topic,-40}] [{PayloadType,-15}]  {@Payload}","Topic":"System.DataFlow","PayloadType":"FlowEvent","Payload":{"Source":"HOST_A","Force":"EchoReaction","Destination":"LINK_SERVER","Timestamp":"2026-07-10T08:00:01.4990000Z"},"AuditLog":true,"CorrelationId":"11111111-1111-1111-1111-111111111111"}""";

    private const string StructuredTextLine =
        """{"@t":"2026-07-10T08:00:02.0000000Z","@mt":"[{Topic,-40}] [{PayloadType,-15}]  {Payload}","Topic":"HOST_A.MsgReceived","PayloadType":"String","Payload":"HELLO|123","AuditLog":true}""";

    [Fact]
    public void TryParseLine_LegacyFormat_RecoversTopicTypeAndPayload()
    {
        Assert.True(AuditLogReader.TryParseLine(LegacyStatusLine, out var record));
        Assert.Equal("All_Elements.StatusMessage", record.Topic);
        Assert.Equal("ElementStatusMessage", record.PayloadType);
        Assert.StartsWith("ElementStatusMessage {", record.PayloadText);
        Assert.Equal(new DateTimeOffset(2026, 6, 25, 14, 54, 15, TimeSpan.Zero).AddTicks(2489828), record.Timestamp);
        Assert.Equal("cabd7e5e-6b95-4dbd-b37b-98a1553bb3f7", record.CorrelationId);
        Assert.Null(record.PayloadJson);
    }

    [Fact]
    public void TryParseLine_StructuredFormat_RecoversStructuredPayload()
    {
        Assert.True(AuditLogReader.TryParseLine(StructuredFlowLine, out var record));
        Assert.Equal("System.DataFlow", record.Topic);
        Assert.Equal("FlowEvent", record.PayloadType);
        Assert.NotNull(record.PayloadJson);
    }

    [Fact]
    public void TryParseLine_StructuredStringPayload_RecoversText()
    {
        Assert.True(AuditLogReader.TryParseLine(StructuredTextLine, out var record));
        Assert.Equal("HOST_A.MsgReceived", record.Topic);
        Assert.Equal("HELLO|123", record.PayloadText);
        Assert.Null(record.PayloadJson);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not json at all")]
    [InlineData("{\"no\":\"timestamp\"}")]
    [InlineData("{\"@t\":\"2026-07-10T08:00:00Z\",\"@mt\":\"unrelated log line\"}")]
    public void TryParseLine_GarbageOrUnrelated_ReturnsFalse(string? line)
    {
        Assert.False(AuditLogReader.TryParseLine(line, out _));
    }

    [Fact]
    public void TryParseFlowEvent_StructuredPayload_Reconstructs()
    {
        AuditLogReader.TryParseLine(StructuredFlowLine, out var record);
        Assert.True(AuditLogReader.TryParseFlowEvent(record, out var flow));
        Assert.Equal("HOST_A", flow.Source);
        Assert.Equal("EchoReaction", flow.Force);
        Assert.Equal("LINK_SERVER", flow.Destination);
    }

    [Fact]
    public void TryParseFlowEvent_LegacyTextPayload_ReturnsFalse()
    {
        // Legacy FlowEvent lines render only the type name — not reconstructable.
        var line = """{"@t":"2026-07-10T08:00:00Z","@mt":"[System.DataFlow                          ] [FlowEvent      ]  Fusion.Common.Messaging.FlowEvent","AuditLog":true}""";
        Assert.True(AuditLogReader.TryParseLine(line, out var record));
        Assert.False(AuditLogReader.TryParseFlowEvent(record, out _));
    }

    [Fact]
    public void TryParseElementStatus_LegacyRendering_RecoversIdStateHealth()
    {
        AuditLogReader.TryParseLine(LegacyStatusLine, out var record);
        Assert.True(AuditLogReader.TryParseElementStatus(record, out var status));
        Assert.Equal("FUSION-SYS-PNA2_151", status.ElementId);
        Assert.Equal("Connected", status.State);
        Assert.Equal(ElementHealth.Normal, status.Health);
    }

    [Fact]
    public void FilterWindow_RespectsInclusiveBounds()
    {
        var records = Enumerable.Range(0, 10)
            .Select(i => new AuditRecord
            {
                Timestamp = new DateTimeOffset(2026, 7, 10, 8, 0, i, TimeSpan.Zero),
                Topic = "T",
                PayloadType = "String",
                PayloadText = i.ToString()
            })
            .ToList();

        var from = new DateTimeOffset(2026, 7, 10, 8, 0, 3, TimeSpan.Zero);
        var to = new DateTimeOffset(2026, 7, 10, 8, 0, 6, TimeSpan.Zero);

        var filtered = AuditLogReader.FilterWindow(records, from, to).ToList();

        Assert.Equal(new[] { "3", "4", "5", "6" }, filtered.Select(r => r.PayloadText));
    }

    [Fact]
    public void FilterWindow_OpenEndedBounds_PassEverything()
    {
        var records = new[]
        {
            new AuditRecord { Timestamp = DateTimeOffset.UtcNow, Topic = "T", PayloadType = "String", PayloadText = "a" }
        };
        Assert.Single(AuditLogReader.FilterWindow(records, null, null));
    }

    [Fact]
    public void ReadRecords_And_ListWindows_ParseFilesOnDisk()
    {
        var dir = Directory.CreateTempSubdirectory("fusion-audit-test").FullName;
        try
        {
            var file = Path.Combine(dir, "Test_audit_20260710.json");
            File.WriteAllLines(file, new[] { LegacyStatusLine, "garbage line", StructuredFlowLine });

            var records = AuditLogReader.ReadRecords(file).ToList();
            Assert.Equal(2, records.Count);

            var windows = AuditLogReader.ListWindows(dir);
            var window = Assert.Single(windows);
            Assert.Equal(2, window.RecordCount);
            Assert.Equal(records[0].Timestamp, window.First);
            Assert.Equal(records[1].Timestamp, window.Last);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void ListWindows_MissingDirectory_ReturnsEmpty()
    {
        Assert.Empty(AuditLogReader.ListWindows(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"))));
    }
}
