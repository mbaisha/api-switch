using System.Text.Json;
using backend.Common.Utils;
using backend.Services;
using Microsoft.AspNetCore.Mvc;

namespace backend.Controllers;

/// <summary>
/// 图片接口转发 - 对外统一入口（与 LLM 转发完全区隔的独立子系统）
///
/// 下游统一暴露 /v1/images/generations，兼容 OpenAI Images 协议，
/// 并以 image 字段扩展支持图生图与多图输入（无需另开 /v1/images/edits）。
/// 文生图与图生图共用同一端点，由是否有 image 字段区分。
/// </summary>
[ApiController]
public class ImageForwardController : ControllerBase
{
    private readonly ImageForwardEngine _imageForwardEngine;

    public ImageForwardController(ImageForwardEngine imageForwardEngine)
    {
        _imageForwardEngine = imageForwardEngine;
    }

    /// <summary>
    /// 图片生成接口（文生图 / 图生图 / 多图，兼容 OpenAI 格式并扩展）
    /// </summary>
    [HttpPost("/v1/images/generations")]
    public async Task<IActionResult> ImagesGenerations()
    {
        var tokenValue = ExtractToken();
        if (string.IsNullOrEmpty(tokenValue))
            return Unauthorized(new { error = new { message = "缺少认证令牌" } });

        using var reader = new StreamReader(Request.Body);
        var requestBody = await reader.ReadToEndAsync();

        var customModelId = ExtractModelId(requestBody);
        if (string.IsNullOrEmpty(customModelId))
            return BadRequest(new { error = new { message = "缺少model参数" } });

        var clientIp = IpHelper.GetClientIp(HttpContext);

        var (responseBody, statusCode, error) = await _imageForwardEngine.ForwardImageAsync(
            tokenValue, customModelId, requestBody, clientIp);

        if (statusCode != 200)
            return new ContentResult { Content = responseBody, ContentType = "application/json", StatusCode = statusCode };

        return Content(responseBody, "application/json");
    }

    private string? ExtractToken()
    {
        var authHeader = Request.Headers.Authorization.ToString();
        if (string.IsNullOrEmpty(authHeader)) return null;
        return authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
            ? authHeader[7..] : authHeader;
    }

    private string? ExtractModelId(string requestBody)
    {
        try
        {
            using var doc = JsonDocument.Parse(requestBody);
            var root = doc.RootElement;
            if (root.TryGetProperty("model", out var model))
                return model.GetString();
        }
        catch { }
        return null;
    }
}
