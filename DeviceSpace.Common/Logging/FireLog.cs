using System.Runtime.CompilerServices;
using System.Text.Json;
using Serilog;
using Serilog.Events;

namespace DeviceSpace.Common.Logging;




public static class FireLog
{
    private const int Width10 = 10;
    private const int Width30 = 30;

    extension(ILogger logger)
    {
        public void FireLogInfo(string subject,
            string verb,
            string identifier,
            string @object,
            string comment)
        {
            FireLogBase(logger, subject, verb, identifier, @object,  SafeFormat(@comment , 40) );
        }

        public void FireLogWarning(string subject,
            string verb,
            string identifier,
            string @object,
            string comment)
        {
            FireLogBase(logger, subject, verb, identifier, @object,  SafeFormat(@comment , 40),LogEventLevel.Warning );
        }

        public void FireLogError(string subject,
            string error,
            string identifier,
            string @object,
            string comment)
        {
            FireLogBase(logger, subject, error, identifier, @object,  comment ,LogEventLevel.Error );
        }

        
        public void FireLogTrace( string comment,  [CallerLineNumber] int lineNumber = 0,
            [CallerMemberName] string methodName = "" )
        {
            Log.Logger.Write(LogEventLevel.Debug, comment);
        }
            
        public void FireLogDebug(
            string comment, 
            Dictionary<string, string>? watchList,
            [CallerLineNumber] int lineNumber = 0,
            [CallerMemberName] string methodName = "")
        {
            // 1. Serialize the dictionary to a compact JSON string
            string jsonWatch = watchList != null && watchList.Count > 0 
                ? JsonSerializer.Serialize(watchList) 
                : "{}";

            // 2. Build a natural, unaligned message string
            // Format: LineNumber [MethodName] Comment | JSON
            string formattedMessage = $"L{lineNumber} [{methodName}] {comment} | {jsonWatch}";

            // 3. Write directly to Serilog at the Verbose (Trace) level
            Log.Logger.Write(LogEventLevel.Verbose, formattedMessage);
        }

        /// <summary>
        /// Logs a debug-level message that includes a comment and optional stack trace information.
        /// The log entry includes the line number and method name from the calling code.
        /// </summary>
        /// <param name="comment">The main message or comment to include in the log entry.</param>
        /// <param name="stack">Additional stack trace information to include in the log entry.</param>
        /// <param name="lineNumber">The line number in the calling code. Automatically captured unless explicitly provided.</param>
        /// <param name="methodName">The name of the method in the calling code. Automatically captured unless explicitly provided.</param>
        public void FireLogDebug(
            string comment, 
             string stack, 
            [CallerLineNumber] int lineNumber = 0,
            [CallerMemberName] string methodName = "")
        {

            // 2. Build a natural, unaligned message string
            // Format: LineNumber [MethodName] Comment | JSON
            string formattedMessage = $"L{lineNumber} [{methodName}] {stack} | {comment}";

            // 3. Write directly to Serilog at the Verbose (Trace) level
            Log.Logger.Write(LogEventLevel.Verbose, formattedMessage);
        }
        
        /// <summary>
        /// Logs a structured 5-part message. 
        /// Restricted to: Information, Warning, and Error levels only.
        /// </summary>
        private void    FireLogBase(string subject,
            string verb,
            string identifier,
            string @object,
            string comment,
            LogEventLevel level = LogEventLevel.Information)
        {
            // 1. Level Filter: Only allow high-value logs
            if (level != LogEventLevel.Information && 
                level != LogEventLevel.Warning && 
                level != LogEventLevel.Error)
            {
                return; 
            }

            // 2. Format the 5-part message with fixed widths
            string formattedMessage = string.Format(
                "{0,-10} {1,-10} {2,-10} {3,-10} {4}",
                SafeFormat(subject, Width10),
                SafeFormat(verb, Width10),
                SafeFormat(identifier, Width10),
                SafeFormat(@object, Width10),
                comment
            );

            logger.Write(level, formattedMessage);
        }
    }

    private static string SafeFormat(string? value, int width)
    {
        string text = value ?? string.Empty;
        if (text.Length > width) return text.Substring(0, width);
        return text.PadRight(width);
    }
}





