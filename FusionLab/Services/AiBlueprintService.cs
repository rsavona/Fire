using System.Text.Json;
using System.Text.Json.Nodes;
using Google.GenAI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Fusion.Common.Blueprints;
using Google.GenAI.Types;
using Environment = System.Environment;

namespace FusionLab.Services;

public class AiBlueprintService
{
    private readonly string _apiKey;
    private readonly string _modelId;
    private readonly IChatClient _chatClient;

    public AiBlueprintService(IConfiguration config)
    {
        _apiKey = config["AI:ApiKey"] ?? Environment.GetEnvironmentVariable("GEMINI_API_KEY") ?? "";
        _modelId = config["AI:Model"] ?? "gemini-1.5-pro-latest";

        if (!string.IsNullOrEmpty(_apiKey))
        {
            var googleClient = new Client(apiKey: _apiKey, httpOptions: new HttpOptions { ApiVersion = "v1beta" });
            
            _chatClient = googleClient.AsIChatClient(_modelId);
        }
        else
        {
            _chatClient = null!;
        }
    }

    public bool IsEnabled => _chatClient != null;

    public async Task<string> GenerateBlueprintAsync(string customerName, string description, string hints = "")
    {
        if (!IsEnabled) throw new InvalidOperationException("AI Service is not configured. Missing API Key.");

        string systemPrompt = GetSystemPrompt(customerName);
        
        var messages = new List<ChatMessage>
        {
            new(ChatRole.User, $"SYSTEM INSTRUCTIONS:\n{systemPrompt}\n\nEXTRA HINTS/CONSTRAINTS:\n{hints}\n\nUSER REQUEST: {description}")
        };

        var response = await _chatClient.GetResponseAsync(messages);
        string jsonResult = response.Text?.Trim() ?? "";

        // Clean up markdown if AI ignored instructions
        if (jsonResult.Contains("```json")) jsonResult = jsonResult.Split("```json")[1].Split("```")[0].Trim();
        else if (jsonResult.StartsWith("```")) jsonResult = jsonResult.Replace("```", "").Trim();

        return jsonResult;
    }

    public async Task<string> GetWittyJokeAsync(string customerName)
    {
        if (!IsEnabled) return "";

        try
        {
            var response = await _chatClient.GetResponseAsync(new List<ChatMessage> 
            { 
                new(ChatRole.User, $"Give me a very short, witty, one-sentence pun or industry joke about the company '{customerName}'. Keep it professional but funny. No markdown.")
            });
            return response.Text?.Trim() ?? "";
        }
        catch { return ""; }
    }

    public async Task<string> GetCommissionEstimateAsync(string jsonConfig)
    {
        if (!IsEnabled) return "Estimate unavailable.";

        try
        {
            var estResponse = await _chatClient.GetResponseAsync(new List<ChatMessage> 
            { 
                new(ChatRole.User, $"Based on the complexity, hardware, and logic in this Fortna Fusion configuration, give a 1 to 2 sentence estimate of how long it will take to commission (install, configure, test, validate) this system on site. Just give the estimate directly.\n\nCONFIG:\n{jsonConfig}") 
            });
            return estResponse.Text?.Trim() ?? "Estimate unavailable.";
        }
        catch { return "Estimate unavailable."; }
    }

    private string GetSystemPrompt(string customerName)
    {
        return $@"
You are a configuration expert for the Fortna Fusion automation system. 
Generate a valid JSON ""Fusion Blueprint"" for: {customerName}.

STRUCTURE:
- Root: ""AppSettings"" -> ""Fusion""
- ""Fusion"": {{ ""Name"", ""ColorConsole"": true, ""IsTestEnvironment"": false, ""Cores"": [ {{ ""Name"": ""Core1"", ""Elements"": [], ""Reactions"": [] }} ] }}

MANAGERS:
[ActiveMqManager, ActiveMqBrowserManager, ActiveMqQueuePeekManager, AiElementManager, ApiElementManager, DatabaseElementManager, RedisCacheManager, TelemetryManager, InventoryManager, FireLogManager, FileMessageElementManager, TcpMessageClientElementManager, TcpMessageServerElementManager, MqttManager, NatsManager, EmailManager, PlcElementManager, AbCipPlcManager, VirtualPlcManager, PrinterManager, PrintClientManager, ScannerSimulatorManager, ScannerClientManager, DiagnosticElementManager, BlueprintVerifierManager]

REACTIONS:
[ReactionSimulation, PrintAndApplyFrc, StandardSort, HostOutputReaction]

BOND OBJECT:
{{ ""Mode"": 1, ""Name"": ""BondName"", ""Source"": ""Topic"", ""Destination"": ""Topic"", ""Handler"": ""Method"" }}

OUTPUT:
Return ONLY raw JSON. No explanations.";
    }
}
