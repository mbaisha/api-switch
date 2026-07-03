using backend.Common.DTOs;
using backend.Common.Models;
using backend.Repository;
using Microsoft.AspNetCore.Mvc;
using System.Text;

namespace backend.Controllers;

/// <summary>
/// 健康检测
/// </summary>
[ApiController]
[Route("api/admin/health")]
public class HealthController : ControllerBase
{
    private readonly BaseRepository<Channel> _channelRepo;
    private readonly BaseRepository<ApiKey> _apiKeyRepo;
    private readonly IHttpClientFactory _httpClientFactory;

    public HealthController(
        BaseRepository<Channel> channelRepo,
        BaseRepository<ApiKey> apiKeyRepo,
        IHttpClientFactory httpClientFactory)
    {
        _channelRepo = channelRepo;
        _apiKeyRepo = apiKeyRepo;
        _httpClientFactory = httpClientFactory;
    }

    [HttpGet]
    public async Task<ApiResult<List<object>>> CheckAll()
    {
        var channels = await _channelRepo.GetAllAsync();
        var results = new List<object>();

        foreach (var channel in channels)
        {
            var keys = await _apiKeyRepo.GetListAsync(k => k.ChannelId == channel.Id && k.Status == 1);
            foreach (var key in keys)
            {
                var healthy = await CheckEndpoint(channel.ApiAddress, key.KeyValue);
                results.Add(new
                {
                    channelId = channel.Id,
                    channelName = channel.Name,
                    keyId = key.Id,
                    healthy,
                    checkedAt = DateTime.UtcNow
                });
            }
        }

        return ApiResult<List<object>>.Success(results);
    }

    private async Task<bool> CheckEndpoint(string apiAddress, string apiKey)
    {
        try
        {
            var client = _httpClientFactory.CreateClient("AIClient");
            client.Timeout = TimeSpan.FromSeconds(10);
            var request = new HttpRequestMessage(HttpMethod.Get, apiAddress);
            request.Headers.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey);
            var response = await client.SendAsync(request);
            // 401/403 也说明端点可达
            return response.StatusCode != System.Net.HttpStatusCode.NotFound;
        }
        catch
        {
            return false;
        }
    }
}
