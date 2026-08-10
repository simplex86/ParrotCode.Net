# 迭代 11c：HTTP SSE 传输 + 连接管理器 + 端到端装配

> **状态**：[设计完成，待实现]
> **前置迭代**：11b [已完成]（MCP 客户端 + 工具适配器）
> **父文档**：[iter-11-design.md](iter-11-design.md)（总览）
> **后续迭代**：12（Skill 系统 + Hook 引擎 + 子 Agent）
>
> **目标**：交付 HTTP SSE 传输、连接管理器、配置接入和端到端装配。配置真实 MCP server 后，AI 能通过 MCP 工具调用外部能力，与内置工具统一接入 AgentLoop。

---

## 一、迭代目标

### 1.1 核心目标

1. **HTTP SSE 传输**：`HttpSseTransport` 通过 `HttpClient` POST 发送 JSON-RPC 请求，通过 SSE 流接收响应
2. **SSE 流解析**：正确解析 `event:` / `data:` 前缀和空行分隔符
3. **连接管理器**：`McpConnectionManager` 并行连接所有配置的 MCP server，单个失败不阻塞其他
4. **配置接入**：`McpConfig` + `McpServerConfig` + `AppConfig.Mcp` + YAML 配置节 `mcp:`
5. **App 装配**：`App.RunAsync` 中构造 `McpConnectionManager`，连接 MCP server，传入 `TerminalApp`
6. **TerminalApp 集成**：注册 MCP 工具到 `ToolRegistry`，Agent 透明调用
7. **StatusCommand 扩展**：`/status` 显示 MCP server 连接状态
8. **生命周期管理**：程序退出时干净关闭所有 MCP server 子进程 / HTTP 连接

### 1.2 本迭代要验证的关键问题

| 问题 | 验证方式 | 失败对策 |
|------|---------|---------|
| HTTP SSE 传输 event/data 分隔是否正确 | 单测：模拟 SSE 流解析 | 逐行解析 `event:` / `data:` 前缀 |
| 单个 MCP server 连接失败是否阻塞其他 | 单测：3 个 server 中 1 个不可达 | `Task.WhenAll` + 每个 catch 独立 |
| MCP 工具是否在 AI 工具列表可见 | 手动：对话中让 AI 调用 MCP 工具 | `ToolRegistry.Register` 注册 |
| MCP server 子进程是否干净关闭 | 手动：退出程序后检查子进程是否退出 | `CancellationToken` 触发 + `Process.Kill` 兜底 |
| 配置的 MCP server 能否正常连接 | 手动：配置 filesystem MCP server | 端到端验证 |
| `/status` 是否显示 MCP 状态 | 手动 | StatusCommand 扩展 |
| MCP 工具 Write 类是否弹 HITL | 手动：Normal 模式调用 MCP Write 工具 | Category 判定 + SecureBatchToolExecutor |
| HTTP SSE 传输的 Bearer Token 是否正确 | 单测：验证 Authorization 头 | `HttpClient.DefaultRequestHeaders` |

### 1.3 非目标（明确不做）

- ❌ 不做 OAuth 认证——本迭代仅支持无认证 / Bearer Token
- ❌ 不做 MCP server 热重载——增删 server 需重启
- ❌ 不做 MCP server 进程自动重启——崩溃后不拉起
- ❌ 不做 `tools/list_changed` 通知处理
- ❌ 不做 MCP Resources / Prompts / Sampling

---

## 二、文件改动清单

### 2.1 新增文件（3 个）

```
Mcp/
├── Transport/
│   └── HttpSseTransport.cs     # HTTP SSE 传输
├── McpConnectionManager.cs     # 连接管理器（并行连接 + 生命周期）
└── McpConfig.cs                # 配置 record（McpConfig）
```

测试文件（1 个）：

```
ParrotCode.Net-xUnit/
└── McpConnectionManagerTests.cs  # 连接管理器 + HTTP SSE 解析
```

### 2.2 修改文件（5 个）

