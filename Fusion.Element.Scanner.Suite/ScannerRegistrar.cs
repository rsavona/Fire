using Fusion.Common.BaseClasses;
using Fusion.Common.Contracts;
using Microsoft.Extensions.DependencyInjection;

namespace Fusion.Element.Scanner.Suite;

public class ScannerRegistrar : IElementRegistrar
{
    public void RegisterServices(IServiceCollection services)
    {
        services.AddSingleton<ScannerServerManager>();
        services.AddSingleton<ScannerClientManager>();
        services.AddSingleton<ScannerSimulatorManager>();
    }
}
