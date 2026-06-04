using System.Text;
using System.Text.Json;
using MQTTnet;
using MQTTnet.Client;
using Fusion.Common;
using Fusion.Common.BaseClasses;
using Fusion.Common.Contracts;
using Fusion.Common.Enums;
using Serilog.Core;

namespace Fusion.Element.Mqtt;

public class MqttElement : ClientElementBase
{
    private readonly IMqttClient _mqttClient;
    private readonly MqttClientOptions _mqttOptions;
    private readonly string _topic;
    private readonly MqttFactory _factory;

    public MqttElement(IMessageBus bus, IElementBlueprint config, IFireLogger logger, LoggingLevelSwitch ls)
        : base(bus, config, logger, ls, needsHb: true)
    {
        _factory = new MqttFactory();
        _mqttClient = _factory.CreateMqttClient();

        string host = config.Properties.TryGetValue("Host", out var h) ? h.ToString() ?? "" : "127.0.0.1";
        int port = config.Properties.TryGetValue("Port", out var p) ? Convert.ToInt32(p) : 1883;
        string clientId = config.Properties.TryGetValue("ClientId", out var c) ? c.ToString() ?? config.Name : config.Name;
        _topic = config.Properties.TryGetValue("Topic", out var t) ? t.ToString() ?? "fusion/elements/#" : "fusion/elements/#";

        _mqttOptions = new MqttClientOptionsBuilder()
            .WithTcpServer(host, port)
            .WithClientId(clientId)
            .WithCleanSession(false)
            .Build();

        _mqttClient.ApplicationMessageReceivedAsync += HandleMqttMessageAsync;
    }

    protected override async Task<bool> ConnectAsync(CancellationToken ct = default)
    {
        try
        {
            var result = await _mqttClient.ConnectAsync(_mqttOptions, ct);
            if (result.ResultCode == MqttClientConnectResultCode.Success)
            {
                var subscribeOptions = _factory.CreateSubscribeOptionsBuilder()
                    .WithTopicFilter(f => f.WithTopic(_topic).WithQualityOfServiceLevel(MQTTnet.Protocol.MqttQualityOfServiceLevel.AtLeastOnce))
                    .Build();

                await _mqttClient.SubscribeAsync(subscribeOptions, ct);
                Logger.Information("[{Dev}] Connected to MQTT Broker and subscribed to {Topic}", Config.Name, _topic);
                return true;
            }
            return false;
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "[{Dev}] MQTT Connection failed", Config.Name);
            return false;
        }
    }

    private async Task HandleMqttMessageAsync(MqttApplicationMessageReceivedEventArgs e)
    {
        try
        {
            string payload = Encoding.UTF8.GetString(e.ApplicationMessage.PayloadSegment);
            string topic = e.ApplicationMessage.Topic;
            
            Logger.Verbose("[{Dev}] MQTT RX << {Topic}: {Payload}", Config.Name, topic, payload);
            Tracker.IncrementInbound();

            var busTopicStr = TranslateToBusTopic(topic);
            var busTopic = new MessageBusTopic(busTopicStr);
            var envelope = new MessageEnvelope(busTopic, payload);
            
            await MessageBus.PublishAsync(busTopicStr, envelope);
            await Machine.FireAsync(Event.MessageReceived);
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "[{Dev}] Error bridging MQTT message", Config.Name);
        }
    }

    private string TranslateToBusTopic(string mqttTopic)
    {
        return mqttTopic.Replace("fusion/elements/", "").Replace("/", ".");
    }

    public override async Task SendAsync(string message, CancellationToken token, bool fireEvent = true)
    {
        if (!_mqttClient.IsConnected) return;

        var mqttMessage = new MqttApplicationMessageBuilder()
            .WithTopic($"{_topic.Replace("/#", "")}/response")
            .WithPayload(message)
            .WithQualityOfServiceLevel(MQTTnet.Protocol.MqttQualityOfServiceLevel.AtLeastOnce)
            .Build();

        await _mqttClient.PublishAsync(mqttMessage, token);

        if (fireEvent)
        {
            Tracker.IncrementOutbound();
            await Machine.FireAsync(Event.MessageSent);
        }
    }

    public override Task SendHeartbeatAsync(CancellationToken token) => Task.CompletedTask;

    protected override void OnElementFaultedAsync(CancellationToken token = default)
    {
        Logger.Warning("[{Dev}] MQTT Element Faulted.", Config.Name);
    }

    protected override async Task ElementDisconnectedAsync()
    {
        if (_mqttClient.IsConnected) await _mqttClient.DisconnectAsync();
        await base.ElementDisconnectedAsync();
    }
}
