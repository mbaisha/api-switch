using System.Text.Json;
using backend.Common.DTOs;
using backend.Common.Models;
using backend.Repository;
using backend.Services;
using FreeSql;
using Microsoft.AspNetCore.Mvc;

namespace backend.Controllers;

/// <summary>
/// 渠道管理 - 支持供应商模板、批量添加模型、模型测试
/// </summary>
[ApiController]
[Route("api/admin/channels")]
public class ChannelController : ControllerBase
{
    private readonly BaseRepository<Channel> _channelRepo;
    private readonly BaseRepository<ModelChain> _chainRepo;
    private readonly IFreeSql _db;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ImageForwardEngine _imageForwardEngine;

    public ChannelController(
        BaseRepository<Channel> channelRepo,
        BaseRepository<ModelChain> chainRepo,
        IFreeSql db,
        IHttpClientFactory httpClientFactory,
        ImageForwardEngine imageForwardEngine)
    {
        _channelRepo = channelRepo;
        _chainRepo = chainRepo;
        _db = db;
        _httpClientFactory = httpClientFactory;
        _imageForwardEngine = imageForwardEngine;
    }

    // ===== 渠道 CRUD =====

    [HttpGet]
    public async Task<ApiResult<List<Channel>>> GetAll()
    {
        var list = await _channelRepo.GetAllAsync();
        return ApiResult<List<Channel>>.Success(list);
    }

    [HttpGet("{id}")]
    public async Task<ApiResult<object>> GetById(long id)
    {
        var channel = await _channelRepo.GetByIdAsync(id);
        if (channel == null) return ApiResult<object>.Fail("渠道不存在", 404);
        var keys = await _db.Select<ApiKey>().Where(k => k.ChannelId == id).ToListAsync();
        var models = await _db.Select<ChannelModel>().Where(m => m.ChannelId == id).ToListAsync();
        channel.ApiKeys = keys;
        channel.Models = models;
        return ApiResult<object>.Success(channel);
    }

