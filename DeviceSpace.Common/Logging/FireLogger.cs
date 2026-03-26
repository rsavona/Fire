using System;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Serilog;
using Serilog.Events;
using DeviceSpace.Common.Contracts;

namespace DeviceSpace.Common.Logging
{
    public class FireLogger<TCategoryName> : FireLogger, IFireLogger<TCategoryName>
    {
        public FireLogger(ILogger logger)
            // Automatically tags every log with SourceContext for the injecting class
            : base(logger.ForContext(typeof(TCategoryName)))
        {
        }
    }

    public class FireLogger : IFireLogger
    {
        private readonly ILogger _logger;
        private const string DefaultGin = "---";
        private static readonly ConcurrentDictionary<string, int> _sampleCounters = new();

        public FireLogger(ILogger logger)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public Serilog.ILogger GetRawLogger()
        {
            return _logger;
        }

        public FireLogger WithContext(string propertyName, object value)
        {
            return new FireLogger(_logger.ForContext(propertyName, value));
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

            contextualLogger.Write(level, ex, safeTemplate, SanitizeArgs(args));
        }

        // 2. The Data Write (Used exclusively by LogInfoData to track warehouse cartons)
        // Hardcoded to LogEventLevel.Information and null Exception.
        private void WriteWithGin(string gin, string methodName, string messageTemplate, object?[] args)
        {
            var contextualLogger = _logger;

            // Use the new formatter
            string methodTag = FormatMethodTag(methodName);
            contextualLogger = contextualLogger.ForContext("MethodTag", methodTag);

            string ginTag = (string.IsNullOrEmpty(gin) || gin == DefaultGin) ? "" : $"[GIN:{gin.PadLeft(3, '0')}] ";
            contextualLogger = contextualLogger.ForContext("GinTag", ginTag);

            string safeTemplate = messageTemplate?.Replace("\r", "[CR]").Replace("\n", "[LF]") ?? "";

            contextualLogger.Write(LogEventLevel.Information, (Exception?)null, safeTemplate, SanitizeArgs(args));
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

        // The ONLY method designed for explicit GIN and Method tracking!
        public void LogInfoData(string message, object?[] args, string gin = DefaultGin,
            [CallerMemberName] string methodName = "") =>
            WriteWithGin(gin, methodName, message, args);

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