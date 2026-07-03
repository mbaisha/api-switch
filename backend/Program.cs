using System.Text;
using System.Text.Json;
using backend.Common.Utils;
using backend.Common.Models;
using backend.Services;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Mvc;
using Microsoft.IdentityModel.Tokens;
using Serilog;
using StackExchange.Redis;

// Npgsql 10.x 兼容：允许将 DateTime 写入 timestamp without time zone 列
// 这避免了 DateTime.Kind=UTC 与 PostgreSQL timestamp without time zone 的类型冲突
AppContext.SetSwitch("Npgsql.EnableLegacyTimestampBehavior", true);

var builder = WebApplication.CreateBuilder(args);

// Serilog 配置 - 从 appsettings.json 读取（Console + File）
Log.Logger = new LoggerConfiguration()
    .ReadFrom.Configuration(builder.Configuration)
    .CreateLogger();
builder.Host.UseSerilog();

// 数据库
var connectionString = builder.Configuration.GetConnectionString("Default")
    ?? "Host=localhost;Port=5432;Database=ai_forward;Username=postgres;Password=postgres";
var dbContext = new DbContext(connectionString);
builder.Services.AddSingleton(dbContext);
builder.Services.AddSingleton(dbContext.Db);

// 仓储注册
builder.Services.AddScoped(typeof(backend.Repository.BaseRepository<>));

// 服务注册
builder.Services.AddScoped<ChannelService>();
builder.Services.AddScoped<TokenService>();
builder.Services.AddScoped<ForwardEngine>();
builder.Services.AddScoped<AuthService>();
builder.Services.AddScoped<BillingService>();

// Redis 缓存（分布式缓存 + 限流 + 冷却）
var redisConfig = builder.Configuration.GetSection("Redis")["Configuration"] ?? "localhost:6379";
var redisInstanceName = builder.Configuration.GetSection("Redis")["InstanceName"] ?? "ai-forward";
builder.Services.AddSingleton(ConnectionMultiplexer.Connect(new ConfigurationOptions
{
    EndPoints = { redisConfig.Split(',')[0] },
    Password = ExtractRedisPassword(redisConfig),
    AbortOnConnectFail = false,
    ConnectRetry = 5,
    ConnectTimeout = 5000
}));
builder.Services.AddScoped<RedisCacheService>();

static string? ExtractRedisPassword(string config)
{
    var parts = config.Split(',');
    foreach (var part in parts)
    {
        var trimmed = part.Trim();
        if (trimmed.StartsWith("password=", StringComparison.OrdinalIgnoreCase))
            return trimmed["password=".Length..];
    }
    return null;
}

// 日志清理后台服务
builder.Services.AddHostedService<LogCleanupService>();

// HttpClient 工厂
builder.Services.AddHttpClient("AIClient", client =>
{
    client.DefaultRequestHeaders.Accept.Add(
        new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("application/json"));
});
builder.Services.AddHttpClient();

// JWT 认证
var jwtKey = builder.Configuration["Jwt:Key"];
if (string.IsNullOrEmpty(jwtKey))
{
    Console.Error.WriteLine("WARNING: Jwt:Key 未设置，使用默认密钥（仅限开发环境）");
    jwtKey = "AI-Forward-Super-Secret-Key-2024-!@#$%^&*()";
}
var jwtIssuer = builder.Configuration["Jwt:Issuer"] ?? "ai-forward";
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            ValidIssuer = jwtIssuer,
            ValidAudience = "admin",
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtKey))
        };
    });
builder.Services.AddAuthorization();

// CORS
builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        policy.AllowAnyOrigin().AllowAnyMethod().AllowAnyHeader();
    });
});

// Controllers
builder.Services.AddControllers()
    .AddJsonOptions(options =>
    {
        options.JsonSerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
        // 将 UTC 时间转为北京时间显示
        options.JsonSerializerOptions.Converters.Add(new LocalDateTimeConverter());
        options.JsonSerializerOptions.Converters.Add(new LocalNullableDateTimeConverter());
    });

builder.Services.AddOpenApi();

var app = builder.Build();

// 自动同步数据库表结构
using (var scope = app.Services.CreateScope())
{
    var fsql = scope.ServiceProvider.GetRequiredService<IFreeSql>();
    fsql.CodeFirst.SyncStructure<Channel>();
    fsql.CodeFirst.SyncStructure<ApiKey>();
    fsql.CodeFirst.SyncStructure<ChannelModel>();
    fsql.CodeFirst.SyncStructure<Token>();
    fsql.CodeFirst.SyncStructure<TokenModel>();
    fsql.CodeFirst.SyncStructure<CallLog>();
    fsql.CodeFirst.SyncStructure<OperationLog>();
    fsql.CodeFirst.SyncStructure<BillingRecord>();
    fsql.CodeFirst.SyncStructure<BillingRule>();
    fsql.CodeFirst.SyncStructure<ModelChain>();
    fsql.CodeFirst.SyncStructure<GlobalConfig>();
    fsql.CodeFirst.SyncStructure<AdminUser>();
    fsql.CodeFirst.SyncStructure<CallImage>();

    // 初始化默认管理员
    var adminRepo = new backend.Repository.BaseRepository<AdminUser>(fsql);
    var existingAdmin = await adminRepo.FirstOrDefaultAsync(a => a.Username == "admin");
    if (existingAdmin == null)
    {
        await adminRepo.InsertAsync(new AdminUser
        {
            Username = "admin",
            PasswordHash = BCrypt.Net.BCrypt.HashPassword("admin123"),
            Enabled = true
        });
    }

    // 初始化默认全局配置
    var configRepo = new backend.Repository.BaseRepository<GlobalConfig>(fsql);
    var defaultConfigs = new Dictionary<string, string>
    {
        ["global_cooldown"] = "60",
        ["fallback_message"] = "所有可用渠道暂时不可用，请稍后重试",
        ["log_retention_days"] = "30",
        ["rate_limit_per_minute"] = "60"
    };
    foreach (var (key, value) in defaultConfigs)
    {
        var existing = await configRepo.FirstOrDefaultAsync(c => c.Key == key);
        if (existing == null)
        {
            await configRepo.InsertAsync(new GlobalConfig { Key = key, Value = value });
        }
    }
}

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseCors();
app.UseSerilogRequestLogging();
app.UseRateLimit();        // IP + Token 双维度限流
app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();

app.Run();