| 文件 | 改动 |
|------|------|
| `Config/Models.cs` | 新增 `AppConfig.Mcp` 字段 |
| `example.parrotcode.yaml` | 新增 `mcp:` 配置节示例 |
| `App/App.cs` | 构造 `McpConnectionManager`，启动连接，传入 `TerminalApp` |
| `Tui/TerminalApp.cs` | 构造函数加 `McpConnectionManager?` 参数；`RunAsync` 中注册 MCP 工具；`Dispose` 中关闭 MCP 连接 |
| `Commands/Builtin/StatusCommand.cs` | 显示 MCP server 连接状态 |
| `Commands/CommandContext.cs` | 新增 `McpSummary` 字段 |

### 2.3 不变文件

- `Mcp/Protocol/JsonRpc.cs` / `McpMethods.cs`——11a 已完成
- `Mcp/Transport/ITransport.cs` / `StdioTransport.cs` / `MockTransport.cs`——11a 已完成
- `Mcp/McpClient.cs` / `McpToolAdapter.cs`——11b 已完成
- `McpServerConfig`——11a 已定义（在 McpMethods.cs 中），11c 不改

---

## 三、详细设计

### 3.1 HTTP SSE 传输

MCP HTTP 传输协议（2025-03-26 spec Streamable HTTP）：
1. Client POST /mcp 发送 JSON-RPC 请求
2. Server 以 SSE 流返回响应（`Content-Type: text/event-stream`）
3. 也支持单次 JSON 响应（`Content-Type: application/json`）

SSE 格式：
```
event: message
data: {"jsonrpc":"2.0","id":1,"result":{...}}

event: message
data: {"jsonrpc":"2.0","id":2,"result":{...}}
```

```csharp
// Mcp/Transport/HttpSseTransport.cs
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;

namespace ParrotCode;

/// <summary>
/// HTTP SSE 传输：POST 发送 JSON-RPC 请求，通过 SSE 流接收响应。
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
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _logger = logger;

        _httpClient = new HttpClient
        {
            BaseAddress = new Uri(_config.Url ?? throw new ArgumentException("HTTP transport 需要 url"))
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
        finally
        {
            writer.TryComplete();
        }
    }

    public ValueTask DisposeAsync()
    {
        _receiveCts?.Cancel();
        _receiveCts?.Dispose();
        _httpClient.Dispose();
        return ValueTask.CompletedTask;
    }
}
```

### 3.2 MCP 连接管理器

```csharp
// Mcp/McpConnectionManager.cs
using Microsoft.Extensions.Logging;

namespace ParrotCode;

/// <summary>
/// MCP 连接管理器：并行连接所有配置的 MCP server，管理生命周期。
/// 单个 server 连接失败不阻塞其他——记日志，跳过该 server。
/// 
/// 职责：
/// 1. 启动时并行连接所有 server（Task.WhenAll）
/// 2. 将成功连接的 server 的工具收集到 Adapters 列表（由 TerminalApp 统一注册）
/// 3. 关闭时并行关闭所有 server
/// </summary>
public sealed class McpConnectionManager : IAsyncDisposable
{
    private readonly IReadOnlyList<McpServerConfig> _serverConfigs;
    private readonly ILogger? _logger;
    private readonly List<McpClient> _clients = new();
    private readonly List<McpToolAdapter> _adapters = new();

    public McpConnectionManager(IReadOnlyList<McpServerConfig> serverConfigs, ILogger? logger = null)
    {
        _serverConfigs = serverConfigs ?? throw new ArgumentNullException(nameof(serverConfigs));
        _logger = logger;
    }

    /// <summary>已连接的 MCP 客户端列表。</summary>
    public IReadOnlyList<McpClient> Clients => _clients;

    /// <summary>已收集的 MCP 工具适配器列表（由 TerminalApp 注册到 ToolRegistry）。</summary>
    public IReadOnlyList<McpToolAdapter> Adapters => _adapters;

    /// <summary>已连接的 server 数量。</summary>
    public int ConnectedCount => _clients.Count;

    /// <summary>已发现的 MCP 工具数量。</summary>
    public int ToolCount => _adapters.Count;

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
        try
        {
            // 创建 transport
            ITransport transport = config.Transport switch
            {
                "stdio" => new StdioTransport(config, _logger),
                "http" or "sse" => new HttpSseTransport(config, _logger),
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
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "MCP server [{Name}] 连接失败，跳过", config.Name);
        }
    }

    /// <summary>并行关闭所有 MCP server。</summary>
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

    /// <summary>获取连接状态概要（/status 用）。</summary>
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
```

