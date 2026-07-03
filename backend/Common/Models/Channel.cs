using FreeSql.DataAnnotations;

namespace backend.Common.Models;

/// <summary>
/// 渠道 - 上游AI接口服务商配置
/// </summary>
[Table(Name = "channels")]
public class Channel
{
    [Column(IsIdentity = true, IsPrimary = true)]
    public long Id { get; set; }

    /// <summary>渠道名称</summary>
    [Column(StringLength = 200)]
    public string Name { get; set; } = string.Empty;

    /// <summary>备注</summary>
    [Column(StringLength = 500)]
    public string? Remark { get; set; }

    /// <summary>供应商类型: OpenAI / Azure / Anthropic / Google / DeepSeek / Groq / Together / Custom</summary>
    [Column(StringLength = 50)]
    public string SupplierType { get; set; } = "OpenAI";

    /// <summary>上游API地址</summary>
    [Column(StringLength = 500)]
    public string ApiAddress { get; set; } = string.Empty;

    /// <summary>请求超时时间(秒)，默认30</summary>
    public int TimeoutSeconds { get; set; } = 30;

    /// <summary>SSE流式开关</summary>
    public bool SseEnabled { get; set; } = true;

    /// <summary>下游协议类型: Chat / Response</summary>
    [Column(StringLength = 20)]
    public string ProtocolType { get; set; } = "Chat";

    /// <summary>支持的接口路径（逗号分隔: chat,responses）</summary>
    [Column(StringLength = 200)]
    public string? SupportedPaths { get; set; } = "chat";

    /// <summary>模型冷却时长(秒)</summary>
    public int CooldownSeconds { get; set; } = 60;

    /// <summary>状态: 1启用 0禁用</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>创建时间</summary>
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>更新时间</summary>
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    [Navigate(nameof(ApiKey.ChannelId))]
    public List<ApiKey> ApiKeys { get; set; } = new();

    [Navigate(nameof(ChannelModel.ChannelId))]
    public List<ChannelModel> Models { get; set; } = new();
}
