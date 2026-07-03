using backend.Common.DTOs;
using backend.Common.Models;
using backend.Repository;
using Microsoft.AspNetCore.Mvc;

namespace backend.Controllers;

/// <summary>
/// 数据看板
/// </summary>
[ApiController]
[Route("api/admin/dashboard")]
public class DashboardController : ControllerBase
{
    private readonly BaseRepository<CallLog> _callLogRepo;
    private readonly BaseRepository<Token> _tokenRepo;
    private readonly BaseRepository<Channel> _channelRepo;
    private readonly BaseRepository<BillingRecord> _billingRepo;

    public DashboardController(
        BaseRepository<CallLog> callLogRepo,
        BaseRepository<Token> tokenRepo,
        BaseRepository<Channel> channelRepo,
        BaseRepository<BillingRecord> billingRepo)
    {
        _callLogRepo = callLogRepo;
        _tokenRepo = tokenRepo;
        _channelRepo = channelRepo;
        _billingRepo = billingRepo;
    }

    [HttpGet]
    public async Task<ApiResult<object>> GetDashboard()
    {
        var today = DateTime.UtcNow.Date;
        var allCalls = await _callLogRepo.GetAllAsync();
        var todayCalls = allCalls.Where(l => l.CreatedAt >= today).ToList();

        var totalInputTokens = allCalls.Sum(l => l.InputTokens);
        var totalOutputTokens = allCalls.Sum(l => l.OutputTokens);
        var todayInputTokens = todayCalls.Sum(l => l.InputTokens);
        var todayOutputTokens = todayCalls.Sum(l => l.OutputTokens);

        // 模型用量排行 (Top 5)
        var modelUsage = allCalls
            .GroupBy(l => l.CustomModelId ?? "unknown")
            .Select(g => new { model = g.Key, count = g.Count(), tokens = g.Sum(x => x.InputTokens + x.OutputTokens) })
            .OrderByDescending(g => g.count)
            .Take(5)
            .ToList();

        // 令牌用量排行 (Top 5)
        var tokenUsage = allCalls
            .GroupBy(l => l.TokenValue ?? "unknown")
            .Select(g => new { token = MaskToken(g.Key), count = g.Count() })
            .OrderByDescending(g => g.count)
            .Take(5)
            .ToList();

        // 今日消费
        var todayBills = (await _billingRepo.GetAllAsync())
            .Where(b => b.CreatedAt >= today).ToList();
        var todayCost = todayBills.Sum(b => b.Cost);

        // 今日成功率
        var todaySuccess = todayCalls.Count(l => l.Status == "Success");
        var todayFailed = todayCalls.Count(l => l.Status == "Failed");
        var successRate = todayCalls.Count > 0
            ? Math.Round((double)todaySuccess / todayCalls.Count * 100, 1)
            : 0.0;

        return ApiResult<object>.Success(new
        {
            totalCalls = allCalls.Count,
            todayCalls = todayCalls.Count,
            todaySuccess,
            todayFailed,
            successRate,
            totalInputTokens,
            totalOutputTokens,
            todayInputTokens,
            todayOutputTokens,
            todayCost = Math.Round(todayCost, 4),
            tokenCount = await _tokenRepo.CountAsync(),
            channelCount = await _channelRepo.CountAsync(l => l.Enabled),
            modelUsage,
            tokenUsage
        });
    }

    private static string MaskToken(string token)
    {
        if (token.Length > 10)
            return token[..5] + "****" + token[^5..];
        return token;
    }
}
