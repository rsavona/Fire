using Apache.NMS;
using Apache.NMS.ActiveMQ;
using Fusion.Common;
using Fusion.Common.BaseClasses;
using Blueprints;
using Fusion.Common.Contracts;
using Fusion.Common.Logging;
using Serilog.Core;

namespace Device.ActiveMQ
{
    /// <summary>
    /// A specialized device that periodically browses a specific ActiveMQ queue and logs the top messages without consuming them.
    /// </summary>
    public class ActiveMqQueuePeekElement : ClientElementBase
    {
        private readonly ConnectionFactory _factory;
        private readonly string _conStr;
        private readonly string _queueName;
        private readonly int _logCount;
        private readonly int _peekIntervalMs;
        private IConnection? _connection;

        public ActiveMqQueuePeekElement(IMessageBus bus, IDeviceConfig config, IFireLogger logger, LoggingLevelSwitch ls)
            : base(bus, config, logger, ls, false)
        {
            _conStr = ConfigurationLoader.GetRequiredConfig<string>(config.Properties, "ConnectionString")!;
            _queueName = ConfigurationLoader.GetRequiredConfig<string>(config.Properties, "QueueName")!;
            _logCount = ConfigurationLoader.GetOptionalConfig(config.Properties, "LogCount", 5);
            _peekIntervalMs = ConfigurationLoader.GetOptionalConfig(config.Properties, "PeekIntervalMs", 30000);
            
            _factory = new ConnectionFactory(_conStr);
            Logger.Information("[{Dev}] Initializing ActiveMQ Queue Peek Device. Queue: {Queue}, Broker: {BrokerUrl}", Config.Name, _queueName, _conStr);
            
            // Heartbeat monitoring is not applicable for this peek-only device
            NeedsHeartbeat = false;
        }

        protected override async Task<bool> ConnectAsync(CancellationToken token)
        {
            try
            {
                Logger.Information("[{Dev}] Attempting to connect to ActiveMQ for queue peeking...", Config.Name);
                _connection?.Close();
                _connection = await _factory.CreateConnectionAsync();
                await _connection.StartAsync();
                Logger.Information("[{Dev}] Connected to ActiveMQ.", Config.Name);
                return true;
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "[{Dev}] Failed to connect to ActiveMQ: {Msg}", Config.Name, ex.Message);
                return false;
            }
        }

        protected override async Task DeviceConnectedAsync()
        {
            Logger.Information("[{Dev}] Starting periodic queue peeking every {Interval}ms for queue: {Queue}", Config.Name, _peekIntervalMs, _queueName);
            RegisterTask(PeekLoopAsync(ConnectionToken));
            await base.DeviceConnectedAsync();
        }

        protected override Task InitPeriodicEvent()
        {
            // Custom loop is started in DeviceConnectedAsync
            return Task.CompletedTask;
        }

        private async Task PeekLoopAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                if (Machine.State == State.Connected && _connection != null)
                {
                    try
                    {
                        using var session = await _connection.CreateSessionAsync();
                        var queue = await session.GetQueueAsync(_queueName);
                        using var browser = await session.CreateBrowserAsync(queue);
                        
                        var enumerator = browser.GetEnumerator();
                        int count = 0;
                        
                        Logger.Information("[{Dev}] --- Peeking top {Max} messages from {Queue} ---", Config.Name, _logCount, _queueName);
                        
                        while (enumerator.MoveNext() && count < _logCount)
                        {
                            var msg = (IMessage)enumerator.Current;
                            if (msg is ITextMessage textMsg)
                            {
                                Logger.Information("[{Dev}] Peek [{Index}]: {Payload}", Config.Name, count + 1, textMsg.Text);
                            }
                            else if (msg is IBytesMessage bytesMsg)
                            {
                                Logger.Information("[{Dev}] Peek [{Index}]: (Bytes message, length: {Len})", Config.Name, count + 1, bytesMsg.BodyLength);
                            }
                            else
                            {
                                Logger.Information("[{Dev}] Peek [{Index}]: (Message type: {Type})", Config.Name, count + 1, msg.GetType().Name);
                            }
                            count++;
                        }
                        
                        if (count == 0)
                        {
                            Logger.Debug("[{Dev}] Queue {Queue} is empty.", Config.Name, _queueName);
                        }

                        Tracker.HeartBeat();
                    }
                    catch (Exception ex)
                    {
                        Logger.Error(ex, "[{Dev}] Error peeking queue {Queue}: {Msg}", Config.Name, _queueName, ex.Message);
                    }
                }

                await Task.Delay(_peekIntervalMs, ct);
            }
        }

        public override Task SendAsync(string message, CancellationToken token, bool fireEvent = true)
        {
            Logger.Warning("[{Dev}] SendAsync not supported for Queue Peek Device.", Config.Name);
            return Task.CompletedTask;
        }

        public override Task SendHeartbeatAsync(CancellationToken token)
        {
            // Heartbeat check is disabled (NeedsHeartbeat = false)
            return Task.CompletedTask;
        }

        protected override void OnDeviceFaultedAsync(CancellationToken token = default)
        {
            Logger.Error("[{Dev}] Device faulted. Reconnecting...", Config.Name);
        }

        protected override async Task OnDeviceStoppingAsync()
        {
            if (_connection != null)
            {
                await _connection.CloseAsync();
                _connection.Dispose();
                _connection = null;
            }
        }
    }
}
