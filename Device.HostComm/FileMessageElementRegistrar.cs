using Fusion.Common.Contracts;
using Microsoft.Extensions.DependencyInjection;

namespace Device.HostComm;

public class FileMessageElementRegistrar : IElementRegistrar
{
    public void RegisterServices(IServiceCollection services)
    {
        services.AddTransient<FileMessageElement>();

        services.AddTransient<Func<IElementBlueprint, IFireLogger, FileMessageElement>>(provider => 
            (config, logger) => 
            {
                return ActivatorUtilities.CreateInstance<FileMessageElement>(provider, config, logger);
            });
    }
}
