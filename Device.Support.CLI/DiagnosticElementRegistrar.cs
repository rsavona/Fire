using Fusion.Common.Contracts;
using Microsoft.Extensions.DependencyInjection;

namespace Device.Support.CLI;

public class DiagnosticElementRegistrar : IElementRegistrar
{
    public void RegisterServices(IServiceCollection services)
    {
        services.AddTransient<DiagnosticElement>();
        services.AddTransient<BlueprintVerifierElement>();

        services.AddTransient<Func<IElementBlueprint, IFireLogger, DiagnosticElement>>(provider => 
            (config, logger) => ActivatorUtilities.CreateInstance<DiagnosticElement>(provider, config, logger));

        services.AddTransient<Func<IElementBlueprint, IFireLogger, BlueprintVerifierElement>>(provider => 
            (config, logger) => ActivatorUtilities.CreateInstance<BlueprintVerifierElement>(provider, config, logger));
    }
}
