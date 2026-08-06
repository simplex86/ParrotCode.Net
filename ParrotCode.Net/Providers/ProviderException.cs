namespace ParrotCode;

/// <summary>
/// Provider 调用异常基类。所有 HTTP / 网络错误转译为 ProviderException 层次，
/// 让调用方按语义捕获而非检查状态码或 HttpRequestException。
/// </summary>
public class ProviderException : Exception
{
    public int? StatusCode { get; }

    public ProviderException(string message, int? statusCode = null, Exception? inner = null)
        : base(message, inner)
    {
        StatusCode = statusCode;
    }
}

/// <summary>
/// 认证失败（401）：ApiKey 无效或缺失。
/// </summary>
public sealed class ProviderAuthException : ProviderException
{
    public ProviderAuthException(string message, Exception? inner = null)
        : base(message, 401, inner) { }
}

/// <summary>
/// 速率限制（429）：请求过快。
/// </summary>
public sealed class ProviderRateLimitException : ProviderException
{
    public ProviderRateLimitException(string message, Exception? inner = null)
        : base(message, 429, inner) { }
}

/// <summary>
/// 服务端错误（5xx）：Provider 内部故障。
/// </summary>
public sealed class ProviderServerException : ProviderException
{
    public ProviderServerException(string message, int statusCode, Exception? inner = null)
        : base(message, statusCode, inner) { }
}

/// <summary>
/// 其他请求错误：网络故障 / 未知状态码 / SSE 解析失败。
/// </summary>
public sealed class ProviderRequestException : ProviderException
{
    public ProviderRequestException(string message, int? statusCode = null, Exception? inner = null)
        : base(message, statusCode, inner) { }
}
