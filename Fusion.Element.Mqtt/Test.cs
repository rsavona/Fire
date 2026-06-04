using MQTTnet;
using MQTTnet.Client;

namespace Fusion.Element.Mqtt;

public class Test
{
    public void Do()
    {
        var factory = new MqttFactory();
        var client = factory.CreateMqttClient();
    }
}
