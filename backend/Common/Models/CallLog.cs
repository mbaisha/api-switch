using FreeSql.DataAnnotations;

namespace backend.Common.Models;

/// <summary>
/// 接口调用日志
/// </summary>
[Table(Name = "call_logs")]
public class CallLog
{
    [Column(IsIdentity = true, IsPrimary = true)]
    public long Id { get; set; }

    /// <summary>调用令牌</summary>
    [Column(StringLength = 200)]
    public string? TokenValue { get; set; }

    /// <summary>请求IP</summary>
    [Column(StringLength = 100)]
    public string? ClientIp { get; set; }

    /// <summary>自定义模型ID(用户调用的)</summary>
    [Column(StringLength = 200)]
    public string? CustomModelId { get; set; }

    /// <summary>上游真实模型ID</summary>
    [Column(StringLength = 200)]
    public string? OriginalModelId { get; set; }

    /// <summary>渠道名称</summary>
    [Column(StringLength = 200)]
    public string? ChannelName { get; set; }

    /// <summary>请求状态: Success/Failed</summary>
    [Column(StringLength = 20)]
    public string Status { get; set; } = string.Empty;

    /// <summary>是否流式调用</summary>
    public bool IsStream { get; set; }

    /// <summary>输入Token数</summary>
    public int InputTokens { get; set; }

    /// <summary>输出Token数</summary>
    public int OutputTokens { get; set; }

    /// <summary>耗时(毫秒)</summary>
    public long DurationMs { get; set; }

    /// <summary>请求参数(JSON)</summary>
    [Column(DbType = "text")]
    public string? RequestBody { get; set; }

    /// <summary>响应结果(JSON)</summary>
    [Column(DbType = "text")]
    public string? ResponseBody { get; set; }

    /// <summary>异常信息</summary>
    [Column(DbType = "text")]
    public string? ErrorMessage { get; set; }

    /// <summary>请求时间</summary>
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
