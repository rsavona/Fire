using FortnaDeviceSpace.Contracts;
using Device.Plc; // For PlcMessage
using System.Collections.Concurrent;
using System.Linq;
using FortnaDeviceSpace.Common.Contracts;

namespace WorkflowPrintApplyFortnaPlus;

    /// <summary>
    /// Manages the stateful "Induct-to-Print" workflow.
    /// It correlates an 'Induct' message with a later 'P0010' message
    /// by caching label data received from the message bus.
    /// </summary>
    public class WorkflowInduction : IWorkflowInduction
    {
        private readonly IMessageBus _bus;
        
        // This is the cache for GIN-to-Label data
        private readonly ConcurrentDictionary<string, LabelDataMessage> _labelCache;

        public WorkflowInduction(IMessageBus bus)
        {
            _bus = bus;
            _labelCache = new ConcurrentDictionary<string, LabelDataMessage>();

            // Subscribe to the "answer" from the message bus
            _bus.Subscribe("label.data.response", HandleLabelDataResponse);
        }

        /// <summary>
        // STEP 1: Handle the 'Induct' message from the PLC.
        /// </summary>
        public void HandleInductRequest(PlcDevice device, PlcMessageBase message)
        {
            // Assuming message contains GIN
            //string gin = message.Gin; 
            //if (string.IsNullOrEmpty(gin)) return;

            // Create the label request
            var labelRequest = new LabelRequestMessage(gin, device.Config.Name);
            
            // Publish to the bus with the GIN as the envelope key
            var envelope = new MessageEnvelope("LabelRequest", gin, labelRequest);
            _bus.PublishAsync("label.request", envelope);
        }

        /// <summary>
        // STEP 2: Handle the 'LabelDataMessage' from the bus.
        /// </summary>
        private void HandleLabelDataResponse(IMessageEnvelope envelope)
        {
            if (envelope.Data is LabelDataMessage labelData)
            {
                // Use the envelope's KEY (the GIN) to cache the data
                string gin = envelope.Key; 
                if (!string.IsNullOrEmpty(gin))
                {
                    _labelCache[gin] = labelData;
                }
            }
        }

        /// <summary>
        // STEP 3: Handle the 'P0010' scan from the PLC.
        /// </summary>
        public void HandleP0010Scan(PlcDevice device, PlcMessage message)
        {
            // Assuming message contains GIN
            string gin = message.Gin;
            if (string.IsNullOrEmpty(gin)) return;

            // Check if the label data is in our cache
            if (_labelCache.TryRemove(gin, out LabelDataMessage labelData))
            {
                // SUCCESS: Data is present. Send it to the message bus.
                var topic = $"devices/{device.Config.Name}/messages/print_label";
                var envelope = new MessageEnvelope("PrintLabel", gin, labelData);
                _bus.PublishAsync(topic, envelope);
            }
            else
            {
                // Data is not in the cache yet.
                // You could log this, or implement retry logic.
                
                // IMPORTANT: This creates a race condition. If P0010
                // arrives before the bus responds, the data is lost.
                // A more robust design might requeue the P0010 message.
            }
        }

        public void HandleInductRequest(IDevice device, PlcMessageBase message)
        {
            throw new NotImplementedException();
        }

        public void HandlePrintStationRequest(IDevice device, PlcMessageBase message)
        {
            throw new NotImplementedException();
        }
    }
