using System.Linq.Expressions;
using backend.Common.DTOs;
using backend.Common.Models;
using backend.Repository;
using Microsoft.AspNetCore.Mvc;

namespace backend.Controllers;

/// <summary>
/// 数据看板
/// </summary>
[ApiController]
[Route("api/admin/dashboard")]
public class DashboardController : ControllerBase
{
    private readonly BaseRepository<CallLog> _callLogRepo;
    private readonly BaseRepository<Token> _tokenRepo;
    private readonly BaseRepository<Channel> _channelRepo;
    private readonly BaseRepository<ApiKey> _apiKeyRepo;
    private readonly BaseRepository<BillingRecord> _billingRepo;
    private readonly IFreeSql _db;

    public DashboardController(
        BaseRepository<CallLog> callLogRepo,
        BaseRepository<Token> tokenRepo,
        BaseRepository<Channel> channelRepo,
        BaseRepository<ApiKey> apiKeyRepo,
        BaseRepository<BillingRecord> billingRepo,
        IFreeSql db)
    {
        _callLogRepo = callLogRepo;
        _tokenRepo = tokenRepo;
        _channelRepo = channelRepo;
        _apiKeyRepo = apiKeyRepo;
        _billingRepo = billingRepo;
        _db = db;
    }

    /// <summary>
    /// 看板总览 —— 全部用 SQL 聚合下推数据库，避免全表加载到内存（修复慢/卡死问题）
    /// </summary>
    [HttpGet]
    public async Task<ApiResult<object>> GetDashboard()
    {
        var today = DateTime.UtcNow.Date;
        var todayEnd = today.AddDays(1);

        // 今日调用数 / 成功 / 失败（SQL Count 下推）
        var todayCallsCount = await _db.Select<CallLog>().Where(a => a.CreatedAt >= today && a.CreatedAt < todayEnd).CountAsync();
        var todaySuccess = await _db.Select<CallLog>().Where(a => a.CreatedAt >= today && a.CreatedAt < todayEnd && a.Status == "Success").CountAsync();
        var todayFailed = await _db.Select<CallLog>().Where(a => a.CreatedAt >= today && a.CreatedAt < todayEnd && a.Status == "Failed").CountAsync();
        var successRate = todayCallsCount > 0 ? Math.Round((double)todaySuccess / todayCallsCount * 100, 1) : 0.0;

        // 总调用数（SQL Count，走索引）
        var totalCalls = await _db.Select<CallLog>().CountAsync();

        // Token 汇总（SQL Sum 下推，不再全表加载）
        var totalInputTokens = await SafeSumAsync<CallLog>(a => a.InputTokens);
        var totalOutputTokens = await SafeSumAsync<CallLog>(a => a.OutputTokens);
        var todayInputTokens = await SafeSumAsync<CallLog>(a => a.InputTokens, a => a.CreatedAt >= today && a.CreatedAt < todayEnd);
        var todayOutputTokens = await SafeSumAsync<CallLog>(a => a.OutputTokens, a => a.CreatedAt >= today && a.CreatedAt < todayEnd);

        // 今日消费（SQL Sum 下推）
        var todayCost = await SafeSumAsync<BillingRecord>(b => b.Cost, b => b.CreatedAt >= today && b.CreatedAt < todayEnd);

        // 模型用量排行 Top 5（SQL GroupBy 下推，只取 Top，不全表内存聚合）
        var modelUsageRaw = await _db.Select<CallLog>()
            .GroupBy(a => a.CustomModelId)
            .OrderByDescending(a => a.Count())
            .ToListAsync(a => new
            {
                model = a.Key ?? "unknown",
                count = a.Count(),
                tokens = a.Sum(a.Value.InputTokens + a.Value.OutputTokens)
            });
        var modelUsage = modelUsageRaw.OrderByDescending(g => g.count).Take(5).ToList();

        // 令牌用量排行 Top 5
        var tokenUsageRaw = await _db.Select<CallLog>()
            .GroupBy(a => a.TokenValue)
            .OrderByDescending(a => a.Count())
            .ToListAsync(a => new
            {
                tokenRaw = a.Key ?? "unknown",
                count = a.Count()
            });
        var tokenUsage = tokenUsageRaw.OrderByDescending(g => g.count).Take(5)
            .Select(g => new { token = MaskToken(g.tokenRaw), g.count }).ToList();

        return ApiResult<object>.Success(new
        {
            totalCalls,
            todayCalls = todayCallsCount,
            todaySuccess,
            todayFailed,
            successRate,
            totalInputTokens,
            totalOutputTokens,
            todayInputTokens,
            todayOutputTokens,
            todayCost = Math.Round(todayCost, 4),
            tokenCount = await _tokenRepo.CountAsync(),
            channelCount = await _channelRepo.CountAsync(l => l.Enabled),
            modelUsage,
            tokenUsage
        });
    }

