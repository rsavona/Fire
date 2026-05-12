using System.Collections.Concurrent;
using DeviceSpace.Common;
using DeviceSpace.Common.BaseClasses;
using DeviceSpace.Common.Configurations;
using DeviceSpace.Common.Contracts;
using DeviceSpace.Common.Logging;
using Microsoft.Extensions.Logging;

namespace Device.Nats;

public class NatsManager : DeviceManagerBase<NatsDevice>
{
    private readonly ConcurrentDictionary<string, List<string>> _subjectToBusMap = new();

    public NatsManager(IMessageBus bus, List<IDeviceConfig> configs,
        IFireLogger<NatsManager> logger,
        Func<IDeviceConfig, IFireLogger, NatsDevice> deviceFactory,
        string managerName)
        : base(bus, configs, logger, deviceFactory, managerName)
    {
    }

    protected override async Task RegisterDeviceSourceBonds(IDevice device)
    {
        var deviceLogger = Logger.WithContext("DeviceName", device.Key.DeviceName);
        var routes = ConfigurationLoader.GetAllWorkflowConfig()
            .SelectMany(w => w.Bonds)
            .Where(r => r.Source.StartsWith(device.Config.Name) && r.Mode > 0)
            .ToList();

        deviceLogger.Information("[{Dev}] NATS Manager initializing Source {Count} Bonds", device.Key.DeviceName, routes.Count);

        foreach (var route in routes)
        {
            deviceLogger.Information("[{Dev}] NATS Manager initializing Route: {RouteName}", device.Config.Name, route.Name);
            var subjectName = new MessageBusTopic(route.Source).Discriminator;
            if (device is not NatsDevice natsDev) continue;

            // Subscribe to NATS subject and route back to manager's common handler
            await natsDev.SubscribeAsync(subjectName, OnDeviceMessageToMessageBusAsync);

            if (_subjectToBusMap.TryGetValue(subjectName, out var list))
            {
                list.Add(route.Source);
            }
            else
            {
                _subjectToBusMap[subjectName] = new List<string> { route.Source };
            }
        }
    }

    protected override Task OnDeviceMessageToMessageBusAsync(object? message, object sender)
    {
        if (sender is not string subject || message is not string payload) return Task.CompletedTask;

        if (_subjectToBusMap.TryGetValue(subject, out var busTopics))
        {
            foreach (var path in busTopics)
            {
                var topic = new MessageBusTopic(path);
                Logger.LogDebug("[{Dev}] NATS Manager forwarding message to {Topic}: {Payload}", subject, topic, payload);
                MessageBus.PublishAsync(topic.ToString(), new MessageEnvelope(topic, payload));
            }
        }

        return Task.CompletedTask;
    }

    protected override async Task HandleBusMessageAsync(MessageEnvelope envelope, CancellationToken ct)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                var deviceName = envelope.Destination.DeviceName;
                var subject = envelope.Destination.Discriminator;
                if (DeviceInstances.TryGetValue(deviceName, out var device))
                {
                    var payload = envelope.Payload?.ToString() ?? "";
                    await device.PublishAsync(subject, payload);
                }
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Background task failed in NatsManager for topic {Topic}", envelope.Destination);
            }
        }, ct);

        await Task.CompletedTask;
    }
}
