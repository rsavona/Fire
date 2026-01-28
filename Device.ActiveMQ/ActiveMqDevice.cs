using Apache.NMS;
using Apache.NMS.ActiveMQ;
using DeviceSpace.Common;
using DeviceSpace.Common.BaseClasses;
using DeviceSpace.Common.Configurations;
using DeviceSpace.Common.Contracts;
using Stateless;
using ILogger = Serilog.ILogger;

namespace Device.ActiveMQ
{
    public class ActiveMqDevice : ClientDeviceBase, IAdapterPort, IMessageProvider
    {
        // --- AMQ-Specific Fields ---
        private IConnection? _connection;
        private readonly int _maxReconnectAttempts = -1;
        private readonly int _reconnectDelayMilliseconds = 2000;
        private readonly SessionConsumerManager _consumermanager;
        private readonly ConnectionFactory _factory;
        private readonly string _defaultReadQueue;
        private readonly string _defaultWriteQueue;
        private readonly object _reconnectLock = new object();
        private readonly string _conStr;
        private readonly string _heartbeatQueue;

        // --- Constructor ---
        public ActiveMqDevice(IDeviceConfig config, ILogger deviceLogger)
            : base(config, deviceLogger, true)
        {
            // Load queues from config with fallbacks
            _defaultReadQueue = ConfigurationLoader.GetOptionalConfig(config.Properties, "DefaultReadQueue", "");
            _defaultWriteQueue = ConfigurationLoader.GetOptionalConfig(config.Properties, "DefaultWriteQueue", "");
            _heartbeatQueue = $"{Key.DeviceName}-Heartbeat";
            _conStr = ConfigurationLoader.GetRequiredConfig<string>(config.Properties, "ConnectionString");

            Logger.Information("[{Dev}] Initializing ActiveMQ Device. Broker: {BrokerUrl}", Config.Name, _conStr);

            _factory = new ConnectionFactory(_conStr);
            _consumermanager = new SessionConsumerManager();
        }

        /// <summary>
        /// Sends a message asynchronously.
        /// This method is overridden in the derived class but should not be used
        /// for ActiveMQ, as sending messages is handled differently in this implementation.
        /// </summary>
        /// <param name="message">The message to be sent.</param>
        /// <returns>A Task representing the asynchronous operation.</returns>
        public override Task SendAsync(string message, CancellationToken token)
        {
            // unneeded for ActiveMQ
            Logger.Warning("[{Dev}] SendAsync was called --  THIS SHOULD NOT HAPPEN -- .", Config.Name);
            return Task.CompletedTask;
        }

        /// <summary>
        /// Sends a periodic heartbeat message to indicate the device's active state.
        /// This method ensures that the device remains connected and operational.
        /// </summary>
        /// <returns>A Task representing the asynchronous operation.</returns>
        public override Task SendHeartbeatAsync(CancellationToken token)
        {
            _ = WriteAsync("Heartbeat", $"{Key.DeviceName}-Heartbeat", false);
            return Task.CompletedTask;
        }


        /// <summary>
        /// Invoked when the device encounters a fault scenario. Executes recovery logic to handle the fault condition.
        /// </summary>
        protected override void DeviceFaultedAsync(CancellationToken token = default)
        {
            Logger.Error("[{Dev}] Device faulted. Initiating recovery logic.", Config.Name);
            HandleConnectionError(token);
        }

        /// <summary>
        /// Initiates a connection attempt to the NMS (ActiveMQ) server.
        /// </summary>
        /// <returns>
        /// A task that represents the asynchronous operation for establishing a connection.
        /// </returns>
        protected override async Task ConnectAsync(CancellationToken token)
        {
            Logger.Information("[{Dev}] Attempting to establish NMS Connection...", Config.Name);
            try
            {
                await CreateConnectionAsync(token);
                Logger.Information("[{Dev}] Connection successful.", Config.Name);
                await Machine.FireAsync(Event.ConnectSuccess);
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "[{Dev}] Initial connection failed.", Config.Name);
                var trigger = new StateMachine<State, Event>.TriggerWithParameters<string>(Event.ConnectFailed);
                await Machine.FireAsync(trigger, ex.Message);
            }
        }

