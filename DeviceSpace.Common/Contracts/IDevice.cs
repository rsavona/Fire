
using System;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Serialization;
using Serilog;

namespace DeviceSpace.Common.Contracts // Or your preferred core namespace
{

    /// <summary>
    /// Defines the core contract for any manageable device in the system.
    /// </summary>
    public interface IDevice : IDisposable, IAsyncDisposable 
    {
        /// <summary>
        /// Gets the unique, immutable key for this device.
        /// </summary>
        DeviceKey Key { get; }

        /// <summary>
        /// Gets the configuration object used to initialize this device.
        /// </summary>
        IDeviceConfig Config { get; }

        /// <summary>
        /// Gets the current status object for this device.
        /// </summary>
        /// <param name="comment"></param>
        IDeviceStatus CreateStatusSnapshot( string comment = "");

        /// <summary>
        /// Fires whenever the device's internal 'Status' object is updated.
        /// This is the primary event for the DeviceManager to subscribe to.
        /// </summary>
        event Action<IDevice, IDeviceStatus> StatusUpdated;
        
        IFireLogger GetLogger();

        /// <summary>
        /// Exports the device's internal state to Graphviz format.'
        /// </summary>
        /// <returns></returns>
         string ExportToGraphviz();

        string GetDeviceVersion();
        
        /// <summary>
        /// Starts the device's internal operations (e.g., starts its TcpServer).
        /// </summary>
        Task StartAsync(CancellationToken token);

        /// <summary>
        /// Stops the device's internal operations.
        /// </summary>
        Task StopAsync(CancellationToken token);

        IEnumerable<DiagCommand> GetAvailableCommands();
        
        void OnError(string context, Exception? ex = null);
        
        public event Action<IDevice>? DeviceReady;
        
        bool NeedsHeartbeat { get; set; }
        
        
    }


}


    