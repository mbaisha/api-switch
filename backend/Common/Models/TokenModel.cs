using FreeSql.DataAnnotations;

namespace backend.Common.Models;

/// <summary>
/// 令牌模型权限白名单
/// </summary>
[Table(Name = "token_models")]
public class TokenModel
{
    [Column(IsIdentity = true, IsPrimary = true)]
    public long Id { get; set; }

    public long TokenId { get; set; }

    /// <summary>自定义模型ID</summary>
    [Column(StringLength = 200)]
    public string CustomModelId { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    [Navigate(nameof(TokenId))]
    public Token? Token { get; set; }
}
