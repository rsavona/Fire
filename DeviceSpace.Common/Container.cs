using DeviceSpace.Common.Contracts;
using DeviceSpace.Common.Enums;
using Stateless;

namespace DeviceSpace.Common;

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
    public StateMachine<DeviceSpace.Common.Enums.ConveyableState, DeviceSpace.Common.Enums.ConveyableTrigger> Workflow { get; private set; }

  
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

        Workflow = new StateMachine<ConveyableState, ConveyableTrigger>(ConveyableState.NotInducted);
        ConfigureWorkflow();
    }


 public Container(string location)
    {
        Location = location;
        Barcodes = new List<string>();
        Workflow = new StateMachine<ConveyableState, ConveyableTrigger>(ConveyableState.NotInducted);
        ConfigureWorkflow();
    }

    public Container()
    {
        throw new NotImplementedException();
    }

    private void ConfigureWorkflow()
    {
        Workflow.Configure(ConveyableState.NotInducted)
            .Permit(ConveyableTrigger.Induct, ConveyableState.Inducted);

        Workflow.Configure(ConveyableState.Inducted)
            .PermitIf(ConveyableTrigger.Print, ConveyableState.Labeling, () => RequiresLabeling)
            .PermitIf(ConveyableTrigger.Insert, ConveyableState.Inserting, () => !RequiresLabeling && RequiresInsertion)
            .Permit(ConveyableTrigger.Verify, ConveyableState.Verified)
            .Permit(ConveyableTrigger.Reject, ConveyableState.Failed);

        Workflow.Configure(ConveyableState.Labeling)
            .PermitIf(ConveyableTrigger.Insert, ConveyableState.Inserting, () => RequiresInsertion)
            .Permit(ConveyableTrigger.Verify, ConveyableState.Verified);
        

            
            
    }
}