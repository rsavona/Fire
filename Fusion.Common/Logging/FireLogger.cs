using System;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Serilog;
using Serilog.Events;
using Fusion.Common.Contracts;

namespace Fusion.Common.Logging
{
    public class FireLogger<TCategoryName> : FireLogger, IFireLogger<TCategoryName>
    {
        public FireLogger(ILogger logger, ILoggingBus? loggingBus = null)
            // Automatically tags every log with SourceContext for the injecting class
            : base(logger.ForContext(typeof(TCategoryName)), loggingBus)
        {
        }
    }

    public class FireLogger : IFireLogger
    {
        private readonly Serilog.ILogger _logger;
        private readonly IMessageBus? _messageBus;
        private readonly ILoggingBus? _loggingBus;
        private string _elementName = "System";

        private const string DefaultValue = "-----";
        private static readonly ConcurrentDictionary<string, int> _sampleCounters = new();

        /// <summary>
        /// When true (config "Fusion:LoggerElementExclusive"), FireLogger stops writing
        /// sub-Warning events directly to Serilog and relies on the Logger element
        /// (Fusion.Element.Logger) consuming the LoggingBus to persist them.
        /// Warning+ events ALWAYS also write directly to Serilog (emergency path), so
        /// serious problems are never lost even if the Logger element is misconfigured.
        /// Default is false: dual-write, nothing changes if the element isn't configured.
        /// </summary>
        public static bool LoggerElementExclusive { get; set; }

        public FireLogger(Serilog.ILogger logger, ILoggingBus? loggingBus = null, IMessageBus? messageBus = null, string elementName = "System")
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _loggingBus = loggingBus;
            _messageBus = messageBus;
            _elementName = elementName;
        }

        public Serilog.ILogger GetRawLogger()
        {
            return _logger;
        }

        public IFireLogger WithContext(string propertyName, object value)
        {
            var newLogger = _logger.ForContext(propertyName, value);
            var newElementName = propertyName == "ElementName" ? value.ToString() ?? _elementName : _elementName;
            return new FireLogger(newLogger, _loggingBus, _messageBus, newElementName);
        }

        private string FormatMethodTag(string methodName)
        {
            if (string.IsNullOrEmpty(methodName)) return "";

            // 1. Truncate if it exceeds 35 characters
            if (methodName.Length > 35)
            {
                methodName = methodName.Substring(0, 35);
            }

            // 2. Pad with spaces on the right to guarantee exactly 35 characters
            string fixedWidthName = methodName.PadRight(35, ' ');

            return $"({fixedWidthName}) ";
        }

        #region Private Structured Core

        // The ultimate carriage return killer for your PLC payloads
        private object?[] SanitizeArgs(object?[] args)
        {
            if (args == null || args.Length == 0) return Array.Empty<object>();

