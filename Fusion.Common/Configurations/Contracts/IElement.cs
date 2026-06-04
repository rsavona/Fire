
using System;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Serialization;
using Serilog;

namespace Fusion.Common.Contracts // Or your preferred core namespace
{

    /// <summary>
    /// Defines the core contract for any manageable element in the system.
    /// </summary>
    public interface IElement : IDisposable, IAsyncDisposable 
    {
        /// <summary>
        /// Gets the unique, immutable key for this element.
        /// </summary>
        ElementKey Key { get; }

        /// <summary>
        /// Gets the configuration object used to initialize this element.
        /// </summary>
        IElementBlueprint Config { get; }

        /// <summary>
        /// Gets the current status object for this element.
        /// </summary>
        /// <param name="comment"></param>
        IDeviceStatus CreateStatusSnapshot( string comment = "");

        /// <summary>
        /// Fires whenever the element's internal 'Status' object is updated.
        /// This is the primary event for the DeviceManager to subscribe to.
        /// </summary>
        event Action<IElement, IDeviceStatus> StatusUpdated;
        
        IFireLogger GetLogger();

        /// <summary>
        /// Exports the element's internal state to Graphviz format.'
        /// </summary>
        /// <returns></returns>
         string ExportToGraphviz();

        string GetDeviceVersion();
        
        /// <summary>
        /// Starts the element's internal operations (e.g., starts its TcpServer).
        /// </summary>
        Task StartAsync(CancellationToken token);

        /// <summary>
        /// Stops the element's internal operations.
        /// </summary>
        Task StopAsync(CancellationToken token);

        IEnumerable<DiagCommand> GetAvailableCommands();
        
        void OnError(string context, Exception? ex = null);

        void RefreshStatus();
        
        public event Action<IElement>? DeviceReady;
        
        bool NeedsHeartbeat { get; set; }
        
        
    }


}


    