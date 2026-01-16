using Serilog;
using DeviceSpace.Common;
using DeviceSpace.Common.BaseClasses;
using DeviceSpace.Common.Configurations;
using DeviceSpace.Common.Contracts;
using DeviceSpace.Common.Enums;
using Workflow.PrintAndApplyFrc;

namespace Workflow.TestPrintAndAppyFRC;

public class TestPrintAndApplyFrc : WorkflowBase
{
    // Constructor now correctly uses Serilog.ILogger
    public TestPrintAndApplyFrc(
        IMessageBus messageBus, 
        WorkflowConfig config, 
        ILogger logger)
        : base(messageBus, config, logger)
    {
        Logger.Information("[{Workflow}] Simulation workflow logic initialized.", WorkflowKey.DeviceName);
    }

    /// <summary>
    /// This method is mapped via reflection from the WorkflowConfig routes.
    /// </summary>
    private async Task<object?> HandleLabelRequest(MessageEnvelope messageEnvelope, CancellationToken ct)
    {
        var correlationId = Guid.NewGuid().ToString()[..8]; 
        
        try
        {
            // Using Serilog structured logging
            Logger.Information("[{Workflow}:{Id}] Processing simulated Label Request for {Type}", 
                WorkflowKey.DeviceName, correlationId, messageEnvelope.Payload?.GetType().Name ?? "Unknown");

            if (messageEnvelope.Payload is LabelRequestFrcMessage msg)
            {
                Logger.Debug("[{Workflow}:{Id}] Mocking data for Session: {Session}", 
                    WorkflowKey.DeviceName, correlationId, msg.SessionId);

                var response = TestDataGenerator.GenerateRandomLabelData(msg);
                
                if (response == null)
                {
                    Logger.Error("[{Workflow}:{Id}] TestDataGenerator returned no data.", WorkflowKey.DeviceName, correlationId);
                    return null;
                }

                // Identify return route
                var route = Config.Routes.FirstOrDefault(r => r.Handler == "HandleLabelRequest");
                if (route != null)
                {
                    Logger.Verbose("[{Workflow}:{Id}] Simulation success. Routing to {Dest}", 
                        WorkflowKey.DeviceName, correlationId, route.Destination);

                    return new MessageEnvelope(new MessageBusTopic(route.Destination), response);
                }
            }
            else
            {
                Logger.Warning("[{Workflow}:{Id}] Payload type mismatch. Got {ActualType}", 
                    WorkflowKey.DeviceName, correlationId, messageEnvelope.Payload?.GetType().Name);
            }
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "[{Workflow}:{Id}] Simulation handler failed!", WorkflowKey.DeviceName, correlationId);
            UpdateStatus(WorkflowState.ActiveWithErrors, WorkflowEvent.Error, DeviceHealth.Error, ex.Message);
        }

        return null;
    }
}