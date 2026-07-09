using backend.Common.Models;
using backend.Common.Utils;
using backend.Repository;

namespace backend.Services;

/// <summary>
/// 渠道服务 - 管理渠道缓存和健康状态（带 Redis 缓存）
/// 支持渠道模型映射 + 模型链两种路由方式
/// </summary>
public class ChannelService
{
    private readonly BaseRepository<Channel> _channelRepo;
    private readonly BaseRepository<ApiKey> _apiKeyRepo;
    private readonly BaseRepository<ChannelModel> _modelRepo;
    private readonly BaseRepository<ModelChain> _chainRepo;
    private readonly RedisCacheService _cache;
    private static readonly TimeSpan NodesCacheTtl = TimeSpan.FromSeconds(30);

    public ChannelService(
        BaseRepository<Channel> channelRepo,
        BaseRepository<ApiKey> apiKeyRepo,
        BaseRepository<ChannelModel> modelRepo,
        BaseRepository<ModelChain> chainRepo,
        RedisCacheService cache)
    {
        _channelRepo = channelRepo;
        _apiKeyRepo = apiKeyRepo;
        _modelRepo = modelRepo;
        _chainRepo = chainRepo;
        _cache = cache;
    }

    /// <summary>
    /// 根据自定义模型ID获取所有可用节点（跨渠道负载均衡池）
    /// 优先使用模型链（ModelChain），其次使用渠道模型映射（ChannelModel）
    /// 结果缓存 30 秒，大幅减少重复查询
    /// </summary>
    public async Task<List<LoadBalanceNode>> GetNodesByCustomModelId(string customModelId, string chainType = "Text")
    {
        var cacheKey = $"nodes:{chainType.ToLower()}:{customModelId}";

        // 1. 尝试从缓存获取
        var cached = await _cache.GetAsync<List<LoadBalanceNode>>(cacheKey);
        if (cached != null)
            return cached;

        // 2. 缓存未命中，执行完整查询
        var nodes = await BuildNodesAsync(customModelId, chainType);

        // 3. 写入缓存
        if (nodes.Count > 0)
            await _cache.SetAsync(cacheKey, nodes, NodesCacheTtl);

        return nodes;
    }

