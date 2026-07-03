namespace backend.Common.DTOs;

/// <summary>
/// 统一API返回格式
/// </summary>
public class ApiResult<T>
{
    public int Code { get; set; }
    public string Message { get; set; } = string.Empty;
    public T? Data { get; set; }

    public static ApiResult<T> Success(T data, string msg = "success") =>
        new() { Code = 200, Message = msg, Data = data };

    public static ApiResult<T> Fail(string msg, int code = 400) =>
        new() { Code = code, Message = msg };

    public static ApiResult<T> Error(string msg = "服务器内部错误") =>
        new() { Code = 500, Message = msg };
}

public class PageResult<T>
{
    public long Total { get; set; }
    public int Page { get; set; }
    public int PageSize { get; set; }
    public List<T> List { get; set; } = new();
}
