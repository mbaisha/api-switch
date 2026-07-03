using backend.Common.DTOs;
using backend.Common.Models;
using backend.Repository;
using FreeSql;
using Microsoft.AspNetCore.Mvc;

namespace backend.Controllers;

/// <summary>
/// 用量统计 - 上游/下游实体令牌使用统计
/// </summary>
[ApiController]
[Route("api/admin/stats")]
public class StatsController : ControllerBase
{
    private readonly IFreeSql _db;

    public StatsController(IFreeSql db)
    {
        _db = db;
    }

    /// <summary>上游统计：查看所有 API Key 的使用情况（按渠道分组）</summary>
    [HttpGet("upstream")]
    public async Task<ApiResult<object>> GetUpstreamStats()
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

    /// <summary>下游统计：查看所有下游令牌的使用情况</summary>
    [HttpGet("downstream")]
    public async Task<ApiResult<object>> GetDownstreamStats()
    {
        var tokens = await _db.Select<Token>().ToListAsync();

        var tokenStats = tokens.Select(t => new
        {
            t.Id,
            tokenValue = MaskToken(t.TokenValue),
            t.Remark,
            t.UsedTokens,
            t.TotalCalls,
            t.SuccessCalls,
            t.FailedCalls,
            t.RemainingBalance,
            t.Enabled,
            t.DailyTokenLimit,
            t.TotalTokenLimit
        }).ToList();

        return ApiResult<object>.Success(new
        {
            totalTokens = tokens.Count,
            totalDownstreamTokens = tokens.Sum(t => t.UsedTokens),
            totalDownstreamCalls = tokens.Sum(t => t.TotalCalls),
            tokens = tokenStats
        });
    }

    private static string MaskToken(string token)
    {
        if (token.Length > 10)
            return token[..5] + "****" + token[^5..];
        return token;
    }

    private static string MaskKey(string key)
    {
        if (key.Length > 12)
            return key[..6] + "****" + key[^6..];
        return key;
    }
}