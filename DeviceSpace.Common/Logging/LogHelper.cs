using System.Reflection;
using Microsoft.Extensions.Logging;

namespace DeviceSpace.Common.Logging;

// --- Helper Enums ---

public enum LogClass
{
   DsCore, // Core orchestration/business logic
   MqWkr,  // Message Queue Worker
   PlcWkr, // PLC Communication Worker
   PrtWkr, // Print Worker
   TcpSrv, // TCP Server Listener
   Init     // Initialization/Startup process
}

public enum LogConsoleColor
{
   Red,     // Bright Red for Errors
   Yellow,  // Yellow for Warnings/Init
   Blue,    // Standard Blue for TCP Server
   Cyan,    // Standard Cyan for PLC
   Magenta, // Standard Magenta for Printer
   Green,   // Standard Green for MQ
   BrightBlue // Bright Blue for Core/Orchestration
}

// --- Console Color Map & Prefix Helper ---

public static class ConsoleColorMap
{
    public const string ResetCode = "\x1b[0m"; // ANSI reset code
    
    public static readonly Dictionary<LogConsoleColor, string> ColorCodes = new Dictionary<LogConsoleColor, string>
    {
        { LogConsoleColor.Red, "\x1b[91m" },
        { LogConsoleColor.Yellow, "\x1b[33m" },
        { LogConsoleColor.Blue, "\x1b[34m" },
        { LogConsoleColor.Cyan, "\x1b[36m" },
        { LogConsoleColor.Magenta, "\x1b[35m" },
        { LogConsoleColor.Green, "\x1b[32m" },
        { LogConsoleColor.BrightBlue, "\x1b[94m" }
    };

    /// <summary>
    /// Gets the console prefix (Color + Tag) for a given LogClass.
    /// </summary>
    public static string GetClassPrefix(LogClass logClass)
    {
        return logClass switch
        {
            LogClass.DsCore => ColorCodes[LogConsoleColor.BrightBlue] + "(PaA-COR)" + ResetCode,
            LogClass.MqWkr => ColorCodes[LogConsoleColor.Green] + "(MQB-LSN)" + ResetCode,
            LogClass.PlcWkr => ColorCodes[LogConsoleColor.Cyan] + "(PLC-LSN)" + ResetCode,
            LogClass.PrtWkr => ColorCodes[LogConsoleColor.Magenta] + "(PRT-LSN)" + ResetCode,
            LogClass.TcpSrv => ColorCodes[LogConsoleColor.Blue] + "(TCP-SRV)" + ResetCode,
            LogClass.Init => ColorCodes[LogConsoleColor.Yellow] + "(INIT)" + ResetCode,
            _ => "(UNKNOWN)",
        };
    }
}

// --- Enum Description Helper ---

/// <summary>
/// Helper extension to retrieve the string from the [Description] attribute for cleaner messages.
/// </summary>
public static class EnumExtensions
{
    public static string GetDescription<T>(this T enumerationValue) where T : Enum
    {
        var type = enumerationValue.GetType();
        var memberInfo = type.GetMember(enumerationValue.ToString());
        
        if (memberInfo.Length > 0)
        {
            var attribute = memberInfo[0].GetCustomAttribute<System.ComponentModel.DescriptionAttribute>(false);
            if (attribute != null)
            {
                return attribute.Description;
            }
        }
        return enumerationValue.ToString();
    }
}

// --- Log Event Enums (Retained from User's input) ---

