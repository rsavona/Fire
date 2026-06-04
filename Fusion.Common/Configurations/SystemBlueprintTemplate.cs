using Fusion.Common.Contracts;

namespace Fusion.Common.Configurations
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
        public string CoreName { get; set; } = string.Empty;
        // The list of routing rules
        public List<ForceBond> Bonds { get; set; } = new();
        public Dictionary<string, object> Properties { get; set; } = new ();
    }

    public class ForceBond
    {
        public string Name { get; set; } = string.Empty;
        // 0 = Disabled, 1 = Method, 2 = Script
        public int Mode { get; set; }

        // Topic to listen to (e.g. "MEDPLC.DReqM.Induct")
        public string Source { get; set; } = string.Empty;

        // Topic to publish result to (e.g. "MEDBroker.LabelRequest")
        public string Destination { get; set; } = string.Empty;

        // Method CustomerName OR File Path
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
        public string CoreName { get; set; } = string.Empty;
        public Dictionary<string, object> Properties { get; set; } = new Dictionary<string, object>();


    }

    public class CoreConfig : ICoreConfig
    {
        public string Name { get; set; } = string.Empty;
        public List<DeviceConfig> Elements { get; set; } = new();
        public List<WorkflowConfig> Forces { get; set; } = new();

        List<IDeviceConfig> ICoreConfig.Elements
        {
            get => Elements.Cast<IDeviceConfig>().ToList();
            set => Elements = value.Cast<DeviceConfig>().ToList();
        }

        List<IWorkflowConfig> ICoreConfig.Forces
        {
            get => Forces.Cast<IWorkflowConfig>().ToList();
            set => Forces = value.Cast<WorkflowConfig>().ToList();
        }
    }


    /// <summary>
    /// Represents the top-level configuration structure.
    /// </summary>
    public class SystemBlueprintTemplate : ISystemBlueprintTemplate
    {
        public string CustomerName { get; set; } = "Fusion";
        public bool ColorConsole { get; set; } = true;
        public bool IsTestEnvironment { get; set; } = false;
        public TimeSpan SimulationRuntime { get; set; } = TimeSpan.Zero;
        public TimeSpan StabilityDuration { get; set; } = TimeSpan.Zero;
        public List<CoreConfig> Cores { get; set; } = new ();
        // Explicitly implement interface properties
        List<ICoreConfig> ISystemBlueprintTemplate.Cores
        {
            get => Cores.Cast<ICoreConfig>().ToList();
            set => Cores = value.Cast<CoreConfig>().ToList();
        }
    }

}