### 3.3 MCP 配置

```csharp
// Mcp/McpConfig.cs
namespace ParrotCode;

/// <summary>
/// MCP 配置（迭代 11 新增）。null 时不启用 MCP。
/// </summary>
public sealed record McpConfig
{
    /// <summary>是否启用 MCP 客户端。默认 true。false 时不连接任何 MCP server。</summary>
    public bool? Enable { get; init; }

    /// <summary>MCP server 配置列表。</summary>
    public IList<McpServerConfig> Servers { get; init; } = Array.Empty<McpServerConfig>();
}
```

> **注**：`McpServerConfig` 在 11a 已定义（含 `Name` / `Transport` / `Command` / `Args` / `WorkingDir` / `Env` / `Url` / `ApiKey`）。

### 3.4 AppConfig 扩展

```csharp
// Config/Models.cs — 迭代 11c 新增

// AppConfig 新增字段：
/// <summary>MCP 客户端配置（迭代 11 新增）。null 时用默认值。</summary>
public McpConfig? Mcp { get; init; }
```

### 3.5 YAML 配置示例

```yaml
# 迭代 11 新增：MCP 客户端配置
mcp:
  enable: true
  servers:
    # Stdio MCP server 示例：filesystem 服务器
    - name: filesystem
      transport: stdio
      command: npx
      args: "-y @anthropic/mcp-server-filesystem /tmp"
      # working_dir: /optional/working/directory
      # env:
      #   NODE_OPTIONS: "--max-old-space-size=4096"

    # HTTP MCP server 示例
    # - name: remote-tools
    #   transport: http
    #   url: https://mcp.example.com/mcp
    #   api_key: ${MCP_API_KEY}
```

### 3.6 App.cs 装配

```csharp
// App/App.cs — 迭代 11c 扩展

public async Task RunAsync()
{
    // ... 既有装配（TuiConfig, SecurityGuard, ContextCompressor, SessionStore, InstructionLoader）...

    // 【迭代 11c】MCP 连接管理器
    var mcpConfig = _config.Mcp ?? new McpConfig();
    McpConnectionManager? mcpManager = null;
    if (mcpConfig.Enable ?? true)
    {
        mcpManager = new McpConnectionManager(
            (mcpConfig.Servers ?? Array.Empty<McpServerConfig>()).ToArray(), _logger);
        // 并行连接所有 MCP server，工具适配器收集到 mcpManager.Adapters
        // TerminalApp.RunAsync 中统一注册到主 ToolRegistry
        await mcpManager.ConnectAllAsync(_ct);
    }

    using var terminalApp = new TerminalApp(_provider,
                                            _providerConfig,
                                            _config.Agent,
                                            tuiConfig,
                                            securityLevel,
                                            securityGuard,
                                            compressor,
                                            sessionStore,
                                            instructions,
                                            mcpManager,     // 11c 新增
                                            _logger,
                                            _ct);
    await terminalApp.RunAsync();

    // 程序退出时关闭 MCP 连接
    if (mcpManager is not null)
        await mcpManager.CloseAllAsync(_ct);
}
```

### 3.7 TerminalApp 扩展