public enum LogEvent
{
    // --- Event Logs ---
    [System.ComponentModel.Description("The service has started and completed its initial setup.")]
    SysInit,
    [System.ComponentModel.Description("A service task is starting.")]
    TaskStart,
    [System.ComponentModel.Description("A channel has been closed.")]
    ChanelCls,
    [System.ComponentModel.Description("A task has been reset.")]
    TaskReset,
    [System.ComponentModel.Description("The service's tasks are running.")]
    SystemRun,
    [System.ComponentModel.Description("The service is awaiting tasks.")]
    AwaitTsks,
    [System.ComponentModel.Description("The service is shutting down.")]
    Stopping,
    [System.ComponentModel.Description("The service is shutting down.")]
    SysShtdwn,
    [System.ComponentModel.Description("A container has been successfully scanned at a read point.")]
    Scanned,
    [System.ComponentModel.Description("An container has been detected by a sensor.")]
    Detected,
    [System.ComponentModel.Description("A container has been inducted.")]
    Inducted,
    [System.ComponentModel.Description("A message was ignored.")]
    MsgIgnore,
    [System.ComponentModel.Description("An action was rejected.")]
    Reject,
    [System.ComponentModel.Description("A queue or dictionary was empty when an item was expected.")]
    Empty,
    [System.ComponentModel.Description("An expected item was found in a queue or dictionary.")]
    FoundInfo,
    [System.ComponentModel.Description("A response has been sent to an external system.")]
    Response,
    [System.ComponentModel.Description("A new client has successfully connected to the server.")]
    ClientConn,
    [System.ComponentModel.Description("A client has disconnected from the server.")]
    ClientDiscon,
    [System.ComponentModel.Description("A TCP port is in use.")]
    PortInUse,
    [System.ComponentModel.Description("A TCP port is listening for connections.")]
    PortListen,
    [System.ComponentModel.Description("A configuration has been loaded.")]
    ConfgLoad,
    [System.ComponentModel.Description("Data has been successfully received from a client.")]
    DataRecvd,
    [System.ComponentModel.Description("Data has been successfully sent to a client.")]
    DataSent,
    [System.ComponentModel.Description("A periodic heartbeat signal from the server.")]
    Heartbeat,
    [System.ComponentModel.Description("A message has been received from an external system.")]
    Receive,
    [System.ComponentModel.Description("A message was successfully written to a channel/queue.")]
    Write,
    [System.ComponentModel.Description("A print job has started for a label.")]
    PrintStart,
    [System.ComponentModel.Description("A label has been successfully printed.")]
    LabelPrint,
    [System.ComponentModel.Description("A Request Message at a print location.")]
    ReqPrtSta,
    [System.ComponentModel.Description("A Request Message at an induct location.")]
    ReqInduct,
    [System.ComponentModel.Description("A Request Message at an validation location.")]
    ReqValidt,
    [System.ComponentModel.Description("A label has been stored in queue.")]
    LabelStrd,
    Exception,
    OpCancel,
    SendFaild
}

public enum LogError
{
    // --- Error Logs ---
    [System.ComponentModel.Description("A data error has occured.")]
    DataError,
    [System.ComponentModel.Description("An exception occurred.")]
    Exception,
    [System.ComponentModel.Description("A critical, unhandled error has occurred.")]
    SysError,
    [System.ComponentModel.Description("A message or object was null when a value was expected.")]
    NullData,
    [System.ComponentModel.Description("An error occurred during message processing.")]
    ProcessErr,
    [System.ComponentModel.Description("A general, unexpected error has occurred.")]
    GeneralErr,
    [System.ComponentModel.Description("An unrecognized container detected.")]
    Unknown,
    [System.ComponentModel.Description("An unread container detected.")]
    ScanNoRead,
    [System.ComponentModel.Description("Failed to listen on a port.")]
    FailListn,
    [System.ComponentModel.Description("Failed to send data.")]
    SendFaild,
    [System.ComponentModel.Description("An unrecognized message was received.")]
    UnknownMsg,
    [System.ComponentModel.Description("A decision point was unknown or not found.")]
    UkownDcPt,
    [System.ComponentModel.Description("Cancelation token has been requested.")]
    Canceltion,
    [System.ComponentModel.Description("An operation was canceled.")]
    OpCancel,
    [System.ComponentModel.Description("Key Not Found")]
    NotFound
}

// --- Logging Enable/Toggle ---

public static class LogEnable 
{
    public static bool Svr { get; set; } = true;
    public static bool Plc { get; set; } = true;
    public static bool Prt { get; set; } = true;
    public static bool MqBroker { get; set; } = true;
    public static bool Core { get; set; } = true;
    
    // Kept for backward compatibility, renamed for clarity
    public static void SetEnableMask(int mask)
    {
        Svr = (mask & 0x01) != 0;
        Plc = (mask & 0x02) != 0;
        Prt = (mask & 0x04) != 0;
        MqBroker = (mask & 0x08) != 0;
        Core = (mask & 0x10) != 0;
    }

    /// <summary>
    /// Checks if logging is enabled for a specific LogClass based on the toggle mask.
    /// </summary>
    public static bool IsEnabled(LogClass logClass)
    {
        return logClass switch
        {
            LogClass.TcpSrv => Svr,
            LogClass.PlcWkr => Plc,
            LogClass.PrtWkr => Prt,
            LogClass.MqWkr => MqBroker,
            LogClass.DsCore => Core,
            LogClass.Init => Core, // INIT is tied to the Core module
            _ => true,
        };
    }
}


// --- Main Log Helper (LH) ---

/// <summary>
/// Centralized Log Helper (LH) for the WCS, enforcing consistent structure, 
/// console coloring, and conditional logging based on LogClass.
/// </summary>
public static class Lh
{
    // A centralized error count is maintained using Interlocked for thread safety.
    private static int _errors = 0; 
    public static int ErrorCount => _errors;