        /// <summary>
        /// Initializes and configures the heartbeat monitoring mechanism for the device.
        /// Sets up a listener to detect heartbeat messages on the designated heartbeat queue.
        /// </summary>
        protected override void InitHeartbeat()
        {
            ReadNotify(NotifyHeartbeatReceived, _heartbeatQueue);
        }

        /// <summary>
        /// Terminates the heartbeat listener by stopping consumption of messages from the heartbeat queue.
        /// </summary>
        protected override void EndHeartbeat()
        {
            ReadNotifyEnd(_heartbeatQueue);
        }

        /// <summary>
        /// Establishes a new connection to the ActiveMQ broker asynchronously.
        /// </summary>
        /// <param name="token"></param>
        /// <returns>A task representing the asynchronous operation to create the connection.</returns>
        private async Task CreateConnectionAsync(CancellationToken token)
        {
            Logger.Debug("[{Dev}] Requesting connection from factory.", Config.Name);
            IConnection connection = await _factory.CreateConnectionAsync();

            connection.ExceptionListener += Connection_ExceptionListener;
            connection.ConnectionInterruptedListener += Connection_ConnectionInterruptedListener;
            connection.ConnectionResumedListener += Connection_ConnectionResumedListener;

            await connection.StartAsync();
            _connection = connection;
        }

        #region Connection Event Listeners

        /// <summary>
        /// Handles NMS exceptions.
        /// </summary>
        /// <param name="exception"></param>
        private void Connection_ExceptionListener(Exception exception)
        {
            Logger.Error(exception, "[{Dev}] NMS Async Exception.", Config.Name);
            var trigger = new StateMachine<State, Event>.TriggerWithParameters<string>(Event.ConnectFailed);
            Machine.Fire(trigger, $"NMS Exception: {exception.Message}");
        }

        /// <summary>
        /// Handles NMS connection interruptions.
        /// </summary>
        private void Connection_ConnectionInterruptedListener()
        {
            Logger.Warning("[{Dev}] NMS Connection Interrupted. Local failover handling...", Config.Name);
        }

        /// <summary>
        /// Handles NMS connection resumptions.
        /// </summary>
        private void Connection_ConnectionResumedListener()
        {
            Logger.Information("[{Dev}] NMS Connection Resumed.", Config.Name);
            Machine.FireAsync(Event.ConnectSuccess);
        }

        #endregion

        /// <summary>
        /// Handles connection errors and manages reconnection logic for the device.
        /// This includes initiating a reconnection loop, attempting to re-establish the connection,
        /// and transitioning the device state based on the success or failure of reconnection attempts.
        /// </summary>
        private void HandleConnectionError(CancellationToken token = default)
        {
            lock (_reconnectLock)
            {
                if (Machine.State != State.ServerOffline) return;
            }

            Logger.Warning("[{Dev}] Entering reconnection loop.", Config.Name);
            Disconnect();

            int attempts = 0;
            while (Machine.State == State.ServerOffline)
            {
                attempts++;
                try
                {
                    Logger.Information("[{Dev}] Reconnect attempt {Count}...", Config.Name, attempts);
                    CreateConnectionAsync(token).Wait();

                    Logger.Information("[{Dev}] Reconnected successfully.", Config.Name);

                    if (_connection != null)
                    {
                        Logger.Debug("[{Dev}] Restoring consumers...", Config.Name);
                        _consumermanager.Reconnect(_connection);
                    }

                    Machine.FireAsync(Event.ConnectSuccess);
                    return;
                }
                catch (Exception ex)
                {
                    int delay = _reconnectDelayMilliseconds * (attempts > 5 ? 5 : attempts);
                    Logger.Warning("[{Dev}] Reconnect failed: {Msg}. Next try in {Delay}ms", Config.Name, ex.Message,
                        delay);
                    Task.Delay(delay).Wait();
                }

                if (_maxReconnectAttempts > 0 && attempts >= _maxReconnectAttempts)
                {
                    Logger.Fatal("[{Dev}] Reconnect exhausted after {Attempts} tries.", Config.Name, attempts);
                    var trigger = new StateMachine<State, Event>.TriggerWithParameters<string>(Event.ConnectFailed);
                    Machine.Fire(trigger, "Max retries reached.");
                    return;
                }
            }
        }

