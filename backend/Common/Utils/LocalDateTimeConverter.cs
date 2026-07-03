using System.Text.Json;
using System.Text.Json.Serialization;

namespace backend.Common.Utils;

/// <summary>
/// JSON DateTime 转换器：序列化时将 UTC 时间转为北京时间（Asia/Shanghai, UTC+8）
/// 反序列化时将本地时间转回 UTC 存储
/// </summary>
public class LocalDateTimeConverter : JsonConverter<DateTime>
{
    private static readonly TimeZoneInfo ChinaZone = GetChinaTimeZone();

    private static TimeZoneInfo GetChinaTimeZone()
    {
        // Linux/macOS 使用 IANA ID，Windows 使用 Windows ID
        // 依次尝试两个 ID，确保跨平台兼容
        try { return TimeZoneInfo.FindSystemTimeZoneById("Asia/Shanghai"); }
        catch { return TimeZoneInfo.FindSystemTimeZoneById("China Standard Time"); }
    }

    public override DateTime Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var value = reader.GetDateTime();
        // 如果前端传来的是本地时间（不带 Z），转成 UTC 存储
        if (value.Kind == DateTimeKind.Unspecified)
            return TimeZoneInfo.ConvertTimeToUtc(value, ChinaZone);
        return value.ToUniversalTime();
    }

    public override void Write(Utf8JsonWriter writer, DateTime value, JsonSerializerOptions options)
    {
        // 先将 UTC 时间转为北京时间
        var localTime = TimeZoneInfo.ConvertTimeFromUtc(value, ChinaZone);
        // 写入时带上 +08:00 时区偏移，前端可正确解析
        writer.WriteStringValue(localTime.ToString("yyyy-MM-ddTHH:mm:ss+08:00"));
    }
}

/// <summary>
/// 可空 DateTime 转换器
/// </summary>
public class LocalNullableDateTimeConverter : JsonConverter<DateTime?>
{
    private static readonly LocalDateTimeConverter Inner = new();

    public override DateTime? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null)
            return null;
        return Inner.Read(ref reader, typeof(DateTime), options);
    }

    public override void Write(Utf8JsonWriter writer, DateTime? value, JsonSerializerOptions options)
    {
        if (value == null)
            writer.WriteNullValue();
        else
            Inner.Write(writer, value.Value, options);
    }
}
