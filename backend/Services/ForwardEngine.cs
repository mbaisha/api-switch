using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using backend.Common.Models;
using backend.Common.Utils;
using backend.Repository;

namespace backend.Services;

/// <summary>
/// AI接口转发引擎 - 核心转发逻辑（节点冷却使用 Redis 实现分布式）
/// </summary>
public class ForwardEngine
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ChannelService _channelService;
    private readonly TokenService _tokenService;
    private readonly BaseRepository<CallLog> _callLogRepo;
    private readonly BaseRepository<ApiKey> _apiKeyRepo;
    private readonly BaseRepository<CallImage> _callImageRepo;
    private readonly BillingService _billingService;
    private readonly RedisCacheService _cache;
    private readonly ILogger<ForwardEngine> _logger;

    public ForwardEngine(
        IHttpClientFactory httpClientFactory,
        ChannelService channelService,
        TokenService tokenService,
        BillingService billingService,
        IFreeSql db,
        ILogger<ForwardEngine> logger,
        RedisCacheService cache)
    {
        _httpClientFactory = httpClientFactory;
        _channelService = channelService;
        _tokenService = tokenService;
        _billingService = billingService;
        _callLogRepo = new BaseRepository<CallLog>(db);
        _apiKeyRepo = new BaseRepository<ApiKey>(db);
        _callImageRepo = new BaseRepository<CallImage>(db);
        _logger = logger;
        _cache = cache;
    }

    /// <summary>
    /// 转发请求 - 标准非流式
    /// </summary>
    public async Task<(string ResponseBody, int StatusCode, string? Error)> ForwardAsync(
        string tokenValue, string customModelId, string requestBody,
        string requestPath, string clientIp, bool isStream)
    {
        var startTime = DateTime.UtcNow;

        // 1. 验证令牌
        var (valid, token, msg) = await _tokenService.ValidateToken(tokenValue);
        if (!valid)
        {
            await LogCall(tokenValue, customModelId, null, null, "Failed", isStream, 0, 0,
                (long)(DateTime.UtcNow - startTime).TotalMilliseconds, requestBody, null, msg, clientIp);
            return (JsonSerializer.Serialize(new { error = new { message = msg } }), 401, msg);
        }

        // 2. 检查模型权限
        var hasPermission = await _tokenService.CheckModelPermission(token!.Id, customModelId);
        if (!hasPermission)
        {
            await LogCall(tokenValue, customModelId, null, null, "Failed", isStream, 0, 0,
                (long)(DateTime.UtcNow - startTime).TotalMilliseconds, requestBody, null, "无模型调用权限", clientIp);
            return (JsonSerializer.Serialize(new { error = new { message = "无该模型调用权限" } }), 403, "无权限");
        }

        // 3. 获取所有节点
        var allNodes = await _channelService.GetNodesByCustomModelId(customModelId);
        if (allNodes.Count == 0)
        {
            var fallbackMsg = "未配置任何可用渠道";
            await LogCall(tokenValue, customModelId, null, null, "Failed", isStream, 0, 0,
                (long)(DateTime.UtcNow - startTime).TotalMilliseconds, requestBody, null, fallbackMsg, clientIp);
            return (JsonSerializer.Serialize(new { error = new { message = fallbackMsg } }), 503, fallbackMsg);
        }

        // 4. 带重试的轮询转发：
        //    - 按优先级分组，跳过冷却中的节点
        //    - 同优先级内通过 Redis 轮询选择 API Key
        //    - 上一个密钥失败 → 等待 5 秒 → 重新评估可用节点 → 继续下一个
        //    - 所有节点冷却 → 等待 5 秒再试（等待冷却过期）
        //    - 最多尝试 18 次（约 90 秒超时）
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
                // 全部节点都在冷却中，等待后重试
                _logger.LogWarning("第 {Attempt} 次尝试：所有节点均在冷却中，等待 {Delay}ms 后重试",
                    attempt + 1, retryDelayMs);
                await Task.Delay(retryDelayMs);
                continue;
            }

            // 轮询选择 API Key（每组 Channel+Model 选一个）
            _logger.LogInformation("======== 节点状态: 候选节点数={Count}", activeNodes.Count);
            foreach (var n in activeNodes)
                _logger.LogInformation("  候选节点: Channel={Ch} Model={M} Key={K} KeyId={Id} Weight={W} Priority={P}",
                    n.ChannelName, n.OriginalModelId,
                    n.ApiKey[..Math.Min(n.ApiKey.Length, 8)], n.ApiKeyId, n.Weight, n.Priority);
            var candidates = await SelectNodesWeightedRoundRobin(activeNodes);

            bool anyAttempted = false;
            var clientType = GetRequestType(requestPath);
            var clientPath = GetTypePath(clientType);

            foreach (var node in candidates)
            {
                anyAttempted = true;
                _logger.LogInformation("尝试转发 → 渠道 {Channel}/{Model} API Key={Key} (第 {Attempt} 轮)",
                    node.ChannelName, node.OriginalModelId,
                    node.ApiKey[..Math.Min(node.ApiKey.Length, 8)], attempt + 1);

                // 检查该路径是否勾选了「支持透传」
                var passthroughPaths = (node.PassthroughPaths ?? node.SupportedPaths ?? "chat")
                    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Select(p => p.Trim().ToLowerInvariant())
                    .ToHashSet();
                var doPassthrough = passthroughPaths.Contains(clientType);

                string fullUrl;
                string convertedBody;

                if (doPassthrough)
                {
                    // 透传：只替换 model ID，走客户端原始路径
                    fullUrl = BuildDownstreamUrl(node.ApiAddress, node.SupplierType, clientPath, node.OriginalModelId);
                    convertedBody = ConvertRequestBody(requestBody, clientPath, node);
                    _logger.LogInformation("透传 → 渠道 {Channel}/{Model}，路径 {Path}",
                        node.ChannelName, node.OriginalModelId, clientPath);
                }
                else
                {
                    // 降级：转换到 FallbackTarget 格式
                    var fallbackTarget = (node.FallbackTarget ?? node.ProtocolType ?? "Chat").ToLowerInvariant() switch
                    {
                        "response" or "responses" => "responses",
                        "messages" => "messages",
                        _ => "chat"
                    };
                    var downstreamPath = GetTypePath(fallbackTarget);
                    fullUrl = BuildDownstreamUrl(node.ApiAddress, node.SupplierType, downstreamPath, node.OriginalModelId);
                    var bodyForUpstream = fallbackTarget == "messages"
                        ? ConvertRequestToMessages(requestBody, node)
                        : ConvertRequestToChat(requestBody, requestPath);
                    convertedBody = ConvertRequestBody(bodyForUpstream, downstreamPath, node);
                    _logger.LogInformation("降级 → 渠道 {Channel}/{Model}，目标={Fallback}，路径 {Path}",
                        node.ChannelName, node.OriginalModelId, fallbackTarget, downstreamPath);
                }

                _logger.LogInformation("发送到上游 {Url} 的请求体: {Body}", fullUrl, 
                    convertedBody.Length > 1000 ? convertedBody[..1000] + "..." : convertedBody);

                try
                {
                    var client = _httpClientFactory.CreateClient("AIClient");
                    client.Timeout = TimeSpan.FromSeconds(node.TimeoutSeconds);

                    var httpRequest = new HttpRequestMessage(HttpMethod.Post, fullUrl)
                    {
                        Content = new StringContent(convertedBody, Encoding.UTF8, "application/json")
                    };
                    SetAuthHeaders(httpRequest, node.SupplierType, node.ApiKey);
                    httpRequest.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

                    var response = await client.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead);
                    var responseBody = await response.Content.ReadAsStringAsync();

                    if (!response.IsSuccessStatusCode)
                    {
                        var sc = (int)response.StatusCode;
                        await HandleError(response, node);
                        var downstreamErr = ExtractDownstreamError(responseBody);
                        lastErrorMsg = downstreamErr.Length > 0
                            ? $"上游[{sc}]: {downstreamErr}"
                            : $"上游接口返回错误: {sc}";
                        lastStatusCode = sc;

                        await LogCall(tokenValue, customModelId, node.ChannelName, node.OriginalModelId, "Failed",
                            isStream, 0, 0, (long)(DateTime.UtcNow - startTime).TotalMilliseconds,
                            requestBody, responseBody, lastErrorMsg + " (等待5秒后重试)", clientIp);

                        if (ShouldRetry(sc))
                        {
                            _logger.LogWarning("第 {Attempt} 次尝试：节点 {Channel}/{Model}（Key={Key}）返回 {StatusCode}，切换下一个API Key",
                                attempt + 1, node.ChannelName, node.OriginalModelId,
                                node.ApiKey[..Math.Min(node.ApiKey.Length, 8)], sc);
                            await Task.Delay(retryDelayMs);
                            continue; // 继续尝试 candidates 中的下一个节点
                        }

                        // 不可重试的错误（如 400 参数错误），直接返回
                        foundUnrecoverable = true;
                        return (JsonSerializer.Serialize(new { error = new { message = "请求失败，请检查参数" } }),
                            sc, lastErrorMsg);
                    }

                    // 成功！
                    var baseResponse = ConvertResponseBody(responseBody, node.ProtocolType, requestPath, node);
                    var convertedResponse = baseResponse;
                    var (inputTokens, outputTokens) = ExtractTokenUsage(convertedResponse);

                    await _tokenService.RecordUsage(token.Id, inputTokens, outputTokens);
                    await _billingService.RecordBilling(token.Id, tokenValue, customModelId, inputTokens, outputTokens);

                    var logId = await LogCall(tokenValue, customModelId, node.ChannelName, node.OriginalModelId, "Success",
                        isStream, inputTokens, outputTokens, (long)(DateTime.UtcNow - startTime).TotalMilliseconds,
                        requestBody, convertedResponse, null, clientIp);

                    // 记录上游API Key使用统计
                    await RecordUpstreamUsage(node.ApiKeyId, inputTokens, outputTokens);

                    // 提取并保存请求/响应中的图片资源
                    if (logId > 0)
                        await SaveCallImagesAsync(logId, requestBody, convertedResponse);

                    return (convertedResponse, 200, null);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "转发请求异常，节点: {Channel}/{Model}，第 {Attempt} 次尝试",
                        node.ChannelName, node.OriginalModelId, attempt + 1);
                    await SetCooldownAsync(node, node.CooldownSeconds);
                    lastEx = ex;
                    lastErrorMsg = ex.Message;
                    lastStatusCode = 503;

                    await LogCall(tokenValue, customModelId, node.ChannelName, node.OriginalModelId, "Failed",
                        isStream, 0, 0, (long)(DateTime.UtcNow - startTime).TotalMilliseconds,
                        requestBody, null, ex.Message + " (等待5秒后重试)", clientIp);

                    await Task.Delay(retryDelayMs);
                    continue; // 继续尝试 candidates 中的下一个节点
                }
            }

            // 本轮所有候选节点都试过了且均失败，等待后进入下一轮（重新评估优先级和冷却）
            if (attempt < maxAttempts - 1 && anyAttempted)
                await Task.Delay(retryDelayMs);
        }

        // 所有重试都失败
        var failMsg = lastEx != null
            ? $"所有渠道均不可用: {lastEx.Message}"
            : $"所有渠道均不可用: {lastErrorMsg}";
        _logger.LogError("转发失败，已耗尽 {MaxAttempts} 次重试: {Msg}", maxAttempts, failMsg);
        return (JsonSerializer.Serialize(new { error = new { message = "服务暂时不可用，请稍后重试" } }),
            lastStatusCode, failMsg);
    }

    /// <summary>
    /// SSE流式转发
    /// </summary>
    public async Task StreamForwardAsync(
        HttpContext context, string tokenValue, string customModelId,
        string requestBody, string requestPath, string clientIp)
    {
        var startTime = DateTime.UtcNow;

        // 1. 验证令牌
        var (valid, token, msg) = await _tokenService.ValidateToken(tokenValue);
        if (!valid)
        {
            context.Response.StatusCode = 401;
            await context.Response.WriteAsync(JsonSerializer.Serialize(new { error = new { message = msg } }));
            return;
        }

        // 2. 检查模型权限
        var hasPermission = await _tokenService.CheckModelPermission(token!.Id, customModelId);
        if (!hasPermission)
        {
            context.Response.StatusCode = 403;
            await context.Response.WriteAsync(JsonSerializer.Serialize(new { error = new { message = "无该模型调用权限" } }));
            return;
        }

        // 3. 获取所有节点，带重试的轮询连接（与普通转发相同策略）
        var allSseNodes = await _channelService.GetNodesByCustomModelId(customModelId);
        if (allSseNodes.Count == 0)
        {
            context.Response.StatusCode = 503;
            await context.Response.WriteAsync(JsonSerializer.Serialize(
                new { error = new { message = "未配置任何可用渠道" } }));
            return;
        }

        HttpResponseMessage? successResponse = null;
        StreamReader? successReader = null;
        LoadBalanceNode? usedNode = null;
        string? lastError = null;

        const int maxAttempts = 18;
        const int retryDelayMs = 5000;

        for (int attempt = 0; attempt < maxAttempts; attempt++)
        {
            var sseNodes = await GetActiveSseNodesAsync(allSseNodes);

            if (sseNodes.Count == 0)
            {
                _logger.LogWarning("SSE 第 {Attempt} 次尝试：所有节点均在冷却中，等待 {Delay}ms 后重试",
                    attempt + 1, retryDelayMs);
                await Task.Delay(retryDelayMs);
                continue;
            }

            // 轮询选择 API Key
            var candidates = await SelectNodesWeightedRoundRobin(sseNodes);

            foreach (var node in candidates)
            {
                var (upstreamPath, needsConversion) = DetermineUpstreamPath(requestPath, node);
                var fullUrl = BuildDownstreamUrl(node.ApiAddress, node.SupplierType, upstreamPath, node.OriginalModelId);
                // 协议降级时，根据上游协议类型选择合适的请求体转换
                var isMessagesUpstream = "Messages".Equals(node.ProtocolType, StringComparison.OrdinalIgnoreCase);
                var bodyForUpstream = needsConversion
                    ? (isMessagesUpstream
                        ? ConvertRequestToMessages(requestBody, node)
                        : ConvertRequestToChat(requestBody, requestPath))
                    : requestBody;
                var convertedBody = ConvertRequestBody(bodyForUpstream, upstreamPath, node);

                _logger.LogInformation("发送到上游 {Url} 的请求体: {Body}", fullUrl,
                    convertedBody.Length > 1000 ? convertedBody[..1000] + "..." : convertedBody);

                if (needsConversion)
                {
                    // ======== 非流式降级路径 ========
                    _logger.LogInformation("协议降级非流式：客户端 {Path} → 上游 {Url}，非流式调用后模拟 SSE",
                        requestPath, fullUrl);
                    var fallbackBody = RemoveStreamFlag(convertedBody);

                    try
                    {
                        var client = _httpClientFactory.CreateClient("AIClient");
                        client.Timeout = TimeSpan.FromSeconds(node.TimeoutSeconds);

                        var httpRequest = new HttpRequestMessage(HttpMethod.Post, fullUrl)
                        {
                            Content = new StringContent(fallbackBody, Encoding.UTF8, "application/json")
                        };
                        SetAuthHeaders(httpRequest, node.SupplierType, node.ApiKey);

                        var response = await client.SendAsync(httpRequest);
                        var responseBody = await response.Content.ReadAsStringAsync();

                        if (!response.IsSuccessStatusCode)
                        {
                            var sc = (int)response.StatusCode;
                            await HandleError(response, node);
                            var errBody = responseBody;
                            var downstreamErr = ExtractDownstreamError(errBody);
                            lastError = downstreamErr.Length > 0
                                ? $"上游[{sc}]: {downstreamErr}"
                                : $"上游返回 {sc}";
                            await LogCall(tokenValue, customModelId, node.ChannelName, node.OriginalModelId, "Failed",
                                true, 0, 0, (long)(DateTime.UtcNow - startTime).TotalMilliseconds,
                                requestBody, errBody, lastError + " (SSE降级等待5秒后重试)", clientIp);

                            if (ShouldRetry(sc))
                            {
                                _logger.LogWarning("SSE降级 第 {Attempt} 次尝试：节点 {Channel}/{Model} 返回 {StatusCode}，等待 {Delay}ms",
                                    attempt + 1, node.ChannelName, node.OriginalModelId, sc, retryDelayMs);
                                await Task.Delay(retryDelayMs);
                                continue;
                            }

                            context.Response.StatusCode = sc;
                            await context.Response.WriteAsync(JsonSerializer.Serialize(
                                new { error = new { message = $"请求失败: {lastError}" } }));
                            return;
                        }

                        // 成功！转换响应 → 模拟 SSE
                        var baseResponse = ConvertResponseBody(responseBody, node.ProtocolType, requestPath, node);
                        var finalResponse = ConvertResponseFormat(baseResponse, upstreamPath, requestPath, node);

                        await FakeSseStreamAsync(context.Response, finalResponse, requestPath);

                        // Token 统计与日志
                        var (inputTokens, outputTokens) = ExtractTokenUsage(finalResponse);
                        var logId = await LogCall(tokenValue, customModelId, node.ChannelName, node.OriginalModelId, "Success",
                            true, inputTokens, outputTokens, (long)(DateTime.UtcNow - startTime).TotalMilliseconds,
                            requestBody, responseBody, null, clientIp);
                        if (token != null)
                            await _tokenService.RecordUsage(token.Id, inputTokens, outputTokens);
                        await RecordUpstreamUsage(node.ApiKeyId, inputTokens, outputTokens);
                        if (logId > 0)
                            await SaveCallImagesAsync(logId, requestBody, null);

                        usedNode = node;
                        return; // 成功，直接返回
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "SSE降级连接异常，节点: {Channel}/{Model}，第 {Attempt} 次尝试",
                            node.ChannelName, node.OriginalModelId, attempt + 1);
                        await SetCooldownAsync(node, node.CooldownSeconds);
                        lastError = ex.Message;
                        await LogCall(tokenValue, customModelId, node.ChannelName, node.OriginalModelId, "Failed",
                            true, 0, 0, (long)(DateTime.UtcNow - startTime).TotalMilliseconds,
                            requestBody, null, ex.Message + " (SSE降级等待5秒后重试)", clientIp);
                        await Task.Delay(retryDelayMs);
                        continue;
                    }
                }

                // ======== 正常 SSE 流式路径 ========
                try
                {
                    var client = _httpClientFactory.CreateClient("AIClient");
                    client.Timeout = TimeSpan.FromSeconds(node.TimeoutSeconds);

                    var httpRequest = new HttpRequestMessage(HttpMethod.Post, fullUrl)
                    {
                        Content = new StringContent(convertedBody, Encoding.UTF8, "application/json")
                    };
                    SetAuthHeaders(httpRequest, node.SupplierType, node.ApiKey);
                    httpRequest.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));

                    var response = await client.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead);

                    // 如果上游返回非成功状态码，冷却并重试下一个节点
                    if (!response.IsSuccessStatusCode)
                    {
                        var sc = (int)response.StatusCode;
                        await HandleError(response, node);
                        var errBody = await response.Content.ReadAsStringAsync();
                        var downstreamErr = ExtractDownstreamError(errBody);
                        lastError = downstreamErr.Length > 0
                            ? $"上游[{sc}]: {downstreamErr}"
                            : $"上游返回 {sc}";
                        await LogCall(tokenValue, customModelId, node.ChannelName, node.OriginalModelId, "Failed",
                            true, 0, 0, (long)(DateTime.UtcNow - startTime).TotalMilliseconds,
                            requestBody, errBody, lastError + " (SSE等待5秒后重试)", clientIp);
                        response.Dispose();

                        if (ShouldRetry(sc))
                        {
                            _logger.LogWarning("SSE 第 {Attempt} 次尝试：节点 {Channel}/{Model} 返回 {StatusCode}，等待 {Delay}ms",
                                attempt + 1, node.ChannelName, node.OriginalModelId, sc, retryDelayMs);
                            await Task.Delay(retryDelayMs);
                            continue; // 继续尝试 candidates 中的下一个节点
                        }

                        // 不可重试的错误，直接返回
                        context.Response.StatusCode = sc;
                        await context.Response.WriteAsync(JsonSerializer.Serialize(
                            new { error = new { message = $"请求失败: {lastError}" } }));
                        return;
                    }

                    // 成功获取响应头，开始流式读取
                    var stream = await response.Content.ReadAsStreamAsync();
                    successReader = new StreamReader(stream);
                    successResponse = response;
                    usedNode = node;
                    goto sseConnected;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "SSE连接异常，节点: {Channel}/{Model}，第 {Attempt} 次尝试",
                        node.ChannelName, node.OriginalModelId, attempt + 1);
                    await SetCooldownAsync(node, node.CooldownSeconds);
                    lastError = ex.Message;
                    await LogCall(tokenValue, customModelId, node.ChannelName, node.OriginalModelId, "Failed",
                        true, 0, 0, (long)(DateTime.UtcNow - startTime).TotalMilliseconds,
                        requestBody, null, ex.Message + " (SSE等待5秒后重试)", clientIp);

                    await Task.Delay(retryDelayMs);
                    continue; // 继续尝试 candidates 中的下一个节点
                }
            }

            // 本轮所有候选节点都试过了且均失败，等待后进入下一轮
            if (attempt < maxAttempts - 1)
                await Task.Delay(retryDelayMs);
        }

        // 所有重试都失败
        context.Response.StatusCode = 503;
        await context.Response.WriteAsync(JsonSerializer.Serialize(
            new { error = new { message = $"所有可用渠道暂时不可用: {lastError}" } }));
        return;

        sseConnected:

        // 设置SSE响应头，开始向客户端透传
        context.Response.ContentType = "text/event-stream";
        context.Response.Headers.CacheControl = "no-cache";
        context.Response.Headers.Connection = "keep-alive";

        try
        {
            // 从请求体中估算输入 token
            var streamInputTokens = TokenEstimator.EstimateInputTokens(requestBody);
            var streamOutputContent = new StringBuilder(4096);
            string? line;
            // 记录是否已从 usage chunk 中提取到准确 token 数
            bool usageFound = false;
            int usageInputTokens = 0, usageOutputTokens = 0;

            while ((line = await successReader.ReadLineAsync()) != null)
            {
                // SSE逐行透传
                await context.Response.WriteAsync(line + "\n");
                await context.Response.Body.FlushAsync();

                // 尝试检测 usage chunk（OpenAI stream_options: {"include_usage": true}）
                if (!usageFound && TokenEstimator.TryExtractStreamUsage(line, out var ui, out var uo))
                {
                    usageInputTokens = ui;
                    usageOutputTokens = uo;
                    usageFound = true;
                    continue;
                }

                // 从 content delta 中累计输出文本用于估算
                if (line.StartsWith("data: ") && !line.Contains("[DONE]"))
                {
                    try
                    {
                        var jsonPayload = line[6..];
                        using var doc = JsonDocument.Parse(jsonPayload);
                        var root = doc.RootElement;
                        if (root.TryGetProperty("choices", out var choices) && choices.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var choice in choices.EnumerateArray())
                            {
                                if (choice.TryGetProperty("delta", out var delta) &&
                                    delta.TryGetProperty("content", out var content))
                                {
                                    streamOutputContent.Append(content.GetString());
                                }
                            }
                        }
                    }
                    catch { /* SSE 解析失败，跳过该行的 Token 统计 */ }
                }
            }

            // 优先使用上游返回的准确 usage，否则用估算值
            int finalInputTokens, finalOutputTokens;
            if (usageFound)
            {
                finalInputTokens = usageInputTokens;
                finalOutputTokens = usageOutputTokens;
                _logger.LogInformation("SSE Token 统计（来自上游 usage）：Input={In} Output={Out}",
                    finalInputTokens, finalOutputTokens);
            }
            else
            {
                finalInputTokens = streamInputTokens;
                finalOutputTokens = TokenEstimator.EstimateOutputTokens(streamOutputContent.ToString());
                _logger.LogInformation("SSE Token 统计（估算）：Input={In} Output={Out}",
                    finalInputTokens, finalOutputTokens);
            }

            var logId = await LogCall(tokenValue, customModelId, usedNode.ChannelName, usedNode.OriginalModelId, "Success",
                true, finalInputTokens, finalOutputTokens, (long)(DateTime.UtcNow - startTime).TotalMilliseconds,
                requestBody, null, null, clientIp);

            if (token != null)
                await _tokenService.RecordUsage(token.Id, finalInputTokens, finalOutputTokens);

            // 记录上游API Key使用统计
            await RecordUpstreamUsage(usedNode.ApiKeyId, finalInputTokens, finalOutputTokens);

            // 提取并保存请求中的图片资源
            if (logId > 0)
                await SaveCallImagesAsync(logId, requestBody, null);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "SSE流式转发异常");
            await context.Response.WriteAsync($"data: {{\"error\":\"{ex.Message}\"}}\n\n");
            await context.Response.Body.FlushAsync();
        }
        finally
        {
            successReader.Dispose();
            successResponse?.Dispose();
        }
    }

    #region 辅助方法

    /// <summary>
    /// 获取指定优先级组中当前可用的节点（跳过冷却中），异步检查冷却状态
    /// </summary>
    private async Task<List<LoadBalanceNode>> GetActiveNodesAsync(List<LoadBalanceNode> allNodes)
    {
        var priorityGroups = allNodes
            .GroupBy(n => n.Priority)
            .OrderBy(g => g.Key);

        foreach (var group in priorityGroups)
        {
            var available = new List<LoadBalanceNode>();
            foreach (var node in group)
            {
                if (!await IsInCooldownAsync(node))
                    available.Add(node);
            }
            if (available.Count > 0)
                return available;
        }

        return new List<LoadBalanceNode>();
    }

    /// <summary>
    /// 获取指定优先级组中当前可用的 SSE 节点（跳过冷却中），异步检查冷却状态
    /// </summary>
    private async Task<List<LoadBalanceNode>> GetActiveSseNodesAsync(List<LoadBalanceNode> allNodes)
    {
        var priorityGroups = allNodes
            .GroupBy(n => n.Priority)
            .OrderBy(g => g.Key);

        foreach (var group in priorityGroups)
        {
            var available = new List<LoadBalanceNode>();
            foreach (var node in group)
            {
                if (node.SseEnabled && !await IsInCooldownAsync(node))
                    available.Add(node);
            }
            if (available.Count > 0)
                return available;
        }

        return new List<LoadBalanceNode>();
    }

    /// <summary>
    /// Redis 分布式轮询选择器：对同一 (ChannelId, OriginalModelId) 下的多个 API Key 做轮询。
    /// 返回同一组内的所有 API Key，轮询选中的 Key 排最前面，其余按权重降序跟随。
    /// 这样当第一个 Key 失败时，可立即尝试同渠道的下一个 Key，无需等待 5 秒重试轮。
    /// </summary>
    private async Task<List<LoadBalanceNode>> SelectNodesWeightedRoundRobin(List<LoadBalanceNode> candidates)
    {
        // 按 (ChannelId, OriginalModelId) 分组 —— 共享同一上游端点的节点仅 API Key 不同
        var groups = candidates.GroupBy(n => $"{n.ChannelId}:{n.OriginalModelId}");
        var result = new List<LoadBalanceNode>();

        foreach (var group in groups)
        {
            var nodes = group.ToList();

            if (nodes.Count == 1)
            {
                result.Add(nodes[0]);
            }
            else
            {
                // 多个 API Key：Redis 原子自增实现轮询
                var rrKey = $"roundrobin:{group.Key}";
                var index = await _cache.IncrementAsync(rrKey);
                var selectedIndex = (int)((index - 1) % (uint)nodes.Count);
                _logger.LogInformation("轮询选择: Key={RrKey} Counter={Counter} Count={Count} SelectedIdx={Idx} KeyValue={Kv}",
                    rrKey, index, nodes.Count, selectedIndex,
                    nodes[selectedIndex].ApiKey[..Math.Min(nodes[selectedIndex].ApiKey.Length, 8)]);

                // 将轮询选中的 Key 排最前面，其余按权重降序排列
                var ordered = new List<LoadBalanceNode> { nodes[selectedIndex] };
                ordered.AddRange(nodes.Where((_, i) => i != selectedIndex)
                    .OrderByDescending(n => n.Weight));
                result.AddRange(ordered);
            }
        }

        // 按权重降序排列，确保同优先级内高权重的渠道优先被尝试
        return result.OrderByDescending(n => n.Weight).ToList();
    }

    private LoadBalanceNode WeightedRandomSelect(List<LoadBalanceNode> nodes)
    {
        var totalWeight = nodes.Sum(n => n.Weight);
        var random = Random.Shared.Next(totalWeight);
        var cumulative = 0;
        foreach (var node in nodes)
        {
            cumulative += node.Weight;
            if (random < cumulative) return node;
        }
        return nodes[^1];
    }

    /// <summary>
    /// 冷却 Key = (ChannelId, OriginalModelId, ApiKeyId) —— 每个 API Key 独立冷却
    /// </summary>
    private static string CooldownKey(LoadBalanceNode node) =>
        $"cooldown:{node.ChannelId}:{node.OriginalModelId}:{node.ApiKeyId}";

    private async Task<bool> IsInCooldownAsync(LoadBalanceNode node)
    {
        var key = CooldownKey(node);
        return await _cache.IsInCooldownAsync(key);
    }

    private async Task SetCooldownAsync(LoadBalanceNode node, int seconds)
    {
        var key = CooldownKey(node);
        await _cache.SetCooldownAsync(key, seconds);
        _logger.LogWarning("节点冷却：渠道 {Channel}/{Model} API Key={Key} 冷却 {Sec}s",
            node.ChannelName, node.OriginalModelId,
            node.ApiKey[..Math.Min(node.ApiKey.Length, 8)], seconds);
    }

    private async Task HandleError(HttpResponseMessage response, LoadBalanceNode node)
    {
        var statusCode = (int)response.StatusCode;
        switch (statusCode)
        {
            case 429:
                await SetCooldownAsync(node, Math.Min(node.CooldownSeconds, 30));
                break;
            case 401:
            case 403:
                // 标记密钥永久失效
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
                catch (Exception ex)
                {
                    _logger.LogError(ex, "标记 API Key 永久失效失败: {KeyId}", node.ApiKeyId);
                }
                break;
            case >= 500:
                await SetCooldownAsync(node, Math.Min(node.CooldownSeconds, 15));
                break;
        }
    }

    /// <summary>
    /// 判断该状态码是否应该重试到下一个节点
    /// 429(限流) / 5xx(服务端错误) / 408(超时) / 401/403(密钥问题,换其他渠道) 均可重试
    /// 400(参数错误) / 404(模型不存在) 等客户端错误不重试
    /// </summary>
    private static bool ShouldRetry(int statusCode)
    {
        return statusCode switch
        {
            408 => true,   // Request Timeout
            401 => true,   // 密钥无效，换其他渠道
            403 => true,   // 权限问题，换其他渠道
            429 => true,   // 限流，换其他渠道
            >= 500 => true, // 服务端错误
            _ => false
        };
    }

    /// <summary>
    /// 从上游 API 错误响应体中提取 error message
    /// 支持多种格式：OpenAI / Anthropic / Google / 标准 HTML 等
    /// </summary>
    private static string ExtractDownstreamError(string responseBody)
    {
        if (string.IsNullOrEmpty(responseBody)) return "";

        try
        {
            using var doc = JsonDocument.Parse(responseBody);
            var root = doc.RootElement;

            // OpenAI: {"error":{"message":"..."}}
            if (root.TryGetProperty("error", out var err))
            {
                if (err.TryGetProperty("message", out var msg))
                    return msg.GetString() ?? "";
                if (err.ValueKind == JsonValueKind.String)
                    return err.GetString() ?? "";
            }

            // Anthropic: {"error":{"message":"..."}}
            if (root.TryGetProperty("error", out var antErr))
            {
                if (antErr.TryGetProperty("message", out var antMsg))
                    return antMsg.GetString() ?? "";
            }

            // Google: {"error":{"message":"..."}}
            if (root.TryGetProperty("error", out var googleErr))
            {
                if (googleErr.TryGetProperty("message", out var googleMsg))
                    return googleMsg.GetString() ?? "";
            }

            // 截取前 200 字符作为 fallback
            return responseBody[..Math.Min(responseBody.Length, 200)];
        }
        catch
        {
            // 非 JSON 响应（如 HTML 504 页面等），截取 body 中有价值的部分
            // 提取 <title> 标签内容
            var titleMatch = System.Text.RegularExpressions.Regex.Match(responseBody,
                @"<title>([^<]+)</title>", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (titleMatch.Success)
                return titleMatch.Groups[1].Value;

            return responseBody[..Math.Min(responseBody.Length, 200)];
        }
    }

    private string ConvertRequestBody(string body, string requestPath, LoadBalanceNode node)
    {
        var supplierType = node.SupplierType ?? "OpenAI";

        // 同协议：替换模型ID后透传（将用户的 customModelId 替换为上游实际模型ID）
        if (supplierType is "OpenAI" or "Azure" or "DeepSeek" or "Groq" or "Together" or "Custom")
        {
            return ReplaceModelId(body, node.OriginalModelId);
        }

        // 如果请求体已经是供应商原生格式（上游通过 SupportedPaths 原生支持），
        // 则只需替换模型ID，不做额外的 body 重构建（避免丢掉字段）
        if ((supplierType == "Anthropic" && requestPath.Contains("/v1/messages")) ||
            (supplierType == "Google" && requestPath.Contains("generateContent")))
        {
            return ReplaceModelId(body, node.OriginalModelId);
        }

        // 非 OpenAI 协议需要转换（从 Chat 格式转换为供应商特定格式）
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;

            // 提取公共字段
            var model = node.OriginalModelId;
            var messages = root.TryGetProperty("messages", out var msgs) ? msgs : default;
            var stream = root.TryGetProperty("stream", out var s) && s.GetBoolean();
            var maxTokens = root.TryGetProperty("max_tokens", out var mt) ? mt.GetInt32() : 1024;
            var temperature = root.TryGetProperty("temperature", out var t) ? t.GetDouble() : 1.0;

            switch (supplierType)
            {
                case "Anthropic":
                    var systemMsg = "";
                    var userMsgs = new List<object>();
                    if (messages.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var msg in messages.EnumerateArray())
                        {
                            var role = msg.TryGetProperty("role", out var r) ? r.GetString() : "user";
                            var content = msg.TryGetProperty("content", out var c) ? c : default;
                            if (role == "system") systemMsg = content.GetString() ?? "";
                            else userMsgs.Add(new { role, content = ExtractContentValue(content) });
                        }
                    }
                    var anthropicBody = new Dictionary<string, object>
                    {
                        ["model"] = model,
                        ["max_tokens"] = maxTokens,
                        ["messages"] = userMsgs,
                        ["stream"] = stream
                    };
                    if (!string.IsNullOrEmpty(systemMsg)) anthropicBody["system"] = systemMsg;
                    if (temperature != 1.0) anthropicBody["temperature"] = temperature;
                    return JsonSerializer.Serialize(anthropicBody);

                case "Google":
                    // Google Gemini API 格式
                    var contents = new List<object>();
                    string? geminiSystem = null;
                    if (messages.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var msg in messages.EnumerateArray())
                        {
                            var role = msg.TryGetProperty("role", out var r) ? r.GetString() : "user";
                            var content = msg.TryGetProperty("content", out var c) ? c : default;
                            if (role == "system")
                            {
                                geminiSystem = content.GetString();
                                continue;
                            }
                            var geminiRole = role == "assistant" ? "model" : "user";
                            contents.Add(new
                            {
                                role = geminiRole,
                                parts = new[] { new { text = ExtractContentValue(content) } }
                            });
                        }
                    }
                    var geminiBody = new Dictionary<string, object>
                    {
                        ["contents"] = contents,
                        ["generationConfig"] = new { maxOutputTokens = maxTokens, temperature }
                    };
                    if (!string.IsNullOrEmpty(geminiSystem))
                        geminiBody["systemInstruction"] = new { parts = new[] { new { text = geminiSystem } } };
                    return JsonSerializer.Serialize(geminiBody);

                default:
                    return body;
            }
        }
        catch
        {
            return body;
        }
    }

    /// <summary>
    /// 从 OpenAI content 字段中提取纯文本（content 可能是 string 或 array）
    /// </summary>
    private static string ExtractContentValue(JsonElement content)
    {
        if (content.ValueKind == JsonValueKind.String) return content.GetString() ?? "";
        if (content.ValueKind == JsonValueKind.Array)
        {
            var texts = new List<string>();
            foreach (var item in content.EnumerateArray())
            {
                if (item.TryGetProperty("text", out var t))
                    texts.Add(t.GetString() ?? "");
                else if (item.ValueKind == JsonValueKind.String)
                    texts.Add(item.GetString() ?? "");
            }
            return string.Join("\n", texts);
        }
        return content.ToString();
    }

    /// <summary>
    /// 将 Responses API 的 content 块数组拍平为纯文本字符串。
    /// 参考 Node.js 版 normalizeContent 实现：提取所有文本块并用换行符拼接。
    /// </summary>
    private static string NormalizeContentForChat(JsonElement content)
    {
        if (content.ValueKind == JsonValueKind.String)
            return content.GetString() ?? "";

        if (content.ValueKind == JsonValueKind.Array)
        {
            var texts = new List<string>();
            foreach (var block in content.EnumerateArray())
            {
                if (block.TryGetProperty("type", out var typeEl))
                {
                    var type = typeEl.GetString();
                    if (type is "input_text" or "text" && block.TryGetProperty("text", out var text))
                        texts.Add(text.GetString() ?? "");
                    else if (type is "input_image" && block.TryGetProperty("image_url", out var imgUrl))
                        texts.Add(imgUrl.GetString() ?? "");
                }
                else if (block.ValueKind == JsonValueKind.String)
                {
                    texts.Add(block.GetString() ?? "");
                }
            }
            return string.Join("\n", texts);
        }

        if (content.ValueKind == JsonValueKind.Object && content.TryGetProperty("text", out var t))
            return t.GetString() ?? "";

        return content.GetRawText();
    }

    /// <summary>
    /// 当协议降级时，将客户端请求体从源格式转换为 Chat 格式。
    /// 支持：Responses 格式（input 字段）→ Chat 格式（messages 字段）
    ///       Messages 格式（Anthropic）→ Chat 格式
    /// </summary>
    private static string ConvertRequestToChat(string body, string clientRequestPath)
    {
        var clientType = GetRequestType(clientRequestPath);
        if (clientType == "chat") return body;

        try
        {
            if (clientType == "responses")
            {
                using var doc = JsonDocument.Parse(body);
                var root = doc.RootElement;
                var chatObj = new Dictionary<string, object?>();

                // 保留 model
                if (root.TryGetProperty("model", out var model))
                    chatObj["model"] = model.GetString();

                // 转换 input → messages
                if (root.TryGetProperty("input", out var input))
                {
                    var messages = new List<object>();
                    if (input.ValueKind == JsonValueKind.String)
                    {
                        // input: "string" → messages: [{role:"user", content:"string"}]
                        messages.Add(new { role = "user", content = input.GetString() });
                    }
                    else if (input.ValueKind == JsonValueKind.Array)
                    {
                        // input: 数组格式，处理每个 item
                        foreach (var item in input.EnumerateArray())
                        {
                            var msg = new Dictionary<string, object?>();

                            // 角色映射：Responses 的 developer → Chat 的 system
                            var role = item.TryGetProperty("role", out var r) ? r.GetString() : "user";
                            msg["role"] = role switch
                            {
                                "developer" => "system",
                                _ => role
                            };

                            // content 处理：数组拍平为纯文本字符串（参考 px.js normalizeContent）
                            if (item.TryGetProperty("content", out var c))
                            {
                                msg["content"] = NormalizeContentForChat(c);
                            }

                            messages.Add(msg);
                        }
                    }
                    chatObj["messages"] = messages;
                }

                // instructions → 作为 system 消息插入到消息列表开头（参考 px.js）
                if (root.TryGetProperty("instructions", out var instructions))
                {
                    var instrText = instructions.GetString();
                    if (!string.IsNullOrEmpty(instrText))
                    {
                        var sysMsg = new Dictionary<string, object?>
                        {
                            ["role"] = "system",
                            ["content"] = instrText
                        };
                        if (chatObj.TryGetValue("messages", out var existing) && existing is List<object> msgList)
                            msgList.Insert(0, sysMsg);
                    }
                }

                // 保留 stream
                if (root.TryGetProperty("stream", out var stream))
                    chatObj["stream"] = stream.GetBoolean();

                // 映射 max_output_tokens → max_tokens
                if (root.TryGetProperty("max_output_tokens", out var maxOut))
                    chatObj["max_tokens"] = maxOut.GetInt32();
                else if (root.TryGetProperty("max_tokens", out var maxTok))
                    chatObj["max_tokens"] = maxTok.GetInt32();
                // 透传 max_completion_tokens（Chat API 也支持）
                if (root.TryGetProperty("max_completion_tokens", out var maxComp))
                    chatObj["max_completion_tokens"] = maxComp.GetInt32();

                // 保留通用参数
                if (root.TryGetProperty("temperature", out var temp))
                    chatObj["temperature"] = temp.GetDouble();
                if (root.TryGetProperty("top_p", out var topP))
                    chatObj["top_p"] = topP.GetDouble();
                // stop 在 Responses 中是 array，Chat 接受 string|array
                if (root.TryGetProperty("stop", out var stop))
                {
                    chatObj["stop"] = stop.ValueKind == JsonValueKind.String
                        ? stop.GetString()
                        : JsonSerializer.Deserialize<object>(stop.GetRawText());
                }

                // tools / tool_choice（参考 px.js 透传，但只保留 type=function 的工具）
                if (root.TryGetProperty("tools", out var tools) && tools.ValueKind == JsonValueKind.Array)
                {
                    var functionTools = new List<object>();
                    foreach (var tool in tools.EnumerateArray())
                    {
                        if (tool.TryGetProperty("type", out var toolType) &&
                            toolType.GetString() == "function")
                        {
                            functionTools.Add(JsonSerializer.Deserialize<object>(tool.GetRawText())!);
                        }
                    }
                    if (functionTools.Count > 0)
                        chatObj["tools"] = functionTools;
                }
                if (root.TryGetProperty("tool_choice", out var toolChoice))
                    chatObj["tool_choice"] = JsonSerializer.Deserialize<object>(toolChoice.GetRawText());

                return JsonSerializer.Serialize(chatObj);
            }

            // Messages (Anthropic) 格式 → Chat 格式
            if (clientType == "messages")
            {
                using var doc = JsonDocument.Parse(body);
                var root = doc.RootElement;
                var chatObj = new Dictionary<string, object?>();

                // 保留 model
                if (root.TryGetProperty("model", out var model))
                    chatObj["model"] = model.GetString();

                // 转换 messages：将 system 角色消息提到最前面（OpenAI 要求）
                var systemMessages = new List<object>();
                var otherMessages = new List<object>();
                if (root.TryGetProperty("messages", out var msgs) && msgs.ValueKind == JsonValueKind.Array)
                {
                    foreach (var msg in msgs.EnumerateArray())
                    {
                        var role = msg.TryGetProperty("role", out var r) ? r.GetString() : "user";
                        var content = msg.TryGetProperty("content", out var c) ? c : default;
                        var entry = new Dictionary<string, object?>
                        {
                            ["role"] = role,
                            ["content"] = NormalizeContentForChat(content)
                        };
                        if (role == "system")
                            systemMessages.Add(entry);
                        else
                            otherMessages.Add(entry);
                    }
                }

                // 提取 Anthropic 的 system 顶层字段，也作为 system 消息加到最前面
                string? topSystem = null;
                if (root.TryGetProperty("system", out var system))
                {
                    if (system.ValueKind == JsonValueKind.String)
                        topSystem = system.GetString();
                    else if (system.ValueKind == JsonValueKind.Array)
                    {
                        // Anthropic 允许 system 为 content block 数组
                        var parts = new List<string>();
                        foreach (var item in system.EnumerateArray())
                        {
                            if (item.TryGetProperty("text", out var t))
                                parts.Add(t.GetString() ?? "");
                        }
                        if (parts.Count > 0)
                            topSystem = string.Join("\n\n", parts);
                    }
                }
                if (!string.IsNullOrEmpty(topSystem))
                {
                    systemMessages.Insert(0, new Dictionary<string, object?>
                    {
                        ["role"] = "system",
                        ["content"] = topSystem
                    });
                }

                var messages = new List<object>();
                messages.AddRange(systemMessages);
                messages.AddRange(otherMessages);
                chatObj["messages"] = messages;

                // 保留 stream
                if (root.TryGetProperty("stream", out var stream))
                    chatObj["stream"] = stream.GetBoolean();

                // max_tokens
                if (root.TryGetProperty("max_tokens", out var maxTok))
                    chatObj["max_tokens"] = maxTok.GetInt32();

                // 保留通用参数
                if (root.TryGetProperty("temperature", out var temp))
                    chatObj["temperature"] = temp.GetDouble();
                if (root.TryGetProperty("top_p", out var topP))
                    chatObj["top_p"] = topP.GetDouble();
                // Anthropic stop_sequences → OpenAI stop
                if (root.TryGetProperty("stop_sequences", out var stopSeq) && stopSeq.ValueKind == JsonValueKind.Array)
                    chatObj["stop"] = JsonSerializer.Deserialize<object>(stopSeq.GetRawText());

                return JsonSerializer.Serialize(chatObj);
            }

            return body;
        }
        catch
        {
            return body;
        }
    }

    /// <summary>
    /// 将 OpenAI Responses API 请求体直接转换为 Anthropic Messages API 请求体。
    /// 保留所有数据：input→messages、instructions→system、tools、tool_choice 等。
    /// </summary>
    private static string ConvertRequestToMessages(string responsesBody, LoadBalanceNode node)
    {
        try
        {
            using var doc = JsonDocument.Parse(responsesBody);
            var root = doc.RootElement;
            var msgObj = new Dictionary<string, object?>();

            // 1. model — 使用上游实际模型 ID
            msgObj["model"] = node.OriginalModelId;

            // 2. max_tokens — Anthropic 必填
            var maxTokens = 1024;
            if (root.TryGetProperty("max_output_tokens", out var maxOut))
                maxTokens = maxOut.GetInt32();
            else if (root.TryGetProperty("max_tokens", out var maxTok))
                maxTokens = maxTok.GetInt32();
            msgObj["max_tokens"] = maxTokens;

            // 3. input → messages + 收集 system/developer 内容到 system 字段
            var messages = new List<object>();
            var systemParts = new List<string>();

            // 收集 instructions 作为 system prompt
            if (root.TryGetProperty("instructions", out var instructions))
            {
                var instrText = instructions.GetString();
                if (!string.IsNullOrEmpty(instrText))
                    systemParts.Add(instrText);
            }

            if (root.TryGetProperty("input", out var input))
            {
                if (input.ValueKind == JsonValueKind.String)
                {
                    // input: "string" → [{role:"user", content:"string"}]
                    messages.Add(new Dictionary<string, object?>
                    {
                        ["role"] = "user",
                        ["content"] = input.GetString() ?? ""
                    });
                }
                else if (input.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in input.EnumerateArray())
                    {
                        var role = item.TryGetProperty("role", out var r) ? r.GetString() : "user";

                        // system/developer → 收集到 system 字段
                        if (role == "system" || role == "developer")
                        {
                            var sysContent = ExtractTextContent(item);
                            if (!string.IsNullOrEmpty(sysContent))
                                systemParts.Add(sysContent);
                            continue;
                        }

                        // 处理 function_call / function_call_output 类型的输入项
                        if (item.TryGetProperty("type", out var itemType))
                        {
                            var typeStr = itemType.GetString();
                            if (typeStr == "function_call" || typeStr == "function_call_output")
                            {
                                var converted = ConvertFunctionCallItem(item, typeStr);
                                if (converted != null)
                                    messages.Add(converted);
                                continue;
                            }
                        }

                        // 普通消息：转换 content 为 Anthropic 格式
                        var content = BuildAnthropicContent(item);
                        messages.Add(new Dictionary<string, object?>
                        {
                            ["role"] = role,
                            ["content"] = content
                        });
                    }
                }
            }

            // 设置 system 字段（如果有）
            if (systemParts.Count > 0)
                msgObj["system"] = string.Join("\n\n", systemParts);

            msgObj["messages"] = messages;

            // 4. stream
            if (root.TryGetProperty("stream", out var stream))
                msgObj["stream"] = stream.GetBoolean();

            // 5. temperature / top_p / top_k
            if (root.TryGetProperty("temperature", out var temp))
                msgObj["temperature"] = temp.GetDouble();
            if (root.TryGetProperty("top_p", out var topP))
                msgObj["top_p"] = topP.GetDouble();

            // 6. stop → stop_sequences
            if (root.TryGetProperty("stop", out var stop))
            {
                msgObj["stop_sequences"] = stop.ValueKind == JsonValueKind.String
                    ? new[] { stop.GetString() }
                    : JsonSerializer.Deserialize<object>(stop.GetRawText());
            }

            // 7. tools — OpenAI → Anthropic 格式转换
            if (root.TryGetProperty("tools", out var tools) && tools.ValueKind == JsonValueKind.Array)
            {
                var anthropicTools = new List<object>();
                foreach (var tool in tools.EnumerateArray())
                {
                    var convertedTool = ConvertOpenAIToolToAnthropic(tool);
                    if (convertedTool != null)
                        anthropicTools.Add(convertedTool);
                }
                if (anthropicTools.Count > 0)
                    msgObj["tools"] = anthropicTools;
            }

            // 8. tool_choice — 格式转换
            if (root.TryGetProperty("tool_choice", out var toolChoice))
                msgObj["tool_choice"] = ConvertToolChoice(toolChoice);

            // 9. metadata 透传
            if (root.TryGetProperty("metadata", out var metadata))
                msgObj["metadata"] = JsonSerializer.Deserialize<object>(metadata.GetRawText());

            return JsonSerializer.Serialize(msgObj);
        }
        catch
        {
            return responsesBody;
        }
    }

    /// <summary>
    /// 从 input item 中提取 content 并转换为 Anthropic 格式（string 或 content block 数组）
    /// </summary>
    private static object BuildAnthropicContent(JsonElement item)
    {
        if (!item.TryGetProperty("content", out var content))
            return "";

        if (content.ValueKind == JsonValueKind.String)
            return content.GetString() ?? "";

        if (content.ValueKind == JsonValueKind.Array)
        {
            var blocks = new List<object>();
            foreach (var block in content.EnumerateArray())
            {
                var converted = ConvertResponsesContentBlockToAnthropic(block);
                if (converted != null)
                    blocks.Add(converted);
            }
            if (blocks.Count == 0) return "";
            // 单个文本块 → 简化为字符串
            if (blocks.Count == 1 && blocks[0] is Dictionary<string, object?> single
                && single.TryGetValue("type", out var typeObj) && typeObj is string typeStr && typeStr == "text"
                && single.TryGetValue("text", out var textVal))
            {
                if (textVal is string ts) return ts;
            }
            return blocks;
        }

        return content.GetRawText();
    }

    /// <summary>
    /// 从 input item 中提取纯文本内容（用于 system/developer 收集）
    /// </summary>
    private static string ExtractTextContent(JsonElement item)
    {
        if (!item.TryGetProperty("content", out var content))
            return "";

        if (content.ValueKind == JsonValueKind.String)
            return content.GetString() ?? "";

        if (content.ValueKind == JsonValueKind.Array)
        {
            var texts = new List<string>();
            foreach (var block in content.EnumerateArray())
            {
                if (block.TryGetProperty("type", out var t))
                {
                    var type = t.GetString();
                    if (type is "input_text" or "text" && block.TryGetProperty("text", out var text))
                        texts.Add(text.GetString() ?? "");
                }
                else if (block.ValueKind == JsonValueKind.String)
                {
                    texts.Add(block.GetString() ?? "");
                }
            }
            return string.Join("\n", texts);
        }

        return "";
    }

    /// <summary>
    /// 将 Responses 的 content 块转为 Anthropic 格式
    /// input_text → text，input_image → image，input_file → 丢弃
    /// </summary>
    private static object? ConvertResponsesContentBlockToAnthropic(JsonElement block)
    {
        if (!block.TryGetProperty("type", out var typeEl)) return block.GetRawText();
        var type = typeEl.GetString();

        switch (type)
        {
            case "input_text":
            case "text":
                if (block.TryGetProperty("text", out var text))
                    return new Dictionary<string, object?> { ["type"] = "text", ["text"] = text.GetString() ?? "" };
                return null;

            case "input_image":
                if (block.TryGetProperty("image_url", out var imgUrl))
                {
                    var url = imgUrl.GetString() ?? "";
                    if (url.StartsWith("data:"))
                    {
                        var (mediaType, base64Data) = ParseDataUrl(url);
                        return new Dictionary<string, object?>
                        {
                            ["type"] = "image",
                            ["source"] = new Dictionary<string, object?>
                            {
                                ["type"] = "base64",
                                ["media_type"] = mediaType,
                                ["data"] = base64Data
                            }
                        };
                    }
                    else
                    {
                        return new Dictionary<string, object?>
                        {
                            ["type"] = "image",
                            ["source"] = new Dictionary<string, object?>
                            {
                                ["type"] = "url",
                                ["url"] = url
                            }
                        };
                    }
                }
                return null;

            case "input_file":
                return null;

            default:
                return block.GetRawText();
        }
    }

    /// <summary>
    /// 将 OpenAI 格式的工具定义转为 Anthropic 格式
    /// OpenAI: {type:"function", function:{name, description, parameters}}
    /// Anthropic: {name, description, input_schema}
    /// </summary>
    private static object? ConvertOpenAIToolToAnthropic(JsonElement tool)
    {
        if (!tool.TryGetProperty("type", out var toolType) || toolType.GetString() != "function")
            return null;
        if (!tool.TryGetProperty("function", out var func))
            return null;

        var result = new Dictionary<string, object?>
        {
            ["name"] = func.TryGetProperty("name", out var name) ? name.GetString() : "unknown"
        };

        if (func.TryGetProperty("description", out var desc))
            result["description"] = desc.GetString();

        // parameters → input_schema
        if (func.TryGetProperty("parameters", out var parameters))
            result["input_schema"] = JsonSerializer.Deserialize<object>(parameters.GetRawText());
        else
            result["input_schema"] = new Dictionary<string, object> { ["type"] = "object", ["properties"] = new Dictionary<string, object>() };

        return result;
    }

    /// <summary>
    /// 将 OpenAI tool_choice 转为 Anthropic 格式
    /// </summary>
    private static object ConvertToolChoice(JsonElement choice)
    {
        if (choice.ValueKind == JsonValueKind.String)
        {
            var val = choice.GetString();
            return val switch
            {
                "auto" => new Dictionary<string, object> { ["type"] = "auto" },
                "required" => new Dictionary<string, object> { ["type"] = "any" },
                "none" => new Dictionary<string, object> { ["type"] = "none" },
                _ => new Dictionary<string, object> { ["type"] = "auto" }
            };
        }

        if (choice.ValueKind == JsonValueKind.Object)
        {
            if (choice.TryGetProperty("type", out var ct))
            {
                var ctStr = ct.GetString();
                if (ctStr == "function" && choice.TryGetProperty("name", out var cn))
                {
                    return new Dictionary<string, object>
                    {
                        ["type"] = "tool",
                        ["name"] = cn.GetString() ?? ""
                    };
                }
            }
        }

        return new Dictionary<string, object> { ["type"] = "auto" };
    }

    /// <summary>
    /// 将 Responses 的 function_call / function_call_output 转为 Anthropic 的 tool_use / tool_result
    /// </summary>
    private static Dictionary<string, object?>? ConvertFunctionCallItem(JsonElement item, string type)
    {
        if (type == "function_call")
        {
            var args = item.TryGetProperty("arguments", out var arguments)
                ? (JsonSerializer.Deserialize<object>(arguments.GetRawText())
                   ?? new Dictionary<string, object?>())
                : new Dictionary<string, object?>();
            return new Dictionary<string, object?>
            {
                ["role"] = "assistant",
                ["content"] = new[]
                {
                    new Dictionary<string, object?>
                    {
                        ["type"] = "tool_use",
                        ["id"] = item.TryGetProperty("id", out var id) ? id.GetString() : "call_" + Guid.NewGuid().ToString("N")[..8],
                        ["name"] = item.TryGetProperty("name", out var name) ? name.GetString() : "",
                        ["input"] = args
                    }
                }
            };
        }

        if (type == "function_call_output")
        {
            var content = item.TryGetProperty("output", out var output) ? output.GetString() : "";
            return new Dictionary<string, object?>
            {
                ["role"] = "user",
                ["content"] = new[]
                {
                    new Dictionary<string, object?>
                    {
                        ["type"] = "tool_result",
                        ["tool_use_id"] = item.TryGetProperty("call_id", out var callId) ? callId.GetString() : "",
                        ["content"] = content ?? ""
                    }
                }
            };
        }

        return null;
    }

    /// <summary>
    /// 解析 data: URL，提取 media_type 和 base64 data
    /// </summary>
    private static (string MediaType, string Data) ParseDataUrl(string url)
    {
        var defaultMediaType = "image/png";
        if (!url.StartsWith("data:")) return (defaultMediaType, url);

        try
        {
            var commaIdx = url.IndexOf(',');
            if (commaIdx < 0) return (defaultMediaType, url);

            var header = url[5..commaIdx];
            var data = url[(commaIdx + 1)..];

            if (header.Contains(";"))
            {
                var parts = header.Split(';');
                return (parts[0] ?? defaultMediaType, data);
            }

            return (defaultMediaType, data);
        }
        catch
        {
            return (defaultMediaType, url);
        }
    }

    /// <summary>
    /// 将请求体中的 model 字段替换为上游实际模型ID
    /// 保留原始请求体的其他所有字段不变
    /// </summary>
    private static string ReplaceModelId(string body, string originalModelId)
    {
        if (string.IsNullOrEmpty(originalModelId)) return body;
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return body;
            // 用字典重建 JSON，确保字段顺序和类型保留
            var dict = new Dictionary<string, object?>();
            foreach (var prop in root.EnumerateObject())
            {
                dict[prop.Name] = prop.Name == "model"
                    ? originalModelId
                    : JsonSerializer.Deserialize<object>(prop.Value.GetRawText());
            }
            return JsonSerializer.Serialize(dict);
        }
        catch
        {
            return body;
        }
    }

    /// <summary>
    /// 根据供应商类型和请求路径构建完整的上游 API URL。
    /// 存储的 ApiAddress 应为 Base URL（如 https://api.openai.com/v1），
    /// 系统在此方法中自动追加接口路径（如 /chat/completions）。
    /// 若已包含完整路径（兼容旧数据），则直接返回原值。
    /// </summary>
    public static string BuildDownstreamUrl(string baseUrl, string? supplierType, string requestPath, string originalModelId)
    {
        var baseUri = baseUrl.TrimEnd('/');

        // 如果用户已经填写了完整路径（兼容旧数据），直接返回
        if (baseUri.Contains("/chat/completions") || baseUri.Contains("/messages") ||
            baseUri.Contains("/responses") || baseUri.Contains("generateContent"))
            return baseUri;

        var type = supplierType ?? "OpenAI";
        var isChat = requestPath.Contains("/chat/completions");

        return type switch
        {
            "OpenAI" or "DeepSeek" or "Groq" or "Together" or "Custom" =>
                requestPath.Contains("/chat/completions") ? $"{baseUri}/chat/completions" :
                requestPath.Contains("/messages") ? $"{baseUri}/messages" :
                $"{baseUri}/responses",

            "Azure" =>
                // Azure 格式：{base}/openai/deployments/{model}/chat/completions?api-version=2024-10-21
                isChat
                    ? $"{baseUri}/openai/deployments/{originalModelId}/chat/completions?api-version=2024-10-21"
                    : $"{baseUri}/openai/deployments/{originalModelId}/chat/completions?api-version=2024-10-21",

            "Anthropic" =>
                $"{baseUri}/v1/messages",

            "Google" =>
                $"{baseUri}/models/{originalModelId}:generateContent",

            _ => isChat ? $"{baseUri}/chat/completions" : baseUri
        };
    }

    private string ConvertResponseBody(string body, string protocolType, string requestPath, LoadBalanceNode node)
    {
        // 同协议直接透传
        var supplierType = node.SupplierType ?? "OpenAI";
        if (supplierType is "OpenAI" or "Azure" or "DeepSeek" or "Groq" or "Together" or "Custom")
            return body;

        // 非 OpenAI → OpenAI 格式转换
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;

            return supplierType switch
            {
                "Anthropic" => ConvertAnthropicToOpenAI(root, node.OriginalModelId),
                "Google" => ConvertGoogleToOpenAI(root, node.OriginalModelId),
                _ => body
            };
        }
        catch { return body; }
    }

    private static string ConvertAnthropicToOpenAI(JsonElement root, string modelId)
    {
        var content = new List<object>();
        if (root.TryGetProperty("content", out var ct) && ct.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in ct.EnumerateArray())
            {
                var type = item.TryGetProperty("type", out var t) ? t.GetString() : "text";
                var text = item.TryGetProperty("text", out var tx) ? tx.GetString() : "";
                content.Add(new { type, text });
            }
        }
        var inputTokens = root.TryGetProperty("usage", out var u) &&
                          u.TryGetProperty("input_tokens", out var it) ? it.GetInt32() : 0;
        var outputTokens = root.TryGetProperty("usage", out var u2) &&
                           u2.TryGetProperty("output_tokens", out var ot) ? ot.GetInt32() : 0;
        return JsonSerializer.Serialize(new
        {
            id = "chatcmpl-" + Guid.NewGuid().ToString("N")[..8],
            @object = "chat.completion",
            created = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            model = modelId,
            choices = new[]
            {
                new
                {
                    index = 0,
                    message = new { role = "assistant", content = content },
                    finish_reason = root.TryGetProperty("stop_reason", out var sr) ? sr.GetString() ?? "stop" : "stop"
                }
            },
            usage = new
            {
                prompt_tokens = inputTokens,
                completion_tokens = outputTokens,
                total_tokens = inputTokens + outputTokens
            }
        });
    }

    private static string ConvertGoogleToOpenAI(JsonElement root, string modelId)
    {
        var text = "";
        if (root.TryGetProperty("candidates", out var candidates) && candidates.ValueKind == JsonValueKind.Array)
        {
            foreach (var c in candidates.EnumerateArray())
            {
                if (c.TryGetProperty("content", out var content) &&
                    content.TryGetProperty("parts", out var parts) &&
                    parts.ValueKind == JsonValueKind.Array)
                {
                    foreach (var part in parts.EnumerateArray())
                    {
                        if (part.TryGetProperty("text", out var t))
                            text += t.GetString();
                    }
                }
            }
        }
        var inputTokens = root.TryGetProperty("usageMetadata", out var um) &&
                          um.TryGetProperty("promptTokenCount", out var ptc) ? ptc.GetInt32() : 0;
        var outputTokens = root.TryGetProperty("usageMetadata", out var um2) &&
                           um2.TryGetProperty("candidatesTokenCount", out var ctc) ? ctc.GetInt32() : 0;
        return JsonSerializer.Serialize(new
        {
            id = "chatcmpl-" + Guid.NewGuid().ToString("N")[..8],
            @object = "chat.completion",
            created = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            model = modelId,
            choices = new[]
            {
                new
                {
                    index = 0,
                    message = new { role = "assistant", content = text },
                    finish_reason = "stop"
                }
            },
            usage = new
            {
                prompt_tokens = inputTokens,
                completion_tokens = outputTokens,
                total_tokens = inputTokens + outputTokens
            }
        });
    }

    #region 协议协商

    /// <summary>
    /// 从客户端请求路径中提取协议类型标识
    /// </summary>
    private static string GetRequestType(string clientPath)
    {
        if (clientPath.Contains("/messages")) return "messages";
        if (clientPath.Contains("/responses")) return "responses";
        return "chat";
    }

    /// <summary>
    /// 将协议类型标识转换为具体的 API 路径
    /// </summary>
    private static string GetTypePath(string type)
    {
        return type switch
        {
            "messages" => "/v1/messages",
            "responses" => "/v1/responses",
            _ => "/v1/chat/completions"
        };
    }

    /// <summary>
    /// 设置上游请求的认证头。
    /// Anthropic 使用 x-api-key，其他供应商使用 Authorization: Bearer。
    /// 对 Anthropic 两种都设置以兼容不同客户端的用法。
    /// </summary>
    private static void SetAuthHeaders(HttpRequestMessage request, string supplierType, string apiKey)
    {
        if (supplierType == "Anthropic")
        {
            request.Headers.TryAddWithoutValidation("x-api-key", apiKey);
        }
        // 所有供应商都设置 Authorization: Bearer（Anthropic 也支持这种）
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
    }

    /// <summary>
    /// 确定上游实际调用的路径，以及是否需要协议转换。
    /// 优先检查 SupportedPaths（上游原生支持的协议列表），
    /// 如果客户端请求的协议在上游支持范围内则直接透传，
    /// 否则降级到 ProtocolType 指定的默认协议。
    /// </summary>
    /// <param name="clientPath">客户端请求路径，如 /v1/chat/completions 或 /v1/responses 或 /v1/messages</param>
    /// <param name="node">当前负载节点</param>
    /// <returns>(上游实际路径, 是否需要协议转换)</returns>
    private (string UpstreamPath, bool NeedsConversion) DetermineUpstreamPath(
        string clientPath, LoadBalanceNode node)
    {
        var clientType = GetRequestType(clientPath);

        // 检查上游 SupportedPaths 是否原生支持客户端请求的协议类型
        var supportedPaths = (node.SupportedPaths ?? "chat")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(p => p.Trim().ToLowerInvariant())
            .ToHashSet();

        if (supportedPaths.Contains(clientType))
        {
            // 上游原生支持该协议，直接透传（只需替换模型ID）
            var directPath = GetTypePath(clientType);
            return (directPath, false);
        }

        // 不支持则降级到 ProtocolType 指定的默认协议
        _logger.LogWarning(
            "协议降级：客户端请求 {ClientPath}（类型={ClientType}），上游 SupportedPaths=[{Paths}] 不支持，降级到默认协议 {Protocol}",
            clientPath, clientType, string.Join(",", supportedPaths), node.ProtocolType);

        var upstreamType = (node.ProtocolType ?? "Chat").ToLowerInvariant() switch
        {
            "response" => "responses",
            "messages" => "messages",
            _ => "chat"
        };
        var upstreamPath = GetTypePath(upstreamType);
        var needsConversion = !clientType.Equals(upstreamType, StringComparison.OrdinalIgnoreCase);

        if (needsConversion)
        {
            _logger.LogWarning(
                "协议转换：客户端请求 {ClientPath}（类型={ClientType}），降级后上游协议={Protocol}，上游路径={Upstream}",
                clientPath, clientType, node.ProtocolType, upstreamPath);
        }

        return (upstreamPath, needsConversion);
    }

    /// <summary>
    /// 将 OpenAI Chat Completions 响应转换为 Responses 格式
    /// </summary>
    private static string ConvertChatToResponse(string chatBody)
    {
        try
        {
            using var doc = JsonDocument.Parse(chatBody);
            var root = doc.RootElement;

            var responseId = root.TryGetProperty("id", out var id) ? id.GetString() : "resp_" + Guid.NewGuid().ToString("N")[..8];
            var createdUnix = root.TryGetProperty("created", out var created) ? created.GetDouble() : DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var model = root.TryGetProperty("model", out var m) ? m.GetString() : "unknown";

            // 提取 choices[0].message.content
            var text = "";
            if (root.TryGetProperty("choices", out var choices) && choices.ValueKind == JsonValueKind.Array)
            {
                foreach (var choice in choices.EnumerateArray())
                {
                    if (choice.TryGetProperty("message", out var msg) &&
                        msg.TryGetProperty("content", out var content) &&
                        content.ValueKind == JsonValueKind.String)
                    {
                        text = content.GetString() ?? "";
                    }
                }
            }

            // 提取 usage
            var inputTokens = 0; var outputTokens = 0;
            if (root.TryGetProperty("usage", out var usage))
            {
                inputTokens = usage.TryGetProperty("prompt_tokens", out var pt) ? pt.GetInt32() : 0;
                outputTokens = usage.TryGetProperty("completion_tokens", out var ct) ? ct.GetInt32() : 0;
            }

            return JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                ["id"] = responseId,
                ["object"] = "response",
                ["created_at"] = createdUnix,
                ["model"] = model,
                ["output"] = new[]
                {
                    new Dictionary<string, object?>
                    {
                        ["id"] = "msg_" + Guid.NewGuid().ToString("N")[..8],
                        ["type"] = "message",
                        ["role"] = "assistant",
                        ["content"] = new[]
                        {
                            new Dictionary<string, object?>
                            {
                                ["type"] = "output_text",
                                ["text"] = text,
                                ["annotations"] = Array.Empty<string>()
                            }
                        }
                    }
                },
                ["usage"] = new Dictionary<string, int>
                {
                    ["input_tokens"] = inputTokens,
                    ["output_tokens"] = outputTokens,
                    ["total_tokens"] = inputTokens + outputTokens
                }
            });
        }
        catch { return chatBody; }
    }

    /// <summary>
    /// 将 OpenAI Responses 响应转换为 Chat Completions 格式
    /// </summary>
    private static string ConvertResponseToChat(string responseBody)
    {
        try
        {
            using var doc = JsonDocument.Parse(responseBody);
            var root = doc.RootElement;

            var chatId = "chatcmpl-" + Guid.NewGuid().ToString("N")[..8];
            var createdUnix = root.TryGetProperty("created_at", out var created)
                ? (long)created.GetDouble() : DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var model = root.TryGetProperty("model", out var m) ? m.GetString() : "unknown";

            // 从 output[0].content[0].text 提取文本
            var text = "";
            if (root.TryGetProperty("output", out var output) && output.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in output.EnumerateArray())
                {
                    if (item.TryGetProperty("type", out var type) && type.GetString() == "message" &&
                        item.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var part in content.EnumerateArray())
                        {
                            if (part.TryGetProperty("type", out var pt) && pt.GetString() == "output_text" &&
                                part.TryGetProperty("text", out var t))
                            {
                                text = t.GetString() ?? "";
                            }
                        }
                    }
                }
            }

            // 提取 usage
            var inputTokens = 0; var outputTokens = 0;
            if (root.TryGetProperty("usage", out var usage))
            {
                inputTokens = usage.TryGetProperty("input_tokens", out var it) ? it.GetInt32() : 0;
                outputTokens = usage.TryGetProperty("output_tokens", out var ot) ? ot.GetInt32() : 0;
            }

            return JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                ["id"] = chatId,
                ["object"] = "chat.completion",
                ["created"] = createdUnix,
                ["model"] = model,
                ["choices"] = new[]
                {
                    new Dictionary<string, object?>
                    {
                        ["index"] = 0,
                        ["message"] = new Dictionary<string, object?>
                        {
                            ["role"] = "assistant",
                            ["content"] = text
                        },
                        ["finish_reason"] = "stop"
                    }
                },
                ["usage"] = new Dictionary<string, int>
                {
                    ["prompt_tokens"] = inputTokens,
                    ["completion_tokens"] = outputTokens,
                    ["total_tokens"] = inputTokens + outputTokens
                }
            });
        }
        catch { return responseBody; }
    }

    /// <summary>
    /// 将 OpenAI Chat Completions 格式转换为 Anthropic Messages 格式
    /// </summary>
    private static string ConvertChatToMessages(string chatBody)
    {
        try
        {
            using var doc = JsonDocument.Parse(chatBody);
            var root = doc.RootElement;

            var msgId = "msg_" + Guid.NewGuid().ToString("N")[..12];
            var model = root.TryGetProperty("model", out var m) ? m.GetString() : "unknown";

            // 提取 choices[0].message.content
            var text = "";
            if (root.TryGetProperty("choices", out var choices) && choices.ValueKind == JsonValueKind.Array)
            {
                foreach (var choice in choices.EnumerateArray())
                {
                    if (choice.TryGetProperty("message", out var msg) &&
                        msg.TryGetProperty("content", out var content) &&
                        content.ValueKind == JsonValueKind.String)
                    {
                        text = content.GetString() ?? "";
                    }
                }
            }

            // 提取 usage
            var inputTokens = 0; var outputTokens = 0;
            if (root.TryGetProperty("usage", out var usage))
            {
                inputTokens = usage.TryGetProperty("prompt_tokens", out var pt) ? pt.GetInt32() : 0;
                outputTokens = usage.TryGetProperty("completion_tokens", out var ct) ? ct.GetInt32() : 0;
            }

            var stopReason = "end_turn";
            if (root.TryGetProperty("choices", out var ch2) && ch2.ValueKind == JsonValueKind.Array &&
                ch2.GetArrayLength() > 0)
            {
                var first = ch2[0];
                if (first.TryGetProperty("finish_reason", out var fr))
                {
                    var frStr = fr.GetString();
                    stopReason = frStr switch
                    {
                        "stop" => "end_turn",
                        "length" => "max_tokens",
                        _ => "end_turn"
                    };
                }
            }

            // 构建 Anthropic Messages 格式
            var contentArr = new[]
            {
                new Dictionary<string, object?>
                {
                    ["type"] = "text",
                    ["text"] = text
                }
            };

            var response = new Dictionary<string, object?>
            {
                ["id"] = msgId,
                ["type"] = "message",
                ["role"] = "assistant",
                ["content"] = contentArr,
                ["model"] = model,
                ["stop_reason"] = stopReason,
                ["stop_sequence"] = null,
                ["usage"] = new Dictionary<string, int>
                {
                    ["input_tokens"] = inputTokens,
                    ["output_tokens"] = outputTokens
                }
            };

            return JsonSerializer.Serialize(response);
        }
        catch { return chatBody; }
    }

    /// <summary>
    /// 将 Anthropic Messages 格式转换为 OpenAI Chat Completions 格式
    /// </summary>
    private static string ConvertMessagesToChat(string messagesBody)
    {
        try
        {
            using var doc = JsonDocument.Parse(messagesBody);
            var root = doc.RootElement;

            var chatId = "chatcmpl-" + Guid.NewGuid().ToString("N")[..8];
            var createdUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var model = root.TryGetProperty("model", out var m) ? m.GetString() : "unknown";

            // 从 content 数组中提取文本
            var text = "";
            if (root.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in content.EnumerateArray())
                {
                    if (item.TryGetProperty("type", out var t) && t.GetString() == "text" &&
                        item.TryGetProperty("text", out var tx))
                    {
                        text = tx.GetString() ?? "";
                    }
                }
            }

            // 提取 usage
            var inputTokens = 0; var outputTokens = 0;
            if (root.TryGetProperty("usage", out var usage))
            {
                inputTokens = usage.TryGetProperty("input_tokens", out var it) ? it.GetInt32() : 0;
                outputTokens = usage.TryGetProperty("output_tokens", out var ot) ? ot.GetInt32() : 0;
            }

            var stopReason = root.TryGetProperty("stop_reason", out var sr) ? sr.GetString() : "stop";
            var finishReason = stopReason switch
            {
                "end_turn" => "stop",
                "max_tokens" => "length",
                _ => "stop"
            };

            return JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                ["id"] = chatId,
                ["object"] = "chat.completion",
                ["created"] = createdUnix,
                ["model"] = model,
                ["choices"] = new[]
                {
                    new Dictionary<string, object?>
                    {
                        ["index"] = 0,
                        ["message"] = new Dictionary<string, object?>
                        {
                            ["role"] = "assistant",
                            ["content"] = text
                        },
                        ["finish_reason"] = finishReason
                    }
                },
                ["usage"] = new Dictionary<string, int>
                {
                    ["prompt_tokens"] = inputTokens,
                    ["completion_tokens"] = outputTokens,
                    ["total_tokens"] = inputTokens + outputTokens
                }
            });
        }
        catch { return messagesBody; }
    }

    /// <summary>
    /// 确定响应体的实际格式（经 ConvertResponseBody 转换后的格式）
    /// </summary>
    private static string DetermineResponseFormat(string upstreamPath, LoadBalanceNode node)
    {
        // 非 OpenAI 兼容供应商的响应已被 ConvertResponseBody 转换为 Chat 格式
        var supplierType = node.SupplierType ?? "OpenAI";
        if (supplierType is not ("OpenAI" or "Azure" or "DeepSeek" or "Groq" or "Together" or "Custom"))
            return "chat";
        // OpenAI 兼容供应商：响应格式取决于实际调用的上游路径
        return GetRequestType(upstreamPath);
    }

    /// <summary>
    /// 协议转换：将上游响应从一种格式转换为客户端请求的格式。
    /// 以 Chat 格式为中间格式，支持 chat ↔ responses ↔ messages 双向转换。
    /// </summary>
    private static string ConvertResponseFormat(string body, string upstreamPath, string clientRequestPath, LoadBalanceNode node)
    {
        var fromType = DetermineResponseFormat(upstreamPath, node);
        var toType = GetRequestType(clientRequestPath);
        if (fromType == toType) return body;

        // 第一步：从源格式转换为 Chat 中间格式
        var chatBody = fromType switch
        {
            "responses" => ConvertResponseToChat(body),
            "messages" => ConvertMessagesToChat(body),
            _ => body // already chat
        };

        // 第二步：从 Chat 中间格式转换为目标格式
        return toType switch
        {
            "responses" => ConvertChatToResponse(chatBody),
            "messages" => ConvertChatToMessages(chatBody),
            _ => chatBody // already chat
        };
    }

    #endregion

    #region 流式降级（非流式调用 + 模拟 SSE）

    /// <summary>移除请求体中的 stream 标志，转为非流式调用</summary>
    private static string RemoveStreamFlag(string jsonBody)
    {
        return System.Text.RegularExpressions.Regex.Replace(jsonBody,
            @"""stream""\s*:\s*(true|false)\s*,?\s*", "");
    }

    /// <summary>
    /// 将完整响应模拟为 SSE 流式事件推送给客户端
    /// </summary>
    private static async Task FakeSseStreamAsync(HttpResponse response, string finalResponse, string requestPath)
    {
        response.ContentType = "text/event-stream";
        response.Headers.CacheControl = "no-cache";
        response.Headers.Connection = "keep-alive";

        var isChat = requestPath.Contains("/chat/completions");
        var isMessages = requestPath.Contains("/messages");
        using var doc = JsonDocument.Parse(finalResponse);
        var root = doc.RootElement;

        if (isChat)
        {
            var id = root.TryGetProperty("id", out var idProp) ? idProp.GetString() : "chatcmpl-xxx";
            var created = root.TryGetProperty("created", out var createdProp) ? createdProp.GetInt64() : DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var model = root.TryGetProperty("model", out var modelProp) ? modelProp.GetString() : "";
            var text = "";
            if (root.TryGetProperty("choices", out var choices) && choices.ValueKind == JsonValueKind.Array && choices.GetArrayLength() > 0)
            {
                var choice = choices[0];
                if (choice.TryGetProperty("message", out var msg) && msg.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.String)
                    text = content.GetString() ?? "";
            }

            // 1. role delta
            var roleEvent = JsonSerializer.Serialize(new
            {
                id, @object = "chat.completion.chunk", created, model,
                choices = new[] { new { index = 0, delta = new { role = "assistant" }, finish_reason = (string?)null } }
            });
            await response.WriteAsync($"data: {roleEvent}\n\n", Encoding.UTF8);
            await response.Body.FlushAsync();

            // 2. content delta（每 ~5 字符一个 chunk）
            var chunkSize = 5;
            for (int i = 0; i < text.Length; i += chunkSize)
            {
                var chunk = text.Substring(i, Math.Min(chunkSize, text.Length - i));
                var contentEvent = JsonSerializer.Serialize(new
                {
                    id, @object = "chat.completion.chunk", created, model,
                    choices = new[] { new { index = 0, delta = new { content = chunk }, finish_reason = (string?)null } }
                });
                await response.WriteAsync($"data: {contentEvent}\n\n", Encoding.UTF8);
                await response.Body.FlushAsync();
            }

            // 3. final chunk
            var finalChunk = JsonSerializer.Serialize(new
            {
                id, @object = "chat.completion.chunk", created, model,
                choices = new[] { new { index = 0, delta = new { }, finish_reason = "stop" } }
            });
            await response.WriteAsync($"data: {finalChunk}\n\n", Encoding.UTF8);
            await response.Body.FlushAsync();

            // 4. [DONE]
            await response.WriteAsync("data: [DONE]\n\n", Encoding.UTF8);
            await response.Body.FlushAsync();
        }
        else if (isMessages)
        {
            // ======== Anthropic Messages SSE 格式 ========
            var msgId = root.TryGetProperty("id", out var idProp2) ? idProp2.GetString() : "msg_" + Guid.NewGuid().ToString("N")[..12];
            var model = root.TryGetProperty("model", out var modelProp2) ? modelProp2.GetString() : "";
            var text = "";
            if (root.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in content.EnumerateArray())
                {
                    if (item.TryGetProperty("type", out var t) && t.GetString() == "text" &&
                        item.TryGetProperty("text", out var tx))
                    {
                        text = tx.GetString() ?? "";
                    }
                }
            }
            var stopReason = root.TryGetProperty("stop_reason", out var sr) ? sr.GetString() : "end_turn";
            var inputTokens = root.TryGetProperty("usage", out var u) &&
                              u.TryGetProperty("input_tokens", out var it) ? it.GetInt32() : 0;
            var outputTokens = root.TryGetProperty("usage", out var u2) &&
                               u2.TryGetProperty("output_tokens", out var ot) ? ot.GetInt32() : 0;

            // 1. message_start
            var msgStart = new Dictionary<string, object?>
            {
                ["type"] = "message_start",
                ["message"] = new Dictionary<string, object?>
                {
                    ["id"] = msgId,
                    ["type"] = "message",
                    ["role"] = "assistant",
                    ["content"] = new object[] { },
                    ["model"] = model,
                    ["stop_reason"] = null,
                    ["stop_sequence"] = null,
                    ["usage"] = new Dictionary<string, int> { ["input_tokens"] = inputTokens, ["output_tokens"] = 0 }
                }
            };
            await response.WriteAsync($"event: message_start\ndata: {JsonSerializer.Serialize(msgStart)}\n\n", Encoding.UTF8);
            await response.Body.FlushAsync();

            // 2. content_block_start
            var blockStart = new Dictionary<string, object?>
            {
                ["type"] = "content_block_start",
                ["index"] = 0,
                ["content_block"] = new Dictionary<string, object?>
                {
                    ["type"] = "text",
                    ["text"] = ""
                }
            };
            await response.WriteAsync($"event: content_block_start\ndata: {JsonSerializer.Serialize(blockStart)}\n\n", Encoding.UTF8);
            await response.Body.FlushAsync();

            // 3. content_block_delta（每 ~5 字符一个 chunk）
            var chunkSize = 5;
            for (int i = 0; i < text.Length; i += chunkSize)
            {
                var chunk = text.Substring(i, Math.Min(chunkSize, text.Length - i));
                var deltaEvent = new Dictionary<string, object?>
                {
                    ["type"] = "content_block_delta",
                    ["index"] = 0,
                    ["delta"] = new Dictionary<string, object?>
                    {
                        ["type"] = "text_delta",
                        ["text"] = chunk
                    }
                };
                await response.WriteAsync($"event: content_block_delta\ndata: {JsonSerializer.Serialize(deltaEvent)}\n\n", Encoding.UTF8);
                await response.Body.FlushAsync();
            }

            // 4. content_block_stop
            var blockStop = new Dictionary<string, object?>
            {
                ["type"] = "content_block_stop",
                ["index"] = 0
            };
            await response.WriteAsync($"event: content_block_stop\ndata: {JsonSerializer.Serialize(blockStop)}\n\n", Encoding.UTF8);
            await response.Body.FlushAsync();

            // 5. message_delta
            var msgDelta = new Dictionary<string, object?>
            {
                ["type"] = "message_delta",
                ["delta"] = new Dictionary<string, object?>
                {
                    ["stop_reason"] = stopReason,
                    ["stop_sequence"] = null
                },
                ["usage"] = new Dictionary<string, int> { ["output_tokens"] = outputTokens }
            };
            await response.WriteAsync($"event: message_delta\ndata: {JsonSerializer.Serialize(msgDelta)}\n\n", Encoding.UTF8);
            await response.Body.FlushAsync();

            // 6. message_stop
            var msgStop = new Dictionary<string, object?>
            {
                ["type"] = "message_stop"
            };
            await response.WriteAsync($"event: message_stop\ndata: {JsonSerializer.Serialize(msgStop)}\n\n", Encoding.UTF8);
            await response.Body.FlushAsync();
        }
        else
        {
            // ======== OpenAI Response SSE 格式 ========
            // 从 output[0].content[0].text 提取文本
            var text = "";
            if (root.TryGetProperty("output", out var output) && output.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in output.EnumerateArray())
                {
                    if (item.TryGetProperty("type", out var type) && type.GetString() == "message" &&
                        item.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var part in content.EnumerateArray())
                        {
                            if (part.TryGetProperty("type", out var pt) && pt.GetString() == "output_text" &&
                                part.TryGetProperty("text", out var t))
                            {
                                text = t.GetString() ?? "";
                            }
                        }
                    }
                }
            }

            // 模拟 Response SSE 事件
            int seq = 0;
            var chunkSize = 5;
            for (int i = 0; i < text.Length; i += chunkSize)
            {
                var chunk = text.Substring(i, Math.Min(chunkSize, text.Length - i));
                seq++;
                var deltaEvent = JsonSerializer.Serialize(new { delta = chunk, sequence_number = seq });
                await response.WriteAsync($"event: response.output_text.delta\ndata: {deltaEvent}\n\n", Encoding.UTF8);
                await response.Body.FlushAsync();
            }

            // completed 事件
            var responseId = root.TryGetProperty("id", out var rid) ? rid.GetString() : "resp_xxx";
            var outArr = root.TryGetProperty("output", out var oa) ? JsonSerializer.Deserialize<object>(oa.GetRawText()) : null;
            var completedEvent = JsonSerializer.Serialize(new { id = responseId, @object = "response", output = outArr });
            await response.WriteAsync($"event: response.completed\ndata: {completedEvent}\n\n", Encoding.UTF8);
            await response.Body.FlushAsync();
        }
    }

    #endregion

    private (int input, int output) ExtractTokenUsage(string responseBody)
    {
        try
        {
            using var doc = JsonDocument.Parse(responseBody);
            var root = doc.RootElement;

            // 1. 优先从 usage 字段读取（OpenAI / Anthropic 转换后格式）
            if (root.TryGetProperty("usage", out var usage))
            {
                var input = usage.TryGetProperty("prompt_tokens", out var pt) ? pt.GetInt32() : 0;
                var output = usage.TryGetProperty("completion_tokens", out var ct) ? ct.GetInt32() : 0;
                if (input > 0 || output > 0)
                    return (input, output);
            }

            // 2. Google Gemini 格式
            if (root.TryGetProperty("usageMetadata", out var gUsage))
            {
                var input = gUsage.TryGetProperty("promptTokenCount", out var git) ? git.GetInt32() : 0;
                var output = gUsage.TryGetProperty("candidatesTokenCount", out var got) ? got.GetInt32() : 0;
                if (input > 0 || output > 0)
                    return (input, output);
            }

            // 3. 后备：从响应内容中估算（当上游未返回 usage 时）
            var contentText = ExtractResponseContent(root);
            var estimatedOutput = TokenEstimator.EstimateTokens(contentText);
            if (estimatedOutput > 0)
            {
                _logger.LogWarning("上游未返回 usage 信息，使用内容估算 Token：Output={Out}", estimatedOutput);
                return (0, estimatedOutput);
            }
        }
        catch { }
        return (0, 0);
    }

    /// <summary>
    /// 从响应体中提取实际文本内容用于 token 估算
    /// 支持 OpenAI / Anthropic / Google 等多种格式
    /// </summary>
    private static string ExtractResponseContent(JsonElement root)
    {
        try
        {
            // OpenAI / Anthropic 转换后格式：choices[0].message.content
            if (root.TryGetProperty("choices", out var choices) && choices.ValueKind == JsonValueKind.Array)
            {
                foreach (var choice in choices.EnumerateArray())
                {
                    // 非流式 choices[0].message.content
                    if (choice.TryGetProperty("message", out var message) &&
                        message.TryGetProperty("content", out var content))
                    {
                        if (content.ValueKind == JsonValueKind.String)
                            return content.GetString() ?? "";
                        if (content.ValueKind == JsonValueKind.Array)
                        {
                            var sb = new System.Text.StringBuilder();
                            foreach (var item in content.EnumerateArray())
                            {
                                if (item.TryGetProperty("text", out var text))
                                    sb.Append(text.GetString());
                            }
                            return sb.ToString();
                        }
                    }

                    // 流式 choices[0].delta.content（兜底）
                    if (choice.TryGetProperty("delta", out var delta) &&
                        delta.TryGetProperty("content", out var deltaContent))
                    {
                        return deltaContent.GetString() ?? "";
                    }
                }
            }

            // Anthropic 原始格式：content[0].text
            if (root.TryGetProperty("content", out var antContent) && antContent.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in antContent.EnumerateArray())
                {
                    if (item.TryGetProperty("text", out var text))
                        return text.GetString() ?? "";
                }
            }

            // Google 原始格式：candidates[0].content.parts[0].text
            if (root.TryGetProperty("candidates", out var candidates) && candidates.ValueKind == JsonValueKind.Array)
            {
                foreach (var candidate in candidates.EnumerateArray())
                {
                    if (candidate.TryGetProperty("content", out var gContent) &&
                        gContent.TryGetProperty("parts", out var parts) &&
                        parts.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var part in parts.EnumerateArray())
                        {
                            if (part.TryGetProperty("text", out var text))
                                return text.GetString() ?? "";
                        }
                    }
                }
            }
        }
        catch { }

        return "";
    }

    private async Task<long> LogCall(string? tokenValue, string? customModelId, string? channelName,
        string? originalModelId, string status, bool isStream, int inputTokens, int outputTokens,
        long durationMs, string? requestBody, string? responseBody, string? error, string clientIp)
    {
        try
        {
            var log = new CallLog
            {
                TokenValue = tokenValue,
                ClientIp = clientIp,
                CustomModelId = customModelId,
                OriginalModelId = originalModelId,
                ChannelName = channelName,
                Status = status,
                IsStream = isStream,
                InputTokens = inputTokens,
                OutputTokens = outputTokens,
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

    /// <summary>
    /// 记录上游API Key使用统计
    /// </summary>
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
        catch (Exception ex)
        {
            _logger.LogError(ex, "记录上游API Key用量失败, KeyId={KeyId}", apiKeyId);
        }
    }

    /// <summary>
    /// 从请求/响应体中提取base64图片并保存到 call_images 表
    /// </summary>
    private async Task SaveCallImagesAsync(long callLogId, string? requestBody, string? responseBody)
    {
        try
        {
            // 从请求中提取图片
            if (!string.IsNullOrEmpty(requestBody))
                await ExtractAndSaveImages(callLogId, "request", requestBody);

            // 从响应中提取图片
            if (!string.IsNullOrEmpty(responseBody))
                await ExtractAndSaveImages(callLogId, "response", responseBody);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "保存调用图片失败, CallLogId={Id}", callLogId);
        }
    }

    /// <summary>
    /// 从 JSON 消息体 content 数组中提取 base64 图片或图片 URL
    /// </summary>
    private async Task ExtractAndSaveImages(long callLogId, string source, string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;

            // 遍历 messages 数组中的 content 字段
            if (root.TryGetProperty("messages", out var messages))
            {
                int contentIdx = 0;
                foreach (var msg in messages.EnumerateArray())
                {
                    if (msg.TryGetProperty("content", out var content))
                    {
                        if (content.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var part in content.EnumerateArray())
                            {
                                if (part.TryGetProperty("type", out var pType))
                                {
                                    var type = pType.GetString();
                                    if (type == "image_url" && part.TryGetProperty("image_url", out var imgUrl))
                                    {
                                        var url = imgUrl.GetString() ?? "";
                                        await SaveOneImage(callLogId, source, url, contentIdx);
                                        contentIdx++;
                                    }
                                }
                            }
                        }
                    }
                }
            }
        }
        catch { /* 非标准结构，跳过图片提取 */ }
    }

    /// <summary>
    /// 保存单张图片到文件系统 + call_images 表
    /// 支持 data:image/xxx;base64,xxxx 格式和纯 URL 格式
    /// base64 图片数据写入 logs/images/{yyyyMMdd}/{callLogId}_{index}.{ext} 文件
    /// </summary>
    private async Task SaveOneImage(long callLogId, string source, string rawValue, int contentIndex)
    {
        try
        {
            var img = new CallImage
            {
                CallLogId = callLogId,
                Source = source,
                ContentIndex = contentIndex,
            };

            if (rawValue.StartsWith("data:"))
            {
                // base64 图片: data:image/png;base64,xxxx
                img.ImageType = "base64";
                var semiIdx = rawValue.IndexOf(';');
                var commaIdx = rawValue.IndexOf(',');
                if (semiIdx > 0 && commaIdx > semiIdx)
                {
                    img.MimeType = rawValue[5..semiIdx]; // "data:" 后跟 MIME
                    var base64Data = rawValue[(commaIdx + 1)..];

                    // 写入文件系统
                    var ext = img.MimeType?.Split('/')?.LastOrDefault() ?? "png";
                    var dateDir = DateTime.UtcNow.ToString("yyyyMMdd");
                    var fileName = $"{callLogId}_{source}_{contentIndex}.{ext}";
                    var relativePath = Path.Combine("logs", "images", dateDir, fileName);
                    var fullDir = Path.Combine(AppContext.BaseDirectory, "logs", "images", dateDir);
                    Directory.CreateDirectory(fullDir);
                    var fullPath = Path.Combine(fullDir, fileName);
                    await File.WriteAllBytesAsync(fullPath, Convert.FromBase64String(base64Data));

                    img.FilePath = relativePath;
                }
                else
                {
                    // 无法解析 MIME，直接写入原始数据
                    var dateDir = DateTime.UtcNow.ToString("yyyyMMdd");
                    var fileName = $"{callLogId}_{source}_{contentIndex}.bin";
                    var relativePath = Path.Combine("logs", "images", dateDir, fileName);
                    var fullDir = Path.Combine(AppContext.BaseDirectory, "logs", "images", dateDir);
                    Directory.CreateDirectory(fullDir);
                    var fullPath = Path.Combine(fullDir, fileName);
                    await File.WriteAllBytesAsync(fullPath, Convert.FromBase64String(rawValue));
                    img.FilePath = relativePath;
                }
            }
            else
            {
                // URL 图片 — 直接存 URL 到 DB
                img.ImageType = "url";
                img.ImageUrl = rawValue[..Math.Min(rawValue.Length, 2000)];
            }

            img.CreatedAt = DateTime.UtcNow;
            await _callImageRepo.InsertAsync(img);
        }
        catch { /* 单张图片保存失败，不阻塞主流程 */ }
    }

    #endregion
}
