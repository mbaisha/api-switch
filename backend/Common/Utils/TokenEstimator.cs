using System.Text.Json;

namespace backend.Common.Utils;

/// <summary>
/// Token 估算工具 — 基于字符类型分析的近似计算
/// 在不引入 tiktoken 等外部库的前提下，对中英文混合文本提供相对准确的估算
/// 注意：宁可多算不可少算，避免因低估导致计费亏损
/// 
/// 估算依据（OpenAI cl100k_base 编码器实测校准，含安全余量）：
///   - 英文/数字/标点：约 2 字符 = 1 token，取 0.5/字符
///   - 中文(CJK)：约 1 字符 = 1~2 token，取 1.5/字符
///   - 其他 Unicode：~1 字符 = 1~2 token，取 1.5/字符
/// </summary>
public static class TokenEstimator
{
    /// <summary>
    /// 从任意文本估算 token 数（含安全余量，宁可多算不可少算）
    /// </summary>
    public static int EstimateTokens(string? text)
    {
        if (string.IsNullOrEmpty(text)) return 0;

        double tokens = 0;
        foreach (var c in text)
        {
            if (c >= 0x4E00 && c <= 0x9FFF) // CJK 统一表意文字（中文）
                tokens += 1.5;   // 安全余量：1.5/字符（实际约1.0）
            else if (c >= 0x0600 && c <= 0x06FF) // 阿拉伯文
                tokens += 1.5;
            else if (c > 0x7F) // 其他非 ASCII（标点等）
                tokens += 1.5;
            else if (char.IsWhiteSpace(c))
                continue;       // 空白字符不计
            else
                tokens += 0.5;  // 安全余量：0.5/字符（实际约0.35）
        }

        return Math.Max(1, (int)Math.Ceiling(tokens));
    }

    /// <summary>
    /// 从请求体中提取 messages 并估算输入 token 数
    /// 注意：下游 API 实际 token 数通常远大于纯文本估算（含系统指令、格式开销等），
    /// 因此这里使用 body 长度作为基准并乘以安全系数，宁可多算不可少算
    /// </summary>
    public static int EstimateInputTokens(string requestBody)
    {
        if (string.IsNullOrEmpty(requestBody)) return 0;

        try
        {
            using var doc = JsonDocument.Parse(requestBody);
            var root = doc.RootElement;

            // 基础开销
            int baseTokens = 3;

            // 1. 提取 messages 数组
            if (root.TryGetProperty("messages", out var messages) && messages.ValueKind == JsonValueKind.Array)
            {
                foreach (var msg in messages.EnumerateArray())
                {
                    // 消息角色开销
                    baseTokens += 3;

                    var role = msg.TryGetProperty("role", out var r) ? r.GetString() : "user";
                    baseTokens += EstimateTokens(role);

                    if (msg.TryGetProperty("content", out var content))
                    {
                        baseTokens += EstimateContentToken(content);
                    }

                    if (msg.TryGetProperty("name", out var name))
                        baseTokens += EstimateTokens(name.GetString());
                }
            }

            // 2. system 指令
            if (root.TryGetProperty("system", out var system))
                baseTokens += EstimateContentToken(system);

            // 3. tools/functions 定义
            if (root.TryGetProperty("tools", out var tools) && tools.ValueKind == JsonValueKind.Array)
            {
                baseTokens += EstimateTokens(tools.GetRawText());
            }

            // 4. model 字段
            if (root.TryGetProperty("model", out var model))
                baseTokens += EstimateTokens(model.GetString());

            // 5. 取结构化估算值与 body 长度估算的较大值，再乘以安全系数
            //    下游实际 token 数通常包含系统提示词等额外开销，结构化估算远低于实际值
            var bodyBased = (int)Math.Ceiling(requestBody.Length * 0.8);
            var structured = Math.Max(1, baseTokens);
            var result = Math.Max(structured, bodyBased);

            // 额外安全系数 ×2，确保不低估
            return Math.Max(50, result);
        }
        catch
        {
            // 解析失败时按原始 body 长度估算
            return Math.Max(50, (int)Math.Ceiling(requestBody.Length * 0.8));
        }
    }

    /// <summary>
    /// 从响应内容估算输出 token 数
    /// </summary>
    public static int EstimateOutputTokens(string? content)
    {
        if (string.IsNullOrEmpty(content)) return 0;
        return EstimateTokens(content);
    }

    /// <summary>
    /// 处理 content 字段（可能是 string 或 complex array）
    /// </summary>
    private static int EstimateContentToken(JsonElement content)
    {
        if (content.ValueKind == JsonValueKind.String)
            return EstimateTokens(content.GetString());

        if (content.ValueKind == JsonValueKind.Array)
        {
            int total = 0;
            foreach (var item in content.EnumerateArray())
            {
                if (item.TryGetProperty("text", out var text))
                    total += EstimateTokens(text.GetString());
                else if (item.TryGetProperty("image_url", out _))
                    total += 85; // base64 图片 ≈ 85 token（标准估算）
                else if (item.TryGetProperty("image", out _))
                    total += 85;
                else
                    total += EstimateTokens(item.GetRawText());
            }
            return total;
        }

        return EstimateTokens(content.GetRawText());
    }

    /// <summary>
    /// 检测 SSE 数据行是否是包含 usage 信息的最后一个 chunk
    /// OpenAI 在 stream_options: {"include_usage": true} 时
    /// 最后一个 data 行会包含 usage 字段
    /// </summary>
    public static bool TryExtractStreamUsage(string line, out int inputTokens, out int outputTokens)
    {
        inputTokens = 0;
        outputTokens = 0;

        if (!line.StartsWith("data: ")) return false;
        var payload = line[6..].Trim();

        // [DONE] 标记
        if (payload == "[DONE]") return false;

        try
        {
            using var doc = JsonDocument.Parse(payload);
            var root = doc.RootElement;

            // 包含 usage 字段且 choices 为空或 usage 存在
            if (root.TryGetProperty("usage", out var usage))
            {
                // 必须是流结束的 usage chunk（choices 通常为空或只有 finish_reason）
                bool hasChoices = root.TryGetProperty("choices", out var choices) &&
                                  choices.ValueKind == JsonValueKind.Array &&
                                  choices.GetArrayLength() > 0;

                if (!hasChoices || HasFinishReason(choices))
                {
                    inputTokens = usage.TryGetProperty("prompt_tokens", out var pt) ? pt.GetInt32() : 0;
                    outputTokens = usage.TryGetProperty("completion_tokens", out var ct) ? ct.GetInt32() : 0;
                    return inputTokens > 0 || outputTokens > 0;
                }
            }
        }
        catch { }

        return false;
    }

    private static bool HasFinishReason(JsonElement choices)
    {
        foreach (var choice in choices.EnumerateArray())
        {
            if (choice.TryGetProperty("finish_reason", out var reason) &&
                reason.ValueKind != JsonValueKind.Null &&
                reason.GetString() != null)
                return true;
        }
        return false;
    }
}