    /// <summary>
    /// 构建节点列表（实际查询逻辑）
    /// </summary>
    private async Task<List<LoadBalanceNode>> BuildNodesAsync(string customModelId, string chainType)
    {
        var nodes = new List<LoadBalanceNode>();

        // 1. 先查模型链（按 ChainType 过滤，文本只取 Text 链，图片只取 Image 链；兼容旧数据空值）
        var chains = await _chainRepo.GetListAsync(c =>
            c.CustomModelId == customModelId && c.Enabled &&
            (c.ChainType == chainType || string.IsNullOrEmpty(c.ChainType)));
        if (chains.Count > 0)
        {
            foreach (var chain in chains.OrderBy(c => c.Priority))
            {
                var channel = await _channelRepo.GetByIdAsync(chain.ChannelId);
                if (channel == null || !channel.Enabled) continue;

                var keys = await _apiKeyRepo.GetListAsync(k =>
                    k.ChannelId == channel.Id && k.Status == 1);

                foreach (var key in keys)
                {
                    nodes.Add(new LoadBalanceNode
                    {
                        ChannelId = channel.Id,
                        ChannelName = channel.Name,
                        SupplierType = channel.SupplierType,
                        ApiAddress = channel.ApiAddress,
                        ApiKey = key.KeyValue,
                        ApiKey2 = key.KeyValue2,
                        ApiKeyId = key.Id,
                        ExtConfig = channel.ExtConfig,
                        ProtocolType = channel.ProtocolType,
                        PassthroughPaths = channel.PassthroughPaths ?? channel.SupportedPaths ?? "chat",
                        SupportedPaths = channel.SupportedPaths ?? "chat",
                        FallbackTarget = channel.FallbackTarget,
                        TimeoutSeconds = channel.TimeoutSeconds,
                        SseEnabled = channel.SseEnabled,
                        OriginalModelId = chain.OriginalModelId,
                        CustomModelId = chain.CustomModelId,
                        Weight = chain.Weight * key.Weight,
                        CooldownSeconds = channel.CooldownSeconds,
                        Priority = chain.Priority,
                    });
                }
            }

            if (nodes.Count > 0) return nodes;
        }

        // 2. 回退到渠道模型映射
        var models = await _modelRepo.GetListAsync(m =>
            m.CustomModelId == customModelId && m.Enabled);

        foreach (var model in models)
        {
            var channel = await _channelRepo.GetByIdAsync(model.ChannelId);
            if (channel == null || !channel.Enabled) continue;

            var keys = await _apiKeyRepo.GetListAsync(k =>
                k.ChannelId == channel.Id && k.Status == 1);

            foreach (var key in keys)
            {
                nodes.Add(new LoadBalanceNode
                    {
                        ChannelId = channel.Id,
                        ChannelName = channel.Name,
                        SupplierType = channel.SupplierType,
                        ApiAddress = channel.ApiAddress,
                        ApiKey = key.KeyValue,
                        ApiKey2 = key.KeyValue2,
                        ApiKeyId = key.Id,
                        ExtConfig = channel.ExtConfig,
                        ProtocolType = channel.ProtocolType,
                        PassthroughPaths = channel.PassthroughPaths ?? channel.SupportedPaths ?? "chat",
                        SupportedPaths = channel.SupportedPaths ?? "chat",
                        FallbackTarget = channel.FallbackTarget,
                        TimeoutSeconds = channel.TimeoutSeconds,
                        SseEnabled = channel.SseEnabled,
                        OriginalModelId = model.OriginalModelId,
                        CustomModelId = model.CustomModelId,
                        Weight = model.Weight * key.Weight,
                        CooldownSeconds = channel.CooldownSeconds,
                        Priority = 0,
                    });
            }
        }

        return nodes;
    }

    /// <summary>
    /// 验证令牌是否有权限调用指定模型
    /// </summary>
    public async Task<bool> ValidateTokenModelPermission(long tokenId, string customModelId)
    {
        var models = await _modelRepo.GetListAsync(m => m.CustomModelId == customModelId);
        if (models.Count > 0) return true;
        var chains = await _chainRepo.GetListAsync(c => c.CustomModelId == customModelId && c.Enabled);
        return chains.Count > 0;
    }
}

public class LoadBalanceNode
{
    public long ChannelId { get; set; }
    public string ChannelName { get; set; } = string.Empty;
    public string SupplierType { get; set; } = string.Empty;
    public string ApiAddress { get; set; } = string.Empty;
    public string ApiKey { get; set; } = string.Empty;
    /// <summary>第二密钥(讯飞 apiSecret 等)</summary>
    public string? ApiKey2 { get; set; }
    public long ApiKeyId { get; set; }
    /// <summary>渠道扩展配置(讯飞 appId 等)</summary>
    public string? ExtConfig { get; set; }
    public string ProtocolType { get; set; } = "Chat";
    /// <summary>支持的接口路径（逗号分隔: chat,responses,messages）</summary>
    public string SupportedPaths { get; set; } = "chat";
    /// <summary>支持透传的路径（逗号分隔）</summary>
    public string PassthroughPaths { get; set; } = "chat";
    /// <summary>降级目标协议</summary>
    public string FallbackTarget { get; set; } = "Chat";
    public int TimeoutSeconds { get; set; } = 30;
    public bool SseEnabled { get; set; } = true;
    public string OriginalModelId { get; set; } = string.Empty;
    public string CustomModelId { get; set; } = string.Empty;
    public int Weight { get; set; } = 1;
    public int CooldownSeconds { get; set; } = 60;
    public int Priority { get; set; } = 0;
}
