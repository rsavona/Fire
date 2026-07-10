using Fusion.Common;
using Fusion.Common.Enums;
using Fusion.Element.Sparkplug.Protocol;

namespace Fusion.Element.Sparkplug.Tests;

public class SparkplugSessionTests
{
    private static ElementStatusMessage SampleStatus(string name = "PLC1", ElementHealth health = ElementHealth.Normal,
        int inbound = 10, int outbound = 20)
    {
        return new ElementStatusMessage(
            new ElementKey("SYS", name),
            state: "Connected",
            health: health,
            comment: "Event: MessageReceived",
            countInbound: inbound,
            countOutbound: outbound,
            countConnections: 1,
            countDisconnects: 0,
            countError: 2,
            inboundRate: 1.5,
            outboundRate: 2.5,
            avgProcessTime: 12.3,
            hb: '+',
            resTasks: 3,
            resContainers: 4,
            resDeepCount: 5,
            extraMetrics: new Dictionary<string, long> { ["LabelsPrinted"] = 42 });
    }

    // ------------------------------------------------------------------
    // Sequence numbers
    // ------------------------------------------------------------------

    [Fact]
    public void Seq_StartsAtZeroOnNbirth_AndWrapsAt255()
    {
        var session = new SparkplugSession();
        session.OpenSession();

        var birth = session.CreateNodeBirthPayload();
        Assert.Equal(0UL, birth.Seq);

        // Consume seq 1..255, then verify wrap back to 0.
        for (int i = 1; i <= 255; i++)
            Assert.Equal((ulong)i, session.NextSeq());

        Assert.Equal(0UL, session.NextSeq());
        Assert.Equal(1UL, session.NextSeq());
    }

    [Fact]
    public void BdSeq_IncrementsAcrossSessions_AndWraps()
    {
        var session = new SparkplugSession();

        Assert.Equal(0UL, session.OpenSession());
        Assert.Equal(1UL, session.OpenSession());
        Assert.Equal(2UL, session.OpenSession());

        // Death payload announces the current session's bdSeq.
        var death = session.CreateNodeDeathPayload();
        Assert.Equal(2L, death.Metrics.Single(m => m.Name == SparkplugSession.BdSeqMetricName).Value);

        // Birth carries the same bdSeq as the death will.
        var birth = session.CreateNodeBirthPayload();
        Assert.Equal(2L, birth.Metrics.Single(m => m.Name == SparkplugSession.BdSeqMetricName).Value);

        for (int i = 3; i <= 255; i++)
            session.OpenSession();
        Assert.Equal(255UL, session.CurrentBdSeq);
        Assert.Equal(0UL, session.OpenSession());
    }

    [Fact]
    public void OpenSession_ResetsSeq_AndRequiresDeviceRebirth()
    {
        var session = new SparkplugSession();
        session.OpenSession();
        session.RecordDevice("PLC1", [SparkplugMetric.Create("State", "Connected")]);
        session.CreateDeviceBirthPayload("PLC1");
        Assert.True(session.IsDeviceBorn("PLC1"));

        session.OpenSession();

        Assert.False(session.IsDeviceBorn("PLC1"));
        Assert.Equal(0UL, session.CreateNodeBirthPayload().Seq);
        Assert.Contains("PLC1", session.GetKnownDevices());
    }

    // ------------------------------------------------------------------
    // Birth construction from element status
    // ------------------------------------------------------------------

    [Fact]
    public void DeviceBirth_ContainsStatusFieldsAsMetrics()
    {
        var session = new SparkplugSession();
        session.OpenSession();
        session.CreateNodeBirthPayload();

        var status = SampleStatus();
        var metrics = SparkplugMetricMapper.FromStatus(status);
        session.RecordDevice("PLC1", metrics);

        var birth = session.CreateDeviceBirthPayload("PLC1");

        Assert.Equal(1UL, birth.Seq); // NBIRTH took 0
        var byName = birth.Metrics.ToDictionary(m => m.Name);

        Assert.Equal("Connected", byName["State"].Value);
        Assert.Equal(SparkplugDataType.String, byName["State"].DataType);
        Assert.Equal("Normal", byName["Health"].Value);
        Assert.Equal(10L, byName["Counts/Inbound"].Value);
        Assert.Equal(20L, byName["Counts/Outbound"].Value);
        Assert.Equal(2L, byName["Counts/Errors"].Value);
        Assert.Equal(SparkplugDataType.Int64, byName["Counts/Inbound"].DataType);
        Assert.Equal(1.5d, byName["Rates/InboundPerSec"].Value);
        Assert.Equal(SparkplugDataType.Double, byName["Rates/InboundPerSec"].DataType);
        Assert.Equal(42L, byName["Metrics/LabelsPrinted"].Value);

        // The whole birth survives a wire round-trip.
        var decoded = SparkplugPayload.Decode(birth.Encode());
        Assert.Equal(birth.Metrics.Count, decoded.Metrics.Count);
        Assert.Equal("Connected", decoded.Metrics.Single(m => m.Name == "State").Value);
    }

