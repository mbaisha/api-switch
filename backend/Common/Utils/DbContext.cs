using FreeSql;

namespace backend.Common.Utils;

/// <summary>
/// 数据库上下文工厂
/// </summary>
public class DbContext
{
    public IFreeSql Db { get; }

    public DbContext(string connectionString)
    {
        // 为 Npgsql 10.x 兼容性：强制使用 UTC 时间戳，避免 DateTime.Kind 冲突
        if (!connectionString.Contains("Timezone=", StringComparison.OrdinalIgnoreCase) &&
            !connectionString.Contains("timezone=", StringComparison.OrdinalIgnoreCase))
        {
            connectionString += ";Timezone=UTC";
        }

        Db = new FreeSqlBuilder()
            .UseConnectionString(DataType.PostgreSQL, connectionString)
            .UseAutoSyncStructure(true)  // 自动同步表结构
            .UseMonitorCommand(cmd => Console.WriteLine($"[SQL] {cmd.CommandText}"))
            .Build();
    }
}
