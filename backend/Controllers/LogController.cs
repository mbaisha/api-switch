using System.Text;
using backend.Common.DTOs;
using backend.Common.Models;
using backend.Repository;
using FreeSql;
using Microsoft.AspNetCore.Mvc;

namespace backend.Controllers;

/// <summary>
/// 日志查询与导出
/// </summary>
[ApiController]
[Route("api/admin/logs")]
public class LogController : ControllerBase
{
    private readonly IFreeSql _db;

    public LogController(IFreeSql db)
    {
        _db = db;
    }

    /// <summary>查询调用日志(分页+筛选)</summary>
    [HttpGet("calls")]
    public async Task<ApiResult<PageResult<CallLog>>> GetCallLogs(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        [FromQuery] string? token = null,
        [FromQuery] string? model = null,
        [FromQuery] string? status = null,
        [FromQuery] string? channel = null,
        [FromQuery] DateTime? startTime = null,
        [FromQuery] DateTime? endTime = null)
    {
        var select = _db.Select<CallLog>()
            .WhereIf(!string.IsNullOrEmpty(token), l => l.TokenValue != null && l.TokenValue.Contains(token))
            .WhereIf(!string.IsNullOrEmpty(model), l => l.CustomModelId != null && l.CustomModelId.Contains(model))
            .WhereIf(!string.IsNullOrEmpty(status), l => l.Status == status)
            .WhereIf(!string.IsNullOrEmpty(channel), l => l.ChannelName != null && l.ChannelName.Contains(channel))
            .WhereIf(startTime != null, l => l.CreatedAt >= startTime.Value)
            .WhereIf(endTime != null, l => l.CreatedAt <= endTime.Value);

        var total = await select.CountAsync();
        var list = await select.OrderByDescending(l => l.CreatedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync();

        return ApiResult<PageResult<CallLog>>.Success(new PageResult<CallLog>
        {
            Total = total, Page = page, PageSize = pageSize, List = list
        });
    }

    /// <summary>导出调用日志为CSV</summary>
    [HttpGet("calls/export")]
    public async Task<IActionResult> ExportCallLogs(
        [FromQuery] DateTime? startTime = null,
        [FromQuery] DateTime? endTime = null,
        [FromQuery] string? status = null)
    {
        var select = _db.Select<CallLog>()
            .WhereIf(startTime != null, l => l.CreatedAt >= startTime.Value)
            .WhereIf(endTime != null, l => l.CreatedAt <= endTime.Value)
            .WhereIf(!string.IsNullOrEmpty(status), l => l.Status == status);

        var all = await select.OrderByDescending(l => l.CreatedAt).ToListAsync();

        var sb = new StringBuilder();
        sb.AppendLine("ID,令牌,客户端IP,自定义模型,原始模型,渠道,状态,流式,输入Token,输出Token,耗时(ms),错误信息,时间");
        foreach (var log in all)
        {
            sb.AppendLine($"{log.Id},{EscapeCsv(log.TokenValue)},{log.ClientIp}," +
                $"{EscapeCsv(log.CustomModelId)},{EscapeCsv(log.OriginalModelId)}," +
                $"{EscapeCsv(log.ChannelName)},{log.Status},{log.IsStream}," +
                $"{log.InputTokens},{log.OutputTokens},{log.DurationMs}," +
                $"{EscapeCsv(log.ErrorMessage)},{log.CreatedAt:yyyy-MM-dd HH:mm:ss}");
        }

        var bytes = Encoding.UTF8.GetBytes(sb.ToString());
        return File(bytes, "text/csv", $"call_logs_{DateTime.Now:yyyyMMdd}.csv");
    }

    /// <summary>查询操作日志</summary>
    [HttpGet("operations")]
    public async Task<ApiResult<PageResult<OperationLog>>> GetOperationLogs(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        [FromQuery] string? action = null,
        [FromQuery] DateTime? startTime = null,
        [FromQuery] DateTime? endTime = null)
    {
        var select = _db.Select<OperationLog>()
            .WhereIf(!string.IsNullOrEmpty(action), l => l.Action == action)
            .WhereIf(startTime != null, l => l.CreatedAt >= startTime.Value)
            .WhereIf(endTime != null, l => l.CreatedAt <= endTime.Value);

        var total = await select.CountAsync();
        var list = await select.OrderByDescending(l => l.CreatedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync();

        return ApiResult<PageResult<OperationLog>>.Success(new PageResult<OperationLog>
        {
            Total = total, Page = page, PageSize = pageSize, List = list
        });
    }

    /// <summary>导出操作日志为CSV</summary>
    [HttpGet("operations/export")]
    public async Task<IActionResult> ExportOperationLogs(
        [FromQuery] DateTime? startTime = null,
        [FromQuery] DateTime? endTime = null)
    {
        var select = _db.Select<OperationLog>()
            .WhereIf(startTime != null, l => l.CreatedAt >= startTime.Value)
            .WhereIf(endTime != null, l => l.CreatedAt <= endTime.Value);

        var all = await select.OrderByDescending(l => l.CreatedAt).ToListAsync();

        var sb = new StringBuilder();
        sb.AppendLine("ID,操作人,操作,目标,内容,IP,时间");
        foreach (var log in all)
        {
            sb.AppendLine($"{log.Id},{EscapeCsv(log.Operator)},{log.Action}," +
                $"{log.Target},{EscapeCsv(log.Content)},{log.ClientIp}," +
                $"{log.CreatedAt:yyyy-MM-dd HH:mm:ss}");
        }

        var bytes = Encoding.UTF8.GetBytes(sb.ToString());
        return File(bytes, "text/csv", $"operation_logs_{DateTime.Now:yyyyMMdd}.csv");
    }

    private static string EscapeCsv(string? value)
    {
        if (string.IsNullOrEmpty(value)) return "";
        if (value.Contains(',') || value.Contains('"') || value.Contains('\n'))
            return $"\"{value.Replace("\"", "\"\"")}\"";
        return value;
    }
}
