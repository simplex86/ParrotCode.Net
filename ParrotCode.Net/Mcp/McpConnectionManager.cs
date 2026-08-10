using Microsoft.Extensions.Logging;

namespace ParrotCode;

/// <summary>
/// MCP 连接管理器：并行连接所有配置的 MCP server，管理生命周期（迭代 11c）。
/// 单个 server 连接失败不阻塞其他——记录到 ConnectionResults，跳过该 server。
///
/// 职责：
/// 1. 启动时并行连接所有 server（Task.WhenAll）
/// 2. 将成功连接的 server 的工具收集到 Adapters 列表（由 TerminalApp 统一注册）
/// 3. 关闭时并行关闭所有 server
/// </summary>
internal sealed class McpConnectionManager : IAsyncDisposable
{
    private readonly IReadOnlyList<McpServerConfig> _serverConfigs;
    private readonly ILogger? _logger;
    private readonly List<McpClient> _clients = new();
    private readonly List<McpToolAdapter> _adapters = new();
    private readonly List<ConnectionResult> _connectionResults = new();

    /// <summary>
    /// 每个 server 的连接结果（成功/失败 + 错误信息），供 TUI 显示。
    /// </summary>
    public IReadOnlyList<ConnectionResult> ConnectionResults => _connectionResults;

    public McpConnectionManager(IReadOnlyList<McpServerConfig> serverConfigs, ILogger? logger = null)
    {
        _serverConfigs = serverConfigs ?? throw new ArgumentNullException(nameof(serverConfigs));
        _logger = logger;
    }

    /// <summary>
    /// 已连接的 MCP 客户端列表。
    /// </summary>
    public IReadOnlyList<McpClient> Clients => _clients;

    /// <summary>
    /// 已收集的 MCP 工具适配器列表（由 TerminalApp 注册到 ToolRegistry）。
    /// </summary>
    public IReadOnlyList<McpToolAdapter> Adapters => _adapters;

    /// <summary>
    /// 已连接的 server 数量。
    /// </summary>
    public int ConnectedCount => _clients.Count;

    /// <summary>
    /// 已发现的 MCP 工具数量。
    /// </summary>
    public int ToolCount => _adapters.Count;

    /// <summary>
    /// 配置的 server 总数。
    /// </summary>
    public int ConfiguredCount => _serverConfigs.Count;

    /// <summary>
    /// 并行连接所有 MCP server。
    /// 单个失败记日志，不抛异常，不阻塞其他。
    /// 连接成功后工具适配器收集到 Adapters 列表，由调用方（TerminalApp）注册到 ToolRegistry。
    /// </summary>
    public async Task ConnectAllAsync(CancellationToken cancellationToken)
    {
        if (_serverConfigs.Count == 0) return;

        _logger?.LogInformation("开始连接 {Count} 个 MCP server...", _serverConfigs.Count);

        var connectTasks = _serverConfigs.Select(config => ConnectOneAsync(config, cancellationToken));
        await Task.WhenAll(connectTasks);

        _logger?.LogInformation("MCP 连接完成：{Connected}/{Total} 个 server 就绪，{Tools} 个工具已发现",
                               _clients.Count, _serverConfigs.Count, _adapters.Count);
    }

    private async Task ConnectOneAsync(McpServerConfig config, CancellationToken cancellationToken)
    {
        ITransport? transport = null;
        try
        {
            // 创建 transport
            transport = config.Transport switch
            {
                "stdio" => new StdioTransport(config, _logger),
                "http" or "sse" or "streamable-http" => new StreamableHttpTransport(config, _logger),
                _ => throw new ArgumentException($"不支持的 MCP transport：{config.Transport}")
            };

            // 创建 client 并连接
            var client = new McpClient(config.Name, transport, _logger);
            await client.ConnectAsync(cancellationToken);

            // 收集工具适配器（不在此处注册到 registry，由 TerminalApp 统一注册）
            foreach (var toolInfo in client.Tools)
            {
                var adapter = new McpToolAdapter(client, toolInfo, _logger);
                _adapters.Add(adapter);
            }

            _clients.Add(client);
            _connectionResults.Add(new ConnectionResult(config.Name, true, client.Tools.Count, null));
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "MCP server [{Name}] 连接失败，跳过", config.Name);

            // 拼接 transport 诊断信息（如子进程 stderr）到错误消息，帮助定位 server 启动失败原因
            var stderr = transport?.GetErrorContext();
            var errorMsg = string.IsNullOrWhiteSpace(stderr)
                ? ex.Message
                : $"{ex.Message}\n  server stderr:\n{stderr}";

            _connectionResults.Add(new ConnectionResult(config.Name, false, 0, errorMsg));
        }
        finally
        {
            // 连接失败时 transport 未被 McpClient 接管，需在此释放避免泄漏
            // 连接成功时 client 已加入 _clients（McpClient.DisposeAsync 会释放 transport），跳过
            if (transport is not null && !_clients.Any(c => c.ServerName == config.Name))
            {
                try { await transport.DisposeAsync(); } catch { }
            }
        }
    }

    /// <summary>
    /// 并行关闭所有 MCP server。
    /// </summary>
    public async Task CloseAllAsync(CancellationToken cancellationToken)
    {
        if (_clients.Count == 0) return;

        _logger?.LogInformation("正在关闭 {Count} 个 MCP server...", _clients.Count);

        var closeTasks = _clients.Select(client => CloseOneAsync(client, cancellationToken));
        await Task.WhenAll(closeTasks);

        _clients.Clear();
        _adapters.Clear();
    }

    private static async Task CloseOneAsync(McpClient client, CancellationToken cancellationToken)
    {
        try
        {
            await client.CloseAsync(cancellationToken);
        }
        catch
        {
            // 关闭失败不影响其他 server
        }
    }

    /// <summary>
    /// 获取连接状态概要（/status 用）。
    /// </summary>
    public string GetStatusSummary()
    {
        if (_serverConfigs.Count == 0) return "未配置";
        if (_clients.Count == 0) return $"已配置 {_serverConfigs.Count} 个，全部连接失败";
        if (_clients.Count < _serverConfigs.Count)
            return $"{_clients.Count}/{_serverConfigs.Count} 个已连接，{_adapters.Count} 个工具";
        return $"{_clients.Count} 个已连接，{_adapters.Count} 个工具";
    }

    public async ValueTask DisposeAsync()
    {
        await CloseAllAsync(CancellationToken.None);
    }
}

/// <summary>
/// 单个 MCP server 的连接结果。
/// </summary>
internal sealed record ConnectionResult(string ServerName, bool Success, int ToolCount, string? ErrorMessage);
