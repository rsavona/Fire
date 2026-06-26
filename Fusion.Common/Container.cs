using Fusion.Common.Contracts;
using Fusion.Common.Enums;
using Stateless;

namespace Fusion.Common;

[System.Diagnostics.DebuggerDisplay("GIN={Gin}, State={ReactionState,nq}, Barcodes={BarcodeCount}")]
public class Container : IConveyable
{
    public int Gin { get; init; }
    public List<string> Barcodes { get; set; }
    public double Weight { get; set; }
    public (int L, int W, int H) Dimensions { get; set; }

    // Routing Logic
    public bool RequiresLabeling { get; set; }
    public bool RequiresInsertion { get; set; }
    public bool RequiresSorting { get; set; }
    
    public int  Destination { get; set; }
    public string Location { get; set; }
    // State Machine for the individual carton
    [System.Diagnostics.DebuggerBrowsable(System.Diagnostics.DebuggerBrowsableState.Never)]
    public StateMachine<ConveyableState, ConveyableTrigger> Reaction { get; private set; }

    private string ReactionState
    {
        get
        {
            try
            {
                return Reaction.State.ToString();
            }
            catch
            {
                return "Unknown";
            }
        }
    }

    private int BarcodeCount => Barcodes?.Count ?? 0;

  
    // Fixed constructor based on your requirements
    public Container(
        int gin, 
        List<string> barcodes, string location, double weight = 0, 
        (int L, int W, int H) dimensions = default)
    {
        Gin = gin;
        Location = location;
        Barcodes = barcodes ?? new List<string>();
        Weight = weight;
        Dimensions = dimensions;

        Reaction = new StateMachine<ConveyableState, ConveyableTrigger>(ConveyableState.NotInducted);
        ConfigureReaction();
    }


 public Container(string location)
    {
        Location = location;
        Barcodes = new List<string>();
        Reaction = new StateMachine<ConveyableState, ConveyableTrigger>(ConveyableState.NotInducted);
        ConfigureReaction();
    }

    public Container()
    {
        throw new NotImplementedException();
    }

    private void ConfigureReaction()
    {
        Reaction.Configure(ConveyableState.NotInducted)
            .Permit(ConveyableTrigger.Induct, ConveyableState.Inducted);

        Reaction.Configure(ConveyableState.Inducted)
            .PermitIf(ConveyableTrigger.Print, ConveyableState.Labeling, () => RequiresLabeling)
            .PermitIf(ConveyableTrigger.Insert, ConveyableState.Inserting, () => !RequiresLabeling && RequiresInsertion)
            .Permit(ConveyableTrigger.Verify, ConveyableState.Verified)
            .Permit(ConveyableTrigger.Reject, ConveyableState.Failed);

        Reaction.Configure(ConveyableState.Labeling)
            .PermitIf(ConveyableTrigger.Insert, ConveyableState.Inserting, () => RequiresInsertion)
            .Permit(ConveyableTrigger.Verify, ConveyableState.Verified);
        

            
            
    }
}
