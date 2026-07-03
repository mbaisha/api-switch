using FreeSql.DataAnnotations;

namespace backend.Common.Models;

/// <summary>
/// 调用日志关联的图片资源（多模态请求中的图片）
/// </summary>
[Table(Name = "call_images")]
public class CallImage
{
    [Column(IsIdentity = true, IsPrimary = true)]
    public long Id { get; set; }

    /// <summary>关联的调用日志ID</summary>
    public long CallLogId { get; set; }

    /// <summary>图片来源: request/response</summary>
    [Column(StringLength = 10)]
    public string Source { get; set; } = "request";

    /// <summary>图片类型: base64/url</summary>
    [Column(StringLength = 10)]
    public string ImageType { get; set; } = "url";

    /// <summary>图片URL（如果是从URL引用的）</summary>
    [Column(StringLength = 2000)]
    public string? ImageUrl { get; set; }

    /// <summary>图片数据（base64编码）</summary>
    [Column(DbType = "text")]
    public string? ImageData { get; set; }

    /// <summary>文件系统路径（base64图片存文件后的路径，不存DB）</summary>
    [Column(StringLength = 500)]
    public string? FilePath { get; set; }

    /// <summary>图片MIME类型</summary>
    [Column(StringLength = 50)]
    public string? MimeType { get; set; }

    /// <summary>图片在消息中的位置索引</summary>
    public int ContentIndex { get; set; }

    /// <summary>关联的调用日志</summary>
    [Navigate(nameof(CallLogId))]
    public CallLog? CallLog { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}