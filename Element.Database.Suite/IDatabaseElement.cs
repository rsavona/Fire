using System.Data;
using Fusion.Common.Contracts;

namespace Device.Database.Suite;

/// <summary>
/// Common interface for database devices.
/// </summary>
public interface IDatabaseElement : IElement
{
    Task InitializeDatabaseAsync();
    Task<IEnumerable<T>> QueryAsync<T>(string sql, object? parameters = null, CommandType commandType = CommandType.Text, bool track = true);
    Task<int> ExecuteAsync(string sql, object? parameters = null, CommandType commandType = CommandType.Text, bool track = true);
}
