using System.Data;
using DeviceSpace.Common.BaseClasses;
using DeviceSpace.Common.Configurations;
using DeviceSpace.Common.Contracts;
using DeviceSpace.Common.Enums;
using Serilog.Core;

namespace Device.Database.Suite;

public abstract class DatabaseDeviceBase : ClientDeviceBase, IDatabaseDevice
{
    protected string ConnectionString { get; } = string.Empty;

    protected DatabaseDeviceBase(IDeviceConfig config, IFireLogger logger) 
        : base(config, logger, new LoggingLevelSwitch(), needsHb: true)
    {
        ConnectionString = ConfigurationLoader.GetOptionalConfig(config.Properties, "ConnectionString", string.Empty);
    }

    public abstract Task<IEnumerable<T>> QueryAsync<T>(string sql, object? parameters = null, CommandType commandType = CommandType.Text);
    public abstract Task<int> ExecuteAsync(string sql, object? parameters = null, CommandType commandType = CommandType.Text);

    // ClientDeviceBase Requirements
    public override Task SendAsync(string message, CancellationToken token, bool fireEvent = true)
    {
        throw new NotSupportedException("Use ExecuteAsync or QueryAsync for database operations.");
    }

    protected override void OnDeviceFaultedAsync(CancellationToken token = default)
    {
        Logger.Error("[{Device}] Database connection faulted.", Config.Name);
    }

    protected override Task<bool> ConnectAsync(CancellationToken ct = default)
    {
        return TryConnectAsync(ct);
    }

    protected abstract Task<bool> TryConnectAsync(CancellationToken ct);
}
