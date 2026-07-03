using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace backend.Common.Utils;

/// <summary>
/// JWT 鉴权特性 - 应用于管理后台 API
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public class AdminAuthAttribute : AuthorizeAttribute
{
    public AdminAuthAttribute()
    {
        AuthenticationSchemes = "Bearer";
    }
}
