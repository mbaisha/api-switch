using backend.Common.DTOs;
using backend.Services;
using Microsoft.AspNetCore.Mvc;

namespace backend.Controllers;

/// <summary>
/// 管理员认证
/// </summary>
[ApiController]
[Route("api/admin/auth")]
public class AuthController : ControllerBase
{
    private readonly AuthService _authService;

    public AuthController(AuthService authService)
    {
        _authService = authService;
    }

    [HttpPost("login")]
    public async Task<ApiResult<object>> Login([FromBody] LoginRequest request)
    {
        var (success, token, message) = await _authService.Login(request.Username, request.Password);
        if (!success)
            return ApiResult<object>.Fail(message, 401);

        return ApiResult<object>.Success(new { token = token! });
    }

    [HttpPost("change-password")]
    public async Task<ApiResult<string>> ChangePassword([FromBody] ChangePasswordRequest request)
    {
        var adminId = GetAdminId();
        if (adminId == null)
            return ApiResult<string>.Fail("未登录", 401);

        try
        {
            await _authService.ChangePassword(adminId.Value, request.OldPassword, request.NewPassword);
            return ApiResult<string>.Success("ok", "密码修改成功");
        }
        catch (Exception ex)
        {
            return ApiResult<string>.Fail(ex.Message);
        }
    }

    private long? GetAdminId()
    {
        var idClaim = User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
        return idClaim != null ? long.Parse(idClaim) : null;
    }
}

public class LoginRequest
{
    public string Username { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
}

public class ChangePasswordRequest
{
    public string OldPassword { get; set; } = string.Empty;
    public string NewPassword { get; set; } = string.Empty;
}