            var safeArgs = new object?[args.Length];
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] is string strArg)
                {
                    // Replaces carriage returns and common PLC control characters
                    safeArgs[i] = strArg.Replace("\r", "[CR]")
                        .Replace("\n", "[LF]")
                        .Replace("\x02", "[STX]")
                        .Replace("\x03", "[ETX]");
                }
                else
                {
                    safeArgs[i] = args[i];
                }
            }

            return safeArgs;
        }

        // 1. The Standard Write (Used by Enter, Exit, and standard logs. No GIN logic needed.)
        private void Write(LogEventLevel level, Exception? ex, string methodName, string messageTemplate,
            object?[] args)
        {
            var contextualLogger = _logger;

            string methodTag = string.IsNullOrEmpty(methodName) ? "" : $"({methodName}) ";
            contextualLogger = contextualLogger.ForContext("MethodTag", methodTag);

            // Standard logs have no GIN
            contextualLogger = contextualLogger.ForContext("GinTag", "");

            string safeTemplate = messageTemplate?.Replace("\r", "[CR]").Replace("\n", "[LF]") ?? "";
            var safeArgs = SanitizeArgs(args);

            // --- DEPARTURE 1: Immediate Dashboard/Console (via Serilog) ---
            // Skipped for sub-Warning events only when LoggerElementExclusive is set AND a
            // LoggingBus exists to carry them. Warning+ always writes directly (emergency path).
            if (!LoggerElementExclusive || _loggingBus == null || level >= LogEventLevel.Warning)
            {
                contextualLogger.Write(level, ex, safeTemplate, safeArgs);
            }

            // --- DEPARTURE 2: Asynchronous Persistent Logs (via LoggingBus) ---
            // Every event is published as a fully structured LogEventMessage (carried on the
            // LogMessage envelope for ILoggingBus contract compatibility).
            if (_loggingBus != null)
            {
                LogEventMessage? evt = null;
                try
                {
                    var (rendered, properties) = LogEventMessage.BindTemplate(safeTemplate, safeArgs);
                    evt = new LogEventMessage
                    {
                        Timestamp = DateTime.UtcNow,
                        Level = level,
                        MessageTemplate = safeTemplate,
                        RenderedMessage = rendered,
                        Properties = properties,
                        Exception = ex?.ToString(),
                        ElementName = _elementName
                    };
                }
                catch
                {
                    // Eat errors to prevent logging loops; consumers fall back to FromLogMessage.
                }

                _ = _loggingBus.PublishAsync(new LogMessage
                {
                    Level = level,
                    Context = _elementName,
                    MessageTemplate = safeTemplate,
                    Args = safeArgs!,
                    Exception = ex,
                    Event = evt
                });
            }

            if (_messageBus != null)
            {
                var topic = $"{_elementName}.{level}.Log";
                PublishToBus(topic, safeTemplate, args, ex);
            }
        }

        // 2. The Data Write (Used exclusively by LogConveyableEvent to track warehouse cartons)
        private void WriteConveyableEvent(string element, string message, string? gin, List<string> barcodes, string? decisionPoint)
        {
            var contextualLogger = _logger;

            contextualLogger = contextualLogger.ForContext("Context", "ConveyableEvents");
            contextualLogger = contextualLogger.ForContext("ElementName", element);
            contextualLogger = contextualLogger.ForContext("GIN", gin);
            contextualLogger = contextualLogger.ForContext("Barcodes", barcodes);
            contextualLogger = contextualLogger.ForContext("DecisionPoint", decisionPoint);


            string ginTag = (string.IsNullOrEmpty(element) || element == DefaultValue)
                ? ""
                : $"[{element.PadLeft(3, ' ')}]" +
                  ((string.IsNullOrEmpty(gin) || gin == DefaultValue) ? "" : $"[GIN:{gin.PadLeft(3, '0')}] ") +
                  $"[BC:{string.Join(", ", barcodes)}]" +
                  (string.IsNullOrEmpty(decisionPoint) ? "" : $" [DP:{decisionPoint}] ");

            contextualLogger = contextualLogger.ForContext("GinTag", ginTag);
            contextualLogger = contextualLogger.ForContext("MethodTag", ""); // No method tag for data events

            string safeTemplate = message?.Replace("\r", "[CR]").Replace("\n", "[LF]") ?? "";

            contextualLogger.Write(LogEventLevel.Information, (Exception?)null, safeTemplate, Array.Empty<object>());

            // --- DEPARTURE 2: Asynchronous Persistent Logs (via LoggingBus) ---
            if (_loggingBus != null)
            {
                _ = _loggingBus.PublishAsync(new LogMessage
                {
                    Level = LogEventLevel.Information,
                    Context = "CONVEYABLE_EVENTS",
                    MessageTemplate = "[{Element}][GIN:{GIN}] {Message}",
                    Args = new object[] { element, gin ?? "0", message },
                    FormattedMessage = $"{ginTag}{message}"
                });
            }

            if (_messageBus != null)
            {
                var topic = $"Conveyable.Event";
                PublishToBus(topic, safeTemplate, Array.Empty<object>(), null, gin, string.Join(", ", barcodes), decisionPoint);
            }
        }

        private void PublishToBus(string topic, string template, object?[] args, Exception? ex, string? gin = null,
            string? barcodes = null, string? decisionPoint = null)
        {
            if (_messageBus == null) return;

            try
            {
                var formattedMessage = args != null && args.Length > 0
                    ? string.Format(template.Replace("{", "{{").Replace("}", "}}"), args)
                    : template;

                var logPayload = new
                {
                    Element = _elementName,
                    Timestamp = DateTime.UtcNow,
                    Message = formattedMessage,
                    Exception = ex?.ToString(),
                    Gin = gin,
                    Barcodes = barcodes,
                    DecisionPoint = decisionPoint
                };

                var envelope = new MessageEnvelope(new MessageBusTopic(topic), logPayload);
                _ = _messageBus.PublishAsync(topic, envelope);
            }
            catch
            {
                // Eat errors to prevent logging loops
            }
        }

        #endregion

        #region Lifecycle & Sampled

        public void LogEnter(object? args = null, [CallerMemberName] string methodName = "",
            [CallerArgumentExpression("args")] string argsExpr = "")
        {
            Write(LogEventLevel.Verbose, null, methodName, "Enter. {ArgsExpr}: {@Args}",
                new[] { argsExpr, args });
        }

        public void LogExit(object? returnValue, [CallerMemberName] string methodName = "",
            [CallerArgumentExpression("returnValue")]
            string returnExpr = "")
        {
            Write(LogEventLevel.Verbose, null, methodName, "Exit. {ReturnExpr}: {@Return}",
                new[] { returnExpr, returnValue });
        }

        public void LogExit([CallerMemberName] string methodName = "")
        {
            Write(LogEventLevel.Verbose, null, methodName, "Exit", Array.Empty<object>());
        }

        public void LogSampled(string key, string message, int sampleRate = 25,
            [CallerMemberName] string methodName = "")
        {
            int current = _sampleCounters.AddOrUpdate(key, 1, (_, val) => val + 1);
            if (current % sampleRate == 0)
            {
                Write(LogEventLevel.Information, null, methodName, "{Message} (Sampled 1/{SampleRate})",
                    new object[] { message, sampleRate });
                _sampleCounters.TryUpdate(key, 0, current);
            }
            else
            {
                Write(LogEventLevel.Verbose, null, methodName, message, Array.Empty<object>());
            }
        }

        #endregion

        #region Log Overloads

        // --- Debug ---
        public void LogDebug(string message, params object?[] args) =>
            Write(LogEventLevel.Debug, null, "", message, args);

        public void Debug(string message, params object?[] args) =>
            Write(LogEventLevel.Debug, null, "", message, args);

        // --- Info ---
        public void LogInfo(string message, params object?[] args) =>
            Write(LogEventLevel.Information, null, "", message, args);

        public void Information(string message, params object?[] args) =>
            Write(LogEventLevel.Information, null, "", message, args);

        public void LogConveyableEvent(string element, string message, string? gin, List<string> barcodes, string? decisionPoint = "")
        {
            WriteConveyableEvent(element, message , gin, barcodes, decisionPoint);
        }

  


        // --- Warning ---
        public void LogWarning(string message, params object?[] args) =>
            Write(LogEventLevel.Warning, null, "", message, args);

        public void Warning(string message, params object?[] args) =>
            Write(LogEventLevel.Warning, null, "", message, args);

        // --- Error ---
        public void LogError(string message, params object?[] args) =>
            Write(LogEventLevel.Error, null, "", message, args);

        public void Error(string message, params object?[] args) =>
            Write(LogEventLevel.Error, null, "", message, args);

        public void LogError(Exception? ex, string message, params object?[] args) =>
            Write(LogEventLevel.Error, ex, "", message, args);

        public void Error(Exception? ex, string message, params object?[] args) =>
            Write(LogEventLevel.Error, ex, "", message, args);

        // --- Verbose ---
        public void LogVerbose(string message, params object?[] args) =>
            Write(LogEventLevel.Verbose, null, "", message, args);

        public void Verbose(string message, params object?[] args) =>
            Write(LogEventLevel.Verbose, null, "", message, args);

        // --- Fatal ---
        public void LogFatal(string message, params object?[] args) =>
            Write(LogEventLevel.Fatal, null, "", message, args);

        public void Fatal(string message, params object?[] args) =>
            Write(LogEventLevel.Fatal, null, "", message, args);

        public void LogFatal(Exception? ex, string message, params object?[] args) =>
            Write(LogEventLevel.Fatal, ex, "", message, args);

        public void Fatal(Exception? ex, string message, params object?[] args) =>
            Write(LogEventLevel.Fatal, ex, "", message, args);

        #endregion
    }
}