using Apache.NMS;
using Apache.NMS.ActiveMQ;
using DeviceSpace.Common;
using DeviceSpace.Common.BaseClasses;
using DeviceSpace.Common.Configurations;
using DeviceSpace.Common.Contracts;
using DeviceSpace.Common.Enums;
using Stateless;
using ILogger = Serilog.ILogger;

namespace Device.Connector.ActiveMQ
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
        private bool _isOpen = false;
        private readonly  string _conStr;

        // --- Constructor ---
        public ActiveMqDevice(IDeviceConfig config, ILogger deviceLogger)
            : base(config, deviceLogger)
        {
            // Load queues from config with fallbacks
            _defaultReadQueue = ConfigurationLoader.GetOptionalConfig(config.Properties, "DefaultReadQueue", "");
            _defaultWriteQueue = ConfigurationLoader.GetOptionalConfig(config.Properties, "DefaultWriteQueue", "");

           _conStr = ConfigurationLoader.GetRequiredConfig<string>(config.Properties, "ConnectionString");

            Logger.Information("[{Dev}] Initializing ActiveMQ Device. Broker: {BrokerUrl}", Config.Name, _conStr);

            _factory = new ConnectionFactory(_conStr);
            _consumermanager = new SessionConsumerManager();
        }

        protected override void DeviceFaultedAsync()
        {
            Logger.Error("[{Dev}] Device faulted. Initiating recovery logic.", Config.Name);
            HandleConnectionError();
        }

        /// <summary>
        /// Orchestrates the initial connection attempt.
        /// </summary>
        protected override async Task ConnectAsync()
        {
            Logger.Information("[{Dev}] Attempting to establish NMS Connection...", Config.Name);
            try
            {
                await CreateConnectionAsync();
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

        private async Task CreateConnectionAsync()
        {
            Logger.Debug("[{Dev}] Requesting connection from factory.", Config.Name);
            IConnection connection = await _factory.CreateConnectionAsync();

            connection.ExceptionListener += Connection_ExceptionListener;
            connection.ConnectionInterruptedListener += Connection_ConnectionInterruptedListener;
            connection.ConnectionResumedListener += Connection_ConnectionResumedListener;

            await connection.StartAsync();
            _connection = connection;
            _isOpen = true;
        }

        #region Connection Event Listeners

        private void Connection_ExceptionListener(Exception exception)
        {
            Logger.Error(exception, "[{Dev}] NMS Async Exception.", Config.Name);
            var trigger = new StateMachine<State, Event>.TriggerWithParameters<string>(Event.ConnectFailed);
            Machine.Fire(trigger, $"NMS Exception: {exception.Message}");
        }

        private void Connection_ConnectionInterruptedListener()
        {
            Logger.Warning("[{Dev}] NMS Connection Interrupted. Local failover handling...", Config.Name);
        }

        private void Connection_ConnectionResumedListener()
        {
            Logger.Information("[{Dev}] NMS Connection Resumed.", Config.Name);
            Machine.FireAsync(Event.ConnectSuccess);
        }

        #endregion

        private void HandleConnectionError()
        {
            lock (_reconnectLock)
            {
                if (Machine.State != State.Reconnect) return;
            }

            Logger.Warning("[{Dev}] Entering reconnection loop.", Config.Name);
            Disconnect();

            int attempts = 0;
            while (Machine.State == State.Reconnect)
            {
                attempts++;
                try
                {
                    Logger.Information("[{Dev}] Reconnect attempt {Count}...", Config.Name, attempts);
                    CreateConnectionAsync().Wait();

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
                    _isOpen = false;
                }
            }
        }

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
            else if (transition.Destination == State.Connecting || transition.Destination == State.Reconnect)
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
                -1,
                contextComment);

            UpdateAndNotify();
        }

        #region IAdapterPort Implementation (I/O)

        public void Write(string message, string queue)
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

                Tracker.IncrementOutbound();
                Machine.Fire(Event.MessageSent);
            }
            catch (NMSException ex)
            {
                Logger.Error(ex, "[{Dev}] Write failure on {Queue}", Config.Name, queue);
                var trigger = new StateMachine<State, Event>.TriggerWithParameters<string>(Event.ConnectFailed);
                Machine.Fire(trigger, $"Write Error: {ex.Message}");
            }
        }

        public void Write(string message) => Write(message, _defaultWriteQueue);

        public async Task WriteAsync(string message) => await Task.Run(() => Write(message));

        public async Task WriteAsync(string? message, string queue)
        {
            if (message != null) await Task.Run(() => Write(message, queue));
        }

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

        public void ReadNotify(Action<string, string> messageHandler, string strQueue)
        {
            Logger.Information("[{Dev}] Registering listener for {Queue}", Config.Name, strQueue);
            Action<IMessage> onMessage = CreateHandler(messageHandler, strQueue);
            if (_connection != null)
            {
                _consumermanager.GetOrCreateConsumer(_connection, strQueue, onMessage);
            }
        }

        public void ReadNotify(Action<string, string> messageHandler) => ReadNotify(messageHandler, _defaultReadQueue);

        public void ReadNotifyEnd(string strQueue)
        {
            Logger.Information("[{Dev}] Ending listener for {Queue}", Config.Name, strQueue);
            _consumermanager.CloseConsumer(strQueue);
        }

        public void ReadNotifyEnd() => ReadNotifyEnd(_defaultReadQueue);

        #endregion

        private Action<IMessage> CreateHandler(Action<string, string> messageReceivedCallback, string queue)
        {
            return (message) =>
            {
                try
                {
                    if (message is ITextMessage textMessage)
                    {
                        Logger.Verbose("[{Dev}] RX << {Queue}: {Data}", Config.Name, queue, textMessage.Text);

                        Tracker.IncrementInbound();
                        Machine.Fire(Event.DataReceived);

                        messageReceivedCallback(textMessage.Text, queue);
                    }
                }
                catch (Exception ex)
                {
                    Logger.Error(ex, "[{Dev}] Handler crash on {Queue}", Config.Name, queue);
                    var trigger = new StateMachine<State, Event>.TriggerWithParameters<string>(Event.ConnectFailed);
                    Machine.Fire(trigger, $"Handler Error: {ex.Message}");
                }
            };
        }

        protected override Task HandleReceivedDataAsync(byte[] buffer, int bytesRead, CancellationToken ct) =>
            Task.CompletedTask;

        public override Task StopAsync(CancellationToken token)
        {
            Logger.Information("[{Dev}] Stop initiated.", Config.Name);
            Machine.Fire(Event.Stop);
            return Task.CompletedTask;
        }

        protected override void DisposeManagedResources()
        {
            Logger.Debug("[{Dev}] Disposing ActiveMQ Device.", Config.Name);
            Disconnect();
            base.DisposeManagedResources();
        }

        public event Action<object, object>? MessageReceived;
    }
}