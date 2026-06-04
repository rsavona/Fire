using System.Text.Json;
using Fusion.Common;
using Fusion.Common.BaseClasses;
using Fusion.Common.Contracts;
using Microsoft.Extensions.Logging;

namespace Fusion.Element.Enterprise.Suite.Redis;

public class RedisCacheManager : ElementManagerBase<RedisCacheElement>
{
    public RedisCacheManager(IMessageBus bus, List<IElementBlueprint> configs,
        IFireLogger<RedisCacheManager> logger,
        Func<IElementBlueprint, IFireLogger, RedisCacheElement> elementFactory,
        string managerName)
        : base(bus, configs, logger, elementFactory, managerName)
    {
    }

    protected override async Task HandleBusMessageAsync(MessageEnvelope envelope, CancellationToken ct)
    {
        var topic = envelope.Destination;
        if (!ElementInstances.TryGetValue(topic.ElementName, out var element)) return;

        try
        {
            // Simple command protocol: "SET:key:value" or "GET:key"
            string payload = envelope.Payload?.ToString() ?? "";
            var parts = payload.Split(':', 3);
            if (parts.Length < 2) return;

            string cmd = parts[0].ToUpper();
            string key = parts[1];

            if (cmd == "SET" && parts.Length == 3)
            {
                await element.SetAsync(key, parts[2]);
                Logger.Information("[{Dev}] Cached key {Key}", element.Config.Name, key);
            }
            else if (cmd == "GET")
            {
                string? value = await element.GetAsync(key);
                // Publish response back to a specific topic
                var responseTopic = $"{topic}.RESULT.{key}";
                await MessageBus.PublishAsync(responseTopic, new MessageEnvelope(responseTopic, value ?? "NULL"));
            }
            else if (cmd == "PUBLISH" && parts.Length == 3)
            {
                await element.PublishAsync(key, parts[2]); // key is channel here
                Logger.Information("[{Dev}] Published to Redis channel {Channel}", element.Config.Name, key);
            }
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "[{Dev}] Redis operation failed", element.Config.Name);
        }
    }

    protected override async Task RegisterElementSourceBonds(IElement element)
    {
        if (element is not RedisCacheElement redis) return;

        // If the config specifies channels to subscribe to
        if (redis.Config.Properties.TryGetValue("SubscribedChannels", out var channelsObj))
        {
            var channels = channelsObj switch
            {
                string s => s.Split(',', StringSplitOptions.RemoveEmptyEntries),
                IEnumerable<string> e => e,
                _ => Array.Empty<string>()
            };

            foreach (var channel in channels)
            {
                await redis.SubscribeAsync(channel.Trim(), (c, m) =>
                {
                    var topic = $"{redis.Config.Name}.REDIS.SUB.{c}";
                    _ = MessageBus.PublishAsync(topic, new MessageEnvelope(topic, m));
                });
            }
        }
    }
}
