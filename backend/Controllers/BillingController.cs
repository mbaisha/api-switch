using backend.Common.DTOs;
using backend.Common.Models;
using backend.Repository;
using backend.Services;
using FreeSql;
using Microsoft.AspNetCore.Mvc;

namespace backend.Controllers;

/// <summary>
/// 计费管理 - 定价规则 + 账单查询
/// </summary>
[ApiController]
[Route("api/admin/billing")]
public class BillingController : ControllerBase
{
    private readonly BillingService _billingService;
    private readonly IFreeSql _db;

    public BillingController(
        BillingService billingService,
        IFreeSql db)
    {
        _billingService = billingService;
        _db = db;
    }

    /// <summary>账单列表(分页+筛选)，含令牌备注</summary>
    [HttpGet("records")]
    public async Task<ApiResult<PageResult<object>>> GetRecords(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        [FromQuery] long? tokenId = null,
        [FromQuery] string? modelId = null,
        [FromQuery] DateTime? startTime = null,
        [FromQuery] DateTime? endTime = null)
    {
        var select = _db.Select<BillingRecord>()
            .WhereIf(tokenId > 0, r => r.TokenId == tokenId.Value)
            .WhereIf(!string.IsNullOrEmpty(modelId), r => r.CustomModelId == modelId)
            .WhereIf(startTime != null, r => r.CreatedAt >= startTime.Value)
            .WhereIf(endTime != null, r => r.CreatedAt <= endTime.Value);

        var total = await select.CountAsync();
        var list = await select.OrderByDescending(r => r.CreatedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync();

        // 查找令牌备注
        var tokenIds = list.Select(r => r.TokenId).Distinct().ToList();
        var remarkMap = new Dictionary<long, string?>();
        if (tokenIds.Count > 0)
        {
            var tokenRemarks = await _db.Select<Token>().Where(t => tokenIds.Contains(t.Id)).ToListAsync(t => new { t.Id, t.Remark });
            foreach (var tr in tokenRemarks)
                remarkMap[tr.Id] = tr.Remark;
        }

        var resultList = list.Select(r => (object)new
        {
            r.Id,
            r.TokenId,
            r.TokenValue,
            TokenRemark = remarkMap.TryGetValue(r.TokenId, out var remark) ? remark : null,
            r.CustomModelId,
            r.InputTokens,
            r.OutputTokens,
            r.Cost,
            r.InputPrice,
            r.OutputPrice,
            r.CreatedAt
        }).ToList();

        return ApiResult<PageResult<object>>.Success(new PageResult<object>
        {
            Total = total, Page = page, PageSize = pageSize, List = resultList
        });
    }

    /// <summary>账单汇总</summary>
    [HttpGet("summary")]
    public async Task<ApiResult<object>> GetSummary(
        [FromQuery] long? tokenId = null,
        [FromQuery] DateTime? startTime = null,
        [FromQuery] DateTime? endTime = null)
    {
        var result = await _billingService.GetBillSummary(tokenId, startTime, endTime, _db);
        return ApiResult<object>.Success(result);
    }

    // ===== 定价规则管理 =====

    [HttpGet("rules")]
    public async Task<ApiResult<List<BillingRule>>> GetRules()
    {
        var rules = await _billingService.GetRulesAsync();
        return ApiResult<List<BillingRule>>.Success(rules);
    }

    [HttpPost("rules")]
    public async Task<ApiResult<BillingRule>> CreateRule([FromBody] BillingRule rule)
    {
        var result = await _billingService.CreateRuleAsync(rule);
        return ApiResult<BillingRule>.Success(result, "创建成功");
    }

    [HttpPut("rules/{id}")]
    public async Task<ApiResult<string>> UpdateRule(long id, [FromBody] BillingRule rule)
    {
        try
        {
            await _billingService.UpdateRuleAsync(id, rule);
            return ApiResult<string>.Success("ok", "更新成功");
        }
        catch (Exception ex)
        {
            return ApiResult<string>.Fail(ex.Message);
        }
    }

    [HttpDelete("rules/{id}")]
    public async Task<ApiResult<string>> DeleteRule(long id)
    {
        await _billingService.DeleteRuleAsync(id);
        return ApiResult<string>.Success("ok", "删除成功");
    }
}