        /// <summary>
        /// Disconnects the ActiveMQ device by closing the current connection and resetting internal state.
        /// </summary>
        /// <remarks>
        /// This method ensures the proper cleanup of resources associated with the device's connection,
        /// including unsubscribing from connection events and releasing the connection object. If already
        /// disconnected, it performs no action.
        /// </remarks>
        public void Disconnect()
        {
            if (_connection != null)
            {
                Logger.Information("[{Dev}] Closing NMS Connection.", Config.Name);
                try
                {
                    _connection.ExceptionListener -= Connection_ExceptionListener;

                    _connection.ConnectionInterruptedListener -= Connection_ConnectionInterruptedListener;
                    _connection.ConnectionResumedListener -= Connection_ConnectionResumedListener;
                    _connection.Close();
                }
                catch (Exception ex)
                {
                    Logger.Debug(ex, "[{Dev}] Error during NMS Close.", Config.Name);
                }
                finally
                {
                    _connection = null;
                }
            }
        }

        /// <summary>
        /// Handles the transition of device states and performs the necessary updates and notifications
        /// associated with the state change.
        /// </summary>
        /// <param name="transition">The state machine transition containing information about the
        /// source state, destination state, and triggering event of the state change.</param>
        protected override void OnStateChange(StateMachine<State, Event>.Transition transition)
        {
            // Log the transition for local debugging
            Logger.Debug("[{Device}] Transition: {Source} -> {Dest} (Trigger: {Trigger})",
                Config.Name, transition.Source, transition.Destination, transition.Trigger);

            // Build a dynamic comment 
            string contextComment;


            if (transition.Destination == State.Connected)
            {
                // When connected, show WHO connected and WHERE (IP and Port)
                contextComment = $"{_conStr}";
            }
            else if (transition.Destination == State.Connecting || transition.Destination == State.ServerOffline)
            {
                contextComment = $" {_conStr}";
            }
            else
            {
                contextComment = $"Event: {transition.Trigger}";
            }

            Tracker.Update(
                transition.Destination,
                transition.Trigger,
                MapStateToHealth(transition.Destination),
                contextComment);

            UpdateAndNotify();
        }

        #region IAdapterPort Implementation (I/O)

        public void Write(string message, string queue, bool fireEvent = true)
        {
            if (Machine.State != State.Connected)
            {
                Logger.Warning("[{Dev}] Write blocked: Device in {State}", Config.Name, Machine.State);
                throw new InvalidOperationException($"Port '{Key}' not connected.");
            }

            Logger.Verbose("[{Dev}] TX >> {Queue}: {Msg}", Config.Name, queue, message);

            try
            {
                using ISession? session = _connection?.CreateSession();
                using IMessageProducer? producer = session?.CreateProducer(session.GetQueue(queue));
                ITextMessage? textMessage = session?.CreateTextMessage(message);

                producer?.Send(textMessage);
                if (fireEvent) // don't fire event if its the heartbeat
                    Machine.Fire(Event.MessageSent);
            }
            catch (NMSException ex)
            {
                Logger.Error(ex, "[{Dev}] Write failure on {Queue}", Config.Name, queue);
                var trigger = new StateMachine<State, Event>.TriggerWithParameters<string>(Event.ConnectFailed);
                Machine.Fire(trigger, $"Write Error: {ex.Message}");
            }
        }

