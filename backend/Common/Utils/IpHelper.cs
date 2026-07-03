using Microsoft.AspNetCore.Http;

namespace backend.Common.Utils;

/// <summary>
/// 获取客户端真实IP（兼容反向代理场景）
/// </summary>
public static class IpHelper
{
    /// <summary>
    /// 获取客户端真实IP
    /// 优先级：X-Forwarded-For > X-Real-IP > RemoteIpAddress
    /// </summary>
    public static string GetClientIp(HttpContext context)
    {
        // X-Forwarded-For: client, proxy1, proxy2
        var forwardedFor = context.Request.Headers["X-Forwarded-For"].ToString();
        if (!string.IsNullOrEmpty(forwardedFor))
        {
            var ips = forwardedFor.Split(',', StringSplitOptions.RemoveEmptyEntries);
            if (ips.Length > 0)
            {
                var ip = ips[0].Trim();
                if (!string.IsNullOrEmpty(ip)) return ip;
            }
        }

        // X-Real-IP（部分代理如 nginx 会设置此头）
        var realIp = context.Request.Headers["X-Real-IP"].ToString();
        if (!string.IsNullOrEmpty(realIp))
            return realIp.Trim();

        // CF-Connecting-IP（Cloudflare）
        var cfIp = context.Request.Headers["CF-Connecting-IP"].ToString();
        if (!string.IsNullOrEmpty(cfIp))
            return cfIp.Trim();

        // 回退到连接IP
        return context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
    }
}
