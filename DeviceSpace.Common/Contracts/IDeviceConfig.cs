using System.Collections.Generic;
using DeviceSpace.Common.Configurations;

namespace DeviceSpace.Common.Contracts;

public interface IDeviceSpace
    {
        string Name { get; set;}
        List<IDeviceConfig> DeviceList { get; set; }
        List<IWorkflowConfig> WorkflowList { get; set; }
    }
 public interface IDeviceConfig
    {
        string Name { get; set; }
        string Manager { get; set; }
        bool Enable { get; set; } // Added based on JSON
        Dictionary<string, object> Properties { get; set; }

    }

   
    // New interfaces based on JSON structure
    public interface IWorkflowConfig
    {
        string Name { get; set; } 
        string Type { get; set; }
        public bool Enable { get; set; } 
        // The list of routing rules
        public List<WorkflowRoute> Routes { get; set; } 
    }

    public interface IMessageBusConnectorConfig
    {
        string MessageType { get; set; }
        string InternalData { get; set; }
    }

