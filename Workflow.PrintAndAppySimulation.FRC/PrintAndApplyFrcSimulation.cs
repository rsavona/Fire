using DeviceSpace.Common;
using DeviceSpace.Common.BaseClasses;
using DeviceSpace.Common.Configurations;
using DeviceSpace.Common.Contracts;
using Serilog;

namespace Workflow.PrintAndAppySimulation.FRC;

public class PrintAndApplyFrcSimulation : WorkflowBase
{
    public PrintAndApplyFrcSimulation(IMessageBus bus, WorkflowConfig config, ILogger logger)
        : base(bus, config, logger)
    {
        Logger.Information("[{Workflow}] Simulation Physics & Logic Initialized.", WorkflowKey.DeviceName);
    }

    /// <summary>
    /// Transform Label request into Label Data 
    /// </summary>
    private async Task<object?>? HandleLabelRequest(MessageEnvelope messageEnvelope, CancellationToken ct)
    {
            var t = messageEnvelope.Payload?.GetType().Name;
            Logger.Debug("[{Workflow}] Received message from Message Bus {msg}", WorkflowKey.DeviceName,
                messageEnvelope.Payload);
          
            var ld = TestDataGenerator.GenerateMockResponse(messageEnvelope.Payload);

            Logger.Debug("[{Workflow}] Generated response {msg}", WorkflowKey.DeviceName, ld);
            

            return ld; // MessageEnvelope(new MessageBusTopic(route.Destination), response);
    }
}