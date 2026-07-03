using System.Text.Json;
using StackExchange.Redis;

namespace backend.Common.Utils;

/// <summary>
/// Redis 缓存服务 - 封装 StackExchange.Redis 的常用操作
/// </summary>
public class RedisCacheService
{
    private readonly IDatabase _db;
    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    public RedisCacheService(ConnectionMultiplexer redis)
    {
        _db = redis.GetDatabase();
    }

    /// <summary>获取缓存对象（JSON 反序列化）</summary>
    public async Task<T?> GetAsync<T>(string key) where T : class
    {
        var value = await _db.StringGetAsync(key);
        return value.HasValue ? JsonSerializer.Deserialize<T>((byte[])value!, _jsonOptions) : null;
    }

    /// <summary>设置缓存对象（JSON 序列化）</summary>
    public async Task SetAsync<T>(string key, T value, TimeSpan? expiry = null) where T : class
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(value, _jsonOptions);
        await _db.StringSetAsync(key, json, expiry);
    }

    /// <summary>删除缓存</summary>
    public async Task RemoveAsync(string key) => await _db.KeyDeleteAsync(key);

    /// <summary>获取原始字符串</summary>
    public async Task<string?> GetStringAsync(string key)
    {
        var value = await _db.StringGetAsync(key);
        return value.HasValue ? value.ToString() : null;
    }

    /// <summary>设置原始字符串</summary>
    public async Task SetStringAsync(string key, string value, TimeSpan? expiry = null)
    {
        await _db.StringSetAsync(key, value, expiry);
    }

    /// <summary>原子自增（用于轮询计数器）</summary>
    public async Task<long> IncrementAsync(string key)
    {
        return await _db.StringIncrementAsync(key);
    }

    /// <summary>原子自增指定数量（用于每日用量统计）</summary>
    public async Task<long> IncrementByAsync(string key, long value)
    {
        return await _db.StringIncrementAsync(key, value);
    }

    /// <summary>设置 Key 在指定时间过期</summary>
    public async Task ExpireAtAsync(string key, DateTime expireTime)
    {
        await _db.KeyExpireAsync(key, expireTime);
    }

    /// <summary>检查节点是否在冷却中</summary>
    public async Task<bool> IsInCooldownAsync(string key)
    {
        return await _db.KeyExistsAsync(key);
    }

    /// <summary>设置节点冷却（使用 Redis TTL 自动过期）</summary>
    public async Task SetCooldownAsync(string key, int seconds)
    {
        await _db.StringSetAsync(key, "1", TimeSpan.FromSeconds(seconds));
    }

    /// <summary>
    /// Redis 分布式滑动窗口限流（Lua 脚本原子操作）
    /// 返回 true 表示允许通过，false 表示超过限制
    /// </summary>
    public async Task<bool> CheckRateLimitAsync(string key, int maxRequests, int windowSeconds)
    {
        var script = @"
            local key = KEYS[1]
            local max = tonumber(ARGV[1])
            local window = tonumber(ARGV[2])
            local now = tonumber(ARGV[3])
            local member = ARGV[4]

            redis.call('ZREMRANGEBYSCORE', key, '-inf', now - window * 1000)
            local count = redis.call('ZCARD', key)

            if count >= max then
                return 0
            end

            redis.call('ZADD', key, now, member)
            redis.call('EXPIRE', key, window * 2 + 5)
            return 1
        ";

        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var member = Guid.NewGuid().ToString("N");
        var result = await _db.ScriptEvaluateAsync(script,
            new RedisKey[] { key },
            new RedisValue[] { maxRequests, windowSeconds, now, member });

        return (int)result == 1;
    }
}
