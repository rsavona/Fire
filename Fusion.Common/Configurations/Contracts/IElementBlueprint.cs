using System.Collections.Generic;
using Fusion.Common.Blueprints;

namespace Fusion.Common.Contracts;

public interface ISystemBlueprintTemplate
    {
        string CustomerName { get; set;}
        bool ColorConsole { get; set; }
        bool IsTestEnvironment { get; set; }
        TimeSpan SimulationRuntime { get; set; }
        TimeSpan StabilityDuration { get; set; }
        List<ICompoundBlueprint> Cores { get; set; }
        List<IElementBlueprint> ElementList { get; set; }
        List<IReactionBlueprint> ReactionList { get; set; }
    }

    public interface ICompoundBlueprint
    {
        string Name { get; set; }
        List<IElementBlueprint> Elements { get; set; }
        List<IReactionBlueprint> Reactions { get; set; }
    }

 public interface IElementBlueprint
    {
        string Name { get; set; }
        string Manager { get; set; }
        bool Enable { get; set; } // Added based on JSON
        string CoreName { get; set; } // Parent Core
        string Comment { get; set; }
        Dictionary<string, object> Properties { get; set; }

    }

   
    // New interfaces based on JSON structure
    public interface IReactionBlueprint
    {
        string Name { get; set; } 
        string Type { get; set; }
        public bool Enable { get; set; } 
        string CoreName { get; set; } // Parent Core
        string Comment { get; set; }
        // The list of routing rules
        public List<BondBlueprint> Bonds { get; set; } 
        Dictionary<string, object> Properties { get; set; }
    }

    public interface IMessageBusConnectorConfig
    {
        string MessageType { get; set; }
        string InternalData { get; set; }
    }