    /// <summary>
    /// API Key 用量（按渠道+密钥）—— 复用 ApiKey 已有的 TotalCalls/SuccessCalls/UsedTokens 字段，
    /// 直接 SQL 查询，无需聚合 CallLog 全表。修复"看不到每个 key/模型的调用数量"问题。
    /// </summary>
    [HttpGet("upstream-keys")]
    public async Task<ApiResult<object>> GetUpstreamKeyUsage()
    {
        var channels = await _db.Select<Channel>().ToListAsync();
        var apiKeys = await _db.Select<ApiKey>().ToListAsync();

        var channelStats = channels.Select(c =>
        {
            var keys = apiKeys.Where(k => k.ChannelId == c.Id).ToList();
            return new
            {
                channelId = c.Id,
                channelName = c.Name,
                supplierType = c.SupplierType,
                totalKeys = keys.Count,
                totalTokens = keys.Sum(k => k.UsedTokens),
                totalCalls = keys.Sum(k => k.TotalCalls),
                successCalls = keys.Sum(k => k.SuccessCalls),
                keys = keys.Select(k => new
                {
                    k.Id,
                    keyValue = MaskKey(k.KeyValue),
                    k.UsedTokens,
                    k.TotalCalls,
                    k.SuccessCalls,
                    k.Status,
                    k.Weight
                })
            };
        }).ToList();

        return ApiResult<object>.Success(new
        {
            totalChannels = channels.Count,
            totalApiKeys = apiKeys.Count,
            totalUpstreamTokens = apiKeys.Sum(k => k.UsedTokens),
            totalUpstreamCalls = apiKeys.Sum(k => k.TotalCalls),
            channels = channelStats
        });
    }

    /// <summary>
    /// 模型用量明细（按自定义模型ID + 上游原始模型ID 双维度）
    /// 直接对 CallLog 做 SQL GroupBy，下推数据库聚合，不全表加载。
    /// </summary>
    [HttpGet("model-usage")]
    public async Task<ApiResult<object>> GetModelUsageDetail()
    {
        // 按自定义模型ID 聚合（用户侧视角）
        var byCustom = await _db.Select<CallLog>()
            .GroupBy(a => a.CustomModelId)
            .OrderByDescending(a => a.Count())
            .ToListAsync(a => new
            {
                model = a.Key ?? "unknown",
                calls = a.Count(),
                success = a.Count(a.Value.Status == "Success"),
                failed = a.Count(a.Value.Status == "Failed"),
                inputTokens = a.Sum(a.Value.InputTokens),
                outputTokens = a.Sum(a.Value.OutputTokens),
                totalTokens = a.Sum(a.Value.InputTokens + a.Value.OutputTokens)
            });

        // 按上游原始模型ID 聚合（上游侧视角，可看每个上游模型被调多少次）
        var byOriginal = await _db.Select<CallLog>()
            .GroupBy(a => new { a.CustomModelId, a.OriginalModelId, a.ChannelName })
            .OrderByDescending(a => a.Count())
            .ToListAsync(a => new
            {
                customModel = a.Key.CustomModelId ?? "unknown",
                originalModel = a.Key.OriginalModelId ?? "unknown",
                channel = a.Key.ChannelName ?? "unknown",
                calls = a.Count(),
                success = a.Count(a.Value.Status == "Success"),
                failed = a.Count(a.Value.Status == "Failed"),
                totalTokens = a.Sum(a.Value.InputTokens + a.Value.OutputTokens)
            });

        return ApiResult<object>.Success(new
        {
            byCustom = byCustom.ToList(),
            byOriginal = byOriginal.ToList()
        });
    }

    /// <summary>安全 Sum：空表时 SumAsync 可能返回 null/异常，统一兜底为 0</summary>
    private async Task<decimal> SafeSumAsync<T>(Expression<Func<T, decimal>> field, Expression<Func<T, bool>>? where = null) where T : class
    {
        try
        {
            var query = _db.Select<T>();
            if (where != null) query = query.Where(where);
            return await query.SumAsync(field);
        }
        catch { return 0m; }
    }

    /// <summary>安全 Sum(int)：CallLog 的 Token 字段是 int</summary>
    private async Task<long> SafeSumAsync<T>(Expression<Func<T, int>> field, Expression<Func<T, bool>>? where = null) where T : class
    {
        try
        {
            var query = _db.Select<T>();
            if (where != null) query = query.Where(where);
            return Convert.ToInt64(await query.SumAsync(field));
        }
        catch { return 0L; }
    }

    private static string MaskToken(string token)
    {
        if (string.IsNullOrEmpty(token)) return "";
        if (token.Length > 10) return token[..5] + "****" + token[^5..];
        return token;
    }

    private static string MaskKey(string key)
    {
        if (string.IsNullOrEmpty(key)) return "";
        if (key.Length > 12) return key[..6] + "****" + key[^6..];
        return key;
    }
}
