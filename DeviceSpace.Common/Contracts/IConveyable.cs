using DeviceSpace.Common.Enums;
using Stateless;

namespace DeviceSpace.Common.Contracts;



public interface IConveyable    
{
    int Gin { get; }
    List<string> Barcodes { get; set; }
    double Weight { get; set; }
    (int L, int W, int H) Dimensions { get; set; }

    // Routing Logic
    bool RequiresLabeling { get; set; }
    bool RequiresInsertion { get; set; }
    bool RequiresSorting { get; set; }
    int Destination { get; set; }
    string Location { get; set;}
    // State Machine access
    StateMachine<ConveyableState, ConveyableTrigger> Workflow { get; }
}