using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using backend.Common.Models;
using backend.Common.Utils;
using backend.Repository;

namespace backend.Services;

/// <summary>
/// 图片接口转发引擎 - 统一聚合多上游平台的文生图/图生图接口，
/// 下游对齐 OpenAI /v1/images/generations 协议（image 字段扩展支持图生图与多图输入）。
///
/// 上游覆盖：
///   OpenAI / Azure / DeepSeek / Together / Groq / Custom  —— 近透传
///   VolcEngine（豆包/火山 Seedream）                      —— OpenAI 兼容，透传
///   SiliconFlow（硅基流动）                               —— size→image_size，多图拆 image/image2/image3，响应 images→data
///   Agnes                                                 —— image 与 response_format 塞进 extra_body
///   ModelScope（魔搭）                                    —— 异步任务模式，images 字段（base64 数组），URL 需下载转 base64
///   SenseNova（商汤 U1）                                  —— size 白名单校验，图生图走 chat/completions + modalities 包装
///   Xfyun（讯飞星火）                                     —— HMAC 签名鉴权，三段式请求体，HiDream 异步轮询包装同步
/// </summary>
public class ImageForwardEngine
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ChannelService _channelService;
    private readonly TokenService _tokenService;
    private readonly IFreeSql _db;
    private readonly BaseRepository<CallLog> _callLogRepo;
    private readonly BaseRepository<ApiKey> _apiKeyRepo;
    private readonly BaseRepository<CallImage> _callImageRepo;
    private readonly BillingService _billingService;
    private readonly RedisCacheService _cache;
    private readonly ILogger<ImageForwardEngine> _logger;

    // 商汤 SenseNova U1 size 白名单（11 个固定值）
    private static readonly HashSet<string> SenseNovaAllowedSizes = new(StringComparer.OrdinalIgnoreCase)
    {
        "1664x2496", "2496x1664", "1760x2368", "2368x1760",
        "1824x2272", "2272x1824", "2048x2048", "2752x1536",
        "1536x2752", "3072x1376", "1344x3136"
    };

    public ImageForwardEngine(
        IHttpClientFactory httpClientFactory,
        ChannelService channelService,
        TokenService tokenService,
        BillingService billingService,
        IFreeSql db,
        ILogger<ImageForwardEngine> logger,
        RedisCacheService cache)
    {
        _httpClientFactory = httpClientFactory;
        _channelService = channelService;
        _tokenService = tokenService;
        _billingService = billingService;
        _db = db;
        _callLogRepo = new BaseRepository<CallLog>(db);
        _apiKeyRepo = new BaseRepository<ApiKey>(db);
        _callImageRepo = new BaseRepository<CallImage>(db);
        _logger = logger;
        _cache = cache;
    }

    /// <summary>
    /// 图片转发主入口 —— 复用 LLM 转发的令牌鉴权/权限/节点轮询/重试/冷却/日志骨架，
    /// 但走图片专用适配路径（ConvertImageRequest/ConvertImageResponse），不做 chat 协议转换。
    /// </summary>
    public async Task<(string ResponseBody, int StatusCode, string? Error)> ForwardImageAsync(
        string tokenValue, string customModelId, string requestBody, string clientIp)
    {
        var startTime = DateTime.UtcNow;

        // 1. 验证令牌
        var (valid, token, msg) = await _tokenService.ValidateToken(tokenValue);
        if (!valid)
        {
            await LogCall(tokenValue, customModelId, null, null, "Failed", false, 0, 0,
                (long)(DateTime.UtcNow - startTime).TotalMilliseconds, requestBody, null, msg, clientIp);
            return (JsonSerializer.Serialize(new { error = new { message = msg } }), 401, msg);
        }

        // 2. 校验图片转发权限（与 LLM 权限独立管控，令牌须显式开启 ImageEnabled）
        if (!token!.ImageEnabled)
        {
            await LogCall(tokenValue, customModelId, null, null, "Failed", false, 0, 0,
                (long)(DateTime.UtcNow - startTime).TotalMilliseconds, requestBody, null, "令牌未开通图片转发权限", clientIp);
            return (JsonSerializer.Serialize(new { error = new { message = "令牌未开通图片转发权限" } }), 403, "无图片权限");
        }

        // 3. 检查模型权限
        var hasPermission = await _tokenService.CheckModelPermission(token.Id, customModelId);
        if (!hasPermission)
        {
            await LogCall(tokenValue, customModelId, null, null, "Failed", false, 0, 0,
                (long)(DateTime.UtcNow - startTime).TotalMilliseconds, requestBody, null, "无模型调用权限", clientIp);
            return (JsonSerializer.Serialize(new { error = new { message = "无该模型调用权限" } }), 403, "无权限");
        }

        // 4. 获取所有节点（图片转发只取 ChainType=Image 的链，与文本 LLM 互不串味）
        var allNodes = await _channelService.GetNodesByCustomModelId(customModelId, "Image");
        if (allNodes.Count == 0)
        {
            var fallbackMsg = "未配置任何可用渠道";
            await LogCall(tokenValue, customModelId, null, null, "Failed", false, 0, 0,
                (long)(DateTime.UtcNow - startTime).TotalMilliseconds, requestBody, null, fallbackMsg, clientIp);
            return (JsonSerializer.Serialize(new { error = new { message = fallbackMsg } }), 503, fallbackMsg);
        }

        // 5. 带重试的轮询转发（与 LLM 转发一致的容错策略）
        const int maxAttempts = 18;
        const int retryDelayMs = 5000;
        Exception? lastEx = null;
        string? lastErrorMsg = null;
        int lastStatusCode = 503;
        bool foundUnrecoverable = false;

        for (int attempt = 0; attempt < maxAttempts && !foundUnrecoverable; attempt++)
        {
            var activeNodes = await GetActiveNodesAsync(allNodes);
            if (activeNodes.Count == 0)
            {
                _logger.LogWarning("第 {Attempt} 次尝试：所有节点均在冷却中，等待 {Delay}ms 后重试", attempt + 1, retryDelayMs);
                await Task.Delay(retryDelayMs);
                continue;
            }

            var candidates = await SelectNodesWeightedRoundRobin(activeNodes);
            bool anyAttempted = false;

            foreach (var node in candidates)
            {
                anyAttempted = true;
                _logger.LogInformation("图片转发尝试 → 渠道 {Channel}/{Model} API Key={Key} (第 {Attempt} 轮)",
                    node.ChannelName, node.OriginalModelId,
                    node.ApiKey[..Math.Min(node.ApiKey.Length, 8)], attempt + 1);

                try
                {
                    var upstreamReq = await BuildUpstreamRequest(requestBody, node);
                    var upstreamUrl = upstreamReq.Url;
                    var upstreamBody = upstreamReq.Body;
                    var extraHeaders = upstreamReq.ExtraHeaders;
                    var client = _httpClientFactory.CreateClient("AIClient");
                    client.Timeout = TimeSpan.FromSeconds(Math.Max(node.TimeoutSeconds, 120)); // 图片生成通常比 chat 慢

                    var httpRequest = new HttpRequestMessage(HttpMethod.Post, upstreamUrl)
                    {
                        Content = new StringContent(upstreamBody, Encoding.UTF8, "application/json")
                    };
                    SetAuthHeaders(httpRequest, node.SupplierType, node.ApiKey, node.ApiKey2);
                    if (extraHeaders != null)
                    {
                        foreach (var pair in extraHeaders)
                            httpRequest.Headers.TryAddWithoutValidation(pair.Key, pair.Value);
                    }

                    _logger.LogInformation("发送到上游 {Url} 的请求体: {Body}", upstreamUrl,
                        upstreamBody.Length > 1000 ? upstreamBody[..1000] + "..." : upstreamBody);

                    var response = await client.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead);
                    var responseBody = await response.Content.ReadAsStringAsync();

                    if (!response.IsSuccessStatusCode)
                    {
                        var sc = (int)response.StatusCode;
                        await HandleError(response, node);
                        var downstreamErr = ExtractDownstreamError(responseBody);
                        lastErrorMsg = downstreamErr.Length > 0 ? $"上游[{sc}]: {downstreamErr}" : $"上游接口返回错误: {sc}";
                        lastStatusCode = sc;

                        await LogCall(tokenValue, customModelId, node.ChannelName, node.OriginalModelId, "Failed",
                            false, 0, 0, (long)(DateTime.UtcNow - startTime).TotalMilliseconds,
                            requestBody, responseBody, lastErrorMsg + " (等待5秒后重试)", clientIp);

                        if (ShouldRetry(sc))
                        {
                            _logger.LogWarning("第 {Attempt} 次尝试：节点 {Channel}/{Model} 返回 {StatusCode}，切换下一个",
                                attempt + 1, node.ChannelName, node.OriginalModelId, sc);
                            await Task.Delay(retryDelayMs);
                            continue;
                        }

                        foundUnrecoverable = true;
                        return (JsonSerializer.Serialize(new { error = new { message = "请求失败，请检查参数" } }), sc, lastErrorMsg);
                    }

                    // 成功 —— 异步上游（魔搭/讯飞 HiDream）已在 BuildUpstreamRequest 内部轮询完成，此处 responseBody 即最终结果
                    var convertedResponse = ConvertImageResponse(responseBody, node);
                    var (inputTokens, outputTokens) = ExtractImageUsage(convertedResponse);

                    await _tokenService.RecordUsage(token.Id, inputTokens, outputTokens);
                    await _billingService.RecordBilling(token.Id, tokenValue, customModelId, inputTokens, outputTokens);

                    var logId = await LogCall(tokenValue, customModelId, node.ChannelName, node.OriginalModelId, "Success",
                        false, inputTokens, outputTokens, (long)(DateTime.UtcNow - startTime).TotalMilliseconds,
                        requestBody, convertedResponse, null, clientIp);

                    await RecordUpstreamUsage(node.ApiKeyId, inputTokens, outputTokens);

                    if (logId > 0)
                        await SaveCallImagesAsync(logId, requestBody, convertedResponse);

                    return (convertedResponse, 200, null);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "图片转发请求异常，节点: {Channel}/{Model}，第 {Attempt} 次尝试",
                        node.ChannelName, node.OriginalModelId, attempt + 1);
                    await SetCooldownAsync(node, node.CooldownSeconds);
                    lastEx = ex;
                    lastErrorMsg = ex.Message;
                    lastStatusCode = 503;

                    await LogCall(tokenValue, customModelId, node.ChannelName, node.OriginalModelId, "Failed",
                        false, 0, 0, (long)(DateTime.UtcNow - startTime).TotalMilliseconds,
                        requestBody, null, ex.Message + " (等待5秒后重试)", clientIp);

                    await Task.Delay(retryDelayMs);
                    continue;
                }
            }

            if (attempt < maxAttempts - 1 && anyAttempted)
                await Task.Delay(retryDelayMs);
        }

        var failMsg = lastEx != null ? $"所有渠道均不可用: {lastEx.Message}" : $"所有渠道均不可用: {lastErrorMsg}";
        _logger.LogError("图片转发失败，已耗尽 {MaxAttempts} 次重试: {Msg}", maxAttempts, failMsg);
        return (JsonSerializer.Serialize(new { error = new { message = "服务暂时不可用，请稍后重试" } }), lastStatusCode, failMsg);
    }

    /// <summary>
    /// 上游接口直测（管理端用）：绕开令牌/模型权限/计费/日志，直接用指定渠道的密钥
    /// 与原始模型ID打一次上游图片接口，验证对接是否可用。返回响应原文与延迟。
    /// </summary>
    public async Task<(string UpstreamUrl, string RequestBody, int StatusCode, string ResponseBody, long LatencyMs, string? Error)> TestUpstreamImageAsync(
        long channelId, string originalModelId, string testPrompt, string? testSize, string? testResponseFormat, string? testImage)
    {
        var channelRepo = new BaseRepository<Channel>(_db);
        var channel = await channelRepo.GetByIdAsync(channelId);
        if (channel == null)
            return ("", "", 0, "", 0, "渠道不存在");

        var keys = await _apiKeyRepo.GetListAsync(k => k.ChannelId == channelId && k.Status == 1);
        if (keys.Count == 0)
            return ("", "", 0, "", 0, "无可用密钥");
        var key = keys[0];

        // 构造下游统一体（与 ForwardImageAsync 入参一致），让 BuildUpstreamRequest 走真实适配
        var downstreamBody = BuildTestDownstreamBody(originalModelId, testPrompt, testSize, testResponseFormat, testImage);
        var node = new LoadBalanceNode
        {
            ChannelId = channel.Id,
            ChannelName = channel.Name,
            SupplierType = channel.SupplierType,
            ApiAddress = channel.ApiAddress,
            ApiKey = key.KeyValue,
            ApiKey2 = key.KeyValue2,
            ExtConfig = channel.ExtConfig,
            TimeoutSeconds = channel.TimeoutSeconds,
            OriginalModelId = originalModelId
        };

        var startTime = DateTime.UtcNow;
        try
        {
            var upstreamReq = await BuildUpstreamRequest(downstreamBody, node);
            var client = _httpClientFactory.CreateClient("AIClient");
            client.Timeout = TimeSpan.FromSeconds(Math.Max(node.TimeoutSeconds, 120));

            var httpRequest = new HttpRequestMessage(HttpMethod.Post, upstreamReq.Url)
            {
                Content = new StringContent(upstreamReq.Body, Encoding.UTF8, "application/json")
            };
            SetAuthHeaders(httpRequest, node.SupplierType, node.ApiKey, node.ApiKey2);
            if (upstreamReq.ExtraHeaders != null)
                foreach (var pair in upstreamReq.ExtraHeaders)
                    httpRequest.Headers.TryAddWithoutValidation(pair.Key, pair.Value);

            var response = await client.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead);
            var responseBody = await response.Content.ReadAsStringAsync();
            var latency = (long)(DateTime.UtcNow - startTime).TotalMilliseconds;

            return (upstreamReq.Url, upstreamReq.Body, (int)response.StatusCode, responseBody, latency, null);
        }
        catch (Exception ex)
        {
            var latency = (long)(DateTime.UtcNow - startTime).TotalMilliseconds;
            return ("", "", 0, "", latency, ex.Message);
        }
    }

    private static string BuildTestDownstreamBody(string modelId, string prompt, string? size, string? responseFormat, string? image)
    {
        var dict = new Dictionary<string, object?> { ["model"] = modelId, ["prompt"] = prompt };
        if (!string.IsNullOrEmpty(size)) dict["size"] = size;
        if (!string.IsNullOrEmpty(responseFormat)) dict["response_format"] = responseFormat;
        if (!string.IsNullOrEmpty(image)) dict["image"] = image;
        return JsonSerializer.Serialize(dict);
    }

    // ============== 上游请求构建（含全部适配分支） ==============

    /// <summary>
    /// 按 SupplierType 构建上游请求：返回 (完整 URL, 转换后的请求体, 额外请求头)。
    /// 异步上游（魔搭/讯飞 HiDream）在此内部完成轮询，对外表现为同步返回。
    /// </summary>
    private async Task<(string Url, string Body, List<(string Key, string Value)>? ExtraHeaders)> BuildUpstreamRequest(
        string clientBody, LoadBalanceNode node)
    {
        var supplier = node.SupplierType ?? "OpenAI";
        var baseUri = (node.ApiAddress ?? "").TrimEnd('/');

        // 提取下游统一字段（OpenAI 扩展格式）
        using var doc = JsonDocument.Parse(clientBody);
        var root = doc.RootElement;
        var prompt = root.TryGetProperty("prompt", out var p) ? p.GetString() ?? "" : "";
        var size = root.TryGetProperty("size", out var sz) ? sz.GetString() ?? "" : "";
        var n = root.TryGetProperty("n", out var nn) && nn.ValueKind == JsonValueKind.Number ? nn.GetInt32() : 1;
        var responseFormat = root.TryGetProperty("response_format", out var rf) ? rf.GetString() ?? "url" : "url";
        var seed = root.TryGetProperty("seed", out var sd) && sd.ValueKind == JsonValueKind.Number ? sd.GetInt32() : -1;

        // 参考图：支持 string 或 array（多图）
        List<string> images = new();
        if (root.TryGetProperty("image", out var img))
        {
            if (img.ValueKind == JsonValueKind.String)
                images.Add(img.GetString() ?? "");
            else if (img.ValueKind == JsonValueKind.Array)
                foreach (var i in img.EnumerateArray())
                    images.Add(i.GetString() ?? "");
        }

        // 按供应商分流
        switch (supplier)
        {
            case "OpenAI" or "DeepSeek" or "Groq" or "Together" or "Custom" or "VolcEngine":
                {
                    // OpenAI 兼容族 + 豆包/火山：近透传，只换 model id
                    var dict = new Dictionary<string, object?>();
                    dict["model"] = node.OriginalModelId;
                    dict["prompt"] = prompt;
                    if (!string.IsNullOrEmpty(size)) dict["size"] = size;
                    if (n > 1) dict["n"] = n;
                    if (!string.IsNullOrEmpty(responseFormat)) dict["response_format"] = responseFormat;
                    if (seed >= 0) dict["seed"] = seed;
                    if (images.Count > 0)
                        dict["image"] = images.Count == 1 ? (object)images[0] : images;
                    // 透传其他未识别字段（watermark 等）
                    foreach (var prop in root.EnumerateObject())
                        if (prop.Name != "model" && prop.Name != "prompt" && prop.Name != "size" &&
                            prop.Name != "n" && prop.Name != "response_format" && prop.Name != "seed" &&
                            prop.Name != "image")
                            dict[prop.Name] = JsonSerializer.Deserialize<object>(prop.Value.GetRawText());

                    var url = supplier == "Azure"
                        ? $"{baseUri}/openai/deployments/{node.OriginalModelId}/images/generations?api-version=2024-10-21"
                        : $"{baseUri}/images/generations";
                    return (url, JsonSerializer.Serialize(dict), null);
                }

            case "Azure":
                {
                    var dict = new Dictionary<string, object?>();
                    dict["model"] = node.OriginalModelId;
                    dict["prompt"] = prompt;
                    if (!string.IsNullOrEmpty(size)) dict["size"] = size;
                    if (n > 1) dict["n"] = n;
                    if (!string.IsNullOrEmpty(responseFormat)) dict["response_format"] = responseFormat;
                    if (images.Count > 0) dict["image"] = images.Count == 1 ? (object)images[0] : images;
                    foreach (var prop in root.EnumerateObject())
                        if (prop.Name != "model" && prop.Name != "prompt" && prop.Name != "size" &&
                            prop.Name != "n" && prop.Name != "response_format" && prop.Name != "image")
                            dict[prop.Name] = JsonSerializer.Deserialize<object>(prop.Value.GetRawText());
                    var url = $"{baseUri}/openai/deployments/{node.OriginalModelId}/images/generations?api-version=2024-10-21";
                    return (url, JsonSerializer.Serialize(dict), null);
                }

            case "SiliconFlow":
                {
                    // 硅基：size→image_size；多图拆 image/image2/image3（各自 string）；响应 images 数组 reshape 为 data
                    var dict = new Dictionary<string, object?>();
                    dict["model"] = node.OriginalModelId;
                    dict["prompt"] = prompt;
                    if (!string.IsNullOrEmpty(size)) dict["image_size"] = size;
                    if (n > 1) dict["batch_size"] = n;
                    if (seed >= 0) dict["seed"] = seed;
                    // 多图：硅基用 image/image2/image3 拆字段（Qwen-Image-Edit-2509 支持）
                    if (images.Count > 0) dict["image"] = images[0];
                    if (images.Count > 1) dict["image2"] = images[1];
                    if (images.Count > 2) dict["image3"] = images[2];
                    // 透传 num_inference_steps/guidance_scale/cfg 等
                    foreach (var prop in root.EnumerateObject())
                        if (prop.Name != "model" && prop.Name != "prompt" && prop.Name != "size" &&
                            prop.Name != "n" && prop.Name != "response_format" && prop.Name != "seed" &&
                            prop.Name != "image")
                            dict[prop.Name] = JsonSerializer.Deserialize<object>(prop.Value.GetRawText());

                    var url = $"{baseUri}/images/generations";
                    return (url, JsonSerializer.Serialize(dict), null);
                }

            case "Agnes":
                {
                    // agnes：image 和 response_format 必须塞进 extra_body，不能放顶层
                    var dict = new Dictionary<string, object?>();
                    dict["model"] = node.OriginalModelId;
                    dict["prompt"] = prompt;
                    if (!string.IsNullOrEmpty(size)) dict["size"] = size;
                    if (responseFormat == "b64_json") dict["return_base64"] = true;

                    var extra = new Dictionary<string, object?>();
                    extra["response_format"] = responseFormat;
                    if (images.Count > 0) extra["image"] = images;
                    dict["extra_body"] = extra;

                    foreach (var prop in root.EnumerateObject())
                        if (prop.Name != "model" && prop.Name != "prompt" && prop.Name != "size" &&
                            prop.Name != "n" && prop.Name != "response_format" && prop.Name != "seed" &&
                            prop.Name != "image")
                            dict[prop.Name] = JsonSerializer.Deserialize<object>(prop.Value.GetRawText());

                    var url = $"{baseUri}/images/generations";
                    return (url, JsonSerializer.Serialize(dict), null);
                }

            case "ModelScope":
                {
                    // �魔搭：图生图用 images 字段（base64 数组 [[b64],[b64]]，1-3 张）；默认异步任务模式
                    // URL 参考图需先下载转 base64（魔搭图生图只接受 base64）
                    var dict = new Dictionary<string, object?>();
                    dict["model"] = node.OriginalModelId;
                    dict["prompt"] = prompt;
                    if (!string.IsNullOrEmpty(size)) dict["size"] = size;
                    if (seed >= 0) dict["seed"] = seed;
                    foreach (var prop in root.EnumerateObject())
                        if (prop.Name != "model" && prop.Name != "prompt" && prop.Name != "size" &&
                            prop.Name != "n" && prop.Name != "response_format" && prop.Name != "seed" &&
                            prop.Name != "image")
                            dict[prop.Name] = JsonSerializer.Deserialize<object>(prop.Value.GetRawText());

                    if (images.Count > 0)
                    {
                        var b64List = new List<List<string>>();
                        foreach (var imgRef in images)
                            b64List.Add(new List<string> { await ToBase64Async(imgRef) });
                        dict["images"] = b64List;
                    }

                    var url = $"{baseUri}/images/generations";
                    // 异步任务头
                    var headers = new List<(string, string)> { ("X-ModelScope-Async-Mode", "true") };
                    var body = JsonSerializer.Serialize(dict);
                    // 异步：先 POST 拿 task_id，再轮询 GET /v1/tasks/{id} 直到 SUCCEED/FAILED
                    var finalBody = await PollModelScopeAsync(url, body, node, headers);
                    return (url, finalBody, headers);
                }

            case "SenseNova":
                {
                    // 商汤 U1：size 白名单校验；图生图实际走 chat/completions + modalities:["image"] 包装
                    if (!string.IsNullOrEmpty(size) && !SenseNovaAllowedSizes.Contains(size))
                    {
                        // 非白名单则降级到默认 2048x2048
                        _logger.LogWarning("商汤 size={Size} 不在白名单，降级为 2048x2048", size);
                        size = "2048x2048";
                    }

                    if (images.Count == 0)
                    {
                        // 文生图：走 /v1/images/generations 简化路径
                        var dict = new Dictionary<string, object?>();
                        dict["model"] = node.OriginalModelId;
                        dict["prompt"] = prompt;
                        dict["size"] = size;
                        dict["n"] = n;
                        if (!string.IsNullOrEmpty(responseFormat)) dict["response_format"] = responseFormat;
                        var url = $"{baseUri}/images/generations";
                        return (url, JsonSerializer.Serialize(dict), null);
                    }
                    else
                    {
                        // 图生图：走 chat/completions + modalities:["image"] + content 包装参考图
                        var contentParts = new List<object>();
                        foreach (var imgRef in images)
                            contentParts.Add(new { type = "image_url", image_url = new { url = imgRef } });
                        contentParts.Add(new { type = "text", text = prompt });

                        var dict = new Dictionary<string, object?>
                        {
                            ["model"] = node.OriginalModelId,
                            ["messages"] = new[] { new { role = "user", content = contentParts } },
                            ["modalities"] = new[] { "image" },
                            ["stream"] = false,
                            ["n"] = n,
                            ["image_config"] = new { aspect_ratio = "16:9", image_size = "2K", dynamic_resolution = true }
                        };
                        var url = $"{baseUri}/chat/completions";
                        return (url, JsonSerializer.Serialize(dict), null);
                    }
                }

            case "Xfyun":
                {
                    // 讯飞星火 tti：三段式 header/parameter/payload，参数对齐官方文档
                    // appId 从 node.ExtConfig 解析（ExtConfig JSON {"appId":"xxx"} 或 "appId:xxx"）
                    var appId = ExtractXfyunAppId(node) ?? "";

                    // 默认分辨率 512x512（官方默认值），scheduler 默认 "DPM++ 2M Karras"
                    var width = 512; var height = 512;
                    if (!string.IsNullOrEmpty(size))
                    {
                        var m = Regex.Match(size, @"(\d+)\s*x\s*(\d+)", RegexOptions.IgnoreCase);
                        if (m.Success) { width = int.Parse(m.Groups[1].Value); height = int.Parse(m.Groups[2].Value); }
                    }

                    // header.uid 可空，不写死；parameter.chat 字段对齐官方文档
                    // patch_id 必填（星辰 MaaS schema 校验强制；非微调模型传空数组即可）
                    var headerDict = new Dictionary<string, object?>
                    {
                        ["app_id"] = appId,
                        ["patch_id"] = Array.Empty<string>()
                    };
                    var chatDict = new Dictionary<string, object?>
                    {
                        ["domain"] = node.OriginalModelId,
                        ["width"] = width,
                        ["height"] = height,
                        ["seed"] = seed >= 0 ? seed : 42,
                        ["num_inference_steps"] = 20,
                        ["guidance_scale"] = 5.0,
                        ["scheduler"] = "DPM++ 2M Karras"
                    };

                    var xfyunBody = new Dictionary<string, object?>
                    {
                        ["header"] = headerDict,
                        ["parameter"] = new { chat = chatDict },
                        ["payload"] = new
                        {
                            message = new
                            {
                                text = new[] { new { role = "user", content = prompt } }
                            }
                        }
                    };

                    var bodyStr = JsonSerializer.Serialize(xfyunBody);
                    // 讯飞 tti 文生图走同步端点；图生图 HiDream 异步任务后续增强
                    if (images.Count > 0)
                    {
                        var finalBody = await PollXfyunHiDreamAsync(baseUri, node, images, prompt, appId);
                        var imgPath = "/v2.1/tti";
                        // apiAddress 可能已含完整路径，避免重复拼接成 /v2.1/tti/v2.1/tti
                        var imgFullUrl = baseUri.Contains(imgPath, StringComparison.OrdinalIgnoreCase) ? baseUri : $"{baseUri}{imgPath}";
                        return (imgFullUrl, finalBody, null);
                    }
                    // 讯飞星辰 MaaS 鉴权：Authorization: Bearer ${APIKey}:${APISecret}
                    // 实测 maas-api.cn-huabei-1.xf-yun.com 的 HMAC 网关拒收星火签名串（enforced header 'date' not used），
                    // 但同端点走 OpenAILike 鉴权（Bearer apikey:apisecret）可通过。Bearer 路线不用 HMAC 签名、不要求 Date 头。
                    var xfPath = "/v2.1/tti";
                    var fullUrl = baseUri.Contains(xfPath, StringComparison.OrdinalIgnoreCase) ? baseUri : $"{baseUri}{xfPath}";
                    var bearerToken = $"{node.ApiKey}:{node.ApiKey2}";
                    var xfHeaders = new List<(string, string)>
                    {
                        ("Authorization", $"Bearer {bearerToken}")
                    };
                    return (fullUrl, bodyStr, xfHeaders);
                }

            default:
                // 未知供应商按 OpenAI 兼容处理
                {
                    var dict = new Dictionary<string, object?>();
                    dict["model"] = node.OriginalModelId;
                    dict["prompt"] = prompt;
                    if (!string.IsNullOrEmpty(size)) dict["size"] = size;
                    if (images.Count > 0) dict["image"] = images.Count == 1 ? (object)images[0] : images;
                    foreach (var prop in root.EnumerateObject())
                        if (prop.Name != "model" && prop.Name != "prompt" && prop.Name != "size" &&
                            prop.Name != "n" && prop.Name != "response_format" && prop.Name != "seed" &&
                            prop.Name != "image")
                            dict[prop.Name] = JsonSerializer.Deserialize<object>(prop.Value.GetRawText());
                    var url = $"{baseUri}/images/generations";
                    return (url, JsonSerializer.Serialize(dict), null);
                }
        }
    }

    /// <summary>把参考图统一转成 base64 字符串（魔搭要求）；URL 则下载后编码，data: URI 则直接返回</summary>
    private async Task<string> ToBase64Async(string imageRef)
    {
        if (string.IsNullOrEmpty(imageRef)) return "";
        if (imageRef.StartsWith("data:")) return imageRef; // 已是 data URI
        if (imageRef.StartsWith("http://") || imageRef.StartsWith("https://"))
        {
            try
            {
                var client = _httpClientFactory.CreateClient("AIClient");
                var bytes = await client.GetByteArrayAsync(imageRef);
                return $"data:image/png;base64,{Convert.ToBase64String(bytes)}";
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "下载参考图转 base64 失败: {Url}", imageRef[..Math.Min(imageRef.Length, 100)]);
                return imageRef; // 失败则原样回退
            }
        }
        return imageRef;
    }

    /// <summary>魔搭异步任务轮询：POST 建 task → GET /v1/tasks/{id} 轮询至 SUCCEED/FAILED，返回最终结果 JSON</summary>
    private async Task<string> PollModelScopeAsync(string url, string body, LoadBalanceNode node, List<(string, string)> headers)
    {
        var client = _httpClientFactory.CreateClient("AIClient");
        client.Timeout = TimeSpan.FromSeconds(Math.Max(node.TimeoutSeconds, 180));

        using var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        req.Headers.TryAddWithoutValidation("X-ModelScope-Async-Mode", "true");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", node.ApiKey);

        var resp = await client.SendAsync(req);
        var respBody = await resp.Content.ReadAsStringAsync();
        if (!resp.IsSuccessStatusCode) return respBody;

        string taskId;
        try
        {
            using var d = JsonDocument.Parse(respBody);
            taskId = d.RootElement.TryGetProperty("task_id", out var tid) ? tid.GetString() ?? "" : "";
        }
        catch { return respBody; }

        if (string.IsNullOrEmpty(taskId)) return respBody;

        // 轮询（最多约 5 分钟）
        for (int i = 0; i < 60; i++)
        {
            await Task.Delay(5000);
            try
            {
                var pollReq = new HttpRequestMessage(HttpMethod.Get, $"{url}/../../tasks/{taskId}");
                pollReq.Headers.TryAddWithoutValidation("X-ModelScope-Task-Type", "image_generation");
                pollReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", node.ApiKey);
                var pollResp = await client.SendAsync(pollReq);
                var pollBody = await pollResp.Content.ReadAsStringAsync();
                using var d2 = JsonDocument.Parse(pollBody);
                var status = d2.RootElement.TryGetProperty("task_status", out var st) ? st.GetString() : "";
                if (status == "SUCCEED")
                {
                    // reshape 成 OpenAI images 格式
                    var urls = new List<string>();
                    if (d2.RootElement.TryGetProperty("output_images", out var imgs) && imgs.ValueKind == JsonValueKind.Array)
                        foreach (var u in imgs.EnumerateArray()) urls.Add(u.GetString() ?? "");
                    return JsonSerializer.Serialize(new { created = DateTimeOffset.UtcNow.ToUnixTimeSeconds(), data = urls.Select(u => new { url = u }).ToArray() });
                }
                if (status == "FAILED") return pollBody;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "魔搭任务轮询异常 taskId={Id}", taskId);
            }
        }
        return JsonSerializer.Serialize(new { error = new { message = "魔搭任务超时" } });
    }

    /// <summary>讯飞 HiDream 图生图异步任务轮询：建任务 → 轮询取图 → reshape 为 OpenAI 格式</summary>
    private async Task<string> PollXfyunHiDreamAsync(string baseUri, LoadBalanceNode node, List<string> images, string prompt, string appId)
    {
        // 讯飞 HiDream 图生图异步任务：建任务 → 轮询查询 → 取图 reshape 为 OpenAI 格式
        // 建任务端点: POST /v1/private/s3fd61810/create，payload.oig.text 为 base64(JSON{image,prompt,aspect_ratio,img_count,resolution})
        // 查询端点:   POST /v1/private/s3fd61810/query，带 task_id 轮询，task_status=3 完成，payload.result.text base64 解码得图片 url 数组
        var client = _httpClientFactory.CreateClient("AIClient");
        client.Timeout = TimeSpan.FromSeconds(Math.Max(node.TimeoutSeconds, 180));

        // 1. 构建建任务请求体（text 字段是 base64 编码的 JSON）
        var textJson = JsonSerializer.Serialize(new
        {
            image = images,                          // 参考图数组（url 或 base64）
            prompt,                                  // 提示词
            aspect_ratio = "1:1",
            negative_prompt = "",
            img_count = 1,
            resolution = "2k"
        });
        var textBase64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(textJson));

        var createPath = "/v1/private/s3fd61810/create";
        var createBody = JsonSerializer.Serialize(new
        {
            header = new { app_id = appId, status = 3, channel = "default", callback_url = "default" },
            parameter = new
            {
                oig = new
                {
                    result = new { encoding = "utf8", compress = "raw", format = "json" }
                }
            },
            payload = new
            {
                oig = new
                {
                    encoding = "utf8", compress = "raw", format = "json", status = 3,
                    text = textBase64
                }
            }
        });

        // 2. 鉴权（建任务端点）：星辰 MaaS 走 Bearer apikey:apisecret，不用 HMAC 签名
        var createHeaders = BuildXfyunBearerHeaders(node.ApiKey, node.ApiKey2);
        var createReq = new HttpRequestMessage(HttpMethod.Post, $"{baseUri}{createPath}")
        {
            Content = new StringContent(createBody, Encoding.UTF8, "application/json")
        };
        if (createHeaders != null)
            foreach (var h in createHeaders) createReq.Headers.TryAddWithoutValidation(h.Key, h.Value);

        // 3. 发起建任务请求
        var createResp = await client.SendAsync(createReq);
        var createRespBody = await createResp.Content.ReadAsStringAsync();
        if (!createResp.IsSuccessStatusCode)
            return JsonSerializer.Serialize(new { error = new { message = $"讯飞 HiDream 建任务失败: HTTP {(int)createResp.StatusCode} {createRespBody}" } });

        string taskId;
        try
        {
            using var d = JsonDocument.Parse(createRespBody);
            taskId = d.RootElement.TryGetProperty("header", out var hdr) && hdr.TryGetProperty("task_id", out var tid) ? tid.GetString() ?? "" : "";
        }
        catch { return JsonSerializer.Serialize(new { error = new { message = "讯飞 HiDream 建任务响应解析失败" } }); }
        if (string.IsNullOrEmpty(taskId))
            return JsonSerializer.Serialize(new { error = new { message = "讯飞 HiDream 建任务未返回 task_id" } });

        // 4. 轮询查询任务状态（最多约 5 分钟）
        var queryPath = "/v1/private/s3fd61810/query";
        var queryBody = JsonSerializer.Serialize(new { header = new { app_id = appId, task_id = taskId } });
        var queryHeaders = BuildXfyunBearerHeaders(node.ApiKey, node.ApiKey2);

        for (int i = 0; i < 60; i++)
        {
            await Task.Delay(5000);
            try
            {
                var qReq = new HttpRequestMessage(HttpMethod.Post, $"{baseUri}{queryPath}")
                {
                    Content = new StringContent(queryBody, Encoding.UTF8, "application/json")
                };
                if (queryHeaders != null)
                    foreach (var h in queryHeaders) qReq.Headers.TryAddWithoutValidation(h.Key, h.Value);

                var qResp = await client.SendAsync(qReq);
                var qBody = await qResp.Content.ReadAsStringAsync();
                using var d2 = JsonDocument.Parse(qBody);
                var hdr2 = d2.RootElement.TryGetProperty("header", out var h2) ? h2 : default;
                var taskStatus = hdr2.TryGetProperty("task_status", out var ts) ? ts.GetString() : "";

                if (taskStatus == "3" || taskStatus == "4")
                {
                    // 处理完成：payload.result.text 是 base64 编码的图片信息 JSON
                    if (d2.RootElement.TryGetProperty("payload", out var payload) &&
                        payload.TryGetProperty("result", out var result) &&
                        result.TryGetProperty("text", out var textEl))
                    {
                        var textBase64Result = textEl.GetString() ?? "";
                        var textJsonResult = Encoding.UTF8.GetString(Convert.FromBase64String(textBase64Result));
                        // 解析图片 url 数组（text JSON 结构为 {"image": ["url1","url2"]} 或类似）
                        List<string> imgUrls = new();
                        try
                        {
                            using var d3 = JsonDocument.Parse(textJsonResult);
                            if (d3.RootElement.TryGetProperty("image", out var imgsArr) && imgsArr.ValueKind == JsonValueKind.Array)
                                foreach (var u in imgsArr.EnumerateArray()) imgUrls.Add(u.GetString() ?? "");
                            else if (d3.RootElement.TryGetProperty("image", out var imgStr) && imgStr.ValueKind == JsonValueKind.String)
                                imgUrls.Add(imgStr.GetString() ?? "");
                        }
                        catch { /* 解析失败则原样返回 */ }

                        return JsonSerializer.Serialize(new
                        {
                            created = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                            data = imgUrls.Select(u => (object)new { url = u }).ToArray()
                        });
                    }
                    return JsonSerializer.Serialize(new { error = new { message = "讯飞 HiDream 任务完成但未返回图片数据" } });
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "讯飞 HiDream 任务轮询异常 taskId={Id}", taskId);
            }
        }
        return JsonSerializer.Serialize(new { error = new { message = "讯飞 HiDream 任务超时" } });
    }

    /// <summary>从 node.ExtConfig 或 Remark 解析讯飞 appId</summary>
    private static string? ExtractXfyunAppId(LoadBalanceNode node)
    {
        // 约定：Channel.Remark 存 "appId:xxx" 或 ExtConfig JSON {"appId":"xxx"}
        var remark = node.ExtConfig;
        if (string.IsNullOrEmpty(remark)) return null;
        if (remark.StartsWith("{"))
        {
            try
            {
                using var d = JsonDocument.Parse(remark);
                return d.RootElement.TryGetProperty("appId", out var ap) ? ap.GetString() : null;
            }
            catch { return null; }
        }
        var m = Regex.Match(remark, @"appId\s*[:：]\s*(\S+)", RegexOptions.IgnoreCase);
        return m.Success ? m.Groups[1].Value : null;
    }

    // ============== 响应转换（统一归一到 OpenAI {created, data:[{url|b64_json}]}） ==============

    private string ConvertImageResponse(string body, LoadBalanceNode node)
    {
        var supplier = node.SupplierType ?? "OpenAI";
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;

            return supplier switch
            {
                // OpenAI 兼容族 + 豆包/火山 + Azure + 商汤文生图：已是标准格式，透传
                "OpenAI" or "Azure" or "DeepSeek" or "Groq" or "Together" or "Custom" or "VolcEngine" or "SenseNova" => body,

                // 硅基：{images:[{url}]} → {data:[{url}]}
                "SiliconFlow" => root.TryGetProperty("images", out var imgs)
                    ? JsonSerializer.Serialize(new { created = DateTimeOffset.UtcNow.ToUnixTimeSeconds(), data = imgs.EnumerateArray().Select(i => (object)new { url = i.TryGetProperty("url", out var u) ? u.GetString() : "" }).ToArray() })
                    : body,

                // agnes：已是标准 {created, data:[{url|b64_json}]}，透传
                "Agnes" => body,

                // 魔搭：异步轮询已 reshape，透传；若未走异步（同步返回），images 字段 reshape
                "ModelScope" => root.TryGetProperty("data", out _) ? body
                    : root.TryGetProperty("images", out var msImgs)
                        ? JsonSerializer.Serialize(new { created = DateTimeOffset.UtcNow.ToUnixTimeSeconds(), data = msImgs.EnumerateArray().Select(i => (object)new { url = i.TryGetProperty("url", out var u) ? u.GetString() : "" }).ToArray() })
                        : body,

                // 讯飞：{payload:{choices:{text:[{content:base64}]}}} → {data:[{b64_json}]}
                "Xfyun" => ConvertXfyunResponse(root),

                _ => body
            };
        }
        catch { return body; }
    }

    private static string ConvertXfyunResponse(JsonElement root)
    {
        var b64List = new List<string>();
        try
        {
            if (root.TryGetProperty("payload", out var payload) &&
                payload.TryGetProperty("choices", out var choices) &&
                choices.TryGetProperty("text", out var texts) &&
                texts.ValueKind == JsonValueKind.Array)
            {
                foreach (var t in texts.EnumerateArray())
                    if (t.TryGetProperty("content", out var c)) b64List.Add(c.GetString() ?? "");
            }
        }
        catch { }
        return JsonSerializer.Serialize(new
        {
            created = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            data = b64List.Select(b => (object)new { b64_json = b }).ToArray()
        });
    }

    // ============== 鉴权 ==============

    private static void SetAuthHeaders(HttpRequestMessage request, string supplierType, string apiKey, string? apiKey2)
    {
        switch (supplierType)
        {
            case "Azure":
                request.Headers.TryAddWithoutValidation("api-key", apiKey);
                break;
            // 讯飞鉴权在 BuildUpstreamRequest 内通过 ExtraHeaders 完整构造（Bearer apikey:apisecret）后传入，
            // 此处不重复处理——若在此先设 Authorization，extraHeaders 的 TryAddWithoutValidation 会因头已存在而静默失败，
            // 导致发出去的是缺 apisecret 的错头。
            case "Xfyun":
                break;
            default:
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
                break;
        }
    }

    /// <summary>
    /// 讯飞星辰 MaaS OpenAILike 鉴权（对齐 docs.iflyaicloud.com/doc/227 规范）：
    /// Authorization: Bearer ${api_key}:${api_secret} —— 不用 HMAC 签名、不要求 Date 头。
    /// 实测 maas-api.cn-huabei-1.xf-yun.com 的 HMAC 网关拒收星火签名串
    /// （"enforced header 'date' not used for signature creation"），同端点走 Bearer 鉴权可通过。
    /// </summary>
    private static List<(string Key, string Value)>? BuildXfyunBearerHeaders(string apiKey, string? apiSecret)
    {
        if (string.IsNullOrEmpty(apiKey) || string.IsNullOrEmpty(apiSecret))
            return null;
        return new List<(string, string)>
        {
            ("Authorization", $"Bearer {apiKey}:{apiSecret}")
        };
    }

    // ============== 用量与计费 ==============

    private (int input, int output) ExtractImageUsage(string responseBody)
    {
        // 图片计费兼容口径：优先用 usage.output_tokens（豆包返回），否则按生成张数估算（每张 1 token 计费占位）
        try
        {
            using var doc = JsonDocument.Parse(responseBody);
            var root = doc.RootElement;
            if (root.TryGetProperty("usage", out var usage))
            {
                var output = usage.TryGetProperty("output_tokens", out var ot) ? ot.GetInt32()
                           : usage.TryGetProperty("completion_tokens", out var ct) ? ct.GetInt32()
                           : usage.TryGetProperty("generated_images", out var gi) ? gi.GetInt32() * 1000
                           : 0;
                var input = usage.TryGetProperty("prompt_tokens", out var pt) ? pt.GetInt32() : 0;
                if (input > 0 || output > 0) return (input, output);
            }
            // 按生成张数估算
            if (root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
                return (0, data.GetArrayLength() * 1000);
            if (root.TryGetProperty("images", out var imgs) && imgs.ValueKind == JsonValueKind.Array)
                return (0, imgs.GetArrayLength() * 1000);
        }
        catch { }
        return (0, 0);
    }

    // ============== 图片存档（复用 CallImage 落盘，不做代理） ==============

    private async Task SaveCallImagesAsync(long callLogId, string? requestBody, string? responseBody)
    {
        try
        {
            if (!string.IsNullOrEmpty(requestBody))
                await ExtractAndSaveImagesAsync(callLogId, "request", requestBody);
            if (!string.IsNullOrEmpty(responseBody))
                await ExtractAndSaveImagesAsync(callLogId, "response", responseBody);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "保存图片调用日志失败, CallLogId={Id}", callLogId);
        }
    }

    private async Task ExtractAndSaveImagesAsync(long callLogId, string source, string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;

            int idx = 0;
            // 1. 响应：data[].url / data[].b64_json
            if (root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in data.EnumerateArray())
                {
                    if (item.TryGetProperty("url", out var u)) await SaveOneImageAsync(callLogId, source, u.GetString() ?? "", idx++);
                    else if (item.TryGetProperty("b64_json", out var b))
                    {
                        var b64 = b.GetString() ?? "";
                        await SaveOneImageAsync(callLogId, source, $"data:image/png;base64,{b64}", idx++);
                    }
                }
            }
            // 2. 硅基响应：images[].url
            if (root.TryGetProperty("images", out var imgs) && imgs.ValueKind == JsonValueKind.Array)
                foreach (var item in imgs.EnumerateArray())
                    if (item.TryGetProperty("url", out var u)) await SaveOneImageAsync(callLogId, source, u.GetString() ?? "", idx++);

            // 3. 请求：顶层 image 字段（string 或 array）
            if (root.TryGetProperty("image", out var img))
            {
                if (img.ValueKind == JsonValueKind.String) await SaveOneImageAsync(callLogId, source, img.GetString() ?? "", idx++);
                else if (img.ValueKind == JsonValueKind.Array)
                    foreach (var i in img.EnumerateArray()) await SaveOneImageAsync(callLogId, source, i.GetString() ?? "", idx++);
            }
        }
        catch { /* 非标准结构，跳过 */ }
    }

    private async Task SaveOneImageAsync(long callLogId, string source, string rawValue, int contentIndex)
    {
        try
        {
            var img = new CallImage { CallLogId = callLogId, Source = source, ContentIndex = contentIndex };
            if (rawValue.StartsWith("data:"))
            {
                img.ImageType = "base64";
                var semiIdx = rawValue.IndexOf(';');
                var commaIdx = rawValue.IndexOf(',');
                if (semiIdx > 0 && commaIdx > semiIdx)
                {
                    img.MimeType = rawValue[5..semiIdx];
                    var base64Data = rawValue[(commaIdx + 1)..];
                    var ext = img.MimeType?.Split('/')?.LastOrDefault() ?? "png";
                    var dateDir = DateTime.UtcNow.ToString("yyyyMMdd");
                    var fileName = $"{callLogId}_{source}_{contentIndex}.{ext}";
                    var relativePath = Path.Combine("logs", "images", dateDir, fileName);
                    var fullDir = Path.Combine(AppContext.BaseDirectory, "logs", "images", dateDir);
                    Directory.CreateDirectory(fullDir);
                    await File.WriteAllBytesAsync(Path.Combine(fullDir, fileName), Convert.FromBase64String(base64Data));
                    img.FilePath = relativePath;
                }
            }
            else
            {
                img.ImageType = "url";
                img.ImageUrl = rawValue[..Math.Min(rawValue.Length, 2000)];
            }
            img.CreatedAt = DateTime.UtcNow;
            await _callImageRepo.InsertAsync(img);
        }
        catch { /* 单张失败不阻塞主流程 */ }
    }

    // ============== 节点轮询/冷却/重试/日志（与 ForwardEngine 同构，独立持有避免跨类耦合） ==============

    private async Task<List<LoadBalanceNode>> GetActiveNodesAsync(List<LoadBalanceNode> allNodes)
    {
        var priorityGroups = allNodes.GroupBy(n => n.Priority).OrderBy(g => g.Key);
        foreach (var group in priorityGroups)
        {
            var available = new List<LoadBalanceNode>();
            foreach (var node in group)
                if (!await _cache.IsInCooldownAsync(CooldownKey(node))) available.Add(node);
            if (available.Count > 0) return available;
        }
        return new List<LoadBalanceNode>();
    }

    private static string CooldownKey(LoadBalanceNode node) => $"cooldown:image:{node.ChannelId}:{node.OriginalModelId}:{node.ApiKeyId}";

    private async Task SetCooldownAsync(LoadBalanceNode node, int seconds) => await _cache.SetCooldownAsync(CooldownKey(node), seconds);

    private async Task HandleError(HttpResponseMessage response, LoadBalanceNode node)
    {
        var statusCode = (int)response.StatusCode;
        switch (statusCode)
        {
            case 429: await SetCooldownAsync(node, Math.Min(node.CooldownSeconds, 30)); break;
            case 401:
            case 403:
                try
                {
                    var apiKey = await _apiKeyRepo.GetByIdAsync(node.ApiKeyId);
                    if (apiKey != null)
                    {
                        apiKey.Status = 2;
                        await _apiKeyRepo.UpdateAsync(apiKey);
                        _logger.LogWarning("API Key 永久失效：渠道 {Channel}/{Model} KeyId={KeyId} 状态码={StatusCode}",
                            node.ChannelName, node.OriginalModelId, node.ApiKeyId, statusCode);
                    }
                }
                catch (Exception ex) { _logger.LogError(ex, "标记 API Key 永久失效失败: {KeyId}", node.ApiKeyId); }
                break;
            case >= 500: await SetCooldownAsync(node, Math.Min(node.CooldownSeconds, 15)); break;
        }
    }

    private static bool ShouldRetry(int statusCode) => statusCode switch
    {
        408 => true, 401 => true, 403 => true, 429 => true, >= 500 => true, _ => false
    };

    private static string ExtractDownstreamError(string responseBody)
    {
        if (string.IsNullOrEmpty(responseBody)) return "";
        try
        {
            using var doc = JsonDocument.Parse(responseBody);
            var root = doc.RootElement;
            if (root.TryGetProperty("error", out var err))
            {
                if (err.TryGetProperty("message", out var msg)) return msg.GetString() ?? "";
                if (err.ValueKind == JsonValueKind.String) return err.GetString() ?? "";
            }
            return responseBody[..Math.Min(responseBody.Length, 200)];
        }
        catch { return responseBody[..Math.Min(responseBody.Length, 200)]; }
    }

    private async Task<List<LoadBalanceNode>> SelectNodesWeightedRoundRobin(List<LoadBalanceNode> candidates)
    {
        var groups = candidates.GroupBy(n => $"{n.ChannelId}:{n.OriginalModelId}");
        var result = new List<LoadBalanceNode>();
        foreach (var group in groups)
        {
            var nodes = group.ToList();
            if (nodes.Count == 1) result.Add(nodes[0]);
            else
            {
                var rrKey = $"roundrobin:image:{group.Key}";
                var index = await _cache.IncrementAsync(rrKey);
                var selectedIndex = (int)((index - 1) % (uint)nodes.Count);
                var ordered = new List<LoadBalanceNode> { nodes[selectedIndex] };
                ordered.AddRange(nodes.Where((_, i) => i != selectedIndex).OrderByDescending(n => n.Weight));
                result.AddRange(ordered);
            }
        }
        return result.OrderByDescending(n => n.Weight).ToList();
    }

    private async Task<long> LogCall(string? tokenValue, string? customModelId, string? channelName,
        string? originalModelId, string status, bool isStream, int inputTokens, int outputTokens,
        long durationMs, string? requestBody, string? responseBody, string? error, string clientIp)
    {
        try
        {
            var log = new CallLog
            {
                TokenValue = tokenValue, ClientIp = clientIp,
                CustomModelId = customModelId, OriginalModelId = originalModelId, ChannelName = channelName,
                Status = status, IsStream = isStream, InputTokens = inputTokens, OutputTokens = outputTokens,
                DurationMs = durationMs,
                RequestBody = requestBody?[..Math.Min(requestBody.Length, 10000)],
                ResponseBody = responseBody?[..Math.Min(responseBody.Length, 5000)],
                ErrorMessage = error?[..Math.Min(error.Length, 2000)],
            };
            await _callLogRepo.InsertAsync(log);
            return log.Id;
        }
        catch { return 0; }
    }

    private async Task RecordUpstreamUsage(long apiKeyId, int inputTokens, int outputTokens)
    {
        try
        {
            var apiKey = await _apiKeyRepo.GetByIdAsync(apiKeyId);
            if (apiKey == null) return;
            apiKey.UsedTokens += inputTokens + outputTokens;
            apiKey.TotalCalls++;
            apiKey.SuccessCalls++;
            await _apiKeyRepo.UpdateAsync(apiKey);
        }
        catch (Exception ex) { _logger.LogError(ex, "记录上游API Key用量失败, KeyId={KeyId}", apiKeyId); }
    }
}
