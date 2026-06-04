using System.Net;
using System.Net.Mail;
using Fusion.Common.BaseClasses;
using Fusion.Common.Configurations;
using Fusion.Common.Contracts;
using Fusion.Common.Enums;
using Serilog.Core;

namespace Fusion.Element.Notification.Suite;

public class EmailElement : ClientElementBase, INotificationElement
{
    private readonly string _host;
    private readonly int _port;
    private readonly string _username;
    private readonly string _password;
    private readonly bool _enableSsl;
    private readonly string _fromAddress;

    public EmailElement(IMessageBus bus, IElementBlueprint config, IFireLogger logger, LoggingLevelSwitch ls)
        : base(bus, config, logger, ls, needsHb: false)
    {
        _host = ConfigurationLoader.GetRequiredConfig<string>(config.Properties, "SmtpHost")!;
        _port = ConfigurationLoader.GetOptionalConfig(config.Properties, "SmtpPort", 587);
        _username = ConfigurationLoader.GetOptionalConfig(config.Properties, "Username", string.Empty);
        _password = ConfigurationLoader.GetOptionalConfig(config.Properties, "Password", string.Empty);
        _enableSsl = ConfigurationLoader.GetOptionalConfig(config.Properties, "EnableSsl", true);
        _fromAddress = ConfigurationLoader.GetRequiredConfig<string>(config.Properties, "FromAddress")!;
    }

    protected override async Task<bool> ConnectAsync(CancellationToken ct = default)
    {
        // For SMTP, we don't necessarily keep a persistent connection in this simplified version,
        // but we can "ping" the host to verify availability.
        try
        {
            using var client = new SmtpClient(_host, _port);
            client.EnableSsl = _enableSsl;
            if (!string.IsNullOrEmpty(_username))
            {
                client.Credentials = new NetworkCredential(_username, _password);
            }

            // We could attempt a NOOP or just assume it's up if the host is reachable.
            // SmtpClient doesn't have a direct "Connect" that we can use without sending.
            // For now, we'll just return true if we can create the client.
            return await Task.FromResult(true);
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "[{Dev}] Failed to verify SMTP host {Host}", Config.Name, _host);
            return false;
        }
    }

    public override Task SendHeartbeatAsync(CancellationToken token)
    {
        return Task.CompletedTask;
    }

    public override async Task SendAsync(string message, CancellationToken token, bool fireEvent = true)
    {
        // Assuming the message is a JSON or formatted string containing To, Subject, Body
        // For simplicity, we'll treat the payload as the body and look for config for 'To' 
        // OR we can parse the message.
        
        try 
        {
            var recipient = ConfigurationLoader.GetOptionalConfig(Config.Properties, "DefaultRecipient", "admin@example.com");
            var subject = ConfigurationLoader.GetOptionalConfig(Config.Properties, "DefaultSubject", "Fusion Notification");

            using var mailMessage = new MailMessage(_fromAddress, recipient, subject, message);
            using var client = new SmtpClient(_host, _port);
            client.EnableSsl = _enableSsl;
            if (!string.IsNullOrEmpty(_username))
            {
                client.Credentials = new NetworkCredential(_username, _password);
            }

            await client.SendMailAsync(mailMessage, token);
            Logger.Information("[{Dev}] Email sent to {Recipient}", Config.Name, recipient);
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "[{Dev}] Failed to send email", Config.Name);
            throw;
        }
    }

    protected override void OnElementFaultedAsync(CancellationToken token = default)
    {
        Logger.Error("[{Dev}] Email element faulted", Config.Name);
    }
}
