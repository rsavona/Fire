using System.Text.Json.Serialization;
using Fusion.Common.Contracts;

namespace Fusion.Common.Blueprints
{
    // --- Interfaces (Assuming these exist elsewhere or you define them) ---
    
    /// <summary>
    /// Represents a Core Adapter configuration entry.
    /// </summary>
    public class ReactionBlueprint : IReactionBlueprint
    {
        public string Name { get; set; } = string.Empty;
        public string Type { get; set; } = string.Empty; 
        public bool Enable { get; set; } 
        public string CoreName { get; set; } = string.Empty;
        public string Comment { get; set; } = string.Empty;
        // The list of routing rules
        public List<BondBlueprint> Bonds { get; set; } = new();
        public Dictionary<string, object> Properties { get; set; } = new ();
    }

    public class BondBlueprint
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
        public string Comment { get; set; } = string.Empty;
    }


    /// <summary>
    /// Represents a specific element configuration.
    /// </summary>
    public class ElementBlueprint : IElementBlueprint
    {
        public string Name { get; set; } = string.Empty;
        public string Manager { get; set; } = string.Empty;
        public bool Enable { get; set; } 
        public string CoreName { get; set; } = string.Empty;
        public string Comment { get; set; } = string.Empty;
        public Dictionary<string, object> Properties { get; set; } = new Dictionary<string, object>();


    }

    public class CompoundBlueprint : ICompoundBlueprint
    {
        public string Name { get; set; } = string.Empty;
        public List<ElementBlueprint> Elements { get; set; } = new();
        public List<ReactionBlueprint> Reactions { get; set; } = new();

        [JsonIgnore]
        List<IElementBlueprint> ICompoundBlueprint.Elements
        {
            get => Elements.Cast<IElementBlueprint>().ToList();
            set { /* Binder skip */ }
        }

        [JsonIgnore]
        List<IReactionBlueprint> ICompoundBlueprint.Reactions
        {
            get => Reactions.Cast<IReactionBlueprint>().ToList();
            set { /* Binder skip */ }
        }
    }


    /// <summary>
    /// Represents the top-level configuration structure.
    /// </summary>
    public class SystemBlueprintTemplate : ISystemBlueprintTemplate
    {
        [JsonPropertyName("Name")]
        [Microsoft.Extensions.Configuration.ConfigurationKeyName("Name")]
        public string CustomerName { get; set; } = "Fusion";

        public string? ServiceName { get; set; }
        public bool ColorConsole { get; set; } = true;
        public bool IsTestEnvironment { get; set; } = false;
        public TimeSpan SimulationRuntime { get; set; } = TimeSpan.Zero;
        public TimeSpan StabilityDuration { get; set; } = TimeSpan.Zero;

        [JsonPropertyName("Cores")]
        [Microsoft.Extensions.Configuration.ConfigurationKeyName("Cores")]
        public List<CompoundBlueprint> Compounds { get; set; } = new ();

        [JsonPropertyName("ElementList")]
        [Microsoft.Extensions.Configuration.ConfigurationKeyName("ElementList")]
        public List<ElementBlueprint> ElementList { get; set; } = new();

        [JsonPropertyName("ReactionList")]
        [Microsoft.Extensions.Configuration.ConfigurationKeyName("ReactionList")]
        public List<ReactionBlueprint> ReactionList { get; set; } = new();

        // Explicitly implement interface properties
        [JsonIgnore]
        List<ICompoundBlueprint> ISystemBlueprintTemplate.Cores
        {
            get => Compounds.Cast<ICompoundBlueprint>().ToList();
            set { /* Binder skip */ }
        }

        [JsonIgnore]
        List<IElementBlueprint> ISystemBlueprintTemplate.ElementList
        {
            get => ElementList.Cast<IElementBlueprint>().ToList();
            set { /* Binder skip */ }
        }

        [JsonIgnore]
        List<IReactionBlueprint> ISystemBlueprintTemplate.ReactionList
        {
            get => ReactionList.Cast<IReactionBlueprint>().ToList();
            set { /* Binder skip */ }
        }
    }

}
