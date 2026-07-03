using backend.Common.Models;
using backend.Repository;

namespace backend.Services;

/// <summary>
/// 日志清理服务 - 定时清理过期日志
/// </summary>
public class LogCleanupService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<LogCleanupService> _logger;

    public LogCleanupService(IServiceScopeFactory scopeFactory, ILogger<LogCleanupService> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromHours(1), stoppingToken);
                await CleanupOldLogs();
            }
            catch (TaskCanceledException) { break; }
            catch (Exception ex)
            {
                _logger.LogError(ex, "日志清理失败");
            }
        }
    }

    private async Task CleanupOldLogs()
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IFreeSql>();

        // 从全局配置读取留存天数，默认30天
        var configRepo = new BaseRepository<GlobalConfig>(db);
        var config = await configRepo.FirstOrDefaultAsync(c => c.Key == "log_retention_days");
        var days = config != null ? int.Parse(config.Value) : 30;

        var cutoff = DateTime.UtcNow.AddDays(-days);

        // 清理调用日志
        var callLogRepo = new BaseRepository<CallLog>(db);
        await callLogRepo.DeleteAsync(l => l.CreatedAt < cutoff);

        // 清理操作日志
        var opLogRepo = new BaseRepository<OperationLog>(db);
        await opLogRepo.DeleteAsync(l => l.CreatedAt < cutoff);

        _logger.LogInformation("日志清理完成: 删除 {Cutoff} 之前的日志", cutoff);
    }
}
