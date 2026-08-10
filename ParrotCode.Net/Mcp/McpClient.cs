using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace ParrotCode;

/// <summary>
/// MCP 客户端：管理单个 MCP server 的完整生命周期（迭代 11b）。
/// 流程：connect → initialize → initialized → tools/list → tools/call → close
///
/// 接收循环：后台 Task 持续从 transport 读取消息，分发给 JsonRpc.HandleMessage。
/// 超时：initialize 30s，tools/list 10s，tools/call 60s。
/// </summary>
internal sealed class McpClient : IAsyncDisposable
{
    private readonly string _serverName;
    private readonly ITransport _transport;
    private readonly JsonRpc _rpc;
    private readonly ILogger? _logger;

    // 接收循环
    private Task? _receiveLoop;
    private CancellationTokenSource? _receiveCts;

    // 已发现的工具列表
    private IReadOnlyList<McpToolInfo> _tools = Array.Empty<McpToolInfo>();

    public McpClient(string serverName, ITransport transport, ILogger? logger = null)
    {
        _serverName = serverName ?? throw new ArgumentNullException(nameof(serverName));
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        _rpc = new JsonRpc(logger);
        _logger = logger;
    }

    /// <summary>
    /// 已发现的工具列表。
    /// </summary>
    public IReadOnlyList<McpToolInfo> Tools => _tools;

    /// <summary>
    /// Server 名称。
    /// </summary>
    public string ServerName => _serverName;

    /// <summary>
    /// 是否已连接并初始化。
    /// </summary>
    public bool IsInitialized { get; private set; }

    /// <summary>
    /// 连接 MCP server 并完成初始化握手。
    /// 流程：transport.Connect → 启动接收循环 → initialize → initialized → tools/list
    /// </summary>
    public async Task ConnectAsync(CancellationToken cancellationToken)
    {
        // 1. 启动传输
        await _transport.ConnectAsync(cancellationToken);

        // 2. 启动接收循环
        _receiveCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _receiveLoop = ReceiveLoopAsync(_receiveCts.Token);

        // 3. initialize 握手
        var initParams = new McpInitializeParams();
        var (json, task) = _rpc.CreateRequest(McpMethods.Initialize, initParams);
        await _transport.SendAsync(json, cancellationToken);

        var initResult = await task.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);
        _logger?.LogInformation("MCP server [{Name}] initialize 成功：{Result}", _serverName, initResult);

        // 4. 发送 initialized 通知
        var notification = _rpc.CreateNotification(McpMethods.Initialized);
        await _transport.SendAsync(notification, cancellationToken);

        // 5. 获取工具列表
        await RefreshToolsAsync(cancellationToken);

        IsInitialized = true;
        _logger?.LogInformation("MCP server [{Name}] 就绪，提供 {Count} 个工具", _serverName, _tools.Count);
    }

    /// <summary>
    /// 刷新工具列表（调用 tools/list）。
    /// </summary>
    public async Task RefreshToolsAsync(CancellationToken cancellationToken)
    {
        var (json, task) = _rpc.CreateRequest(McpMethods.ToolsList);
        await _transport.SendAsync(json, cancellationToken);

        var result = await task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
        _tools = ParseToolsList(result);
    }

    /// <summary>
    /// 调用 MCP 工具。
    /// </summary>
    public async Task<McpToolCallResult> CallToolAsync(string toolName, JsonElement arguments, CancellationToken cancellationToken)
    {
        var @params = new McpToolCallParams { Name = toolName, Arguments = arguments };
        var (json, task) = _rpc.CreateRequest(McpMethods.ToolsCall, @params);
        await _transport.SendAsync(json, cancellationToken);

        var result = await task.WaitAsync(TimeSpan.FromSeconds(60), cancellationToken);
        return ParseToolCallResult(result);
    }

    /// <summary>
    /// 关闭 MCP 客户端。
    /// </summary>
    public async Task CloseAsync(CancellationToken cancellationToken)
    {
        _logger?.LogInformation("MCP client [{Name}] 正在关闭", _serverName);

        // 停止接收循环
        _receiveCts?.Cancel();

        // 关闭传输
        try
        {
            await _transport.CloseAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "MCP transport [{Name}] 关闭异常", _serverName);
        }

        // 等待接收循环结束
        if (_receiveLoop is not null)
        {
            try { await _receiveLoop; } catch { }
        }

        // 取消所有 pending 请求
        _rpc.CancelAllPending();
        IsInitialized = false;
    }

    /// <summary>
    /// 接收循环：从 transport 读取消息，分发给 JsonRpc 处理。
    /// </summary>
    private async Task ReceiveLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var message = await _transport.ReceiveAsync(ct);
                if (message is null) break;  // 连接关闭
                if (string.IsNullOrWhiteSpace(message)) continue;
                _rpc.HandleMessage(message);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "MCP client [{Name}] 接收循环异常", _serverName);
        }
        finally
        {
            _rpc.CancelAllPending();
        }
    }

    /// <summary>
    /// 解析 tools/list 响应。
    /// </summary>
    private static IReadOnlyList<McpToolInfo> ParseToolsList(JsonElement result)
    {
        if (result.ValueKind != JsonValueKind.Object) return Array.Empty<McpToolInfo>();
        if (!result.TryGetProperty("tools", out var toolsEl)) return Array.Empty<McpToolInfo>();
        if (toolsEl.ValueKind != JsonValueKind.Array) return Array.Empty<McpToolInfo>();

        var tools = new List<McpToolInfo>();
        foreach (var toolEl in toolsEl.EnumerateArray())
        {
            var name = toolEl.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
            var desc = toolEl.TryGetProperty("description", out var d) ? d.GetString() ?? "" : "";
            var schema = toolEl.TryGetProperty("inputSchema", out var s) ? s.Clone() : default;

            McpToolAnnotations? annotations = null;
            if (toolEl.TryGetProperty("annotations", out var a))
            {
                annotations = new McpToolAnnotations
                {
                    ReadOnlyHint = a.TryGetProperty("readOnlyHint", out var ro) && ro.ValueKind == JsonValueKind.True ? true : (ro.ValueKind == JsonValueKind.False ? false : null),
                    DestructiveHint = a.TryGetProperty("destructiveHint", out var dh) && dh.ValueKind == JsonValueKind.True ? true : (dh.ValueKind == JsonValueKind.False ? false : null)
                };
            }

            tools.Add(new McpToolInfo
            {
                Name = name,
                Description = desc,
                InputSchema = schema,
                Annotations = annotations
            });
        }
        return tools;
    }

    /// <summary>
    /// 解析 tools/call 响应。
    /// </summary>
    private static McpToolCallResult ParseToolCallResult(JsonElement result)
    {
        var content = new List<McpContentBlock>();
        var isError = false;

        if (result.ValueKind == JsonValueKind.Object)
        {
            if (result.TryGetProperty("isError", out var ie) && ie.ValueKind == JsonValueKind.True)
                isError = true;

            if (result.TryGetProperty("content", out var contentEl) && contentEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var block in contentEl.EnumerateArray())
                {
                    var type = block.TryGetProperty("type", out var t) ? t.GetString() ?? "text" : "text";
                    var text = block.TryGetProperty("text", out var tx) ? tx.GetString() ?? "" : "";
                    content.Add(new McpContentBlock { Type = type, Text = text });
                }
            }
        }

        return new McpToolCallResult { Content = content, IsError = isError };
    }

    public async ValueTask DisposeAsync()
    {
        await CloseAsync(CancellationToken.None);
        await _transport.DisposeAsync();
    }
}
