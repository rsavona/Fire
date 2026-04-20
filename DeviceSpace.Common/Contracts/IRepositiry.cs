namespace DeviceSpace.Common.Contracts;

public interface IRepository<TEntity> 
{
    // The contract: Anyone using this interface must provide code to do these 5 things.
    Task<IEnumerable<TEntity>> GetAllAsync();
    Task<TEntity> GetByIdAsync(int id);
    Task<int> CreateAsync(TEntity entity); 
    Task<bool> UpdateAsync(TEntity entity);
    Task<bool> DeleteAsync(int id);
}

public interface IEntity
{
    int Id { get; set; }
}

