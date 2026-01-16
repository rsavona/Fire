
using Device.Plc;
using FortnaDeviceSpace.Common.Contracts;

namespace WorkflowPrintApplyFortnaPlus
{
    /// <summary>
    /// Defines the contract for a stateful induction workflow.
    /// </summary>
    public interface IWorkflowInduction
    {
        /// <summary>
        /// Handles the 'Induct' message from the PLC.
        /// </summary>
        void HandleInductRequest(IDevice device, PlcMessageBase message);

        /// <summary>
        /// Handles the 'P0010' scan from the PLC.
        /// </summary>
        void HandlePrintStationRequest(IDevice device, PlcMessageBase message);


        
        // Note: HandleLabelDataResponse is NOT here.
        // That is a private callback for the bus subscriber.
    }
}