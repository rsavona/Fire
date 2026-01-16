using System.Diagnostics;
using Apache.NMS;

namespace Device.Connector.ActiveMQ
{
    // when the session information needs to be persistent
    // this class sores the info and will be placed in a map
    internal class SessionConsumer : IDisposable
    { 
        public ISession Session { get; }
        public IMessageConsumer Consumer { get; }
        public Action<IMessage>? ActionHandler { get; set; }
        public bool Valid { get; set; } = false;

        private bool _disposed = false;

        public SessionConsumer( ISession session, IMessageConsumer consumer, Action<IMessage>? actionHandler)
        {
            if (session == null)
            {
                throw new ArgumentNullException(nameof(session), "Session cannot be null.");
            }

            if (consumer == null)
            {
                throw new ArgumentNullException(nameof(consumer), "Consumer cannot be null.");
            }

            if (actionHandler == null)
            {
                throw new ArgumentNullException(nameof(actionHandler), "Action handler cannot be null.");
            }

            Session = session;
            Consumer = consumer;
            ActionHandler = actionHandler;
            Valid = true;

        }

        public void Close()
        {
            Consumer?.Close();
            Session?.Close();
            Valid = false;

        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (!_disposed)
            {
                if (disposing)
                {
                    // Dispose managed resources
                    try
                    {
                        Consumer?.Close();
                    }
                    catch (NMSException ex)
                    {
                        Debug.WriteLine($"Faulted closing consumer: {ex.Message}");
                        // Log the exception using your logging framework
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"Unexpected error closing consumer: {ex.Message}");
                    }

                    try
                    {
                        Session?.Close();
                    }
                    catch (NMSException ex)
                    {
                        Debug.WriteLine($"Faulted closing session: {ex.Message}");
                        // Log the exception using your logging framework
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"Unexpected error closing session: {ex.Message}");
                    }
                }

                Valid = false;
                _disposed = true;
            }
        }

        ~SessionConsumer()
        {
            Dispose(false);
        }
    }
}


 

    
