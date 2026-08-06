using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;

namespace ParrotCode;

/// <summary>
/// OpenAI 兼容协议 Provider。通过 BaseUrl 覆盖 OpenAI 官方与 DeepSeek 等兼容服务。
/// 流式：POST /v1/chat/completions (stream=true) → SSE 逐行解析 → yield content delta。
/// </summary>
public sealed class OpenAIProvider : IBaseProvider
{
    private const string ChatCompletionsPath = "chat/completions";
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(5);

    private readonly ProviderConfig _config;
    private readonly HttpClient _httpClient;

    public OpenAIProvider(ProviderConfig config)
        : this(config, CreateHttpClient(config))
    {
    }

    /// <summary>测试用：注入自定义 HttpMessageHandler。</summary>
    internal OpenAIProvider(ProviderConfig config, HttpMessageHandler handler)
        : this(config, CreateHttpClientFromHandler(config, handler))
    {
    }

    private OpenAIProvider(ProviderConfig config, HttpClient httpClient)
    {
        _config = config;
        _httpClient = httpClient;
    }

    // —— 非流式 ——

    public async Task<string> ChatAsync(IReadOnlyList<Message> messages, CancellationToken cancellationToken)
    {
        var body = BuildRequestBody(messages, stream: false);
        using var response = await SendAsync(body, cancellationToken);
        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.GetProperty("choices")[0]
                              .GetProperty("message")
                              .GetProperty("content")
                              .GetString() ?? string.Empty;
    }

    // —— 流式 ——

    public async IAsyncEnumerable<string> ChatStreamAsync(IReadOnlyList<Message> messages, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var body = BuildRequestBody(messages, stream: true);
        using var response = await SendAsync(body, cancellationToken);

        // ResponseHeadersRead：读到响应头即返回，不缓冲整个 body（流式必需）
        using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(stream);

        while (!cancellationToken.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(cancellationToken);
            if (line is null) break;  // 流结束

            // SSE 协议：空行是事件分隔符，跳过
            if (string.IsNullOrEmpty(line)) continue;
            // 其他前缀（event: / id: / 注释 :...）本迭代不处理，跳过
            if (!line.StartsWith("data: ")) continue;

            var data = line["data: ".Length..];
            if (data == "[DONE]") break;  // OpenAI 流终止标记

            // 解析 JSON，提取 choices[0].delta.content
            using var doc = JsonDocument.Parse(data);
            var choices = doc.RootElement.GetProperty("choices");
            if (choices.GetArrayLength() == 0) continue;

            var delta = choices[0].GetProperty("delta");
            if (delta.TryGetProperty("content", out var contentEl))
            {
                var text = contentEl.GetString();
                if (!string.IsNullOrEmpty(text))
                    yield return text;
            }
            // reasoning_content（DeepSeek-reasoner）被自然忽略：只读 content
        }
    }

    // —— 内部方法 ——

    private string BuildRequestBody(IReadOnlyList<Message> messages, bool stream)
    {
        var msgArray = messages.Select(m => new
        {
            role = m.Role switch
            {
                MessageRole.System => "system",
                MessageRole.User => "user",
                MessageRole.Assistant => "assistant",
                MessageRole.Tool => "tool",
                _ => "user"
            },
            content = m.Content
        });

        var request = new
        {
            model = _config.Model,
            messages = msgArray,
            stream
        };

        return JsonSerializer.Serialize(request);
    }

    private async Task<HttpResponseMessage> SendAsync(string jsonBody, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, ChatCompletionsPath)
        {
            Content = new StringContent(jsonBody, Encoding.UTF8, "application/json")
        };

        HttpResponseMessage response;
        try
        {
            // HttpCompletionOption.ResponseHeadersRead：流式请求的关键——读到头即返回
            response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        }
        catch (HttpRequestException ex)
        {
            throw new ProviderRequestException($"无法连接到 {_config.BaseUrl}：{ex.Message}", null, ex);
        }
        catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            // 非用户取消的超时
            throw new ProviderRequestException($"请求超时（{_httpClient.Timeout.TotalSeconds}s）", null, ex);
        }

        if (!response.IsSuccessStatusCode)
        {
            var statusCode = (int)response.StatusCode;
            var errorBody = await response.Content.ReadAsStringAsync(cancellationToken);
            var msg = FormatError(statusCode, errorBody);
            response.Dispose();
            throw statusCode switch
            {
                401 => new ProviderAuthException(msg),
                429 => new ProviderRateLimitException(msg),
                >= 500 => new ProviderServerException(msg, statusCode),
                _ => new ProviderRequestException(msg, statusCode)
            };
        }

        return response;
    }

    private static string FormatError(int statusCode, string errorBody)
    {
        // 尝试提取 OpenAI 错误格式 {"error":{"message":"..."}}
        try
        {
            using var doc = JsonDocument.Parse(errorBody);
            if (doc.RootElement.TryGetProperty("error", out var errEl) && 
                errEl.TryGetProperty("message", out var msgEl))
            {
                return $"HTTP {statusCode}: {msgEl.GetString()}";
            }
        }
        catch 
        { 
            /* 非 JSON 错误体，用原文 */ 
        }

        return string.IsNullOrWhiteSpace(errorBody) ? $"HTTP {statusCode}"
                                                    : $"HTTP {statusCode}: {errorBody}";
    }

    private static HttpClient CreateHttpClient(ProviderConfig config)
    {
        ValidateConfig(config);
        var client = new HttpClient
        {
            BaseAddress = new Uri(EnsureTrailingSlash(config.BaseUrl)),
            Timeout = DefaultTimeout
        };
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", config.ApiKey);
        return client;
    }

    private static HttpClient CreateHttpClientFromHandler(ProviderConfig config, HttpMessageHandler handler)
    {
        ValidateConfig(config);
        return new HttpClient(handler)
        {
            BaseAddress = new Uri(EnsureTrailingSlash(config.BaseUrl))
        };
    }

    /// <summary>配置必填校验（迭代 2b 延迟到此处）。</summary>
    private static void ValidateConfig(ProviderConfig config)
    {
        if (string.IsNullOrWhiteSpace(config.Model))
            throw new ConfigException($"provider '{config.Name}' 的 model 不能为空");
        if (string.IsNullOrWhiteSpace(config.BaseUrl))
            throw new ConfigException($"provider '{config.Name}' 的 base_url 不能为空");
        if (string.IsNullOrWhiteSpace(config.ApiKey))
            throw new ConfigException($"provider '{config.Name}' 的 api_key 不能为空");
    }

    /// <summary>
    /// 确保 BaseUrl 以 / 结尾，使相对路径 chat/completions 能正确拼接到 base_url 之后。
    /// .NET URI 解析：无尾斜杠时 "https://host/v1" + "chat/completions" = "https://host/chat/completions"（丢失 /v1）。
    /// </summary>
    private static string EnsureTrailingSlash(string baseUrl) => baseUrl.EndsWith('/') ? baseUrl : baseUrl + "/";
}
