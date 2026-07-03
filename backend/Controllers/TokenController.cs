using backend.Common.DTOs;
using backend.Common.Models;
using backend.Repository;
using Microsoft.AspNetCore.Mvc;

namespace backend.Controllers;

/// <summary>
/// 令牌管理
/// </summary>
[ApiController]
[Route("api/admin/tokens")]
public class TokenController : ControllerBase
{
    private readonly BaseRepository<Token> _tokenRepo;
    private readonly BaseRepository<TokenModel> _tokenModelRepo;

    public TokenController(
        BaseRepository<Token> tokenRepo,
        BaseRepository<TokenModel> tokenModelRepo)
    {
        _tokenRepo = tokenRepo;
        _tokenModelRepo = tokenModelRepo;
    }

    /// <summary>获取令牌列表</summary>
    [HttpGet]
    public async Task<ApiResult<List<Token>>> GetAll()
    {
        var list = await _tokenRepo.GetAllAsync();
        // 加载每个令牌的模型权限
        foreach (var t in list)
        {
            t.AllowedModels = await _tokenModelRepo.GetListAsync(m => m.TokenId == t.Id);
        }
        return ApiResult<List<Token>>.Success(list);
    }

    /// <summary>生成令牌</summary>
    [HttpPost]
    public async Task<ApiResult<Token>> Create([FromBody] Token token)
    {
        token.TokenValue = "sk-" + Guid.NewGuid().ToString("N")[..32];
        token.CreatedAt = DateTime.UtcNow;
        token.UpdatedAt = DateTime.UtcNow;
        var result = await _tokenRepo.InsertAsync(token);
        return ApiResult<Token>.Success(result, "创建成功");
    }

    /// <summary>编辑令牌</summary>
    [HttpPut("{id}")]
    public async Task<ApiResult<string>> Update(long id, [FromBody] Token token)
    {
        var existing = await _tokenRepo.GetByIdAsync(id);
        if (existing == null) return ApiResult<string>.Fail("令牌不存在", 404);

        existing.Remark = token.Remark;
        existing.Enabled = token.Enabled;
        existing.DailyTokenLimit = token.DailyTokenLimit;
        existing.TotalTokenLimit = token.TotalTokenLimit;
        existing.RemainingBalance = token.RemainingBalance;
        existing.RateLimitWindow = token.RateLimitWindow;
        existing.RateLimitCount = token.RateLimitCount;
        existing.UpdatedAt = DateTime.UtcNow;

        await _tokenRepo.UpdateAsync(existing);
        return ApiResult<string>.Success("ok", "更新成功");
    }

    /// <summary>删除令牌</summary>
    [HttpDelete("{id}")]
    public async Task<ApiResult<string>> Delete(long id)
    {
        await _tokenRepo.DeleteAsync(id);
        await _tokenModelRepo.DeleteAsync(m => m.TokenId == id);
        return ApiResult<string>.Success("ok", "删除成功");
    }

    /// <summary>配置令牌模型权限</summary>
    [HttpPut("{id}/models")]
    public async Task<ApiResult<string>> SetModels(long id, [FromBody] List<string> modelIds)
    {
        await _tokenModelRepo.DeleteAsync(m => m.TokenId == id);
        foreach (var modelId in modelIds)
        {
            await _tokenModelRepo.InsertAsync(new TokenModel
            {
                TokenId = id,
                CustomModelId = modelId
            });
        }
        return ApiResult<string>.Success("ok", "权限配置成功");
    }
}
