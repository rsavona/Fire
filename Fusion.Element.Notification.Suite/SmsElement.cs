using System.Text;
using System.Text.Json;
using Fusion.Common.BaseClasses;
using Fusion.Common.Configurations;
using Fusion.Common.Contracts;
using Fusion.Common.Enums;
using Serilog.Core;

namespace Fusion.Element.Notification.Suite;

public class SmsElement : ClientElementBase, INotificationElement
{
    private readonly HttpClient _httpClient;
    private readonly string _apiUrl;
    private readonly string _apiKey;
    private readonly string _fromNumber;

    public SmsElement(IMessageBus bus, IElementBlueprint config, IFireLogger logger, LoggingLevelSwitch ls)
        : base(bus, config, logger, ls, needsHb: false)
    {
        _httpClient = new HttpClient();
        _apiUrl = ConfigurationLoader.GetRequiredConfig<string>(config.Properties, "ApiUrl")!;
        _apiKey = ConfigurationLoader.GetRequiredConfig<string>(config.Properties, "ApiKey")!;
        _fromNumber = ConfigurationLoader.GetRequiredConfig<string>(config.Properties, "FromNumber")!;
    }

    protected override async Task<bool> ConnectAsync(CancellationToken ct = default)
    {
        // For an API, we can just check if the URL is reachable or return true.
        return await Task.FromResult(true);
    }

    public override Task SendHeartbeatAsync(CancellationToken token)
    {
        return Task.CompletedTask;
    }

    public override async Task SendAsync(string message, CancellationToken token, bool fireEvent = true)
    {
        try
        {
            var toNumber = ConfigurationLoader.GetRequiredConfig<string>(Config.Properties, "DefaultToNumber")!;

            var payload = new
            {
                from = _fromNumber,
                to = toNumber,
                body = message
            };

            var content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
            _httpClient.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _apiKey);

            var response = await _httpClient.PostAsync(_apiUrl, content, token);
            response.EnsureSuccessStatusCode();

            Logger.Information("[{Dev}] SMS sent to {Recipient}", Config.Name, toNumber);
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "[{Dev}] Failed to send SMS", Config.Name);
            throw;
        }
    }

    protected override void OnElementFaultedAsync(CancellationToken token = default)
    {
        Logger.Error("[{Dev}] SMS element faulted", Config.Name);
    }
    
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _httpClient.Dispose();
        }
        base.Dispose(disposing);
    }
}
