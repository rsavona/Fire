using Apache.NMS;
using Apache.NMS.ActiveMQ;
using Fusion.Common;
using Fusion.Common.BaseClasses;
using Fusion.Common.Configurations;
using Fusion.Common.Contracts;
using Fusion.Common.Logging;
using Serilog.Core;

namespace Fusion.Element.ActiveMQ
{
    /// <summary>
    /// A specialized element that periodically browses ActiveMQ queue names and logs them.
    /// </summary>
    public class ActiveMqBrowserElement : ClientElementBase
    {
        private readonly ConnectionFactory _factory;
        private readonly string _conStr;
        private IConnection? _connection;
        private readonly int _browseIntervalMs;

        public ActiveMqBrowserElement(IMessageBus bus, IElementBlueprint config, IFireLogger logger, LoggingLevelSwitch ls)
            : base(bus, config, logger, ls, false)
        {
            _conStr = ConfigurationLoader.GetRequiredConfig<string>(config.Properties, "ConnectionString");
            _browseIntervalMs = ConfigurationLoader.GetOptionalConfig(config.Properties, "BrowseIntervalMs", 30000);
            
            _factory = new ConnectionFactory(_conStr);
            Logger.Information("[{Dev}] Initializing ActiveMQ Browser Fusion.Element. Broker: {BrokerUrl}", Config.Name, _conStr);
            
            // Explicitly ensure heartbeat is disabled
            NeedsHeartbeat = false;
        }

        protected override async Task<bool> ConnectAsync(CancellationToken token)
        {
            try
            {
                Logger.Information("[{Dev}] Attempting to connect to ActiveMQ for browsing...", Config.Name);
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

        protected override async Task ElementConnectedAsync()
        {
            Logger.Information("[{Dev}] Starting periodic queue browsing every {Interval}ms.", Config.Name, _browseIntervalMs);
            RegisterTask(BrowseQueuesLoopAsync(ConnectionToken));
            await base.ElementConnectedAsync();
        }

        protected override Task InitPeriodicEvent()
        {
            // Heartbeat check is disabled, but we use ElementConnectedAsync to start our custom loop
            return Task.CompletedTask;
        }

        private async Task BrowseQueuesLoopAsync(CancellationToken ct)
        {
            // The DestinationSource helper class is not present in NMS 2.2.0.
            // We implement discovery by monitoring Advisory Topics and querying Broker Statistics.
            
            try
            {
                // Subscribe to Queue Advisory Topic to catch new queues
                using var advisorySession = await _connection!.CreateSessionAsync();
                var queueAdvisoryTopic = await advisorySession.GetTopicAsync("ActiveMQ.Advisory.Queue");
                using var advisoryConsumer = await advisorySession.CreateConsumerAsync(queueAdvisoryTopic);
                advisoryConsumer.Listener += (msg) =>
                {
                    Logger.Information("[{Dev}] ADVISORY: Queue activity detected on broker.", Config.Name);
                };

                while (!ct.IsCancellationRequested)
                {
                    if (Machine.State == State.Connected && _connection != null)
                    {
                        using var session = await _connection.CreateSessionAsync();
                        
                        // Query Broker Statistics for a list of all destinations
                        var replyTo = await session.CreateTemporaryQueueAsync();
                        using var consumer = await session.CreateConsumerAsync(replyTo);
                        var adminQueue = await session.GetQueueAsync("ActiveMQ.Statistics.Broker");
                        using var producer = await session.CreateProducerAsync(adminQueue);
                        
                        var request = await producer.CreateMessageAsync();
                        request.NMSReplyTo = replyTo;
                        await producer.SendAsync(request);
                        
                        // Wait for a snapshot of current destinations
                        var reply = await consumer.ReceiveAsync(TimeSpan.FromSeconds(5));
                        if (reply is IMapMessage mapMsg)
                        {
                            Logger.Information("[{Dev}] --- ActiveMQ Broker Queue Discovery ---", Config.Name);
                            var foundQueues = false;
                            foreach (var key in mapMsg.Body.Keys.Cast<string>())
                            {
                                if (key.Contains("Queue", StringComparison.OrdinalIgnoreCase) || key.Equals("destinations", StringComparison.OrdinalIgnoreCase))
                                {
                                    Logger.Information("[{Dev}] Discovered: {Key} = {Value}", Config.Name, key, mapMsg.Body[key]);
                                    foundQueues = true;
                                }
                            }
                            if (!foundQueues) Logger.Information("[{Dev}] No active queues reported in broker statistics.", Config.Name);
                        }
                        else
                        {
                            Logger.Warning("[{Dev}] No statistics reply received. (Queue discovery requires statistics plugin enabled on broker)", Config.Name);
                        }

                        Tracker.HeartBeat();
                    }

                    await Task.Delay(_browseIntervalMs, ct);
                }
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "[{Dev}] Error in queue discovery loop: {Msg}", Config.Name, ex.Message);
            }
        }

        public override Task SendAsync(string message, CancellationToken token, bool fireEvent = true)
        {
            Logger.Warning("[{Dev}] SendAsync not supported for Browser Fusion.Element.", Config.Name);
            return Task.CompletedTask;
        }

        public override Task SendHeartbeatAsync(CancellationToken token)
        {
            // Heartbeat check is disabled in constructor (NeedsHeartbeat = false)
            return Task.CompletedTask;
        }

        protected override void OnElementFaultedAsync(CancellationToken token = default)
        {
            Logger.Error("[{Dev}] Element faulted. Reconnecting...", Config.Name);
        }

        protected override async Task OnElementStoppingAsync()
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
