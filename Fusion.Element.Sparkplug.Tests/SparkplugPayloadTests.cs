using Fusion.Element.Sparkplug.Protocol;

namespace Fusion.Element.Sparkplug.Tests;

public class SparkplugPayloadTests
{
    [Fact]
    public void EncodeDecode_RoundTrips_AllScalarDataTypes()
    {
        var payload = new SparkplugPayload
        {
            TimestampMs = 1720000000123,
            Seq = 42,
            Metrics =
            [
                SparkplugMetric.Create("Counts/Inbound", 12345L, 1720000000100),
                SparkplugMetric.Create("Counts/Negative", -77L, 1720000000100),
                SparkplugMetric.Create("Rates/InboundPerSec", 3.75d, 1720000000100),
                SparkplugMetric.Create("FloatValue", 1.5f, 1720000000100),
                SparkplugMetric.Create("IsRunning", true, 1720000000100),
                SparkplugMetric.Create("State", "Connected", 1720000000100),
                SparkplugMetric.Create("Unsigned", 18446744073709551615UL, 1720000000100)
            ]
        };

        var decoded = SparkplugPayload.Decode(payload.Encode());

        Assert.Equal(payload.TimestampMs, decoded.TimestampMs);
        Assert.Equal(payload.Seq, decoded.Seq);
        Assert.Equal(payload.Metrics.Count, decoded.Metrics.Count);

        for (int i = 0; i < payload.Metrics.Count; i++)
        {
            Assert.Equal(payload.Metrics[i].Name, decoded.Metrics[i].Name);
            Assert.Equal(payload.Metrics[i].DataType, decoded.Metrics[i].DataType);
            Assert.Equal(payload.Metrics[i].TimestampMs, decoded.Metrics[i].TimestampMs);
            Assert.Equal(payload.Metrics[i].Value, decoded.Metrics[i].Value);
        }
    }

    [Fact]
    public void EncodeDecode_RoundTrips_NullMetric()
    {
        var payload = new SparkplugPayload
        {
            Seq = 0,
            Metrics = [SparkplugMetric.Create("Empty", null)]
        };

        var decoded = SparkplugPayload.Decode(payload.Encode());

        Assert.Single(decoded.Metrics);
        Assert.True(decoded.Metrics[0].IsNull);
        Assert.Null(decoded.Metrics[0].Value);
    }

    [Fact]
    public void EncodeDecode_RoundTrips_Alias()
    {
        var payload = new SparkplugPayload
        {
            Seq = 7,
            Metrics = [SparkplugMetric.Create("Aliased", 5L) with { Alias = 99 }]
        };

        var decoded = SparkplugPayload.Decode(payload.Encode());

        Assert.Equal(99UL, decoded.Metrics[0].Alias);
        Assert.Equal(5L, decoded.Metrics[0].Value);
    }

    [Fact]
    public void Decode_NdeathPayload_HasNoSeq()
    {
        var session = new SparkplugSession();
        session.OpenSession();

        var decoded = SparkplugPayload.Decode(session.CreateNodeDeathPayload().Encode());

        Assert.Null(decoded.Seq);
        var bdSeq = Assert.Single(decoded.Metrics);
        Assert.Equal(SparkplugSession.BdSeqMetricName, bdSeq.Name);
        Assert.Equal(0L, bdSeq.Value);
    }

    [Fact]
    public void FromText_InfersNumericAndBooleanTypes()
    {
        Assert.Equal(SparkplugDataType.Double, SparkplugMetric.FromText("A", "42.5").DataType);
        Assert.Equal(SparkplugDataType.Boolean, SparkplugMetric.FromText("B", "true").DataType);
        Assert.Equal(SparkplugDataType.String, SparkplugMetric.FromText("C", "hello world").DataType);
    }
}