        /// <summary>
        /// Writes a message to the default write queue.
        /// </summary>
        /// <param name="message">The message to be written to the default queue.</param>
        public void Write(string message) => Write(message, _defaultWriteQueue);

        /// <summary>
        /// Asynchronously writes a message to the default write queue.
        /// </summary>
        /// <param name="message">The message to be written to the queue.</param>
        /// <returns>A task that represents the asynchronous operation.</returns>
        public async Task WriteAsync(string message) => await Task.Run(() => Write(message));

        /// <summary>
        /// Writes a message asynchronously to the specified queue, with an option to fire an event.
        /// </summary>
        /// <param name="message">The message to be written.</param>
        /// <param name="queue">The destination queue for the message.</param>
        /// <param name="fireEvent">Specifies whether an event should be fired after writing the message. Defaults to true.</param>
        /// <returns>A task representing the asynchronous write operation.</returns>
        public async Task WriteAsync(string? message, string queue, bool fireEvent = true)
        {
            if (message != null) await Task.Run(() => Write(message, queue, fireEvent));
        }

        /// <summary>
        /// Reads a message synchronously from the specified message queue.
        /// If no message is available within the timeout period, it returns an empty string.
        /// </summary>
        /// <param name="queue">The name of the queue to read the message from.</param>
        /// <returns>The message text retrieved from the specified queue, or an empty string if no message is received within the timeout period.</returns>
        public string Read(string queue)
        {
            Logger.Verbose("[{Dev}] Synchronous Read requested from {Queue}", Config.Name, queue);
            try
            {
                using ISession? session = _connection?.CreateSession();
                using IMessageConsumer? consumer = session?.CreateConsumer(session.GetQueue(queue));

                if (consumer?.Receive(TimeSpan.FromSeconds(2)) is ITextMessage msg)
                {
                    return msg.Text;
                }

                return string.Empty;
            }
            catch (NMSException ex)
            {
                Logger.Error(ex, "[{Dev}] Read error on {Queue}", Config.Name, queue);
                HandleConnectionError();
                throw;
            }
        }

        /// <summary>
        /// Registers a listener for receiving messages from a specified queue, with the provided message handler.
        /// </summary>
        /// <param name="messageHandler">The callback function to execute when a message is received. Takes the message content and queue name as parameters.</param>
        /// <param name="strQueue">The name of the queue to listen to for incoming messages.</param>
        public async void ReadNotify(Action<string, string> messageHandler, string strQueue)
        {
            try
            {
                int retryCount = 0;
                while (_connection == null)
                {
                    if (retryCount >= 10) return;
                    Logger.Warning("[{Dev}] Connection not ready, waiting... (Attempt {Count})", Config.Name,
                        ++retryCount);
                    await Task.Delay(2000);
                }

                Logger.Debug("[{Dev}] Registering listener for {Queue}", Config.Name, strQueue);

                // Match the Action<string, string> signature here
                Action<IMessage> onMessage = CreateHandler(messageHandler, strQueue);
                _consumermanager.GetOrCreateConsumer(_connection, strQueue, onMessage);
            }
            catch (Exception e)
            {
                Logger.Error(e, "[{Dev}] Error registering listener for {Queue}", Config.Name, strQueue);
            }
        }

        /// <summary>
        /// Registers a default listener for receiving messages using the provided message handler.
        /// </summary>
        /// <param name="messageHandler">The callback function to execute when a message is received. Takes the message content and queue name as parameters.</param>
        public void ReadNotify(Action<string, string> messageHandler) => ReadNotify(messageHandler, _defaultReadQueue);


