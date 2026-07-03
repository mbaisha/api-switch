using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using backend.Common.Models;
using backend.Repository;
using Microsoft.IdentityModel.Tokens;

namespace backend.Services;

/// <summary>
/// 管理员认证服务
/// </summary>
public class AuthService
{
    private readonly BaseRepository<AdminUser> _adminRepo;
    private readonly IConfiguration _config;

    public AuthService(BaseRepository<AdminUser> adminRepo, IConfiguration config)
    {
        _adminRepo = adminRepo;
        _config = config;
    }

    public async Task<(bool Success, string? Token, string Message)> Login(string username, string password)
    {
        var user = await _adminRepo.FirstOrDefaultAsync(u => u.Username == username);
        if (user == null || !user.Enabled)
            return (false, null, "用户名或密码错误");

        if (!BCrypt.Net.BCrypt.Verify(password, user.PasswordHash))
            return (false, null, "用户名或密码错误");

        var jwtKey = _config["Jwt:Key"] ?? "AI-Forward-Super-Secret-Key-2024-!@#$%^&*()";
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtKey));
        var creds = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

        var claims = new[]
        {
            new Claim(ClaimTypes.Name, user.Username),
            new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()),
            new Claim("iat", DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString())
        };

        var token = new JwtSecurityToken(
            issuer: "ai-forward",
            audience: "admin",
            claims: claims,
            expires: DateTime.UtcNow.AddHours(24),
            signingCredentials: creds
        );

        return (true, new JwtSecurityTokenHandler().WriteToken(token), "ok");
    }

    public async Task<AdminUser?> GetAdminById(long id)
    {
        return await _adminRepo.GetByIdAsync(id);
    }

    public async Task ChangePassword(long adminId, string oldPassword, string newPassword)
    {
        var user = await _adminRepo.GetByIdAsync(adminId);
        if (user == null) throw new Exception("用户不存在");

        if (!BCrypt.Net.BCrypt.Verify(oldPassword, user.PasswordHash))
            throw new Exception("旧密码错误");

        user.PasswordHash = BCrypt.Net.BCrypt.HashPassword(newPassword);
        await _adminRepo.UpdateAsync(user);
    }
}
