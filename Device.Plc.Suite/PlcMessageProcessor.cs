using System.Diagnostics;
using System.Net.Sockets;
using System.Text;
using DeviceSpace.Common;
using DeviceSpace.Common.Contracts;
using Serilog; // Added for structured logging

namespace Device.Plc.Suite
{
    public class PlcMessageProcessor : IMessageProcessor
    {
        private readonly PlcMessageParser _parser;
        private readonly string _deviceName;
        private readonly ILogger _logger;

        private const char STX = '\u0002';
        private const char ETX = '\u0003';

        public event Action<string>? HeartbeatReceived;
        public event Action<MessageEnvelope>? MessageReceived;
        public event Action<string>? OnMessageError;

        public PlcMessageProcessor(PlcMessageParser parser, string deviceName)
        {
            _parser = parser;
            _deviceName = deviceName;
            // Create a context-aware logger
            _logger = Log.ForContext("SourceContext", $"PlcProcessor:{_deviceName}");
        }

        public async Task<bool> ProcessMessageAsync(NetworkStream stream, byte[] rawMessage, int len, string clientKey,
            CancellationToken token)
        {
            _logger.Verbose("[{Client}] Incoming raw bytes: {Len}", clientKey, len);
            string strMessage = Encoding.ASCII.GetString(rawMessage, 0, len);
            return await ProcessMessageAsync(stream, strMessage, clientKey, token);
        }

        public string HandleResponse(string deviceName, string payload)
        {
            _logger.Debug("[{Dev}] Framing response payload: {Payload}", deviceName, payload);
            return _parser.FramePayload(deviceName, payload);
        }

        public async Task<bool> ProcessMessageAsync(NetworkStream stream, string rawMessage, string clientKey, CancellationToken token)
        {
            if (string.IsNullOrEmpty(rawMessage))
            {
                _logger.Warning("[{Client}] Received null or empty data string.", clientKey);
                OnMessageError?.Invoke($"Empty inbound data from {clientKey}");
                return false;
            }

            // 1) Extract framed messages
            var framedMessages = ExtractFramedMessages(rawMessage, clientKey);

            if (framedMessages.Count == 0)
            {
                _logger.Warning("[{Client}] Incomplete frame or noise received: {Raw}", clientKey, rawMessage);
                OnMessageError?.Invoke($"No complete [stx]/[etx] frames found from {clientKey}");
                return false;
            }

            var anySucceeded = false;

            // 2) Process each framed message independently
            foreach (var msg in framedMessages)
            {
                var message = SanitizeMessage(msg);
                _logger.Verbose("[{Client}] Processing frame: {Msg}", clientKey, message);

                bool success = _parser.TryParseToPlcMessage(message, out var plcMessage);
                if (!success || plcMessage == null)
                {
                    _logger.Error("[{Client}] Parsing failed for frame: {Msg}", clientKey, message);
                    OnMessageError?.Invoke($"Failed to parse framed message from {clientKey}: '{message}'");
                    continue;
                }

                plcMessage.ClientKey = clientKey;

                // 3) Handle Heartbeats
                if (plcMessage.Header == PlcMessageHeaders.HB)
                {
                    _logger.Debug("[{Client}] Heartbeat received. Sending ACK.", clientKey);
                    try
                    {
                        string ack = _parser.CreateHeartbeatAck(_deviceName);
                        byte[] ackBytes = Encoding.ASCII.GetBytes(ack);
                        await stream.WriteAsync(ackBytes, 0, ackBytes.Length, token);
                        await stream.FlushAsync(token);
                    }
                    catch (Exception ex)
                    {
                        _logger.Error(ex, "[{Client}] Failed to send Heartbeat ACK.", clientKey);
                        OnMessageError?.Invoke($"Failed to send ACK to {clientKey}: {ex.Message}");
                    }

                    HeartbeatReceived?.Invoke(clientKey);
                    anySucceeded = true;
                    continue;
                }

                // 4) Extract Business Data
                string decisionPoint = "Unknown";
                int gin = 0;

                if (plcMessage.Payload is DecisionRequestPayload req)
                {
                    decisionPoint = req.DecisionPoint;
                    gin = req.Gin;
                    _logger.Information("[{Client}] Decision Request: GIN={Gin} at DP={DP}", clientKey, gin, decisionPoint);
                }
                else if (plcMessage.Payload is DecisionUpdatePayload update)
                {
                    decisionPoint = update.DecisionPoint;
                    gin = update.Gin;
                    _logger.Information("[{Client}] Decision Update: GIN={Gin} at DP={DP}", clientKey, gin, decisionPoint);
                }
                else
                {
                    _logger.Error("[{Client}] Unsupported payload type for header {Header}", clientKey, plcMessage.Header);
                    OnMessageError?.Invoke($"Unknown payload type for header: {plcMessage.Header}");
                    continue;
                }

                // 5) Build envelope and fire event
                var topic = new MessageBusTopic("Unknown", plcMessage.Header.ToString(), decisionPoint);
                var envelope = new MessageEnvelope(topic, plcMessage.Payload, gin, clientKey);

                MessageReceived?.Invoke(envelope);
                anySucceeded = true;
            }

            return anySucceeded;
        }

        private List<string> ExtractFramedMessages(string raw, string clientKey)
        {
            var results = new List<string>();
            int idx = 0;
            while (idx < raw.Length)
            {
                int start = raw.IndexOf(STX.ToString(), idx, StringComparison.Ordinal);
                if (start < 0) break;

                start += 1;
                int end = raw.IndexOf(ETX.ToString(), start, StringComparison.Ordinal);
                
                if (end < 0)
                {
                    _logger.Warning("[{Client}] Partial frame detected (STX without ETX). Buffering expected.", clientKey);
                    break;
                }

                string payload = raw.Substring(start, end - start);
                results.Add(payload);
                _logger.Debug("[{Client}] Extracted frame ({Len} chars)", clientKey, payload.Length);

                idx = end + 1;
            }

            return results;
        }

        private static string SanitizeMessage(string message)
        {
            if (string.IsNullOrEmpty(message)) return string.Empty;
            return message.Replace("\r\n", "\n").Replace("\r", "\n").Trim();
        }
    }
}