    // --- CORE LOGGING METHODS ---
    
    /// <summary>
    /// Logs a routine operational event using the Serilog Information level.
    /// This method is the central implementation for all standard logs.
    /// </summary>
    public static void Log(
        ILogger logger, 
        LogClass logClass, 
        LogEvent what, 
        string group = "", 
        string message = "", 
        object? data = null)
    {
        if (!LogEnable.IsEnabled(logClass)) return;
        
        var whoPrefix = ConsoleColorMap.GetClassPrefix(logClass);
        var eventDescription = what.GetDescription();

        // Standard Information Log: Uses structured properties for cleaner data handling 
        // while preserving console padding/coloring.
        logger.LogInformation(
            "{WhoPrefix} {EventName,-12} {Group,-20} {Message}", 
            whoPrefix, 
            what.ToString(), 
            group, 
            message);
            
        // Log detailed data at the Debug level
        if (data != null)
        {
            logger.LogDebug("{WhoPrefix} {EventName} | Data: {@Data}", whoPrefix, what.ToString(), data);
        }
    }
    
    /// <summary>
    /// Logs an error or abnormal condition using the Serilog Error level (appropriate for WCS faults).
    /// This method is the central implementation for all error logs.
    /// </summary>
    public static void LogError(
        ILogger logger, 
        LogClass logClass, 
        LogError what, 
        string group, 
        string message, 
        Exception? ex = null)
    {
        if (!LogEnable.IsEnabled(logClass)) return;
        
        var whoPrefix = ConsoleColorMap.GetClassPrefix(logClass);
        
        // Log the main error message using Error level
        // Uses ANSI bright red for the ERROR indicator.
        logger.LogError(
            "{WhoPrefix} \x1b[91mERROR!!!\x1b[0m {ErrorName,-12} {Group,-20} {Message}",
            whoPrefix,
            what.ToString(),
            group,
            message);
            
        // Log the exception details using Serilog's standard exception formatting.
        if (ex != null)
        {
            // The Serilog .Error(ex, ...) signature automatically includes the exception stack trace.
            logger.LogError(ex, "{WhoPrefix} Exception in {ErrorName}: {ExMessage}", whoPrefix, what.ToString(), ex.Message);
        }
        
        // Atomically increment the error count for potential health monitoring.
        Interlocked.Increment(ref _errors);
    }
    
    // --- SIMPLIFIED WRAPPER METHODS (User-friendly API) ---
    
    public static void LogSvr(ILogger logger, LogEvent what, string group = "", string msg = "", object? data = null)
        => Log(logger, LogClass.TcpSrv, what, group, msg, data);

    public static void LogSvrErr(ILogger logger, LogError what , string group , string msg , Exception? ex = null)
        => LogError(logger, LogClass.TcpSrv, what, group, msg, ex);
        
    public static void LogPlc(ILogger logger, LogEvent what, string group = "", string msg = "", object? data = null)
        => Log(logger, LogClass.PlcWkr, what, group, msg, data);
        
    public static void LogPlcErr(ILogger logger, LogError what , string group , string msg , Exception? ex = null)
        => LogError(logger, LogClass.PlcWkr, what, group, msg, ex);
        
    public static void LogPrt(ILogger logger, LogEvent what, string group = "", string msg = "", object? data = null)
        => Log(logger, LogClass.PrtWkr, what, group, msg, data);
        
    public static void LogPrtErr(ILogger logger, LogError what , string group , string msg , Exception? ex = null)
        => LogError(logger, LogClass.PrtWkr, what, group, msg, ex);
        
    public static void LogMqBroker(ILogger logger, LogEvent what, string group = "", string msg = "", object? data = null)
        => Log(logger, LogClass.MqWkr, what, group, msg, data);

    public static void LogMqBrokerErr(ILogger logger, LogError what , string group , string msg , Exception? ex = null)
        => LogError(logger, LogClass.MqWkr, what, group, msg, ex);

    public static void LogCore(ILogger logger, LogEvent what, string group = "", string msg = "", object? data = null)
        => Log(logger, LogClass.DsCore, what, group, msg, data);
        
    public static void LogCoreErr(ILogger logger, LogError what , string group , string msg , Exception? ex = null)
        => LogError(logger, LogClass.DsCore, what, group, msg, ex);

    public static void LogInit(ILogger logger, LogEvent what, string group = "", string msg = "", object? data = null)
        => Log(logger, LogClass.Init, what, group, msg, data);
}
