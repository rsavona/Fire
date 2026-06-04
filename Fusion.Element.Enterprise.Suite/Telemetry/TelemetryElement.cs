using System.Diagnostics;
using System.Diagnostics.Metrics;
using OpenTelemetry;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using OpenTelemetry.Metrics;
using Fusion.Common.BaseClasses;
using Fusion.Common.Configurations;
using Fusion.Common.Contracts;
using Fusion.Common.Enums;
using Fusion.Common.Messaging;
using Serilog.Core;

namespace Fusion.Element.Enterprise.Suite.Telemetry;

public class TelemetryElement : ElementBase<TelemetryElement.State, TelemetryElement.Event, TelemetryElement.Metric>
{
    public enum State { Idle, Collecting }
    public enum Event { Start, Stop }
    public enum Metric { EventsCollected, SpansExported }

    private TracerProvider? _tracerProvider;
    private MeterProvider? _meterProvider;
    private readonly ActivitySource _activitySource;
    private readonly Meter _meter;
    private readonly Counter<long> _eventCounter;

    public TelemetryElement(IMessageBus bus, IElementBlueprint config, IFireLogger logger) 
        : base(bus, config, logger, new LoggingLevelSwitch(), State.Idle, Event.Start)
    {
        _activitySource = new ActivitySource("Fusion.Telemetry");
        _meter = new Meter("Fusion.Metrics");
        _eventCounter = _meter.CreateCounter<long>("fusion_events_total");
    }

    protected override void ConfigureStateMachine()
    {
        Machine.Configure(State.Idle)
            .Permit(Event.Start, State.Collecting);
        
        Machine.Configure(State.Collecting)
            .Permit(Event.Stop, State.Idle);
    }

    public override async Task StartAsync(CancellationToken token)
    {
        string endpoint = ConfigurationLoader.GetOptionalConfig(Config.Properties, "OtlpEndpoint", "http://localhost:4317");
        
        _tracerProvider = Sdk.CreateTracerProviderBuilder()
            .SetResourceBuilder(ResourceBuilder.CreateDefault().AddService(Config.Name))
            .AddSource(_activitySource.Name)
            .AddOtlpExporter(opt => opt.Endpoint = new Uri(endpoint))
            .AddConsoleExporter()
            .Build();

        _meterProvider = Sdk.CreateMeterProviderBuilder()
            .SetResourceBuilder(ResourceBuilder.CreateDefault().AddService(Config.Name))
            .AddMeter(_meter.Name)
            .AddOtlpExporter(opt => opt.Endpoint = new Uri(endpoint))
            .AddConsoleExporter()
            .Build();

        UpdateStatus(State.Collecting, Event.Start, ElementHealth.Normal, "Observability Active");
        Logger.Information("[{Dev}] OpenTelemetry Collection started. Exporting to {Endpoint}", Config.Name, endpoint);
        
        await Task.CompletedTask;
    }

    public override async Task StopAsync(CancellationToken token)
    {
        _tracerProvider?.Dispose();
        _meterProvider?.Dispose();
        UpdateStatus(State.Idle, Event.Stop, ElementHealth.Normal, "Observability Stopped");
        await Task.CompletedTask;
    }

    public Activity? StartActivity(string name, ActivityKind kind = ActivityKind.Internal)
    {
        _eventCounter.Add(1);
        return _activitySource.StartActivity(name, kind);
    }

    protected override ElementHealth MapStateToHealth(State state) => ElementHealth.Normal;
}
