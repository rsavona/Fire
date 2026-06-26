using System.Text.Json.Nodes;
using Fusion.Common;
using Fusion.Common.BaseClasses;
using Fusion.Common.Contracts;
using Fusion.Common.PayloadParsers;
using Fusion.Common.TCP_Classes;
using Serilog.Core;

namespace Fusion.Element.HostComm;

/// <summary>
/// A TCP Client Element that expects CR-terminated messages and parses them 
/// using a configurable strategy (Delimited, FixedLength, JSON, XML).
/// </summary>
public class TcpMessageClientElement : TcpClientElementBase, IMessageProvider
{
    public event Func<object, object, Task>? MessageReceived;
    private readonly IPayloadParser _payloadParser;
    private readonly IReadOnlyList<IReadOnlyList<string>> _scriptedMessageGroups;
    private readonly bool _autoStartScript;
    private readonly bool _scriptedDisconnectAfterEachGroup;
    private readonly bool _scriptedRepeat;
    private readonly bool _scriptedStopWhenComplete;
    private readonly int _scriptedInitialDelayMs;
    private readonly int _scriptedMessageIntervalMs;
    private readonly string _scriptedMessageTerminator;
    private int _scriptedGroupIndex;
    private int _scriptedRunnerActive;

    public TcpMessageClientElement(IMessageBus bus, IElementBlueprint config, IFireLogger logger, LoggingLevelSwitch swtch)
        : base(bus, config, logger, swtch, config.Properties.ContainsKey("HeartbeatIntervalMs"))
    {
        _payloadParser = PayloadParserFactory.Create(config);
        _scriptedMessageGroups = ParseScriptedMessageGroups(config);
        _autoStartScript = GetBool(config, "AutoStartScript", false) && _scriptedMessageGroups.Count > 0;
        _scriptedDisconnectAfterEachGroup = GetBool(config, "ScriptedDisconnectAfterEachGroup", false);
        _scriptedRepeat = GetBool(config, "ScriptedRepeat", false);
        _scriptedStopWhenComplete = GetBool(config, "ScriptedStopWhenComplete", false);
        _scriptedInitialDelayMs = GetInt(config, "ScriptedInitialDelayMs", 250);
        _scriptedMessageIntervalMs = GetInt(config, "ScriptedMessageIntervalMs", 1000);
        _scriptedMessageTerminator = GetDecodedString(
            config,
            "ScriptedMessageTerminator",
            GetDecodedString(config, "OutboundTerminator", "\u0003"));
    }

    protected override async Task HandleReceivedDataAsync(string incomingData)
    {
        string sanitized = incomingData.TrimEnd('\r', '\n');
        
        // Use a generic topic that matches the chamber bond source
        var topic = new MessageBusTopic(Config.Name, "Inbound");
        var envelope = new MessageEnvelope(topic, sanitized, 0, "Server");

        if (MessageReceived != null)
        {
            await MessageReceived.Invoke(this, envelope);
        }
    }

    protected override string GetHeartbeatMessage()
    {
        return Config.Properties.TryGetValue("HeartbeatMessage", out var m) 
            ? m.ToString() ?? string.Empty 
            : string.Empty;
    }

    protected override bool IsHeartbeat(string incomingData)
    {
        if (Config.Properties.TryGetValue("HeartbeatAck", out var ack))
        {
            return incomingData.Contains(ack.ToString() ?? "HB_ACK");
        }
        return false;
    }

    protected override async Task ElementConnectedAsync()
    {
        await base.ElementConnectedAsync();

        if (_autoStartScript)
        {
            _ = Task.Run(RunNextScriptedGroupAsync);
        }
    }

