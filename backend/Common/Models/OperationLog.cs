using FreeSql.DataAnnotations;

namespace backend.Common.Models;

/// <summary>
/// 后台操作日志
/// </summary>
[Table(Name = "operation_logs")]
public class OperationLog
{
    [Column(IsIdentity = true, IsPrimary = true)]
    public long Id { get; set; }

    /// <summary>操作人</summary>
    [Column(StringLength = 100)]
    public string? Operator { get; set; }

    /// <summary>操作类型: Create/Update/Delete</summary>
    [Column(StringLength = 50)]
    public string Action { get; set; } = string.Empty;

    /// <summary>操作目标: Channel/Token/Model/Config</summary>
    [Column(StringLength = 50)]
    public string Target { get; set; } = string.Empty;

    /// <summary>操作内容描述</summary>
    [Column(StringLength = 2000)]
    public string? Content { get; set; }

    /// <summary>操作IP</summary>
    [Column(StringLength = 100)]
    public string? ClientIp { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