```csharp
// Tui/TerminalApp.cs — 迭代 11c 扩展要点

internal sealed class TerminalApp : IUiControl, IDisposable
{
    private readonly McpConnectionManager? _mcpManager;  // 11c 新增

    public TerminalApp(/* 既有参数 */,
                       McpConnectionManager? mcpManager,  // 11c 新增
                       ILogger? logger,
                       CancellationToken ct)
    {
        // ... 既有赋值 ...
        _mcpManager = mcpManager;
    }

    public Task RunAsync()
    {
        // 1. 装配工具注册中心（内置工具）
        _registry = new ToolRegistry();
        _registry.Register(new ReadFileTool());
        _registry.Register(new WriteFileTool());
        _registry.Register(new EditFileTool());
        _registry.Register(new GlobTool());
        _registry.Register(new GrepTool());
        _registry.Register(new RunCommandTool());

        // 【迭代 11c】注册 MCP 工具
        if (_mcpManager is not null)
        {
            foreach (var adapter in _mcpManager.Adapters)
            {
                try { _registry.Register(adapter); }
                catch (ArgumentException ex)
                {
                    _logger?.LogWarning(ex, "MCP 工具注册失败（名称冲突）：{Name}", adapter.Name);
                }
            }
        }

        // ... 既有 Terminal.Gui 初始化 ...

        // 更新 Tab 补全数据源（含 MCP 工具名）
        _inputFieldView.SetCommands(_commandRegistry.GetAllNamesWithAliases());

        // ... 既有运行逻辑 ...
    }

    public void Dispose()
    {
        // MCP 连接由 App.RunAsync 中 CloseAllAsync 关闭，此处只清理 UI
        _top?.Dispose();
    }
}
```

### 3.8 StatusCommand 扩展

```csharp
// Commands/CommandContext.cs — 迭代 11c 新增字段

/// <summary>MCP 连接状态概要（/status 显示，11c 填充）。</summary>
public string? McpSummary { get; init; }
```

```csharp
// Commands/Builtin/StatusCommand.cs — 迭代 11c 扩展

// ExecuteAsync 中追加：
if (!string.IsNullOrEmpty(context.McpSummary))
{
    sb.AppendLine($"MCP: {context.McpSummary}");
}
```

`TerminalApp.BuildCommandContext` 中填充：

```csharp
private CommandContext BuildCommandContext()
{
    return new CommandContext(/* 既有参数 */)
    {
        // ... 既有字段 ...
        McpSummary = _mcpManager?.GetStatusSummary(),
    };
}
```

---

## 四、验收标准

### 4.1 编译与测试

| 编号 | 标准 | 验证方式 |
|------|------|---------|
| 11c-01 | `dotnet build` 0 错误 0 警告 | 编译 |
| 11c-02 | 全量测试全绿（含 11c 新增） | `dotnet test` |
| 11c-03 | `McpConnectionManagerTests` 全绿 | `dotnet test` |
| 11c-04 | 既有测试不回归（含 11a/11b） | `dotnet test` |

### 4.2 HTTP SSE 传输

| 编号 | 标准 | 验证方式 |
|------|------|---------|
| 11c-10 | `ConnectAsync` 初始化 HttpClient 和 Channel | 单测 |
| 11c-11 | `SendAsync` POST JSON 到 /mcp 端点 | 单测（mock HttpMessageHandler） |
| 11c-12 | SSE 流正确解析 `data:` 行 | 单测（模拟 SSE 响应） |
| 11c-13 | SSE 空行分隔符正确触发消息投递 | 单测 |
| 11c-14 | 单次 JSON 响应（非 SSE）正确处理 | 单测 |
| 11c-15 | Bearer Token 正确设置到 Authorization 头 | 单测 |
| 11c-16 | `CloseAsync` 取消接收循环 | 单测 |

### 4.3 连接管理器

| 编号 | 标准 | 验证方式 |
|------|------|---------|
| 11c-20 | `ConnectAllAsync` 并行连接所有 server | 单测（用 MockTransport 工厂） |
| 11c-21 | 单个 server 连接失败不阻塞其他 | 单测（1 个抛异常，2 个成功） |
| 11c-22 | 成功连接的 server 工具适配器收集到 Adapters | 单测 |
| 11c-23 | 空 server 列表时不报错 | 单测 |
| 11c-24 | `CloseAllAsync` 并行关闭所有 server | 单测 |
| 11c-25 | `GetStatusSummary` 返回正确的状态文本 | 单测（0/部分/全部连接） |

### 4.4 配置

| 编号 | 标准 | 验证方式 |
|------|------|---------|
| 11c-30 | `mcp:` 段正确解析为 `McpConfig` | 单测 |
| 11c-31 | 无 `mcp:` 段时用默认值（enable=true, servers=[]） | 单测 |
| 11c-32 | `mcp.enable: false` 时不连接任何 server | 单测 |
| 11c-33 | `McpServerConfig` stdio 字段正确解析 | 单测 |
| 11c-34 | `McpServerConfig` http 字段正确解析 | 单测 |

