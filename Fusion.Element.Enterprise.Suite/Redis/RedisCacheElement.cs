using StackExchange.Redis;
using Fusion.Common.BaseClasses;
using Fusion.Common.Contracts;
using Fusion.Common.Enums;
using Serilog.Core;

namespace Fusion.Element.Enterprise.Suite.Redis;

public class RedisCacheElement : ClientElementBase
{
    private ConnectionMultiplexer? _redis;
    private IDatabase? _db;
    private string _connectionString = string.Empty;

    public RedisCacheElement(IMessageBus bus, IElementBlueprint config, IFireLogger logger, LoggingLevelSwitch ls)
        : base(bus, config, logger, ls, needsHb: true)
    {
        _connectionString = config.Properties.TryGetValue("ConnectionString", out var conn) ? conn.ToString() ?? "" : "localhost:6379";
    }

    protected override async Task<bool> ConnectAsync(CancellationToken ct = default)
    {
        try
        {
            _redis = await ConnectionMultiplexer.ConnectAsync(_connectionString);
            _db = _redis.GetDatabase();
            Logger.Information("[{Dev}] Connected to Redis at {Conn}", Config.Name, _connectionString);
            return true;
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "[{Dev}] Failed to connect to Redis", Config.Name);
            return false;
        }
    }

    public async Task SetAsync(string key, string value, TimeSpan? expiry = null)
    {
        if (_db == null) return;
        
        // Using separate calls to avoid Expiration struct conversion issues in SE.Redis 2.13
        if (expiry.HasValue)
        {
            await _db.StringSetAsync(key, value, expiry.Value);
        }
        else
        {
            await _db.StringSetAsync(key, value);
        }
        
        Tracker.IncrementOutbound();
    }

    public async Task<string?> GetAsync(string key)
    {
        if (_db == null) return null;
        Tracker.IncrementInbound();
        var result = await _db.StringGetAsync(key);
        return result.IsNull ? null : result.ToString();
    }

    public async Task PublishAsync(string channel, string message)
    {
        if (_redis == null) return;
        var pub = _redis.GetSubscriber();
        await pub.PublishAsync(RedisChannel.Literal(channel), message);
        Tracker.IncrementOutbound();
    }

    public async Task SubscribeAsync(string channel, Action<string, string> handler)
    {
        if (_redis == null) return;
        var sub = _redis.GetSubscriber();
        await sub.SubscribeAsync(RedisChannel.Literal(channel), (c, m) => handler(c.ToString(), m.ToString()));
        Logger.Information("[{Dev}] Subscribed to Redis channel: {Channel}", Config.Name, channel);
    }

    public override Task SendAsync(string message, CancellationToken token, bool fireEvent = true)
    {
        // Default send can be a publish if a default channel is configured
        if (Config.Properties.TryGetValue("DefaultChannel", out var channel))
        {
            return PublishAsync(channel.ToString() ?? "fusion-default", message);
        }
        return Task.CompletedTask;
    }

    public override Task SendHeartbeatAsync(CancellationToken token)
    {
        return Task.CompletedTask;
    }

    protected override void OnElementFaultedAsync(CancellationToken token = default)
    {
        Logger.Warning("[{Dev}] Redis Element Faulted.", Config.Name);
    }

    protected override async Task ElementDisconnectedAsync()
    {
        if (_redis != null) await _redis.CloseAsync();
        await base.ElementDisconnectedAsync();
    }
}
