using FreeSql.DataAnnotations;

namespace backend.Common.Models;

/// <summary>
/// 模型链 - 用户自定义的模型路由，一个自定义模型ID可以映射到多个渠道的不同模型
/// 支持按权重分发、优先级降级
/// </summary>
[Table(Name = "model_chains")]
public class ModelChain
{
    [Column(IsIdentity = true, IsPrimary = true)]
    public long Id { get; set; }

    /// <summary>自定义模型ID（对外暴露）</summary>
    [Column(StringLength = 200)]
    public string CustomModelId { get; set; } = string.Empty;

    /// <summary>链类型: Text(文本LLM) / Image(图片转发)。转发时按此类型只取对应链，文本与图片互不串味</summary>
    [Column(StringLength = 10)]
    public string ChainType { get; set; } = "Text";

    /// <summary>模型显示名称</summary>
    [Column(StringLength = 200)]
    public string? DisplayName { get; set; }

    /// <summary>渠道ID</summary>
    public long ChannelId { get; set; }

    /// <summary>上游原始模型ID</summary>
    [Column(StringLength = 200)]
    public string OriginalModelId { get; set; } = string.Empty;

    /// <summary>权重（负载均衡用）</summary>
    public int Weight { get; set; } = 1;

    /// <summary>优先级（数字越小越优先，0=最高）</summary>
    public int Priority { get; set; } = 0;

    /// <summary>状态: 1启用 0禁用</summary>
    public bool Enabled { get; set; } = true;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    [Navigate(nameof(ChannelId))]
    public Channel? Channel { get; set; }
}
