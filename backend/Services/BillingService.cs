using backend.Common.Models;
using backend.Common.Utils;
using backend.Repository;
using FreeSql;

namespace backend.Services;

/// <summary>
/// 计费服务（带 Redis 缓存计费规则）
/// </summary>
public class BillingService
{
    private readonly BaseRepository<BillingRecord> _recordRepo;
    private readonly BaseRepository<BillingRule> _ruleRepo;
    private readonly BaseRepository<Token> _tokenRepo;
    private readonly RedisCacheService _cache;
    private static readonly TimeSpan RulesCacheTtl = TimeSpan.FromSeconds(60);

    public BillingService(
        BaseRepository<BillingRecord> recordRepo,
        BaseRepository<BillingRule> ruleRepo,
        BaseRepository<Token> tokenRepo,
        RedisCacheService cache)
    {
        _recordRepo = recordRepo;
        _ruleRepo = ruleRepo;
        _tokenRepo = tokenRepo;
        _cache = cache;
    }

    /// <summary>
    /// 记录计费（实时扣减余额，计费规则带缓存）
    /// </summary>
    public async Task RecordBilling(long tokenId, string tokenValue, string customModelId,
        int inputTokens, int outputTokens)
    {
        // 查找定价规则（带缓存）
        var rule = await GetCachedRuleAsync(tokenId, customModelId);

        // 默认定价：输入0.01元/千Token，输出0.03元/千Token
        var inputPrice = rule?.InputPrice ?? 0.01m;
        var outputPrice = rule?.OutputPrice ?? 0.03m;

        var cost = (inputTokens / 1000m) * inputPrice + (outputTokens / 1000m) * outputPrice;

        // 写入账单
        await _recordRepo.InsertAsync(new BillingRecord
        {
            TokenId = tokenId,
            TokenValue = tokenValue,
            CustomModelId = customModelId,
            InputTokens = inputTokens,
            OutputTokens = outputTokens,
            Cost = cost,
            InputPrice = inputPrice,
            OutputPrice = outputPrice,
            CreatedAt = DateTime.UtcNow
        });

        // 扣减余额
        var token = await _tokenRepo.GetByIdAsync(tokenId);
        if (token != null)
        {
            token.RemainingBalance -= cost;
            token.UpdatedAt = DateTime.UtcNow;
            await _tokenRepo.UpdateAsync(token);
        }
    }

    /// <summary>
    /// 获取缓存的计费规则
    /// </summary>
    private async Task<BillingRule?> GetCachedRuleAsync(long tokenId, string customModelId)
    {
        var cacheKey = $"billing:rule:{tokenId}:{customModelId}";
        var cached = await _cache.GetAsync<BillingRule>(cacheKey);
        if (cached != null)
            return cached;

        var rule = await _ruleRepo.FirstOrDefaultAsync(r =>
            r.CustomModelId == customModelId && r.Enabled &&
            (r.TokenId == 0 || r.TokenId == tokenId));

        if (rule != null)
            await _cache.SetAsync(cacheKey, rule, RulesCacheTtl);

        return rule;
    }

    /// <summary>
    /// 获取账单统计（SQL 聚合查询，不加载全表）
    /// </summary>
    public async Task<object> GetBillSummary(long? tokenId, DateTime? start, DateTime? end, IFreeSql db)
    {
        var select = db.Select<BillingRecord>()
            .WhereIf(tokenId > 0, r => r.TokenId == tokenId.Value)
            .WhereIf(start != null, r => r.CreatedAt >= start.Value)
            .WhereIf(end != null, r => r.CreatedAt <= end.Value);

        return new
        {
            totalCost = await select.SumAsync(r => r.Cost),
            totalInputTokens = (int)(await select.SumAsync(r => (long)r.InputTokens)),
            totalOutputTokens = (int)(await select.SumAsync(r => (long)r.OutputTokens)),
            totalRecords = await select.CountAsync()
        };
    }

    // ===== 定价规则 CRUD（更新/删除时清除相关缓存） =====

    public Task<List<BillingRule>> GetRulesAsync() => _ruleRepo.GetAllAsync();

    public async Task<BillingRule> CreateRuleAsync(BillingRule rule)
    {
        rule.CreatedAt = DateTime.UtcNow;
        rule.UpdatedAt = DateTime.UtcNow;
        return await _ruleRepo.InsertAsync(rule);
    }

    public async Task UpdateRuleAsync(long id, BillingRule rule)
    {
        var existing = await _ruleRepo.GetByIdAsync(id);
        if (existing == null) throw new Exception("规则不存在");

        existing.CustomModelId = rule.CustomModelId;
        existing.TokenId = rule.TokenId;
        existing.InputPrice = rule.InputPrice;
        existing.OutputPrice = rule.OutputPrice;
        existing.Enabled = rule.Enabled;
        existing.UpdatedAt = DateTime.UtcNow;
        await _ruleRepo.UpdateAsync(existing);

        // 清除相关联的缓存
        await InvalidateRuleCacheAsync(existing);
    }

    public async Task DeleteRuleAsync(long id)
    {
        var existing = await _ruleRepo.GetByIdAsync(id);
        if (existing != null)
            await InvalidateRuleCacheAsync(existing);
        await _ruleRepo.DeleteAsync(id);
    }

    /// <summary>
    /// 清除计费规则缓存
    /// </summary>
    private async Task InvalidateRuleCacheAsync(BillingRule rule)
    {
        if (rule.TokenId > 0)
        {
            await _cache.RemoveAsync($"billing:rule:{rule.TokenId}:{rule.CustomModelId}");
        }
        // 也清除 tokenId=0 的公有规则缓存
        await _cache.RemoveAsync($"billing:rule:0:{rule.CustomModelId}");
    }
}
