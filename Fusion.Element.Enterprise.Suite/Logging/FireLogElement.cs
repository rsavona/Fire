using System.Text.Json;
using Fusion.Common;
using Fusion.Common.BaseClasses;
using Fusion.Common.Configurations;
using Fusion.Common.Contracts;
using Fusion.Common.Enums;
using Fusion.Common.Logging;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using Serilog.Formatting.Compact;

namespace Fusion.Element.Enterprise.Suite.Logging;

public class FireLogElement : ElementBase<FireLogElement.State, FireLogElement.Event, FireLogElement.Metric>
{
    public enum State { Idle, Logging }
    public enum Event { Start, Stop }
    public enum Metric { LogsWritten, BytesStored }

    private readonly ILoggingBus _loggingBus;
    private Serilog.ILogger? _fileLogger;
    private string _logFormat;
    private string _filePath;
    private int _retainDays;

    public FireLogElement(IMessageBus bus, ILoggingBus loggingBus, IElementBlueprint config, IFireLogger logger) 
        : base(bus, config, logger, new LoggingLevelSwitch(), State.Idle, Event.Start)
    {
        _loggingBus = loggingBus;
        _logFormat = ConfigurationLoader.GetOptionalConfig(config.Properties, "LogFormat", "Text").ToUpper();
        _filePath = ConfigurationLoader.GetOptionalConfig(config.Properties, "FilePath", "logs/fusion_central_.log");
        _retainDays = ConfigurationLoader.GetOptionalConfig(config.Properties, "RetainDays", 30);
    }

    protected override void ConfigureStateMachine()
    {
        Machine.Configure(State.Idle)
            .Permit(Event.Start, State.Logging);
        
        Machine.Configure(State.Logging)
            .Permit(Event.Stop, State.Idle);
    }

    public override async Task StartAsync(CancellationToken token)
    {
        // Configure the internal Serilog instance for file writing
        var loggerConfig = new LoggerConfiguration()
            .MinimumLevel.Verbose();

        if (_logFormat == "JSON")
        {
            loggerConfig.WriteTo.File(new CompactJsonFormatter(), _filePath, rollingInterval: RollingInterval.Day, retainedFileCountLimit: _retainDays);
        }
        else
        {
            loggerConfig.WriteTo.File(_filePath, rollingInterval: RollingInterval.Day, retainedFileCountLimit: _retainDays,
                outputTemplate: "[{Timestamp:HH:mm:ss.fff}][{Level:u3}][{Context}] {Message:lj}{NewLine}{Exception}");
        }

        _fileLogger = loggerConfig.CreateLogger();

        // Subscribe to all logs on the high-speed bus
        await _loggingBus.SubscribeAsync("LOG.#", HandleLogMessageAsync);

        UpdateStatus(State.Logging, Event.Start, ElementHealth.Normal, $"Logging to {_filePath} ({_logFormat})");
        Logger.Information("[{Dev}] Centralized Logging Element active. Format: {Format}", Config.Name, _logFormat);
    }

    public override Task StopAsync(CancellationToken token)
    {
        (_fileLogger as IDisposable)?.Dispose();
        UpdateStatus(State.Idle, Event.Stop, ElementHealth.Normal, "Logging Stopped");
        return Task.CompletedTask;
    }

    private async Task HandleLogMessageAsync(LogMessage msg, CancellationToken ct)
    {
        if (_fileLogger == null) return;

        // Re-construct the log event for the internal file logger
        var level = msg.Level;
        var template = msg.MessageTemplate;
        var args = msg.Args;
        
        // Add context for the formatter
        var loggerWithContext = _fileLogger.ForContext("Context", msg.Context);

        if (msg.Exception != null)
        {
            loggerWithContext.Write(level, msg.Exception, template, args);
        }
        else
        {
            loggerWithContext.Write(level, template, args);
        }

        Tracker.IncrementInbound();
        await Task.CompletedTask;
    }

    protected override ElementHealth MapStateToHealth(State state) => ElementHealth.Normal;
}
