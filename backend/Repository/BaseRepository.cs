using System.Linq.Expressions;
using FreeSql;

namespace backend.Repository;

/// <summary>
/// 通用仓储基类
/// </summary>
public class BaseRepository<T> where T : class
{
    protected readonly IFreeSql _db;

    public BaseRepository(IFreeSql db)
    {
        _db = db;
    }

    public Task<T?> GetByIdAsync(long id)
    {
        return _db.Select<T>().WhereDynamic(new { Id = id }).FirstAsync();
    }

    public Task<List<T>> GetAllAsync() => _db.Select<T>().ToListAsync();

    public Task<List<T>> GetListAsync(Expression<Func<T, bool>> predicate) =>
        _db.Select<T>().Where(predicate).ToListAsync();

    public Task<T?> FirstOrDefaultAsync(Expression<Func<T, bool>> predicate) =>
        _db.Select<T>().Where(predicate).FirstAsync();

    public Task<long> CountAsync(Expression<Func<T, bool>>? predicate = null) =>
        predicate == null ? _db.Select<T>().CountAsync() : _db.Select<T>().Where(predicate).CountAsync();

    public Task<List<T>> GetPagedAsync(int page, int pageSize, Expression<Func<T, bool>>? predicate = null)
    {
        var query = _db.Select<T>();
        if (predicate != null) query = query.Where(predicate);
        return query.Skip((page - 1) * pageSize).Take(pageSize).ToListAsync();
    }

    public async Task<T> InsertAsync(T entity)
    {
        var id = await _db.Insert(entity).ExecuteIdentityAsync();
        // 回填自增 ID，确保后续逻辑能拿到正确的 Id
        var idProp = typeof(T).GetProperty("Id");
        if (idProp != null && idProp.CanWrite)
            idProp.SetValue(entity, Convert.ChangeType(id, idProp.PropertyType));
        return entity;
    }

    public Task<int> UpdateAsync(T entity) => _db.Update<T>().SetSource(entity).ExecuteAffrowsAsync();

    public Task<int> DeleteAsync(long id) => _db.Delete<T>(id).ExecuteAffrowsAsync();

    public Task<int> DeleteAsync(Expression<Func<T, bool>> predicate) =>
        _db.Delete<T>().Where(predicate).ExecuteAffrowsAsync();
}
