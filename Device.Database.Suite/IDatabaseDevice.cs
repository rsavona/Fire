using System.Data;
using DeviceSpace.Common.Contracts;

namespace Device.Database.Suite;

/// <summary>
/// Common interface for database devices.
/// </summary>
public interface IDatabaseDevice : IDevice
{
    Task<IEnumerable<T>> QueryAsync<T>(string sql, object? parameters = null, CommandType commandType = CommandType.Text);
    Task<int> ExecuteAsync(string sql, object? parameters = null, CommandType commandType = CommandType.Text);
}
