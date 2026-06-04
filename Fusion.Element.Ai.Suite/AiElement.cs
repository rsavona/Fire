using Google.GenAI;
using Google.GenAI.Types;
using Microsoft.Extensions.AI;
using Fusion.Common;
using Fusion.Common.BaseClasses;
using Fusion.Common.Contracts;
using Serilog.Core;
using Fusion.Common.Configurations;
using Fusion.Common.Enums;

namespace Fusion.Element.Ai.Suite;

/// <summary>
/// An AI Element that wraps an IChatClient (e.g., Gemini) to provide AI capabilities
/// within the Fusion ecosystem.
/// </summary>
public class AiElement : ClientElementBase, IMessageProvider
{
    public event Func<object, object, Task>? MessageReceived;
    private IChatClient? _chatClient;
    private readonly string _modelId;
    private readonly string _apiKey;

    public AiElement(IMessageBus bus, IElementBlueprint config, IFireLogger logger, LoggingLevelSwitch swtch, IChatClient? chatClient = null)
        : base(bus, config, logger, swtch, false)
    {
        _modelId = ConfigurationLoader.GetOptionalConfig(config.Properties, "Model", "gemini-1.5-pro-latest");
        _apiKey = ConfigurationLoader.GetOptionalConfig(config.Properties, "ApiKey", "");
        _chatClient = chatClient;
    }

    protected override async Task<bool> ConnectAsync(CancellationToken ct = default)
    {
        try
        {
            if (_chatClient != null)
            {
                Logger.Information("[{Dev}] AI Element using provided chat client.", Config.Name);
                return true;
            }

            if (string.IsNullOrWhiteSpace(_apiKey))
            {
                Logger.Error("[{Dev}] API Key is missing for AI Element.", Config.Name);
                return false;
            }

            var googleClient = new Client(apiKey: _apiKey, httpOptions: new HttpOptions { ApiVersion = "v1beta" });
            _chatClient = googleClient.AsIChatClient(_modelId);
            
            Logger.Information("[{Dev}] AI Element connected using model: {Model}", Config.Name, _modelId);
            return true;
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "[{Dev}] Failed to connect AI Element.", Config.Name);
            return false;
        }
    }

    protected override Task OnElementStoppingAsync()
    {
        _chatClient?.Dispose();
        _chatClient = null;
        return Task.CompletedTask;
    }

    public override async Task SendAsync(string message, CancellationToken token, bool fireEvent = true)
    {
        await ChatAsync(message, token);
    }

    public override Task SendHeartbeatAsync(CancellationToken token)
    {
        return Task.CompletedTask;
    }

    protected override void OnElementFaultedAsync(CancellationToken token = default)
    {
        Logger.Error("[{Dev}] AI Element entered faulted state.", Config.Name);
    }

    public async Task<string> ChatAsync(string prompt, CancellationToken ct = default)
    {
        if (_chatClient == null)
        {
            throw new InvalidOperationException("AI Element is not connected.");
        }

        Logger.Debug("[{Dev}] Sending prompt to AI: {Prompt}", Config.Name, prompt);

        var response = await _chatClient.GetResponseAsync(prompt, cancellationToken: ct);
        string result = response.Text ?? string.Empty;

        Logger.Debug("[{Dev}] Received response from AI: {Response}", Config.Name, result);

        // Notify that a message (response) was received
        if (MessageReceived != null)
        {
            var topic = new MessageBusTopic(Config.Name, "Response");
            var envelope = new MessageEnvelope(topic, result, 0, "AI");
            await MessageReceived.Invoke(this, envelope);
        }

        return result;
    }

    protected override ElementHealth MapStateToHealth(ClientElementBase.State state)
    {
        return state switch
        {
            ClientElementBase.State.Connected => ElementHealth.Normal,
            ClientElementBase.State.Offline => ElementHealth.Critical,
            ClientElementBase.State.Faulted => ElementHealth.Critical,
            _ => ElementHealth.Warning
        };
    }
}
