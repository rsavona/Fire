using Fusion.Common.Contracts;
using Microsoft.Extensions.DependencyInjection;

namespace Fusion.Element.Mqtt;

public class MqttRegistrar : IElementRegistrar
{
    public void RegisterServices(IServiceCollection services)
    {
        services.AddTransient<MqttElement>();
        services.AddTransient<Func<IElementBlueprint, IFireLogger, MqttElement>>(provider => 
            (config, logger) => ActivatorUtilities.CreateInstance<MqttElement>(provider, config, logger));
    }
}
