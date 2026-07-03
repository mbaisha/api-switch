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
            foreach (var node in candidates)
            {
                anyAttempted = true;
                _logger.LogInformation("尝试转发 → 渠道 {Channel}/{Model} API Key={Key} (第 {Attempt} 轮)",
                    node.ChannelName, node.OriginalModelId,
                    node.ApiKey[..Math.Min(node.ApiKey.Length, 8)], attempt + 1);
                var fullUrl = BuildDownstreamUrl(node.ApiAddress, node.SupplierType, requestPath, node.OriginalModelId);
                var convertedBody = ConvertRequestBody(requestBody, requestPath, node);

                try
                {
                    var client = _httpClientFactory.CreateClient("AIClient");
                    client.Timeout = TimeSpan.FromSeconds(node.TimeoutSeconds);

                    var httpRequest = new HttpRequestMessage(HttpMethod.Post, fullUrl)
                    {
                        Content = new StringContent(convertedBody, Encoding.UTF8, "application/json")
                    };
                    httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", node.ApiKey);
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
                    var convertedResponse = ConvertResponseBody(responseBody, node.ProtocolType, requestPath, node);
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
                var fullUrl = BuildDownstreamUrl(node.ApiAddress, node.SupplierType, requestPath, node.OriginalModelId);
                var convertedBody = ConvertRequestBody(requestBody, requestPath, node);

                try
                {
                    var client = _httpClientFactory.CreateClient("AIClient");
                    client.Timeout = TimeSpan.FromSeconds(node.TimeoutSeconds);

                    var httpRequest = new HttpRequestMessage(HttpMethod.Post, fullUrl)
                    {
                        Content = new StringContent(convertedBody, Encoding.UTF8, "application/json")
                    };
                    httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", node.ApiKey);
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
        var isChatPath = requestPath.Contains("/chat/completions");

        // 同协议：替换模型ID后透传（将用户的 customModelId 替换为上游实际模型ID）
        if ((isChatPath && supplierType is "OpenAI" or "Azure" or "DeepSeek" or "Groq" or "Together" or "Custom") ||
            (!isChatPath && supplierType == "OpenAI"))
        {
            return ReplaceModelId(body, node.OriginalModelId);
        }

        // 非 OpenAI 协议需要转换
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
                isChat ? $"{baseUri}/chat/completions" : $"{baseUri}/responses",

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