    [HttpPost]
    public async Task<ApiResult<object>> Create([FromBody] CreateChannelRequest request)
    {
        var channel = new Channel
        {
            Name = request.Name,
            Remark = request.Remark,
            SupplierType = request.SupplierType,
            ApiAddress = request.ApiAddress,
            TimeoutSeconds = request.TimeoutSeconds,
            SseEnabled = request.SseEnabled,
            ProtocolType = request.ProtocolType,
            SupportedPaths = request.SupportedPaths,
            PassthroughPaths = request.PassthroughPaths ?? request.SupportedPaths,
            FallbackTarget = request.FallbackTarget ?? request.ProtocolType,
            CooldownSeconds = request.CooldownSeconds,
            ExtConfig = request.ExtConfig, // 透传扩展配置（讯飞 appId 等）
            SupportedSizes = request.SupportedSizes, // 图片转发：支持分辨率列表
            Enabled = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        await _channelRepo.InsertAsync(channel);

        // 同时添加密钥（批量插入）
        if (request.ApiKeys is { Count: > 0 })
        {
            var keys = request.ApiKeys.Select(k => new ApiKey
            {
                ChannelId = channel.Id,
                KeyValue = k,
                KeyValue2 = request.ApiKey2, // 透传第二密钥（讯飞 APISecret 等，同一渠道共用）
                Weight = 1,
                Status = 1,
                CreatedAt = DateTime.UtcNow
            }).ToList();
            await _db.Insert(keys).ExecuteAffrowsAsync();
        }

        // 同时添加模型（批量插入）
        if (request.Models is { Count: > 0 })
        {
            var models = request.Models.Select(m => new ChannelModel
            {
                ChannelId = channel.Id,
                OriginalModelId = m.OriginalModelId,
                ModelName = m.ModelName,
                CustomModelId = m.CustomModelId,
                Weight = m.Weight,
                Enabled = true,
                CreatedAt = DateTime.UtcNow
            }).ToList();
            await _db.Insert(models).ExecuteAffrowsAsync();
        }

        return ApiResult<object>.Success(new { channel.Id, channel.Name }, "渠道创建成功");
    }

    [HttpPut("{id}")]
    public async Task<ApiResult<string>> Update(long id, [FromBody] UpdateChannelRequest channel)
    {
        var existing = await _channelRepo.GetByIdAsync(id);
        if (existing == null) return ApiResult<string>.Fail("渠道不存在", 404);

        existing.Name = channel.Name ?? existing.Name;
        existing.Remark = channel.Remark;
        existing.SupplierType = channel.SupplierType ?? existing.SupplierType;
        existing.ApiAddress = channel.ApiAddress ?? existing.ApiAddress;
        existing.TimeoutSeconds = channel.TimeoutSeconds;
        existing.SseEnabled = channel.SseEnabled;
        existing.ProtocolType = channel.ProtocolType ?? existing.ProtocolType;
        existing.PassthroughPaths = channel.PassthroughPaths ?? channel.SupportedPaths;
        existing.SupportedPaths = channel.SupportedPaths;
        existing.FallbackTarget = channel.FallbackTarget ?? channel.ProtocolType;
        existing.CooldownSeconds = channel.CooldownSeconds;
        existing.ExtConfig = channel.ExtConfig; // 透传扩展配置（讯飞 appId 等）
        existing.SupportedSizes = channel.SupportedSizes; // 图片转发：支持分辨率列表
        existing.Enabled = channel.Enabled;
        existing.UpdatedAt = DateTime.UtcNow;

        await _channelRepo.UpdateAsync(existing);

        // 同步更新 API Key（先删后插，批量操作）
        if (channel.ApiKeys is { Count: > 0 })
        {
            await _db.Delete<ApiKey>().Where(k => k.ChannelId == id).ExecuteAffrowsAsync();
            // 讯飞等供应商第二密钥可从顶层 ApiKey2 透传（编辑保存时不动密钥列表也能更新 APISecret）
            var apiKey2 = channel.ApiKey2;
            var newKeys = channel.ApiKeys
                .Where(k => !string.IsNullOrWhiteSpace(k.KeyValue))
                .Select(k => new ApiKey
                {
                    ChannelId = id,
                    KeyValue = k.KeyValue,
                    KeyValue2 = !string.IsNullOrWhiteSpace(k.KeyValue2) ? k.KeyValue2 : apiKey2, // 透传第二密钥（讯飞 apiSecret 等）
                    Weight = k.Weight > 0 ? k.Weight : 1,
                    Status = k.Status,
                    CreatedAt = DateTime.UtcNow
                }).ToList();
            if (newKeys.Count > 0)
                await _db.Insert(newKeys).ExecuteAffrowsAsync();
        }
        else if (!string.IsNullOrWhiteSpace(channel.ApiKey2))
        {
            // 编辑保存时未动密钥列表，但顶层传了第二密钥（讯飞 APISecret）：更新现有所有密钥的 KeyValue2
            await _db.Update<ApiKey>().Where(k => k.ChannelId == id)
                .Set(a => a.KeyValue2, channel.ApiKey2)
                .ExecuteAffrowsAsync();
        }

        // 同步更新模型映射（先删后插，批量操作）
        if (channel.Models is { Count: > 0 })
        {
            await _db.Delete<ChannelModel>().Where(m => m.ChannelId == id).ExecuteAffrowsAsync();
            var newModels = channel.Models
                .Where(m => !string.IsNullOrWhiteSpace(m.OriginalModelId))
                .Select(m => new ChannelModel
                {
                    ChannelId = id,
                    OriginalModelId = m.OriginalModelId,
                    ModelName = m.ModelName,
                    CustomModelId = m.CustomModelId,
                    Weight = m.Weight > 0 ? m.Weight : 1,
                    Enabled = m.Enabled,
                    CreatedAt = DateTime.UtcNow
                }).ToList();
            if (newModels.Count > 0)
                await _db.Insert(newModels).ExecuteAffrowsAsync();
        }

        return ApiResult<string>.Success("ok", "更新成功");
    }

    [HttpDelete("{id}")]
    public async Task<ApiResult<string>> Delete(long id)
    {
        await _channelRepo.DeleteAsync(id);
        await _db.Delete<ApiKey>().Where(k => k.ChannelId == id).ExecuteAffrowsAsync();
        await _db.Delete<ChannelModel>().Where(m => m.ChannelId == id).ExecuteAffrowsAsync();
        await _chainRepo.DeleteAsync(c => c.ChannelId == id);
        return ApiResult<string>.Success("ok", "删除成功");
    }

    // ===== API Key 管理 =====

    [HttpGet("{channelId}/keys")]
    public async Task<ApiResult<List<ApiKey>>> GetKeys(long channelId)
    {
        var list = await _db.Select<ApiKey>().Where(k => k.ChannelId == channelId).ToListAsync();
        foreach (var key in list)
        {
            if (key.KeyValue.Length > 10)
                key.KeyValue = key.KeyValue[..5] + "****" + key.KeyValue[^5..];
        }
        return ApiResult<List<ApiKey>>.Success(list);
    }

    [HttpPost("{channelId}/keys")]
    public async Task<ApiResult<ApiKey>> AddKey(long channelId, [FromBody] ApiKey key)
    {
        key.ChannelId = channelId;
        key.CreatedAt = DateTime.UtcNow;
        var result = await _db.Insert(key).ExecuteIdentityAsync();
        key.Id = (long)result;
        return ApiResult<ApiKey>.Success(key, "密钥添加成功");
    }

    [HttpPost("{channelId}/keys/batch")]
    public async Task<ApiResult<int>> BatchAddKeys(long channelId, [FromBody] List<string> keys)
    {
        var entities = keys
            .Where(k => !string.IsNullOrWhiteSpace(k))
            .Select(k => new ApiKey
            {
                ChannelId = channelId,
                KeyValue = k.Trim(),
                Weight = 1,
                Status = 1,
                CreatedAt = DateTime.UtcNow
            }).ToList();

        if (entities.Count > 0)
            await _db.Insert(entities).ExecuteAffrowsAsync();

        return ApiResult<int>.Success(entities.Count, $"成功添加 {entities.Count} 个密钥");
    }

    [HttpDelete("{channelId}/keys/{keyId}")]
    public async Task<ApiResult<string>> DeleteKey(long channelId, long keyId)
    {
        await _db.Delete<ApiKey>().Where(k => k.Id == keyId).ExecuteAffrowsAsync();
        return ApiResult<string>.Success("ok", "删除成功");
    }

    // ===== 模型映射管理 =====

    [HttpGet("{channelId}/models")]
    public async Task<ApiResult<List<ChannelModel>>> GetModels(long channelId)
    {
        var list = await _db.Select<ChannelModel>().Where(m => m.ChannelId == channelId).ToListAsync();
        return ApiResult<List<ChannelModel>>.Success(list);
    }

    [HttpPost("{channelId}/models")]
    public async Task<ApiResult<ChannelModel>> AddModel(long channelId, [FromBody] ChannelModel model)
    {
        model.ChannelId = channelId;
        model.CreatedAt = DateTime.UtcNow;
        var id = await _db.Insert(model).ExecuteIdentityAsync();
        model.Id = (long)id;
        return ApiResult<ChannelModel>.Success(model, "模型添加成功");
    }

    [HttpPost("{channelId}/models/batch")]
    public async Task<ApiResult<int>> BatchAddModels(long channelId, [FromBody] List<ChannelModel> models)
    {
        var entities = models
            .Where(m => !string.IsNullOrWhiteSpace(m.OriginalModelId))
            .Select(m =>
            {
                m.ChannelId = channelId;
                m.CreatedAt = DateTime.UtcNow;
                return m;
            }).ToList();

        if (entities.Count > 0)
            await _db.Insert(entities).ExecuteAffrowsAsync();

        return ApiResult<int>.Success(entities.Count, $"成功添加 {entities.Count} 个模型");
    }

    [HttpDelete("{channelId}/models/{modelId}")]
    public async Task<ApiResult<string>> DeleteModel(long channelId, long modelId)
    {
        await _db.Delete<ChannelModel>().Where(m => m.Id == modelId).ExecuteAffrowsAsync();
        return ApiResult<string>.Success("ok", "删除成功");
    }

    // ===== 模型测试 =====

    [HttpPost("test-model")]
    public async Task<ApiResult<object>> TestModel([FromBody] TestModelRequest request)
    {
        var results = new List<object>();
        var httpClient = _httpClientFactory.CreateClient();
        httpClient.Timeout = TimeSpan.FromSeconds(15);

        foreach (var channelId in request.ChannelIds)
        {
            var channel = await _channelRepo.GetByIdAsync(channelId);
            if (channel == null || !channel.Enabled) continue;

            var keys = await _db.Select<ApiKey>().Where(k => k.ChannelId == channelId && k.Status == 1).ToListAsync();
            if (keys.Count == 0)
            {
                results.Add(new { channelId, channelName = channel.Name, success = false, error = "无可用密钥", latencyMs = 0 });
                continue;
            }

            var key = keys[0]; // 取第一个可用密钥测试
            var startTime = DateTime.UtcNow;

            try
            {
                var payload = new
                {
                    model = request.ModelId ?? "gpt-4o-mini",
                    messages = new[]
                    {
                        new { role = "user", content = request.TestMessage ?? "Hello, respond with just 'OK'." }
                    },
                    max_tokens = 10,
                    stream = false
                };

                var fullUrl = ForwardEngine.BuildDownstreamUrl(
                    channel.ApiAddress, channel.SupplierType,
                    "/chat/completions", request.ModelId ?? "gpt-4o-mini");

                var httpRequest = new HttpRequestMessage(HttpMethod.Post, fullUrl)
                {
                    Content = new StringContent(JsonSerializer.Serialize(payload), System.Text.Encoding.UTF8, "application/json")
                };
                httpRequest.Headers.Add("Authorization", $"Bearer {key.KeyValue}");

                var response = await httpClient.SendAsync(httpRequest);
                var body = await response.Content.ReadAsStringAsync();
                var latencyMs = (long)(DateTime.UtcNow - startTime).TotalMilliseconds;

                results.Add(new
                {
                    channelId,
                    channelName = channel.Name,
                    modelId = request.ModelId ?? "gpt-4o-mini",
                    success = response.IsSuccessStatusCode,
                    statusCode = (int)response.StatusCode,
                    latencyMs,
                    responseBody = body
                });
            }
            catch (Exception ex)
            {
                var latencyMs = (long)(DateTime.UtcNow - startTime).TotalMilliseconds;
                results.Add(new
                {
                    channelId,
                    channelName = channel.Name,
                    success = false,
                    error = ex.Message,
                    latencyMs
                });
            }
        }

        return ApiResult<object>.Success(new
        {
            testedAt = DateTime.UtcNow,
            total = results.Count,
            successCount = results.Count(r => ((dynamic)r).success),
            results
        });
    }

    /// <summary>
    /// 上游图片接口直测（管理端用）：跳过令牌/模型权限/计费，直接用渠道密钥+原始模型ID
    /// 打一次上游平台图片接口，验证对接是否可用。仅用于"编辑上游对接"场景。
    /// </summary>
    [HttpPost("test-image-model")]
    public async Task<ApiResult<object>> TestImageModel([FromBody] TestImageModelRequest request)
    {
        var (url, reqBody, status, respBody, latency, error) = await _imageForwardEngine.TestUpstreamImageAsync(
            request.ChannelId, request.ModelId ?? "", request.Prompt ?? "一只柴犬",
            request.Size, request.ResponseFormat, request.Image);

        return ApiResult<object>.Success(new
        {
            channelId = request.ChannelId,
            modelId = request.ModelId,
            upstreamUrl = url,
            requestBody = reqBody,
            statusCode = status,
            responseBody = respBody,
            latencyMs = latency,
            success = status >= 200 && status < 300,
            error
        });
    }

    // ===== 模型链管理 =====

    [HttpGet("chains")]
    public async Task<ApiResult<List<object>>> GetModelChains()
    {
        var chains = await _chainRepo.GetAllAsync();
        var result = new List<object>();

        // 按 CustomModelId 分组
        var groups = chains.GroupBy(c => c.CustomModelId);
        foreach (var g in groups)
        {
            var nodes = new List<object>();
            foreach (var c in g.OrderBy(c => c.Priority).ThenByDescending(c => c.Weight))
            {
                var channel = await _channelRepo.GetByIdAsync(c.ChannelId);
                nodes.Add(new
                {
                    c.Id,
                    c.ChannelId,
                    channelName = channel?.Name ?? "已删除",
                    c.OriginalModelId,
                    c.Weight,
                    c.Priority,
                    c.Enabled,
                    c.ChainType
                });
            }
            result.Add(new
            {
                customModelId = g.Key,
                displayName = g.FirstOrDefault(c => !string.IsNullOrEmpty(c.DisplayName))?.DisplayName ?? g.Key,
                chainType = g.FirstOrDefault()?.ChainType ?? "Text",
                nodes
            });
        }

        return ApiResult<List<object>>.Success(result);
    }

    [HttpPost("chains")]
    public async Task<ApiResult<ModelChain>> CreateChain([FromBody] ModelChain chain)
    {
        chain.CreatedAt = DateTime.UtcNow;
        chain.UpdatedAt = DateTime.UtcNow;
        var result = await _chainRepo.InsertAsync(chain);
        return ApiResult<ModelChain>.Success(result, "模型链节点添加成功");
    }

    [HttpPut("chains/{id}")]
    public async Task<ApiResult<string>> UpdateChain(long id, [FromBody] ModelChain chain)
    {
        var existing = await _chainRepo.GetByIdAsync(id);
        if (existing == null) return ApiResult<string>.Fail("模型链节点不存在", 404);
        existing.Weight = chain.Weight;
        existing.Priority = chain.Priority;
        existing.Enabled = chain.Enabled;
        existing.ChainType = string.IsNullOrEmpty(chain.ChainType) ? existing.ChainType : chain.ChainType;
        existing.UpdatedAt = DateTime.UtcNow;
        await _chainRepo.UpdateAsync(existing);
        return ApiResult<string>.Success("ok", "更新成功");
    }

    [HttpDelete("chains/{id}")]
    public async Task<ApiResult<string>> DeleteChain(long id)
    {
        await _chainRepo.DeleteAsync(id);
        return ApiResult<string>.Success("ok", "删除成功");
    }

    // ===== 供应商预设 =====

    [HttpGet("supplier-presets")]
    public ApiResult<List<SupplierPreset>> GetSupplierPresets()
    {
        var presets = new List<SupplierPreset>
        {
            new() {
                Type = "OpenAI", Name = "OpenAI",
                DefaultApi = "https://api.openai.com/v1",
                SupportedPaths = ["chat", "responses"],
                IsOpenAIProtocol = true,
                DefaultModels = ["gpt-4o", "gpt-4o-mini", "gpt-4-turbo", "o3-mini", "o1"]
            },
            new() {
                Type = "Azure", Name = "Azure OpenAI",
                DefaultApi = "https://{your-resource}.openai.azure.com",
                SupportedPaths = ["chat"],
                IsOpenAIProtocol = true,
                DefaultModels = ["gpt-4o", "gpt-4o-mini"]
            },
            new() {
                Type = "Anthropic", Name = "Anthropic Claude",
                DefaultApi = "https://api.anthropic.com",
                SupportedPaths = ["messages"],
                IsOpenAIProtocol = false,
                DefaultModels = ["claude-sonnet-4-20250514", "claude-3-5-sonnet-20241022", "claude-3-5-haiku-20241022"]
            },
            new() {
                Type = "Google", Name = "Google Gemini",
                DefaultApi = "https://generativelanguage.googleapis.com/v1beta",
                SupportedPaths = ["generateContent"],
                IsOpenAIProtocol = false,
                DefaultModels = ["gemini-2.5-pro", "gemini-2.5-flash", "gemini-2.0-flash"]
            },
            new() {
                Type = "DeepSeek", Name = "DeepSeek",
                DefaultApi = "https://api.deepseek.com/v1",
                SupportedPaths = ["chat"],
                IsOpenAIProtocol = true,
                DefaultModels = ["deepseek-chat", "deepseek-reasoner"]
            },
            new() {
                Type = "Groq", Name = "Groq",
                DefaultApi = "https://api.groq.com/openai/v1",
                SupportedPaths = ["chat"],
                IsOpenAIProtocol = true,
                DefaultModels = ["llama-4-maverick-17b-128e-instruct", "mixtral-8x7b-32768", "gemma2-9b-it"]
            },
            new() {
                Type = "Together", Name = "Together AI",
                DefaultApi = "https://api.together.xyz/v1",
                SupportedPaths = ["chat"],
                IsOpenAIProtocol = true,
                DefaultModels = ["meta-llama/Llama-4-Maverick-17B-128E-Instruct-FP8", "Qwen/Qwen3-235B-A22B"]
            },
            new() {
                Type = "Custom", Name = "自定义 (OpenAI 兼容)",
                DefaultApi = "",
                SupportedPaths = ["chat", "responses"],
                IsOpenAIProtocol = true,
                DefaultModels = []
            },
            // ===== 图片生成供应商（图片转发子系统专用，下游统一 /v1/images/generations，image 字段扩展支持图生图/多图） =====
            new() {
                Type = "VolcEngine", Name = "火山引擎/豆包 Seedream",
                DefaultApi = "https://ark.cn-beijing.volces.com/api/v3",
                SupportedPaths = ["images"],
                IsOpenAIProtocol = true,
                DefaultModels = ["doubao-seedream-4-0-250828", "doubao-seedream-4-5-251128", "doubao-seedream-5-0-260128"]
            },
            new() {
                Type = "SiliconFlow", Name = "硅基流动",
                DefaultApi = "https://api.siliconflow.cn/v1",
                SupportedPaths = ["images"],
                IsOpenAIProtocol = true,
                DefaultModels = ["Kwai-Kolors/Kolors", "Qwen/Qwen-Image", "Qwen/Qwen-Image-Edit-2509", "Tongyi-MAI/Z-Image-Turbo", "blackforest-labs/FLUX.1-dev"]
            },
            new() {
                Type = "Agnes", Name = "Agnes-Ai",
                DefaultApi = "https://apihub.agnes-ai.com/v1",
                SupportedPaths = ["images"],
                IsOpenAIProtocol = true,
                DefaultModels = ["agnes-image-2.0-flash", "agnes-image-2.1-flash"]
            },
            new() {
                Type = "ModelScope", Name = "魔搭 ModelScope",
                DefaultApi = "https://api-inference.modelscope.cn/v1",
                SupportedPaths = ["images"],
                IsOpenAIProtocol = true,
                DefaultModels = ["Tongyi-MAI/Z-Image-Turbo", "Qwen/Qwen-Image", "Qwen/Qwen-Image-Edit-2509", "kolors"]
            },
            new() {
                Type = "SenseNova", Name = "商汤 SenseNova U1",
                DefaultApi = "https://token.sensenova.cn/v1",
                SupportedPaths = ["images"],
                IsOpenAIProtocol = true,
                DefaultModels = ["sensenova-u1-fast"]
            },
            new() {
                Type = "Xfyun", Name = "讯飞星辰 MaaS",
                DefaultApi = "https://maas-api.cn-huabei-1.xf-yun.com",
                SupportedPaths = ["images"],
                IsOpenAIProtocol = false,
                DefaultModels = ["qwen-image-2512", "xopqwentti20b", "tti", "HiDream"]
            },
            new() {
                Type = "Gitee", Name = "Gitee AI",
                DefaultApi = "https://ai.gitee.com/api/serverless",
                SupportedPaths = ["images"],
                IsOpenAIProtocol = true,
                DefaultModels = ["FLUX.1-dev", "FLUX.1-schnell", "Kolors"]
            },
            new() {
                Type = "DashScope", Name = "阿里云百炼 DashScope",
                DefaultApi = "https://dashscope.aliyuncs.com/api/v1",
                SupportedPaths = ["images"],
                IsOpenAIProtocol = false,
                DefaultModels = ["wanx2.1-t2i-turbo", "wanx2.1-t2i-plus", "wanx2.1-i2i-turbo"]
            }
        };
        return ApiResult<List<SupplierPreset>>.Success(presets);
    }

    // ===== 获取所有可用自定义模型ID（供令牌权限选择） =====

    [HttpGet("available-models")]
    public async Task<ApiResult<List<object>>> GetAvailableModels()
    {
        // 从渠道模型映射收集
        var channelModels = await _db.Select<ChannelModel>().ToListAsync();
        var modelMap = new Dictionary<string, List<string>>();

        foreach (var cm in channelModels)
        {
            if (!modelMap.ContainsKey(cm.CustomModelId))
                modelMap[cm.CustomModelId] = new List<string>();
            if (!string.IsNullOrEmpty(cm.ModelName))
                modelMap[cm.CustomModelId].Add(cm.ModelName);
        }

        // 从模型链收集
        var chains = await _chainRepo.GetAllAsync();
        foreach (var c in chains)
        {
            if (!modelMap.ContainsKey(c.CustomModelId))
                modelMap[c.CustomModelId] = new List<string>();
        }

        var result = modelMap.Select(kv => (object)new
        {
            customModelId = kv.Key,
            displayName = kv.Value.FirstOrDefault() ?? kv.Key,
            aliases = kv.Value.Distinct().ToList()
        }).ToList();

        return ApiResult<List<object>>.Success(result);
    }
}

// ===== DTOs =====

public class CreateChannelRequest
{
    public string Name { get; set; } = string.Empty;
    public string? Remark { get; set; }
    public string SupplierType { get; set; } = "OpenAI";
    public string ApiAddress { get; set; } = string.Empty;
    public int TimeoutSeconds { get; set; } = 30;
    public bool SseEnabled { get; set; } = true;
    public string ProtocolType { get; set; } = "Chat";
    public string? SupportedPaths { get; set; } = "chat";
    public string? PassthroughPaths { get; set; }
    public string? FallbackTarget { get; set; }
    public int CooldownSeconds { get; set; } = 60;
    /// <summary>扩展配置(JSON): 讯飞 appId 等供应商专属参数</summary>
    public string? ExtConfig { get; set; }
    /// <summary>图片转发：支持分辨率列表（逗号分隔，如 "720x1280,1280x720"）；为空表示不限制</summary>
    public string? SupportedSizes { get; set; }
    public List<string>? ApiKeys { get; set; }
    /// <summary>第二密钥（讯飞 APISecret 等，同一渠道共用）</summary>
    public string? ApiKey2 { get; set; }
    public List<ModelEntry>? Models { get; set; }
}

public class ModelEntry
{
    public string OriginalModelId { get; set; } = string.Empty;
    public string? ModelName { get; set; }
    public string CustomModelId { get; set; } = string.Empty;
    public int Weight { get; set; } = 1;
}

/// <summary>渠道更新入参：含原渠道字段 + ApiKey2 顶层透传（讯飞 APISecret 等，编辑保存时不动密钥列表也能更新第二密钥）</summary>
public class UpdateChannelRequest
{
    public long Id { get; set; }
    public string? Name { get; set; }
    public string? Remark { get; set; }
    public string? SupplierType { get; set; }
    public string? ApiAddress { get; set; }
    public int TimeoutSeconds { get; set; } = 30;
    public bool SseEnabled { get; set; }
    public string? ProtocolType { get; set; }
    public string? SupportedPaths { get; set; }
    public string? PassthroughPaths { get; set; }
    public string? FallbackTarget { get; set; }
    public int CooldownSeconds { get; set; } = 60;
    public string? ExtConfig { get; set; }
    /// <summary>图片转发：支持分辨率列表（逗号分隔）；为空表示不限制</summary>
    public string? SupportedSizes { get; set; }
    public bool Enabled { get; set; } = true;
    /// <summary>密钥列表：传空数组=不同步保留原值；传新值=先删后插全量替换</summary>
    public List<ApiKey>? ApiKeys { get; set; }
    /// <summary>第二密钥（讯飞 APISecret 等）：顶层透传，传非空值时即便 ApiKeys 为空也会更新现有所有密钥的 KeyValue2</summary>
    public string? ApiKey2 { get; set; }
    /// <summary>模型映射：传值=先删后插全量同步；空数组/null=保留原值</summary>
    public List<ChannelModel>? Models { get; set; }
}

public class TestModelRequest
{
    public List<long> ChannelIds { get; set; } = new();
    public string? ModelId { get; set; }
    public string? TestMessage { get; set; }
}

public class TestImageModelRequest
{
    public long ChannelId { get; set; }
    public string? ModelId { get; set; }
    public string? Prompt { get; set; }
    public string? Size { get; set; }
    public string? ResponseFormat { get; set; }
    public string? Image { get; set; }
}

public class SupplierPreset
{
    public string Type { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string DefaultApi { get; set; } = string.Empty;
    public List<string> SupportedPaths { get; set; } = new();
    public List<string> DefaultModels { get; set; } = new();
    public bool IsOpenAIProtocol { get; set; } = true;
}
