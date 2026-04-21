using DeviceSpace.Common.BaseClasses;
using DeviceSpace.Common.Contracts;

namespace Device.Api.Suite;

using Microsoft.Extensions.Hosting;
using System.Threading;
using System.Threading.Tasks;
using System.Diagnostics;

public class WcsMessageListener : BackgroundService //,  IDeviceManager
{
    // If you need to write to the DB when a message arrives, inject your DatabaseConnector here
    
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        Debug.WriteLine("INFO: WCS Message Listener is booting up...");

        // 1. Connect to your message bus (ActiveMQ, RabbitMQ, etc.)
        // var connection = await MyBusFactory.ConnectAsync();
        
        // 2. Subscribe to the topic
        // connection.Subscribe("wcs.commands.execute", OnMessageReceived);

        // 3. Keep the thread alive until the application shuts down
        while (!stoppingToken.IsCancellationRequested)
        {
            // The listener just hangs out here in the background
            await Task.Delay(1000, stoppingToken); 
        }
        
        Debug.WriteLine("INFO: WCS Message Listener is shutting down.");
    }

    // The event handler that fires when a message actually arrives from the bus
    private void OnMessageReceived(string message)
    {
        Debug.WriteLine($"RECEIVED FROM BUS: {message}");
        
        // Parse the message and tell your PLC or database what to do
    }
}