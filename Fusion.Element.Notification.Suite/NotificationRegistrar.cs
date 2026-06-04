using Fusion.Common.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Serilog.Core;

namespace Fusion.Element.Notification.Suite;

public class NotificationRegistrar : IElementRegistrar
{
    public void RegisterServices(IServiceCollection services)
    {
        services.AddTransient<EmailElement>();
        services.AddTransient<SmsElement>();

        services.AddTransient<Func<IElementBlueprint, IFireLogger, INotificationElement>>(provider => 
            (config, logger) => 
            {
                var type = config.Properties.TryGetValue("Type", out var t) ? t.ToString() : string.Empty;
                
                return type?.ToUpper() switch
                {
                    "EMAIL" => ActivatorUtilities.CreateInstance<EmailElement>(provider, config, logger, new LoggingLevelSwitch()),
                    "SMS" => ActivatorUtilities.CreateInstance<SmsElement>(provider, config, logger, new LoggingLevelSwitch()),
                    _ => throw new ArgumentException($"Unsupported notification type: {type}")
                };
            });
            
        // The manager registration is usually handled by the core based on discovered registrars, 
        // but some projects register their specific manager if it has unique requirements.
        // Assuming the standard discovery mechanism works.
    }
}
