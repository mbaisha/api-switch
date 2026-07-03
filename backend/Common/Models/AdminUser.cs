using FreeSql.DataAnnotations;

namespace backend.Common.Models;

/// <summary>
/// 管理员用户
/// </summary>
[Table(Name = "admin_users")]
public class AdminUser
{
    [Column(IsIdentity = true, IsPrimary = true)]
    public long Id { get; set; }

    [Column(StringLength = 100)]
    public string Username { get; set; } = string.Empty;

    [Column(StringLength = 200)]
    public string PasswordHash { get; set; } = string.Empty;

    public bool Enabled { get; set; } = true;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