### 4.5 端到端

| 编号 | 标准 | 验证方式 |
|------|------|---------|
| 11c-40 | 配置一个 Stdio MCP server，能列出其工具 | 手动 |
| 11c-41 | MCP 工具在 AI 的工具列表可见 | 手动 |
| 11c-42 | AI 能自主调用 MCP 工具并拿到结果 | 手动 |
| 11c-43 | MCP server 子进程在程序退出时被干净关闭 | 手动（检查进程列表） |
| 11c-44 | MCP 工具 Write 类默认弹 HITL（Normal 模式） | 手动 |
| 11c-45 | MCP 工具 Read 类不弹 HITL | 手动 |
| 11c-46 | `/status` 显示 MCP server 连接状态 | 手动 |
| 11c-47 | 单个 MCP server 连接失败时程序正常启动（记日志） | 手动 |
| 11c-48 | MCP 工具调用结果正确显示在 ChatView | 手动 |
| 11c-49 | MCP 工具调用超时返回友好错误（AI 可自我修正） | 手动 |
| 11c-50 | 既有功能不受影响（内置工具/安全层/HITL/命令/会话/指令） | 手动回归 |

---

## 五、测试计划

| 测试文件 | 覆盖范围 | 预估用例数 |
|---------|---------|-----------|
| `McpConnectionManagerTests.cs` | 连接管理器 + HTTP SSE 解析 | ~15 |

**HTTP SSE 测试策略**：

用 `HttpMessageHandler` mock HTTP 响应：

```csharp
internal sealed class MockHttpMessageHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, Task<HttpResponseMessage>> _handler;

    public MockHttpMessageHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> handler)
        => _handler = handler;

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        => _handler(request);
}

[Fact]
public async Task SendAsync_SseResponse_ParsesDataLines()
{
    var sseContent = "data: {\"jsonrpc\":\"2.0\",\"id\":1,\"result\":{}}\n\n";
    var handler = new MockHttpMessageHandler(_ => Task.FromResult(new HttpResponseMessage
    {
        Content = new StringContent(sseContent, Encoding.UTF8, "text/event-stream")
    }));

    var config = new McpServerConfig { Name = "test", Transport = "http", Url = "http://localhost" };
    var transport = new HttpSseTransportWithHandler(handler, config);

    await transport.ConnectAsync(CancellationToken.None);
    await transport.SendAsync("{}", CancellationToken.None);

    var received = await transport.ReceiveAsync(CancellationToken.None);
    received.Should().Contain("\"id\":1");
}
```

**连接管理器测试策略**：

用可测试的 `McpClientFactory` 注入 mock client：

```csharp
[Fact]
public async Task ConnectAllAsync_OneServerFails_OthersStillConnect()
{
    var configs = new[]
    {
        new McpServerConfig { Name = "ok1", Transport = "stdio", Command = "echo" },
        new McpServerConfig { Name = "fail", Transport = "stdio", Command = "nonexistent" },
        new McpServerConfig { Name = "ok2", Transport = "stdio", Command = "echo" }
    };

    var manager = new McpConnectionManager(configs);
    await manager.ConnectAllAsync(CancellationToken.None);

    manager.ConnectedCount.Should().Be(2);  // ok1 + ok2, fail 跳过
}
```

**端到端手动测试清单**（对照 11c-40 ~ 11c-50）：

1. **Stdio MCP server 连接**：
   - 配置 `filesystem` MCP server（`npx -y @anthropic/mcp-server-filesystem /tmp`）
   - 启动程序，观察日志显示连接成功
   - 让 AI 调用 `filesystem/read_file` 读取一个文件

2. **MCP 工具 HITL 验证**：
   - 让 AI 调用 MCP Write 工具（如 `filesystem/write_file`）
   - Normal 模式下应弹 HITL 确认
   - Deny 后 AI 收到拒绝原因并调整

3. **连接失败容错**：
   - 配置一个不存在的 command（如 `nonexistent-mcp-server`）
   - 启动程序，应正常启动（日志有错误但不崩溃）
   - 其他正常 server 不受影响

