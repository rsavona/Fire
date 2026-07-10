using Fusion.Common.Logging;
using Serilog.Events;

namespace Fusion.Element.Logger.Tests;

public class LogEventMessageClefTests
{
    [Fact]
    public void ToClef_FromClef_RoundTripsAllCoreFields()
    {
        var original = new LogEventMessage
        {
            Timestamp = new DateTime(2026, 7, 10, 12, 34, 56, 789, DateTimeKind.Utc),
            Level = LogEventLevel.Error,
            MessageTemplate = "Carton {Gin} diverted to {Lane}",
            RenderedMessage = "Carton \"G123\" diverted to 7",
            Exception = "System.InvalidOperationException: lane offline",
            ElementName = "PLC1",
            Properties = new Dictionary<string, object?>
            {
                ["Gin"] = "G123",
                ["Lane"] = 7L,
                ["Weight"] = 1.25,
                ["Recirculated"] = true,
                ["Operator"] = null
            }
        };

        var parsed = LogEventMessage.FromClef(original.ToClef());

        Assert.Equal(original.Timestamp, parsed.Timestamp);
        Assert.Equal(original.Level, parsed.Level);
        Assert.Equal(original.MessageTemplate, parsed.MessageTemplate);
        Assert.Equal(original.RenderedMessage, parsed.RenderedMessage);
        Assert.Equal(original.Exception, parsed.Exception);
        Assert.Equal(original.ElementName, parsed.ElementName);
        Assert.Equal(original.Properties.Keys.Order(), parsed.Properties.Keys.Order());
        foreach (var kv in original.Properties)
        {
            var actual = parsed.Properties[kv.Key];
            Assert.True(Equals(kv.Value, actual),
                $"Key '{kv.Key}': expected {kv.Value?.GetType().Name ?? "null"} '{kv.Value}', got {actual?.GetType().Name ?? "null"} '{actual}'");
        }
    }

    [Fact]
    public void ToClef_ProducesClefReservedFields()
    {
        var evt = new LogEventMessage
        {
            Level = LogEventLevel.Warning,
            MessageTemplate = "Hello {Name}",
            RenderedMessage = "Hello \"World\"",
            ElementName = "TEST"
        };

        string clef = evt.ToClef();

        Assert.Contains("\"@t\"", clef);
        Assert.Contains("\"@l\":\"Warning\"", clef);
        Assert.Contains("\"@mt\":\"Hello {Name}\"", clef);
        Assert.Contains("\"@m\":", clef);
        Assert.DoesNotContain("\"@x\"", clef); // no exception given
    }

    [Fact]
    public void FromClef_MissingLevel_DefaultsToInformation()
    {
        var parsed = LogEventMessage.FromClef("{\"@t\":\"2026-07-10T00:00:00.0000000Z\",\"@mt\":\"hi\"}");
        Assert.Equal(LogEventLevel.Information, parsed.Level);
    }

    [Fact]
    public void PropertyNamesStartingWithAt_AreEscapedAndRoundTrip()
    {
        var original = new LogEventMessage
        {
            MessageTemplate = "x",
            RenderedMessage = "x",
            Properties = new Dictionary<string, object?> { ["@Odd"] = "value" }
        };

        string clef = original.ToClef();
        Assert.Contains("\"@@Odd\"", clef);

        var parsed = LogEventMessage.FromClef(clef);
        Assert.Equal("value", parsed.Properties["@Odd"]);
    }

    [Fact]
    public void FromLogMessage_BindsTemplateArgsToNamedProperties()
    {
        var logMessage = new LogMessage
        {
            Level = LogEventLevel.Warning,
            Context = "SCAN1",
            MessageTemplate = "Bad read on {Device} after {Attempts} attempts",
            Args = ["Scanner-4", 3]
        };

        var evt = LogEventMessage.FromLogMessage(logMessage);

        Assert.Equal("SCAN1", evt.ElementName);
        Assert.Equal(LogEventLevel.Warning, evt.Level);
        Assert.Equal("Scanner-4", evt.Properties["Device"]);
        Assert.Equal(3, Convert.ToInt32(evt.Properties["Attempts"]));
        Assert.Contains("Scanner-4", evt.RenderedMessage);
        Assert.Contains("3", evt.RenderedMessage);
    }
}
