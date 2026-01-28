using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;


namespace DeviceSpace.Core;

public class BusAuditLogger : BackgroundService
{
    // We use the generic ILogger. Serilog intercepts this.
    private readonly ILogger<BusAuditLogger> _logger;
    public bool IsEnabled { get; set; } = true;
    
    // Configuration: Ignore these types to save disk space
    private readonly HashSet<string> _ignoredTypes = new()
    {
        "Heartbeat",
        "KeepAlive",
     };

    public BusAuditLogger(ILogger<BusAuditLogger> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Call this to log a message from the bus.
    /// The 'Async' sink in Serilog ensures this does not block the calling thread.
    /// </summary>
    public void LogMessage(object? message, string topic = "Global")
    {
        if (message == null) return;

        string messageType = message.GetType().Name;

        // Filter Noise
        if (_ignoredTypes.Contains(messageType)) return;

        // Log using the "AuditLog" property.
        // The Serilog config in Program.cs looks for this property 
        // to route it to the specific audit file.
        using (_logger.BeginScope(new Dictionary<string, object> { { "AuditLog", true } }))
        {
            // We use structured logging "{@Payload}" to serialize the object to JSON automatically
            _logger.LogInformation($"[{topic,-40}] [{messageType,-15}]  {message}");
        }
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // This service is now purely reactive, but we keep it as a BackgroundService 
        // in case we want to add periodic stats (e.g. "Logged 5000 messages this hour")
        _logger.LogInformation("Bus Audit Logger Service started.");
        return Task.CompletedTask;
    }
}
