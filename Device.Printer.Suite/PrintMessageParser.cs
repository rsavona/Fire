using DeviceSpace.Common;
using DeviceSpace.Common.Contracts;

namespace Device.Printer.Suite
{
   
    public class PrintMessageParser : IMessageParser
    {
        private readonly IFireLogger _logger;

        public PrintMessageParser(IFireLogger logger)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public bool CanHandle(SourceIdentifier source)
        {
            throw new NotImplementedException();
        }

        object IMessageParser.Parse(string rawPayload)
        {
            return Parse(rawPayload);
        }

        public string FrameResponse(object payload, string deviceName)
        {
            throw new NotImplementedException();
        }

        public object Parse(string rawMessage)
        {
            _logger.LogEnter(rawMessage);
            
          

            try
            {
                // Split Zebra ~HS response into lines
                var lines = rawMessage.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries);
                
                // Line 2 contains the error flags (Index 1)
                if (lines.Length >= 2)
                {
                    var parts = lines[1].Split(',');

                    if (parts.Length >= 6)
                    {
                     
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error occurred while parsing Zebra status string.");
            }

            _logger.LogExit();
            return rawMessage;
        }
    }
}