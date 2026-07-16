using System.Text.Json;
using backend.Common.Utils;
using backend.Services;
using Microsoft.AspNetCore.Mvc;

namespace backend.Controllers;

/// <summary>
/// AI接口转发 - 对外统一入口（LLM 文本对话转发）
/// 图片转发见 ImageForwardController，两者完全区隔。
/// </summary>
[ApiController]
public class ForwardController : ControllerBase
{
    private readonly ForwardEngine _forwardEngine;

    public ForwardController(ForwardEngine forwardEngine)
    {
        _forwardEngine = forwardEngine;
    }

    /// <summary>
    /// Chat Completions 接口 (兼容 OpenAI 格式)
    /// </summary>
    [HttpPost("/v1/chat/completions")]
    public Task<IActionResult> ChatCompletions() =>
        HandleForwardAsync("/v1/chat/completions");

    /// <summary>
    /// Responses 接口 (兼容 OpenAI 格式)
    /// </summary>
    [HttpPost("/v1/responses")]
    public Task<IActionResult> Responses() =>
        HandleForwardAsync("/v1/responses");

    /// <summary>
    /// Messages 接口 (兼容 Anthropic 格式)
    /// </summary>
    [HttpPost("/v1/messages")]
    public Task<IActionResult> Messages() =>
        HandleForwardAsync("/v1/messages");

    /// <summary>
    /// 统一转发处理（提取公共逻辑，避免重复代码）
    /// </summary>
    private async Task<IActionResult> HandleForwardAsync(string requestPath)
    {
        var tokenValue = ExtractToken();
        if (string.IsNullOrEmpty(tokenValue))
            return Unauthorized(new { error = new { message = "缺少认证令牌" } });

        using var reader = new StreamReader(Request.Body);
        var requestBody = await reader.ReadToEndAsync();

        var customModelId = ExtractModelId(requestBody);
        if (string.IsNullOrEmpty(customModelId))
            return BadRequest(new { error = new { message = "缺少model参数" } });

        var isStream = IsStreamRequest(requestBody);
        var clientIp = IpHelper.GetClientIp(HttpContext);

        if (isStream)
        {
            await _forwardEngine.StreamForwardAsync(
                HttpContext, tokenValue, customModelId, requestBody,
                requestPath, clientIp);
            return new EmptyResult();
        }

        var (responseBody, statusCode, error) = await _forwardEngine.ForwardAsync(
            tokenValue, customModelId, requestBody,
            requestPath, clientIp, false);

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

    private bool IsStreamRequest(string requestBody)
    {
        try
        {
            using var doc = JsonDocument.Parse(requestBody);
            var root = doc.RootElement;
            if (root.TryGetProperty("stream", out var stream))
                return stream.GetBoolean();
        }
        catch { }
        return false;
    }
}
