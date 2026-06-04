using System.Data;
using Fusion.Common.BaseClasses;
using Fusion.Common.Configurations;
using Fusion.Common.Contracts;
using Fusion.Common.Enums;
using Serilog.Core;

namespace Fusion.Element.Database.Suite;

public abstract class DatabaseElementBase : ClientElementBase, IDatabaseElement
{
    protected string ConnectionString { get; } = string.Empty;
    public bool Initialize { get; private set; }

    protected DatabaseElementBase(IMessageBus bus, IElementBlueprint config, IFireLogger logger) 
        : base(bus, config, logger, new LoggingLevelSwitch(), needsHb: true)
    {
        ConnectionString = ConfigurationLoader.GetOptionalConfig(config.Properties, "ConnectionString", string.Empty);
        Initialize = ConfigurationLoader.GetOptionalConfig(config.Properties, "Initialize", false);
    }

    public abstract Task InitializeDatabaseAsync();
    public abstract Task<IEnumerable<T>> QueryAsync<T>(string sql, object? parameters = null, CommandType commandType = CommandType.Text, bool track = true);
    public abstract Task<int> ExecuteAsync(string sql, object? parameters = null, CommandType commandType = CommandType.Text, bool track = true);

    // ClientElementBase Requirements
    public override Task SendAsync(string message, CancellationToken token, bool fireEvent = true)
    {
        throw new NotSupportedException("Use ExecuteAsync or QueryAsync for database operations.");
    }

    protected override void OnElementFaultedAsync(CancellationToken token = default)
    {
        Logger.Error("[{Element}] Database connection faulted.", Config.Name);
    }

    protected override Task<bool> ConnectAsync(CancellationToken ct = default)
    {
        return TryConnectAsync(ct);
    }

    protected abstract Task<bool> TryConnectAsync(CancellationToken ct);
}
