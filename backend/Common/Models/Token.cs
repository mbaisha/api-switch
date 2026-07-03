using FreeSql.DataAnnotations;

namespace backend.Common.Models;

/// <summary>
/// 下游调用令牌
/// </summary>
[Table(Name = "tokens")]
public class Token
{
    [Column(IsIdentity = true, IsPrimary = true)]
    public long Id { get; set; }

    /// <summary>令牌值(对外唯一凭证)</summary>
    [Column(StringLength = 200)]
    public string TokenValue { get; set; } = string.Empty;

    /// <summary>备注/归属用户</summary>
    [Column(StringLength = 200)]
    public string? Remark { get; set; }

    /// <summary>状态: 1启用 0禁用</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>日Token限额(0不限制)</summary>
    public long DailyTokenLimit { get; set; }

    /// <summary>总Token限额(0不限制)</summary>
    public long TotalTokenLimit { get; set; }

    /// <summary>已使用总Token</summary>
    public long UsedTokens { get; set; }

    /// <summary>总调用次数</summary>
    public long TotalCalls { get; set; }

    /// <summary>成功调用次数</summary>
    public long SuccessCalls { get; set; }

    /// <summary>失败调用次数</summary>
    public long FailedCalls { get; set; }

    /// <summary>剩余额度(金额)</summary>
    public decimal RemainingBalance { get; set; }

    /// <summary>限流时间窗口(秒, 0使用全局默认60)</summary>
    public int RateLimitWindow { get; set; } = 60;

    /// <summary>窗口内每个IP最大请求数(0=不限流)</summary>
    public int RateLimitCount { get; set; } = 60;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    [Navigate(nameof(TokenModel.TokenId))]
    public List<TokenModel> AllowedModels { get; set; } = new();
}
