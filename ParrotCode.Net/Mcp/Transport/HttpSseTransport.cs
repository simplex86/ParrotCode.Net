using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;

namespace ParrotCode;

/// <summary>
/// HTTP SSE 传输：POST 发送 JSON-RPC 请求，通过 SSE 流接收响应（迭代 11c）。
///
/// 用 Channel 解耦收发：
/// - SendAsync：POST JSON 到 /mcp，根据 Content-Type 决定 SSE 流解析或单次 JSON
/// - ReceiveAsync：从 Channel 读取已解析的消息
///
/// Bearer Token 认证：构造时设置 Authorization 头。
/// </summary>
internal sealed class HttpSseTransport : ITransport
{
    private readonly McpServerConfig _config;
    private readonly ILogger? _logger;
    private readonly HttpClient _httpClient;
    private CancellationTokenSource? _receiveCts;
    private Channel<string>? _receiveChannel;

    public HttpSseTransport(McpServerConfig config, ILogger? logger = null)
        : this(config, new HttpClientHandler(), logger)
    {
    }

    /// <summary>测试用：注入 HttpMessageHandler 以模拟 HTTP 响应。</summary>
    internal HttpSseTransport(McpServerConfig config, HttpMessageHandler handler, ILogger? logger = null)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _logger = logger;

        if (string.IsNullOrWhiteSpace(_config.Url))
            throw new ArgumentException("HTTP transport 需要 url", nameof(config));

        _httpClient = new HttpClient(handler)
        {
            BaseAddress = new Uri(_config.Url)
        };

        if (!string.IsNullOrWhiteSpace(_config.ApiKey))
            _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _config.ApiKey);
    }

    public Task ConnectAsync(CancellationToken cancellationToken)
    {
        _receiveCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _receiveChannel = Channel.CreateUnbounded<string>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = true
        });

        // HTTP 传输无需预连接——首个 POST 即建立
        _logger?.LogInformation("MCP HTTP server [{Name}] 已就绪 ({Url})", _config.Name, _config.Url);
        return Task.CompletedTask;
    }

    public async Task SendAsync(string json, CancellationToken cancellationToken)
    {
        if (_receiveChannel is null) throw new InvalidOperationException("Transport 未连接");

        var content = new StringContent(json, Encoding.UTF8, "application/json");
        var response = await _httpClient.PostAsync("/mcp", content, cancellationToken);
        response.EnsureSuccessStatusCode();

        var contentType = response.Content.Headers.ContentType?.MediaType;

        if (contentType == "text/event-stream")
        {
            // SSE 流响应：在后台解析 SSE 事件并写入 channel
            _ = Task.Run(() => ParseSseStreamAsync(response, _receiveChannel.Writer, _receiveCts!.Token), _receiveCts!.Token);
        }
        else
        {
            // 单次 JSON 响应
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!string.IsNullOrWhiteSpace(body))
                await _receiveChannel.Writer.WriteAsync(body, cancellationToken);
        }
    }

    public async Task<string?> ReceiveAsync(CancellationToken cancellationToken)
    {
        if (_receiveChannel is null) throw new InvalidOperationException("Transport 未连接");
        if (await _receiveChannel.Reader.WaitToReadAsync(cancellationToken))
        {
            if (_receiveChannel.Reader.TryRead(out var msg))
                return msg;
        }
        return null;
    }

    public Task CloseAsync(CancellationToken cancellationToken)
    {
        _receiveCts?.Cancel();
        _receiveChannel?.Writer.TryComplete();
        _logger?.LogInformation("MCP HTTP server [{Name}] 已关闭", _config.Name);
        return Task.CompletedTask;
    }

    /// <summary>解析 SSE 流，提取 data 行写入 channel。</summary>
    private async Task ParseSseStreamAsync(HttpResponseMessage response, ChannelWriter<string> writer, CancellationToken ct)
    {
        try
        {
            using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var reader = new StreamReader(stream);
            string? currentData = null;

            while (!ct.IsCancellationRequested && !reader.EndOfStream)
            {
                var line = await reader.ReadLineAsync(ct);
                if (line is null) break;

                if (line.StartsWith("data: "))
                {
                    currentData = line["data: ".Length..];
                }
                else if (line.Length == 0 && currentData is not null)
                {
                    // 空行 = 事件分隔符，发送累积的 data
                    await writer.WriteAsync(currentData, ct);
                    currentData = null;
                }
            }

            // 流结束时如果有未发送的 data
            if (currentData is not null)
                await writer.WriteAsync(currentData, ct);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _logger?.LogDebug("MCP HTTP SSE 流结束：{Error}", ex.Message);
        }
        // 不调用 writer.TryComplete()——channel 供后续请求复用，仅 CloseAsync 关闭
    }

    public ValueTask DisposeAsync()
    {
        _receiveCts?.Cancel();
        _receiveCts?.Dispose();
        _httpClient.Dispose();
        return ValueTask.CompletedTask;
    }

    /// <summary>HTTP 传输无 stderr，返回 null。</summary>
    public string? GetErrorContext() => null;
}
