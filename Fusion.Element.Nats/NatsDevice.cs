using System.Collections.Concurrent;
using NATS.Client.Core;
using Fusion.Common;
using Fusion.Common.BaseClasses;
using Fusion.Common.Configurations;
using Fusion.Common.Contracts;
using Fusion.Common.Logging;
using Serilog.Core;
using ILogger = Serilog.ILogger;

namespace Fusion.Element.Nats;

public class NatsElement : ClientElementBase, IMessageProvider
{
    private NatsConnection? _connection;
    private readonly string _url;
    private readonly string _heartbeatSubject;
    private readonly ConcurrentDictionary<string, INatsSub<string>> _subscriptions = new();

    public event Func<object, object, Task>? MessageReceived;

    public NatsElement(IMessageBus bus, IElementBlueprint config, IFireLogger elementLogger, LoggingLevelSwitch ls)
        : base(bus, config, elementLogger, ls, true)
    {
        _url = ConfigurationLoader.GetOptionalConfig(config.Properties, "Url", "nats://127.0.0.1:4222");
        _heartbeatSubject = $"{Key.ElementName}.heartbeat";
        
        Logger.Information("[{Dev}] Initializing NATS Fusion.Element. URL: {Url}", Config.Name, _url);
    }

    public override async Task SendAsync(string message, CancellationToken token, bool fireEvent = true)
    {
        if (_connection == null) return;
        
        var subject = ConfigurationLoader.GetOptionalConfig(Config.Properties, "DefaultWriteSubject", $"{Key.ElementName}.out");
        await _connection.PublishAsync(subject, message, cancellationToken: token);
        
        if (fireEvent)
        {
            Logger.Information("[{Dev}] TX >> {Subject}: {Msg}", Config.Name, subject, message);
            await Machine.FireAsync(Event.MessageSent);
        }
    }

    public async Task PublishAsync(string subject, string message, bool fireEvent = true)
    {
        if (_connection == null) return;

        await _connection.PublishAsync(subject, message);
        
        if (fireEvent)
        {
            Logger.Information("[{Dev}] TX >> {Subject}: {Msg}", Config.Name, subject, message);
            await Machine.FireAsync(Event.MessageSent);
        }
    }

    public override async Task SendHeartbeatAsync(CancellationToken token)
    {
        if (_connection == null) return;
        await _connection.PublishAsync(_heartbeatSubject, "HB", cancellationToken: token);
    }

    protected override void OnElementFaultedAsync(CancellationToken token = default)
    {
        Logger.Error("[{Dev}] Element faulted. Reconnecting...", Config.Name);
        _ = Machine.FireAsync(Event.Start);
    }

    protected override async Task<bool> ConnectAsync(CancellationToken ct = default)
    {
        try
        {
            Logger.Information("[{Dev}] Connecting to NATS at {Url}...", Config.Name, _url);
            
            var opts = NatsOpts.Default with { Url = _url };
            _connection = new NatsConnection(opts);
            
            // Connect is implicit in NATS.Net but we can try a Ping to verify
            await _connection.ConnectAsync();
            await _connection.PingAsync(ct);

            Logger.Information("[{Dev}] NATS Connected successfully.", Config.Name);
            return true;
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "[{Dev}] NATS Connection failed.", Config.Name);
            return false;
        }
    }

    protected override async Task InitPeriodicEvent()
    {
        await SubscribeAsync(_heartbeatSubject, NotifyHeartbeatReceived);
    }

    protected override void EndPeriodicEvent()
    {
        _ = UnsubscribeAsync(_heartbeatSubject);
    }

    public async Task SubscribeAsync(string subject, Func<object, object, Task> handler)
    {
        if (_connection == null) return;

        var sub = await _connection.SubscribeCoreAsync<string>(subject);
        _subscriptions[subject] = sub;

        _ = Task.Run(async () =>
        {
            try
            {
                await foreach (var msg in sub.Msgs.ReadAllAsync())
                {
                    if (msg.Data != null)
                    {
                        if (subject != _heartbeatSubject)
                        {
                            Logger.Information("[{Dev}] RX << {Subject}: {Data}", Config.Name, subject, msg.Data);
                            await Machine.FireAsync(Event.MessageReceived);
                        }
                        
                        // Fire the local event for manager/others
                        if (MessageReceived != null)
                        {
                            await MessageReceived.Invoke(msg.Data, subject);
                        }

                        // Also call the specific handler if provided
                        if (handler != null)
                        {
                            await handler(msg.Data, subject);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "[{Dev}] Subscription loop failed for {Subject}", Config.Name, subject);
            }
        });

        Logger.Debug("[{Dev}] Subscribed to NATS subject: {Subject}", Config.Name, subject);
    }

    public async Task UnsubscribeAsync(string subject)
    {
        if (_subscriptions.TryRemove(subject, out var sub))
        {
            await sub.DisposeAsync();
            Logger.Information("[{Dev}] Unsubscribed from {Subject}", Config.Name, subject);
        }
    }

    protected override async Task OnElementStoppingAsync()
    {
        if (_connection != null)
        {
            foreach (var subject in _subscriptions.Keys)
            {
                await UnsubscribeAsync(subject);
            }
            _subscriptions.Clear();
            await _connection.DisposeAsync();
            _connection = null;
        }
    }

    protected override void DisposeManagedResources()
    {
        if (_connection != null)
        {
            _connection.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
        base.DisposeManagedResources();
    }
}