        /// <summary>
        /// Stops and releases any resources tied to a listener associated with the specified queue.
        /// </summary>
        /// <param name="strQueue">The name of the queue whose listener should be terminated.</param>
        public void ReadNotifyEnd(string strQueue)
        {
            try
            {
                Logger.Information("[{Dev}] Ending listener for {Queue}", Config.Name, strQueue);
                _consumermanager.CloseConsumer(strQueue);
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "[{Dev}] Error ending listener for {Queue}", Config.Name, strQueue);
            }
        }

        /// <summary>
        /// Stops and releases any resources tied to a listener associated with the default queue.
        /// </summary>
        public void ReadNotifyEnd() => ReadNotifyEnd(_defaultReadQueue);

        #endregion

        /// <summary>
        /// Creates a message handler to process incoming messages from the specified queue.
        /// </summary>
        /// <param name="messageReceivedCallback">The callback action to invoke when a message is received.</param>
        /// <param name="queue">The name of the queue from which messages will be processed.</param>
        /// <returns>A function that processes the received messages and invokes the callback with the message content and queue name.</returns>
        private Action<IMessage> CreateHandler(Action<string, string> messageReceivedCallback, string queue)
        {
            return (message) =>
            {
                try
                {
                    if (message is ITextMessage textMessage)
                    {
                        string payload = textMessage.Text;
                        Logger.Verbose("[{Dev}] RX << {Queue}: {Data}", Config.Name, queue, payload);

                        if (queue != _heartbeatQueue)
                        {
                            Machine.Fire(Event.DataReceived);
                            var env = new MessageEnvelope(new MessageBusTopic(this.Key.DeviceName,"LabelRequestMessage" , queue) , payload);
                            // 1. Notify the Manager via the existing Event
                            // This is likely what triggers OnDeviceMessageReceivedAsync in the manager
                            MessageReceived?.Invoke(this, env);
                        }

                        // 2. Execute the local callback (Matches Action<string, string>)
                        messageReceivedCallback(payload, queue);
                    }
                }
                catch (Exception ex)
                {
                    Logger.Error(ex, "[{Dev}] Handler crash on {Queue}", Config.Name, queue);
                    Machine.Fire(Event.ConnectFailed);
                }
            };
        }

        /// <summary>
        /// Processes the received data asynchronously after it has been read.
        /// This method enables handling and processing of incoming data in a controlled manner.
        /// </summary>
        /// <param name="buffer">The byte array containing the received data.</param>
        /// <param name="bytesRead">The number of bytes read into the buffer.</param>
        /// <param name="ct">A cancellation token to observe while waiting for the task to complete.</param>
        /// <returns>An asynchronous task representing the data handling operation.</returns>
        protected override Task<bool> HandleReceivedDataAsync(byte[] buffer, int bytesRead, CancellationToken ct) =>
            (Task<bool>)Task.CompletedTask;

        /// <summary>
        /// Stops the device asynchronously. Transitions the device state to 'Stop' and performs necessary cleanup operations.
        /// </summary>
        /// <param name="token">A cancellation token that can be used to signal the request to cancel the stop operation.</param>
        /// <returns>A task that represents the asynchronous operation of stopping the device.</returns>
        public override Task StopAsync(CancellationToken token)
        {
            Logger.Information("[{Dev}] Stop initiated.", Config.Name);
            Machine.Fire(Event.Stop);
            return Task.CompletedTask;
        }

        /// <summary>
        /// Performs cleanup of managed resources utilized by the ActiveMqDevice instance.
        /// Invokes any necessary disconnect operations and ensures base class resource cleanup is executed.
        /// </summary>
        protected override void DisposeManagedResources()
        {
            Logger.Debug("[{Dev}] Disposing ActiveMQ Device.", Config.Name);
            Disconnect();
            base.DisposeManagedResources();
        }

        /// <summary>
        /// Event triggered when a message is received.
        /// </summary>
        /// <remarks>
        /// The <c>MessageReceived</c> event is used to notify when the implementing device or component
        /// receives a new message. It provides two parameters: the sender object and a data object containing
        /// further details about the received message.
        /// </remarks>
        /// <example>
        /// This event can be subscribed to by external components that need to process incoming messages
        /// from a device. The event handler should be implemented to handle the sender and the message data appropriately.
        /// </example>
        public event Action<object, object>? MessageReceived;
    }
}