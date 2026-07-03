using FreeSql.DataAnnotations;

namespace backend.Common.Models;

/// <summary>
/// 渠道模型映射
/// </summary>
[Table(Name = "channel_models")]
public class ChannelModel
{
    [Column(IsIdentity = true, IsPrimary = true)]
    public long Id { get; set; }

    /// <summary>所属渠道ID</summary>
    public long ChannelId { get; set; }

    /// <summary>上游原始模型ID</summary>
    [Column(StringLength = 200)]
    public string OriginalModelId { get; set; } = string.Empty;

    /// <summary>下游模型名称</summary>
    [Column(StringLength = 200)]
    public string? ModelName { get; set; }

    /// <summary>自定义对外暴露的模型ID(核心: 跨渠道同ID归入同一负载池)</summary>
    [Column(StringLength = 200)]
    public string CustomModelId { get; set; } = string.Empty;

    /// <summary>权重(负载均衡用)</summary>
    public int Weight { get; set; } = 1;

    /// <summary>状态: 1启用 0禁用</summary>
    public bool Enabled { get; set; } = true;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    [Navigate(nameof(ChannelId))]
    public Channel? Channel { get; set; }
}