    // ------------------------------------------------------------------
    // Report by exception
    // ------------------------------------------------------------------

    [Fact]
    public void RecordDevice_ReportsOnlyChangedMetrics()
    {
        var session = new SparkplugSession();
        session.OpenSession();

        var first = session.RecordDevice("PLC1", SparkplugMetricMapper.FromStatus(SampleStatus()));
        Assert.True(first.NeedsBirth);
        session.CreateDeviceBirthPayload("PLC1");

        // Same values → nothing to report.
        var unchanged = session.RecordDevice("PLC1", SparkplugMetricMapper.FromStatus(SampleStatus()));
        Assert.False(unchanged.NeedsBirth);
        Assert.Empty(unchanged.ChangedMetrics);

        // One counter moved → exactly that metric is reported.
        var moved = session.RecordDevice("PLC1", SparkplugMetricMapper.FromStatus(SampleStatus(inbound: 11)));
        Assert.False(moved.NeedsBirth);
        var changed = Assert.Single(moved.ChangedMetrics);
        Assert.Equal("Counts/Inbound", changed.Name);
        Assert.Equal(11L, changed.Value);
    }

    [Fact]
    public void DeviceDeath_RequiresRebirthOnRecovery()
    {
        var session = new SparkplugSession();
        session.OpenSession();
        session.RecordDevice("PLC1", SparkplugMetricMapper.FromStatus(SampleStatus()));
        session.CreateDeviceBirthPayload("PLC1");

        session.CreateDeviceDeathPayload("PLC1");
        Assert.False(session.IsDeviceBorn("PLC1"));
        Assert.DoesNotContain("PLC1", session.GetKnownDevices());

        var recovery = session.RecordDevice("PLC1", SparkplugMetricMapper.FromStatus(SampleStatus()));
        Assert.True(recovery.NeedsBirth);
    }

    // ------------------------------------------------------------------
    // NCMD Rebirth
    // ------------------------------------------------------------------

    [Fact]
    public void NcmdRebirth_IsDetected_AndTriggersRebirthState()
    {
        var session = new SparkplugSession();
        session.OpenSession();
        session.CreateNodeBirthPayload();
        session.RecordDevice("PLC1", SparkplugMetricMapper.FromStatus(SampleStatus()));
        session.CreateDeviceBirthPayload("PLC1");

        // Simulate what Ignition sends: an NCMD payload with Node Control/Rebirth = true.
        var ncmdWire = new SparkplugPayload
        {
            Seq = null,
            Metrics = [SparkplugMetric.Create(SparkplugSession.RebirthMetricName, true)]
        }.Encode();

        var ncmd = SparkplugPayload.Decode(ncmdWire);
        Assert.True(SparkplugSession.IsRebirthRequest(ncmd));

        ulong bdSeqBefore = session.CurrentBdSeq;
        session.ResetForRebirth();

        // bdSeq is unchanged (same MQTT session), seq restarts at 0, and all
        // devices need a fresh DBIRTH.
        Assert.Equal(bdSeqBefore, session.CurrentBdSeq);
        Assert.False(session.IsDeviceBorn("PLC1"));
        var rebirth = session.CreateNodeBirthPayload();
        Assert.Equal(0UL, rebirth.Seq);

        var deviceRebirth = session.CreateDeviceBirthPayload("PLC1");
        Assert.Equal(1UL, deviceRebirth.Seq);
        Assert.Contains(deviceRebirth.Metrics, m => m.Name == "State");
    }

    [Fact]
    public void RebirthRequest_FalseValue_IsNotRebirth()
    {
        var payload = new SparkplugPayload
        {
            Metrics = [SparkplugMetric.Create(SparkplugSession.RebirthMetricName, false)]
        };

        Assert.False(SparkplugSession.IsRebirthRequest(payload));
    }
}
