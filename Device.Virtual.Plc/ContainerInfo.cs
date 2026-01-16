using Device.Connector.Plc;
using Stateless;

namespace Device.Virtual.Plc;

public class ContainerInfo
{
    public int Gin { get; init; }
    public List<string> Barcodes { get; set; }
    public double Weight { get; set; }
    public (int L, int W, int H) Dimensions { get; set; }

    // Routing Logic
    public bool RequiresLabeling { get; set; }
    public bool RequiresInsertion { get; set; }
    public bool RequiresSorting { get; set; }

    // State Machine for the individual carton
    public StateMachine<CartonState, CartonTrigger> Workflow { get; private set; }

    public enum CartonState { NotInducted, Inducted, Labeling, Inserting, Verified, Failed }
    public enum CartonTrigger { Induct, Print, Insert, Verify, Reject }

    // Fixed constructor based on your requirements
    public ContainerInfo(
        int gin, 
        List<string> barcodes, 
        double weight = 0, 
        (int L, int W, int H) dimensions = default)
    {
        Gin = gin;
        Barcodes = barcodes ?? new List<string>();
        Weight = weight;
        Dimensions = dimensions;

        Workflow = new StateMachine<CartonState, CartonTrigger>(CartonState.NotInducted);
        ConfigureWorkflow();
    }

    private void ConfigureWorkflow()
    {
        Workflow.Configure(CartonState.NotInducted)
            .Permit(CartonTrigger.Induct, CartonState.Inducted);

        Workflow.Configure(CartonState.Inducted)
            .PermitIf(CartonTrigger.Print, CartonState.Labeling, () => RequiresLabeling)
            .PermitIf(CartonTrigger.Insert, CartonState.Inserting, () => !RequiresLabeling && RequiresInsertion)
            .Permit(CartonTrigger.Verify, CartonState.Verified)
            .Permit(CartonTrigger.Reject, CartonState.Failed);

        Workflow.Configure(CartonState.Labeling)
            .PermitIf(CartonTrigger.Insert, CartonState.Inserting, () => RequiresInsertion)
            .Permit(CartonTrigger.Verify, CartonState.Verified);
    }
}