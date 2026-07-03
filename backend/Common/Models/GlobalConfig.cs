using FreeSql.DataAnnotations;

namespace backend.Common.Models;

/// <summary>
/// 全局配置
/// </summary>
[Table(Name = "global_configs")]
public class GlobalConfig
{
    [Column(IsIdentity = true, IsPrimary = true)]
    public long Id { get; set; }

    [Column(StringLength = 100)]
    public string Key { get; set; } = string.Empty;

    [Column(StringLength = 2000)]
    public string Value { get; set; } = string.Empty;

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
