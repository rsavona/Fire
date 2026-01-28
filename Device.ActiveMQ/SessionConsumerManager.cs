using System.Collections.Concurrent;
using Apache.NMS;
using Microsoft.Extensions.Logging;

namespace Device.ActiveMQ
{
    internal class SessionConsumerManager
    {
        private readonly ConcurrentDictionary<string, SessionConsumer> _queueData = new ConcurrentDictionary<string, SessionConsumer>();
       
        public SessionConsumerManager()
        {

        }

        public SessionConsumer GetOrCreateConsumer(IConnection connection, string queueName, Action<IMessage> actionHandler)
        {
            return _queueData.GetOrAdd(queueName, (queue) =>
            {
                var session = connection.CreateSession();
                var consumer = session.CreateConsumer(session.GetQueue(queue));
                consumer.Listener += (message) => { actionHandler(message); };
                return new SessionConsumer(session, consumer, actionHandler);
            });
        }

        public void CloseConsumer(string queueName)
        {
            if (_queueData.TryRemove(queueName, out SessionConsumer? data))
            {
                if (data != null)
                {
                    data.Close();
                }
            }
        }

        public void CloseAllConsumers()
        {
            foreach (var data in _queueData.Values)
            {
                
                data.Close();
            }

            _queueData.Clear();
        }

        public void HandleConnectionError(ILogger logger)
        {
            foreach (var data in _queueData.Values)
            {
                if (data != null && data.ActionHandler != null)
                {   logger.LogInformation( $"Closing {data.ActionHandler.ToString}");
                    data.Close();
                }else { logger.LogInformation($"element was null while closing connection in sessionConsumerManager ");}
            }   

        }

        public void Reconnect(IConnection connection )
        {
            var temp = new ConcurrentDictionary<string, SessionConsumer>(_queueData); 
            CloseAllConsumers();
            foreach (var key in temp.Keys)
            {
                if (temp.TryGetValue(key, out SessionConsumer? data))
                {
                    if (data.Valid == false && data.ActionHandler != null)
                    {
                        GetOrCreateConsumer(connection, key, data.ActionHandler);
                    }
                }
            }
        }
    }
}

