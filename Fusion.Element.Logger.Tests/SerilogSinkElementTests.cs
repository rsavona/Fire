using Fusion.Common;
using Fusion.Common.Blueprints;
using Fusion.Common.Contracts;
using Fusion.Common.Logging;
using Serilog;
using Serilog.Core;
using Serilog.Events;

namespace Fusion.Element.Logger.Tests;

public class SerilogSinkElementTests : IDisposable
{
    private readonly string _tempDir;
    private readonly FakeMessageBus _messageBus = new();
    private readonly FakeLoggingBus _loggingBus = new();

    public SerilogSinkElementTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "FusionLoggerTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best effort */ }
    }

    private async Task<SerilogSinkElement> CreateStartedElementAsync(Dictionary<string, object>? extraProps = null)
    {
        var props = new Dictionary<string, object>
        {
            ["LogFilePath"] = Path.Combine(_tempDir, "sink_.clef")
        };
        if (extraProps != null)
        {
            foreach (var kv in extraProps) props[kv.Key] = kv.Value;
        }

        var blueprint = new ElementBlueprint
        {
            Name = "LOGSINK",
            Manager = "LogSinkElementManager",
            Enable = true,
            Properties = props
        };

        var fireLogger = new FireLogger(new LoggerConfiguration().CreateLogger());
        var element = new SerilogSinkElement(_messageBus, _loggingBus, blueprint, fireLogger, new LoggingLevelSwitch());
        await element.StartAsync(CancellationToken.None);
        return element;
    }

    private static LogMessage Raw(LogEventLevel level, string context, string template = "something happened")
        => new()
        {
            Level = level,
            Context = context,
            MessageTemplate = template,
            Args = []
        };

    [Fact]
    public async Task WarningEvent_IsRepublishedOnMessageBus_WithSysLogTopic()
    {
        var element = await CreateStartedElementAsync();

        await element.HandleLogMessageAsync(Raw(LogEventLevel.Warning, "PLC1"), CancellationToken.None);

        var published = Assert.Single(_messageBus.Published);
        Assert.Equal("SYS.LOG.PLC1.Warning", published.Topic);
        var payload = Assert.IsType<LogEventMessage>(published.Envelope.Payload);
        Assert.Equal("PLC1", payload.ElementName);
        Assert.Equal(LogEventLevel.Warning, payload.Level);

        await element.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task DebugEvent_IsNotRepublished()
    {
        var element = await CreateStartedElementAsync();

        await element.HandleLogMessageAsync(Raw(LogEventLevel.Debug, "PLC1"), CancellationToken.None);
        await element.HandleLogMessageAsync(Raw(LogEventLevel.Verbose, "PLC1"), CancellationToken.None);
        await element.HandleLogMessageAsync(Raw(LogEventLevel.Information, "PLC1"), CancellationToken.None);

        Assert.Empty(_messageBus.Published);
        Assert.Equal(3, element.ConsumedCount); // still consumed and written to the sink

        await element.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task ErrorEvent_IsRepublished()
    {
        var element = await CreateStartedElementAsync();

        await element.HandleLogMessageAsync(Raw(LogEventLevel.Error, "SCAN2"), CancellationToken.None);

        var published = Assert.Single(_messageBus.Published);
        Assert.Equal("SYS.LOG.SCAN2.Error", published.Topic);

        await element.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task RepublishMinimumLevel_IsConfigurable()
    {
        var element = await CreateStartedElementAsync(new Dictionary<string, object>
        {
            ["RepublishMinimumLevel"] = "Error"
        });

        await element.HandleLogMessageAsync(Raw(LogEventLevel.Warning, "PLC1"), CancellationToken.None);
        Assert.Empty(_messageBus.Published);

        await element.HandleLogMessageAsync(Raw(LogEventLevel.Error, "PLC1"), CancellationToken.None);
        Assert.Single(_messageBus.Published);

        await element.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task RecursionGuard_SysLogTopicEvent_IsNotRepublishedAgain()
    {
        var element = await CreateStartedElementAsync();

        // A log event whose context already carries a SYS.LOG topic (i.e. something logged
        // a republished event and it re-entered the firehose) must not be republished again.
        await element.HandleLogMessageAsync(
            Raw(LogEventLevel.Error, "SYS.LOG.PLC1.Warning"), CancellationToken.None);

        Assert.Empty(_messageBus.Published);
        Assert.Equal(1, element.ConsumedCount); // still written to the sink, just not republished

        await element.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task RecursionGuard_OwnEvents_AreNotRepublished()
    {
        var element = await CreateStartedElementAsync();

        await element.HandleLogMessageAsync(Raw(LogEventLevel.Error, "LOGSINK"), CancellationToken.None);

        Assert.Empty(_messageBus.Published);

        await element.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task StoppedElement_IgnoresEvents()
    {
        var element = await CreateStartedElementAsync();
        await element.StopAsync(CancellationToken.None);

        await element.HandleLogMessageAsync(Raw(LogEventLevel.Error, "PLC1"), CancellationToken.None);

        Assert.Empty(_messageBus.Published);
        Assert.Equal(0, element.ConsumedCount);
    }

    [Fact]
    public async Task ConsumedEvents_AreWrittenToClefSink()
    {
        var element = await CreateStartedElementAsync();

        await element.HandleLogMessageAsync(new LogMessage
        {
            Level = LogEventLevel.Information,
            Context = "PLC1",
            MessageTemplate = "Carton {Gin} at {Lane}",
            Args = ["G42", 3]
        }, CancellationToken.None);

        await element.StopAsync(CancellationToken.None); // disposes + flushes the sink

        var files = Directory.GetFiles(_tempDir, "*.clef");
        var file = Assert.Single(files);
        string content = File.ReadAllText(file);
        Assert.Contains("Carton {Gin} at {Lane}", content); // template preserved, not flattened
        Assert.Contains("G42", content);                    // structured property value present
    }
}
