using backend.Common.Models;
using backend.Common.Utils;
using backend.Repository;

namespace backend.Services;

/// <summary>
/// 令牌服务 - 令牌校验和用量管理（带 Redis 缓存）
/// </summary>
public class TokenService
{
    private readonly BaseRepository<Token> _tokenRepo;
    private readonly BaseRepository<TokenModel> _tokenModelRepo;
    private readonly RedisCacheService _cache;
    private static readonly TimeSpan TokenCacheTtl = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan PermissionCacheTtl = TimeSpan.FromSeconds(60);

    public TokenService(
        BaseRepository<Token> tokenRepo,
        BaseRepository<TokenModel> tokenModelRepo,
        RedisCacheService cache)
    {
        _tokenRepo = tokenRepo;
        _tokenModelRepo = tokenModelRepo;
        _cache = cache;
    }

    /// <summary>
    /// 验证令牌有效性（带 Redis 缓存，减少 DB 查询）
    /// </summary>
    public async Task<(bool Valid, Token? Token, string Message)> ValidateToken(string tokenValue)
    {
        // 1. 尝试从缓存加载令牌
        var cacheKey = $"token:{tokenValue}";
        var token = await _cache.GetAsync<Token>(cacheKey);

        if (token != null)
        {
            // 缓存命中，但用量/余额等动态字段需从 DB 刷新
            var freshToken = await _tokenRepo.FirstOrDefaultAsync(t => t.TokenValue == tokenValue);
            if (freshToken == null)
            {
                await _cache.RemoveAsync(cacheKey);
                return (false, null, "无效的令牌");
            }

            // 用缓存中的静态字段 + DB 中的动态字段
            freshToken.RateLimitWindow = token.RateLimitWindow;
            freshToken.RateLimitCount = token.RateLimitCount;
            token = freshToken;
        }
        else
        {
            // 缓存未命中，查询 DB
            token = await _tokenRepo.FirstOrDefaultAsync(t => t.TokenValue == tokenValue);
            if (token == null)
                return (false, null, "无效的令牌");

            // 缓存令牌（不含动态字段的轻量拷贝），用于限流配置查询
            await _cache.SetAsync(cacheKey, new TokenCacheEntry
            {
                Id = token.Id,
                Enabled = token.Enabled,
                TotalTokenLimit = token.TotalTokenLimit,
                RateLimitWindow = token.RateLimitWindow,
                RateLimitCount = token.RateLimitCount
            }, TokenCacheTtl);
        }

        if (!token.Enabled)
            return (false, null, "令牌已被禁用");

        // 检查总限额
        if (token.TotalTokenLimit > 0 && token.UsedTokens >= token.TotalTokenLimit)
            return (false, null, "令牌总用量已超限");

        // 检查日限额（基于 Redis 每日计数器）
        if (token.DailyTokenLimit > 0)
        {
            var dailyKey = $"daily_usage:{token.Id}:{DateTime.UtcNow:yyyyMMdd}";
            var dailyUsedStr = await _cache.GetStringAsync(dailyKey);
            if (dailyUsedStr != null && long.TryParse(dailyUsedStr, out var dailyUsed) && dailyUsed >= token.DailyTokenLimit)
                return (false, null, "令牌日用量已超限");
        }

        // 检查余额
        if (token.RemainingBalance <= 0 && token.TotalTokenLimit > 0)
            return (false, null, "令牌余额不足");

        return (true, token, "ok");
    }

    /// <summary>
    /// 检查模型权限（带 Redis 缓存）
    /// </summary>
    public async Task<bool> CheckModelPermission(long tokenId, string customModelId)
    {
        var cacheKey = $"token:permission:{tokenId}:{customModelId}";
        var cached = await _cache.GetStringAsync(cacheKey);
        if (cached != null)
            return cached == "1";

        var allowedModels = await _tokenModelRepo.GetListAsync(m => m.TokenId == tokenId);
        var hasPermission = allowedModels.Count == 0 ||
                          allowedModels.Any(m => m.CustomModelId == customModelId);

        await _cache.SetStringAsync(cacheKey, hasPermission ? "1" : "0", PermissionCacheTtl);
        return hasPermission;
    }

    /// <summary>
    /// 记录令牌用量（同时更新 Redis 每日计数器）
    /// </summary>
    public async Task RecordUsage(long tokenId, int inputTokens, int outputTokens)
    {
        var token = await _tokenRepo.GetByIdAsync(tokenId);
        if (token == null) return;

        token.UsedTokens += inputTokens + outputTokens;
        token.TotalCalls++;
        token.UpdatedAt = DateTime.UtcNow;
        await _tokenRepo.UpdateAsync(token);

        // 更新 Redis 每日计数器（第二天自动过期）
        if (token.DailyTokenLimit > 0)
        {
            var dailyKey = $"daily_usage:{tokenId}:{DateTime.UtcNow:yyyyMMdd}";
            var dailyTotal = await _cache.IncrementByAsync(dailyKey, inputTokens + outputTokens);
            if (dailyTotal == inputTokens + outputTokens)
            {
                // 首次创建，设置过期时间为明天午夜
                var now = DateTime.UtcNow;
                var tomorrow = now.Date.AddDays(1);
                await _cache.ExpireAtAsync(dailyKey, tomorrow);
            }
        }
    }
}

/// <summary>
/// 令牌缓存条目（轻量级，仅缓存不常变化的字段）
/// </summary>
internal class TokenCacheEntry
{
    public long Id { get; set; }
    public bool Enabled { get; set; }
    public long TotalTokenLimit { get; set; }
    public int RateLimitWindow { get; set; }
    public int RateLimitCount { get; set; }
}
