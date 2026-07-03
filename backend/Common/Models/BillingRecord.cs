using FreeSql.DataAnnotations;

namespace backend.Common.Models;

/// <summary>
/// 账单记录
/// </summary>
[Table(Name = "billing_records")]
public class BillingRecord
{
    [Column(IsIdentity = true, IsPrimary = true)]
    public long Id { get; set; }

    public long TokenId { get; set; }

    /// <summary>令牌值</summary>
    [Column(StringLength = 200)]
    public string TokenValue { get; set; } = string.Empty;

    /// <summary>自定义模型ID</summary>
    [Column(StringLength = 200)]
    public string? CustomModelId { get; set; }

    /// <summary>输入Token</summary>
    public int InputTokens { get; set; }

    /// <summary>输出Token</summary>
    public int OutputTokens { get; set; }

    /// <summary>消费金额</summary>
    public decimal Cost { get; set; }

    /// <summary>输入Token单价</summary>
    public decimal InputPrice { get; set; }

    /// <summary>输出Token单价</summary>
    public decimal OutputPrice { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