4. **生命周期**：
   - 退出程序（/exit），检查 MCP server 子进程是否已退出

5. **综合回归**：
   - 内置工具正常（read_file / write_file / edit_file / glob / grep / run_command）
   - 安全层正常（/mode strict / normal / permissive）
   - HITL 正常
   - 命令系统正常（/help /clear /mode /compress /status /session /exit）
   - 会话持久化正常（/session save/load/list）
   - 项目指令正常

---

## 六、实施步骤

1. 新建 `Mcp/Transport/HttpSseTransport.cs`
2. 新建 `Mcp/McpConnectionManager.cs`
3. 新建 `Mcp/McpConfig.cs`
4. 修改 `Config/Models.cs`——新增 `AppConfig.Mcp`
5. 修改 `example.parrotcode.yaml`——新增 `mcp:` 配置节
6. 修改 `Commands/CommandContext.cs`——新增 `McpSummary`
7. 修改 `Commands/Builtin/StatusCommand.cs`——显示 MCP 状态
8. 修改 `App/App.cs`——构造 `McpConnectionManager`，连接 MCP server
9. 修改 `Tui/TerminalApp.cs`——注册 MCP 工具，构造函数加 `McpConnectionManager?`
10. 新建 `McpConnectionManagerTests.cs`（~15 用例）
11. 验证：`dotnet build` 0 警告 + `dotnet test` 全绿
12. 端到端手动验收（11c-40 ~ 11c-50）
13. 标记迭代 11c [已完成]，迭代 11 [已完成]

---

## 七、风险与对策

| 风险 | 概率 | 影响 | 对策 |
|------|------|------|------|
| MCP server 子进程僵死（不响应不退出） | 中 | 中 | CloseAsync 中 WaitForExit(3000) + Kill(entireProcessTree) 兜底 |
| HTTP SSE 流解析边界（多行 data / BOM） | 低 | 中 | 本迭代只处理单行 data；MCP spec 当前无多行 data 场景 |
| MCP spec 版本差异（2024-11-05 vs 2025-03-26） | 中 | 中 | `ProtocolVersion` 声明 2025-03-26；initialize 响应中检查 server 返回的版本 |
| Windows 上 npx 启动 MCP server 路径问题 | 中 | 中 | Args 传递原样；测试时用完整路径或 PATH 中的命令 |
| HttpClient 生命周期管理 | 低 | 低 | HttpSseTransport 实现 IAsyncDisposable，Dispose 中释放 HttpClient |
| 工具名冲突（两个 server 的工具前缀后同名） | 极低 | 低 | `{server}/{tool}` 前缀已避免；极端情况 TerminalApp 注册时 catch 记日志 |

---

## 八、关键设计决策

### Q1：为什么 HTTP SSE 传输用 Channel 而非直接 StreamReader？

- **解耦**：SendAsync（POST）触发 SSE 流响应，ReceiveAsync 从 Channel 读取——收发解耦
- **可测试**：MockTransport 可以用同样的 Channel 机制
- **多事件**：单个 POST 可能触发多个 SSE 事件（如进度通知），Channel 缓冲后逐个消费

### Q2：为什么连接管理器不直接注册工具到 ToolRegistry？

- **职责分离**：连接管理器负责连接和收集适配器，TerminalApp 负责注册到 Registry
- **可测试**：连接管理器测试不需要构造 ToolRegistry
- **统一注册**：TerminalApp 统一管理内置工具 + MCP 工具的注册，便于处理冲突

### Q3：为什么 App.cs 而非 TerminalApp 管理 MCP 连接？

- **生命周期对齐**：MCP 连接在 App 入口启动，在 App 退出时关闭，与 TerminalApp 的 UI 生命周期解耦
- **TerminalApp 可测性**：TerminalApp 接收已连接的 `McpConnectionManager`，不需要自己启动连接
- **资源清理**：`using var terminalApp` 确保 UI 先清理，然后 `await mcpManager.CloseAllAsync()` 确保网络/进程后清理

---

**文档结束**。状态：[设计完成，待实现]