    private async Task RunNextScriptedGroupAsync()
    {
        if (Interlocked.Exchange(ref _scriptedRunnerActive, 1) == 1)
        {
            return;
        }

        try
        {
            if (_scriptedMessageGroups.Count == 0)
            {
                return;
            }

            if (_scriptedGroupIndex >= _scriptedMessageGroups.Count)
            {
                if (!_scriptedRepeat)
                {
                    Logger.Information("[{Dev}] Scripted TCP message sequence is complete.", Config.Name);
                    return;
                }

                _scriptedGroupIndex = 0;
            }

            int currentGroupIndex = _scriptedGroupIndex++;
            var group = _scriptedMessageGroups[currentGroupIndex];

            Logger.Information(
                "[{Dev}] Sending scripted TCP message group {Group}/{Total} ({Count} messages).",
                Config.Name,
                currentGroupIndex + 1,
                _scriptedMessageGroups.Count,
                group.Count);

            if (_scriptedInitialDelayMs > 0)
            {
                await Task.Delay(_scriptedInitialDelayMs);
            }

            foreach (string message in group)
            {
                await SendAsync(ApplyTerminator(message), CancellationToken.None);

                if (_scriptedMessageIntervalMs > 0)
                {
                    await Task.Delay(_scriptedMessageIntervalMs);
                }
            }

            bool scriptComplete = !_scriptedRepeat && _scriptedGroupIndex >= _scriptedMessageGroups.Count;

            if (scriptComplete && _scriptedStopWhenComplete)
            {
                Logger.Information("[{Dev}] Scripted TCP sequence complete. Stopping client.", Config.Name);
                await StopAsync(CancellationToken.None);
                return;
            }

            if (_scriptedDisconnectAfterEachGroup)
            {
                Logger.Information("[{Dev}] Scripted TCP group complete. Closing connection before next group.", Config.Name);
                await CloseConnectionAsync();
            }
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "[{Dev}] Scripted TCP message sequence failed.", Config.Name);
            OnError("ScriptedTcpMessageSequence", ex);
        }
        finally
        {
            Interlocked.Exchange(ref _scriptedRunnerActive, 0);
        }
    }

    private string ApplyTerminator(string payload)
    {
        if (string.IsNullOrEmpty(_scriptedMessageTerminator) ||
            payload.EndsWith(_scriptedMessageTerminator, StringComparison.Ordinal))
        {
            return payload;
        }

        return payload + _scriptedMessageTerminator;
    }

    private static IReadOnlyList<IReadOnlyList<string>> ParseScriptedMessageGroups(IElementBlueprint config)
    {
        if (!config.Properties.TryGetValue("ScriptedMessageGroups", out var rawValue))
        {
            return [];
        }

        string rawGroups = rawValue?.ToString() ?? string.Empty;
        if (string.IsNullOrEmpty(rawGroups))
        {
            return [];
        }

        string groupSeparator = GetDecodedString(config, "ScriptedGroupSeparator", "||");
        string messageSeparator = GetDecodedString(config, "ScriptedMessageSeparator", "|");

        return rawGroups
            .Split(groupSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(group => (IReadOnlyList<string>)group
                .Split(messageSeparator, StringSplitOptions.RemoveEmptyEntries)
                .Select(TcpTextEncoding.DecodeEscapedSequence)
                .Where(message => !string.IsNullOrEmpty(message))
                .ToArray())
            .Where(group => group.Count > 0)
            .ToArray();
    }

    private static string GetDecodedString(IElementBlueprint config, string key, string defaultValue)
    {
        return config.Properties.TryGetValue(key, out var value)
            ? TcpTextEncoding.DecodeEscapedSequence(value?.ToString())
            : defaultValue;
    }

    private static bool GetBool(IElementBlueprint config, string key, bool defaultValue)
    {
        if (!config.Properties.TryGetValue(key, out var value))
        {
            return defaultValue;
        }

        if (value is bool boolValue)
        {
            return boolValue;
        }

        return bool.TryParse(value?.ToString(), out bool parsed) ? parsed : defaultValue;
    }

    private static int GetInt(IElementBlueprint config, string key, int defaultValue)
    {
        if (!config.Properties.TryGetValue(key, out var value))
        {
            return defaultValue;
        }

        return int.TryParse(value?.ToString(), out int parsed) ? parsed : defaultValue;
    }
}
