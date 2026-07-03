using backend.Common.Models;
using backend.Repository;

namespace backend.Common.Utils;

/// <summary>
/// IP + 令牌双维度限流中间件（Redis 分布式滑动窗口）
/// 支持 per-token 的 IP 限流配置（RateLimitWindow / RateLimitCount）
/// 全局限流从 GlobalConfig 读取（rate_limit_per_minute）
/// </summary>
public class RateLimitMiddleware
{
    private readonly RequestDelegate _next;
    private const int DefaultGlobalIpLimit = 120;
    private const int GlobalWindow = 60;

    public RateLimitMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(
        HttpContext context,
        BaseRepository<Token> tokenRepo,
        BaseRepository<GlobalConfig> configRepo,
        RedisCacheService cache)
    {
        // 仅对转发接口限流
        var path = context.Request.Path.ToString();
        if (!path.StartsWith("/v1/"))
        {
            await _next(context);
            return;
        }

        var clientIp = IpHelper.GetClientIp(context);
        var token = ExtractToken(context);

        // 全局 IP 维度限流（从 GlobalConfig 读取配置，带 Redis 缓存 30s）
        var globalLimit = await GetGlobalRateLimit(configRepo, cache);
        var ipKey = $"ratelimit:ip:{clientIp}";
        if (!await cache.CheckRateLimitAsync(ipKey, globalLimit, GlobalWindow))
        {
            context.Response.StatusCode = 429;
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync(
                "{\"error\":{\"message\":\"请求过于频繁，请稍后重试 (IP限流)\"}}");
            return;
        }

        // Token + IP 维度限流（按 token 自定义配置）
        if (!string.IsNullOrEmpty(token))
        {
            var (windowSec, maxCount) = await GetTokenRateConfig(tokenRepo, cache, token);

            // maxCount > 0 才限流；0 表示该 token 不限流
            if (maxCount > 0)
            {
                var tokenKey = $"ratelimit:token:{token}:ip:{clientIp}";
                if (!await cache.CheckRateLimitAsync(tokenKey, maxCount, windowSec))
                {
                    context.Response.StatusCode = 429;
                    context.Response.ContentType = "application/json";
                    await context.Response.WriteAsync(
                        "{\"error\":{\"message\":\"请求过于频繁，请稍后重试 (令牌IP限流)\"}}");
                    return;
                }
            }
        }

        await _next(context);
    }

    /// <summary>
    /// 从 GlobalConfig 读取全局限流配置（带 Redis 缓存 30s）
    /// </summary>
    private static async Task<int> GetGlobalRateLimit(
        BaseRepository<GlobalConfig> configRepo, RedisCacheService cache)
    {
        var cacheKey = "global:rate_limit_per_minute";
        var cached = await cache.GetStringAsync(cacheKey);
        if (cached != null && int.TryParse(cached, out var limit))
            return limit;

        var config = await configRepo.FirstOrDefaultAsync(c => c.Key == "rate_limit_per_minute");
        limit = config != null && int.TryParse(config.Value, out var v) ? v : DefaultGlobalIpLimit;

        await cache.SetStringAsync(cacheKey, limit.ToString(), TimeSpan.FromSeconds(30));
        return limit;
    }

    /// <summary>
    /// 获取 token 的限流配置（带 Redis 缓存，10s TTL）
    /// </summary>
    private static async Task<(int Window, int Count)> GetTokenRateConfig(
        BaseRepository<Token> tokenRepo, RedisCacheService cache, string tokenValue)
    {
        var cacheKey = $"token:config:{tokenValue}";
        var cached = await cache.GetStringAsync(cacheKey);
        if (cached != null)
        {
            var parts = cached.Split(':');
            if (parts.Length == 2 && int.TryParse(parts[0], out var w) && int.TryParse(parts[1], out var c))
                return (w, c);
        }

        // 查库
        var token = await tokenRepo.FirstOrDefaultAsync(t => t.TokenValue == tokenValue);
        var window = token?.RateLimitWindow > 0 ? token.RateLimitWindow : 60;
        var count = token != null ? token.RateLimitCount : 60;

        await cache.SetStringAsync(cacheKey, $"{window}:{count}", TimeSpan.FromSeconds(10));
        return (window, count);
    }

    private static string? ExtractToken(HttpContext context)
    {
        var authHeader = context.Request.Headers.Authorization.ToString();
        if (string.IsNullOrEmpty(authHeader)) return null;
        return authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
            ? authHeader[7..] : authHeader;
    }
}

public static class RateLimitMiddlewareExtensions
{
    public static IApplicationBuilder UseRateLimit(this IApplicationBuilder builder)
    {
        return builder.UseMiddleware<RateLimitMiddleware>();
    }
}
