using System.Data;
using Fusion.Common.Contracts;

namespace Fusion.Element.Database.Suite;

/// <summary>
/// Common interface for database elements.
/// </summary>
public interface IDatabaseElement : IElement
{
    Task InitializeDatabaseAsync();
    Task<IEnumerable<T>> QueryAsync<T>(string sql, object? parameters = null, CommandType commandType = CommandType.Text, bool track = true);
    Task<int> ExecuteAsync(string sql, object? parameters = null, CommandType commandType = CommandType.Text, bool track = true);
}
