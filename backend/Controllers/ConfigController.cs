using backend.Common.DTOs;
using backend.Common.Models;
using backend.Repository;
using Microsoft.AspNetCore.Mvc;

namespace backend.Controllers;

/// <summary>
/// 全局配置管理
/// </summary>
[ApiController]
[Route("api/admin/config")]
public class ConfigController : ControllerBase
{
    private readonly BaseRepository<GlobalConfig> _configRepo;

    public ConfigController(BaseRepository<GlobalConfig> configRepo)
    {
        _configRepo = configRepo;
    }

    [HttpGet]
    public async Task<ApiResult<Dictionary<string, string>>> GetAll()
    {
        var configs = await _configRepo.GetAllAsync();
        var dict = configs.ToDictionary(c => c.Key, c => c.Value);
        return ApiResult<Dictionary<string, string>>.Success(dict);
    }

    [HttpPut]
    public async Task<ApiResult<string>> SetConfig([FromBody] Dictionary<string, string> configs)
    {
        foreach (var (key, value) in configs)
        {
            var existing = await _configRepo.FirstOrDefaultAsync(c => c.Key == key);
            if (existing != null)
            {
                existing.Value = value;
                existing.UpdatedAt = DateTime.UtcNow;
                await _configRepo.UpdateAsync(existing);
            }
            else
            {
                await _configRepo.InsertAsync(new GlobalConfig
                {
                    Key = key,
                    Value = value,
                    UpdatedAt = DateTime.UtcNow
                });
            }
        }
        return ApiResult<string>.Success("ok", "配置已保存");
    }

    [HttpGet("{key}")]
    public async Task<ApiResult<string?>> GetByKey(string key)
    {
        var config = await _configRepo.FirstOrDefaultAsync(c => c.Key == key);
        return ApiResult<string?>.Success(config?.Value);
    }
}
