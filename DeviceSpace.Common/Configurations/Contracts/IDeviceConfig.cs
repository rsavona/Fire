using System.Collections.Generic;
using DeviceSpace.Common.Configurations;

namespace DeviceSpace.Common.Contracts;

public interface IDeviceSpace
    {
        string Name { get; set;}
        bool ColorConsole { get; set; }
        bool IsTestEnvironment { get; set; }
        TimeSpan SimulationRuntime { get; set; }
        TimeSpan StabilityDuration { get; set; }
        List<ICoreConfig> Cores { get; set; }
    }

    public interface ICoreConfig
    {
        string Name { get; set; }
        List<IDeviceConfig> Elements { get; set; }
        List<IWorkflowConfig> Forces { get; set; }
    }

 public interface IDeviceConfig
    {
        string Name { get; set; }
        string Manager { get; set; }
        bool Enable { get; set; } // Added based on JSON
        string CoreName { get; set; } // Parent Core
        Dictionary<string, object> Properties { get; set; }

    }

   
    // New interfaces based on JSON structure
    public interface IWorkflowConfig
    {
        string Name { get; set; } 
        string Type { get; set; }
        public bool Enable { get; set; } 
        string CoreName { get; set; } // Parent Core
        // The list of routing rules
        public List<ForceBond> Bonds { get; set; } 
        Dictionary<string, object> Properties { get; set; }
    }

    public interface IMessageBusConnectorConfig
    {
        string MessageType { get; set; }
        string InternalData { get; set; }
    }

