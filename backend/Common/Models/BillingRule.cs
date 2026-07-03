using FreeSql.DataAnnotations;

namespace backend.Common.Models;

/// <summary>
/// 计费规则 - 按模型定价
/// </summary>
[Table(Name = "billing_rules")]
public class BillingRule
{
    [Column(IsIdentity = true, IsPrimary = true)]
    public long Id { get; set; }

    /// <summary>令牌ID (0=全局默认定价)</summary>
    public long TokenId { get; set; }

    /// <summary>自定义模型ID</summary>
    [Column(StringLength = 200)]
    public string CustomModelId { get; set; } = string.Empty;

    /// <summary>输入Token单价 (元/千Token)</summary>
    public decimal InputPrice { get; set; }

    /// <summary>输出Token单价 (元/千Token)</summary>
    public decimal OutputPrice { get; set; }

    /// <summary>是否启用</summary>
    public bool Enabled { get; set; } = true;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
