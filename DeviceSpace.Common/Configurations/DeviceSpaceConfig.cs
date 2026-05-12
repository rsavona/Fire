using DeviceSpace.Common.Contracts;

namespace DeviceSpace.Common.Configurations
{
    // --- Interfaces (Assuming these exist elsewhere or you define them) ---
    
    /// <summary>
    /// Represents a Core Adapter configuration entry.
    /// </summary>
    public class WorkflowConfig : IWorkflowConfig
    {
        public string Name { get; set; } = string.Empty;
        public string Type { get; set; } = string.Empty; 
        public bool Enable { get; set; } 
        // The list of routing rules
        public List<WorkflowRoute> Routes { get; set; } = new();
        public Dictionary<string, object> Properties { get; set; } = new ();
    }

    public class WorkflowRoute
    {
        public string Name { get; set; } = string.Empty;
        // 0 = Disabled, 1 = Method, 2 = Script
        public int Mode { get; set; }

        // Topic to listen to (e.g. "MEDPLC.DReqM.Induct")
        public string Source { get; set; } = string.Empty;

        // Topic to publish result to (e.g. "MEDBroker.LabelRequest")
        public string Destination { get; set; } = string.Empty;

        // Method Name OR File Path
        public string Handler { get; set; } = string.Empty;
    }
    

    /// <summary>
    /// Represents a specific device configuration.
    /// </summary>
    public class DeviceConfig : IDeviceConfig
    {
        public string Name { get; set; } = string.Empty;
        public string Manager { get; set; } = string.Empty;
        public bool Enable { get; set; } 
        public Dictionary<string, object> Properties { get; set; } = new Dictionary<string, object>();
        
  
    }
    

    /// <summary>
    /// Represents the top-level configuration structure.
    /// </summary>
    public class DeviceSpace : IDeviceSpace
    {
        public string Name { get; set; } = "Fire";
        public bool ColorConsole { get; set; } = true;
        
        public int DiagnosticsPort { get; set; } = 9999;
        public List<WorkflowConfig> WorkflowList { get; set; } = new (); // Added
        public List<DeviceConfig> DeviceList { get; set; } = new ();
   
        // Explicitly implement interface properties
        List<IDeviceConfig> IDeviceSpace.DeviceList
        {
            get => DeviceList.Cast<IDeviceConfig>().ToList();
            set => DeviceList = value.Cast<DeviceConfig>().ToList();
        }

        List<IWorkflowConfig> IDeviceSpace.WorkflowList
        {
            get => WorkflowList.Cast<IWorkflowConfig>().ToList();
            set => WorkflowList = value.Cast<WorkflowConfig>().ToList();
        }
    }
}