using FreeSql.DataAnnotations;

namespace backend.Common.Models;

/// <summary>
/// 渠道API密钥（上游API密钥）
/// </summary>
[Table(Name = "api_keys")]
public class ApiKey
{
    [Column(IsIdentity = true, IsPrimary = true)]
    public long Id { get; set; }

    /// <summary>所属渠道ID</summary>
    public long ChannelId { get; set; }

    /// <summary>API Key值(加密存储)</summary>
    [Column(StringLength = 1000)]
    public string KeyValue { get; set; } = string.Empty;

    /// <summary>第二密钥(部分供应商需要双密钥，如讯飞星火的 apiSecret 用于 HMAC 签名)</summary>
    [Column(StringLength = 1000)]
    public string? KeyValue2 { get; set; }

    /// <summary>权重(负载均衡用)</summary>
    public int Weight { get; set; } = 1;

    /// <summary>状态: 1启用 0禁用 2永久失效</summary>
    public int Status { get; set; } = 1;

    /// <summary>已使用Token数（上游用量统计）</summary>
    public long UsedTokens { get; set; }

    /// <summary>总调用次数</summary>
    public long TotalCalls { get; set; }

    /// <summary>成功调用次数</summary>
    public long SuccessCalls { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    [Navigate(nameof(ChannelId))]
    public Channel? Channel { get; set; }
}