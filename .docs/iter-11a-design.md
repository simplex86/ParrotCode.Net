# 迭代 11a：JSON-RPC 协议层 + Stdio 传输

> **状态**：[设计完成，待实现]
> **前置迭代**：10c [已完成]（项目指令）
> **父文档**：[iter-11-design.md](iter-11-design.md)（总览）
> **后续子迭代**：11b（MCP 客户端 + 工具适配器，依赖本迭代的 JsonRpc + ITransport + MockTransport）
>
> **目标**：交付 MCP 的协议基础层——JSON-RPC 2.0 编解码 + id 匹配 + Stdio 子进程传输 + MockTransport（测试用）。本迭代不涉及 MCP 协议语义（initialize/tools/list 等在 11b），只交付"收发 JSON-RPC 消息"的能力。

---

## 一、迭代目标

### 1.1 核心目标

1. **JSON-RPC 2.0 编解码**：`CreateRequest` 生成带自增 id 的请求，`CreateNotification` 生成无 id 的通知，`HandleMessage` 按 id 匹配响应到 `TaskCompletionSource<JsonElement>`
2. **id 匹配的 Future 模式**：`ConcurrentDictionary<int, TaskCompletionSource<JsonElement>>` + `Interlocked.Increment` 保证并发安全
3. **传输层抽象**：`ITransport` 接口定义 `ConnectAsync` / `SendAsync` / `ReceiveAsync` / `CloseAsync`
4. **Stdio 传输**：`System.Diagnostics.Process` 重定向 stdin/stdout，stderr 独立线程收集日志（不污染 JSON-RPC 通道）
5. **MockTransport**：内存 Channel 实现的 ITransport，供 11b/11c 测试用
6. **MCP 方法常量与 record**：`McpMethods` 常量 + `McpToolInfo` / `McpToolCallResult` 等 record（11b 会用到）

### 1.2 本迭代要验证的关键问题

| 问题 | 验证方式 | 失败对策 |
|------|---------|---------|
| JSON-RPC id 匹配是否可靠（并发请求） | 单测：多请求并发，按 id 正确匹配响应 | `ConcurrentDictionary` + 自增 id |
| Stdio 传输 stderr 是否污染 JSON-RPC 通道 | 单测：server stderr 输出日志不干扰 | stderr 独立管道接收，不混入 stdout |
| 子进程是否干净关闭 | 单测：CloseAsync 后进程退出 | `CancellationToken` 触发 + `Process.Kill` 兜底 |
| 进程已退出时 ReceiveAsync 行为 | 单测：返回 null 表示连接关闭 | `IOException` 捕获转 null |
| 环境变量是否正确传递 | 单测：子进程读到父进程设置的环境变量 | `ProcessStartInfo.Environment` |
| CancelAllPending 是否取消所有等待请求 | 单测：连接关闭后所有 pending TCS 抛异常 | 遍历 SetException |
| 响应含 error 时是否正确抛异常 | 单测：JsonRpcException 含 code 和 message | error.code + error.message 提取 |

### 1.3 非目标（明确不做）

- ❌ 不做 MCP 协议语义（initialize / tools/list / tools/call）——11b
- ❌ 不做 MCP 工具适配（IBaseTool）——11b
- ❌ 不做 HTTP SSE 传输——11c
- ❌ 不做连接管理器——11c
- ❌ 不做配置 / App 装配——11c
- ❌ 不处理 Server → Client 通知（如 `tools/list_changed`）——可选扩展

---

## 二、文件改动清单

### 2.1 新增文件（5 个）

```
Mcp/
├── Protocol/
│   ├── JsonRpc.cs              # JSON-RPC 2.0 请求/响应/通知/错误码 + id 匹配
│   └── McpMethods.cs           # MCP 方法名常量 + 请求/响应 record
└── Transport/
    ├── ITransport.cs           # 传输层抽象接口
    ├── StdioTransport.cs       # Stdio 子进程传输
    └── MockTransport.cs        # 内存 Channel 传输（测试用，11b/11c 依赖）
```

测试文件（2 个）：

```
ParrotCode.Net-xUnit/
├── McpProtocolTests.cs         # JsonRpc 编解码 + id 匹配 + 并发 + CancelAllPending
└── McpTransportTests.cs        # StdioTransport 子进程管理 + MockTransport
```

### 2.2 修改文件

无。本迭代不修改任何既有文件——纯新增协议层和传输层，与既有代码零耦合。

### 2.3 不变文件

- `Tools/IBaseTool.cs` / `ToolRegistry.cs`——11b 才接入
- `Agent/AgentLoop.cs`——11c 才接入
- `App/App.cs` / `Tui/TerminalApp.cs`——11c 才接入

---

## 三、详细设计

### 3.1 JSON-RPC 2.0 协议概述

JSON-RPC 2.0 是无状态、轻量级的远程过程调用协议。MCP 基于 JSON-RPC 2.0 通信。

**消息类型**：
- **请求**（Request）：含 `jsonrpc` / `id` / `method` / `params`，期望响应
- **响应**（Response）：含 `jsonrpc` / `id` / `result`（或 `error`），匹配请求的 id
- **通知**（Notification）：含 `jsonrpc` / `method` / `params`，无 `id`，不期望响应

**消息格式示例**：

```json
// 请求
{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-03-26"}}

// 成功响应
{"jsonrpc":"2.0","id":1,"result":{"protocolVersion":"2025-03-26","capabilities":{...}}}

// 错误响应
{"jsonrpc":"2.0","id":1,"error":{"code":-32601,"message":"Method not found"}}

// 通知（无 id）
{"jsonrpc":"2.0","method":"notifications/initialized"}
```

**id 匹配机制**：Client 发请求时分配自增 id，存入 `ConcurrentDictionary<id, TaskCompletionSource>`。收到响应时按 id 查找对应的 TCS，`SetResult`（成功）或 `SetException`（错误）。

### 3.2 JsonRpc 类

```csharp
// Mcp/Protocol/JsonRpc.cs
using System.Text.Json;
using System.Threading;

namespace ParrotCode;

/// <summary>
/// JSON-RPC 2.0 编解码 + Future 匹配。
/// 每个请求带自增 id，响应按 id 匹配到 TaskCompletionSource&lt;JsonElement&gt;。
/// 线程安全：ConcurrentDictionary + Interlocked.Increment。
/// </summary>
internal sealed class JsonRpc
{
    private int _nextId = 1;
    private readonly ConcurrentDictionary<int, TaskCompletionSource<JsonElement>> _pending = new();
    private readonly ILogger? _logger;

    public JsonRpc(ILogger? logger = null) => _logger = logger;

    /// <summary>
    /// 创建带自增 id 的请求，返回 (JSON 字符串, 等待响应的 Task)。
    /// 调用方发送 JSON 字符串后 await Task 等待响应。
    /// </summary>
    public (string Json, Task<JsonElement> ResponseTask) CreateRequest(string method, object? @params = null)
    {
        var id = Interlocked.Increment(ref _nextId);
        var tcs = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = tcs;

        var request = new
        {
            jsonrpc = "2.0",
            id,
            method,
            @params
        };
        var json = JsonSerializer.Serialize(request);

        return (json, tcs.Task);
    }

    /// <summary>创建通知（无 id，不期望响应）。</summary>
    public string CreateNotification(string method, object? @params = null)
    {
        var notification = new
        {
            jsonrpc = "2.0",
            method,
            @params
        };
        return JsonSerializer.Serialize(notification);
    }

    /// <summary>
    /// 处理从 transport 收到的 JSON 消息，按类型分发。
    /// - 有 id 且有 result/error → 响应，匹配到 pending TCS
    /// - 有 method 无 id → 通知（本迭代不处理，记日志）
    /// </summary>
    public void HandleMessage(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        // 有 id 且有 result/error → 响应
        if (root.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.Number)
        {
            var id = idEl.GetInt32();
            if (_pending.TryRemove(id, out var tcs))
            {
                if (root.TryGetProperty("error", out var error))
                {
                    var message = error.TryGetProperty("message", out var msgEl)
                        ? msgEl.GetString() ?? "Unknown error" : "Unknown error";
                    var code = error.TryGetProperty("code", out var codeEl) ? codeEl.GetInt32() : -1;
                    tcs.SetException(new JsonRpcException(code, message));
                }
                else if (root.TryGetProperty("result", out var result))
                {
                    tcs.SetResult(result.Clone());
                }
                else
                {
                    tcs.SetException(new JsonRpcException(-1, "响应缺少 result 和 error 字段"));
                }
            }
            else
            {
                _logger?.LogWarning("收到未知 id={Id} 的 JSON-RPC 响应", id);
            }
        }
        // 有 method 无 id → 通知
        else if (root.TryGetProperty("method", out _))
        {
            // 本迭代不处理 Server → Client 通知（如 tools/list_changed）
            _logger?.LogDebug("收到 JSON-RPC 通知，暂不处理：{Json}", json);
        }
    }

    /// <summary>取消所有等待中的请求（transport 关闭时调用）。</summary>
    public void CancelAllPending()
    {
        foreach (var kvp in _pending)
        {
            kvp.Value.TrySetException(new JsonRpcException(-1, "连接已关闭"));
        }
        _pending.Clear();
    }
}

/// <summary>JSON-RPC 错误异常。</summary>
public sealed class JsonRpcException : Exception
{
    public int Code { get; }
    public JsonRpcException(int code, string message) : base($"JSON-RPC 错误 [{code}]: {message}") => Code = code;
}
```

### 3.3 MCP 方法常量与 record

```csharp
// Mcp/Protocol/McpMethods.cs
using System.Text.Json;

namespace ParrotCode;

/// <summary>MCP 方法名常量。</summary>
internal static class McpMethods
{
    public const string Initialize = "initialize";
    public const string Initialized = "notifications/initialized";
    public const string ToolsList = "tools/list";
    public const string ToolsCall = "tools/call";
}

/// <summary>MCP initialize 请求参数。</summary>
internal sealed record McpInitializeParams
{
    public string ProtocolVersion { get; init; } = "2025-03-26";
    public McpClientCapabilities Capabilities { get; init; } = new();
    public McpClientInfo ClientInfo { get; init; } = new();
}

internal sealed record McpClientCapabilities
{
    // 本迭代不声明任何能力（tools 只需 server 端声明）
}

internal sealed record McpClientInfo
{
    public string Name { get; init; } = "ParrotCode.Net";
    public string Version { get; init; } = "0.11.0";
}

/// <summary>MCP tools/list 响应中的工具描述。</summary>
public sealed record McpToolInfo
{
    public string Name { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public JsonElement InputSchema { get; init; }
    /// <summary>MCP 协议的 annotations 字段（可能含 readOnlyHint）。</summary>
    public McpToolAnnotations? Annotations { get; init; }
}

/// <summary>MCP 工具注解（2025-03-26 spec 新增）。</summary>
public sealed record McpToolAnnotations
{
    /// <summary>提示此工具是否为只读（无副作用）。null 时默认 false（安全优先）。</summary>
    public bool? ReadOnlyHint { get; init; }
    /// <summary>提示此工具是否可能破坏性操作。null 时默认 false。</summary>
    public bool? DestructiveHint { get; init; }
}

/// <summary>MCP tools/call 请求参数。</summary>
internal sealed record McpToolCallParams
{
    public string Name { get; init; } = string.Empty;
    public JsonElement Arguments { get; init; }
}

/// <summary>MCP tools/call 响应。</summary>
public sealed record McpToolCallResult
{
    /// <summary>工具调用结果内容列表（可能含多个 text/image/resource）。</summary>
    public IReadOnlyList<McpContentBlock> Content { get; init; } = Array.Empty<McpContentBlock>();
    /// <summary>是否调用出错。</summary>
    public bool IsError { get; init; }
}

/// <summary>MCP 内容块（text 类型用于工具结果）。</summary>
public sealed record McpContentBlock
{
    public string Type { get; init; } = "text";
    public string Text { get; init; } = string.Empty;
}
```

### 3.4 传输层抽象

```csharp
// Mcp/Transport/ITransport.cs
namespace ParrotCode;

/// <summary>
/// MCP 传输层抽象。上层（McpClient）通过此接口收发 JSON-RPC 消息。
/// 职责：序列化/反序列化由 JsonRpc 层负责；Transport 只负责"发送 JSON 字符串 + 接收 JSON 字符串"。
/// </summary>
internal interface ITransport : IAsyncDisposable
{
    /// <summary>启动传输（连接 server / 启动子进程）。成功后可 Send/Receive。</summary>
    Task ConnectAsync(CancellationToken cancellationToken);

    /// <summary>发送 JSON-RPC 消息。</summary>
    Task SendAsync(string json, CancellationToken cancellationToken);

    /// <summary>接收一条 JSON-RPC 消息。返回 null 表示连接关闭。</summary>
    Task<string?> ReceiveAsync(CancellationToken cancellationToken);

    /// <summary>关闭传输（发送关闭通知 + 关闭连接/进程）。</summary>
    Task CloseAsync(CancellationToken cancellationToken);
}
```

### 3.5 Stdio 传输

```csharp
// Mcp/Transport/StdioTransport.cs
using System.Diagnostics;
using System.IO;
using Microsoft.Extensions.Logging;

namespace ParrotCode;

/// <summary>
/// Stdio 传输：通过子进程的 stdin/stdout 通信，stderr 独立收集日志。
/// 子进程在 ConnectAsync 启动，CloseAsync 关闭。
/// 
/// 关键设计：
/// - stdin 写入 JSON-RPC 消息（每条一行）
/// - stdout 读取 JSON-RPC 响应（每条一行）
/// - stderr 独立线程读取，输出到日志（不混入 JSON-RPC 通道）
/// - CloseAsync 先关闭 stdin 触发 server 优雅退出，超时 3 秒后 Kill
/// </summary>
internal sealed class StdioTransport : ITransport
{
    private readonly McpServerConfig _config;
    private readonly ILogger? _logger;
    private Process? _process;
    private StreamReader? _stdoutReader;
    private StreamWriter? _stdinWriter;

    public StdioTransport(McpServerConfig config, ILogger? logger = null)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _logger = logger;
    }

    public Task ConnectAsync(CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = _config.Command,
            Arguments = _config.Args ?? string.Empty,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = _config.WorkingDir ?? string.Empty
        };

        // 设置环境变量
        if (_config.Env is not null)
        {
            foreach (var (key, value) in _config.Env)
                startInfo.Environment[key] = value;
        }

        _process = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"无法启动 MCP server：{_config.Command}");

        _stdoutReader = _process.StandardOutput;
        _stdinWriter = _process.StandardInput;
        _stdinWriter.AutoFlush = true;

        // stderr 独立线程收集（不污染 JSON-RPC 通道）
        _ = Task.Run(async () =>
        {
            try
            {
                while (!_process.StandardError.EndOfStream)
                {
                    var line = await _process.StandardError.ReadLineAsync(cancellationToken);
                    if (line is not null)
                        _logger?.LogDebug("MCP server [{Name}] stderr: {Line}", _config.Name, line);
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                _logger?.LogDebug("MCP server [{Name}] stderr 读取结束：{Error}", _config.Name, ex.Message);
            }
        }, cancellationToken);

        _logger?.LogInformation("MCP Stdio server [{Name}] 已启动 (PID={Pid})", _config.Name, _process.Id);
        return Task.CompletedTask;
    }

    public async Task SendAsync(string json, CancellationToken cancellationToken)
    {
        if (_stdinWriter is null) throw new InvalidOperationException("Transport 未连接");
        await _stdinWriter.WriteLineAsync(json.AsMemory(), cancellationToken);
    }

    public async Task<string?> ReceiveAsync(CancellationToken cancellationToken)
    {
        if (_stdoutReader is null) throw new InvalidOperationException("Transport 未连接");
        try
        {
            var line = await _stdoutReader.ReadLineAsync(cancellationToken);
            return line;
        }
        catch (IOException)
        {
            return null;  // 进程已退出
        }
    }

    public async Task CloseAsync(CancellationToken cancellationToken)
    {
        if (_process is null || _process.HasExited) return;

        _logger?.LogInformation("MCP Stdio server [{Name}] 正在关闭", _config.Name);

        // 关闭 stdin 触发 server 优雅退出
        try { _stdinWriter?.Close(); } catch { }

        // 等待进程退出（最多 3 秒）
        if (!_process.WaitForExit(3000))
        {
            _logger?.LogWarning("MCP server [{Name}] 未在 3 秒内退出，强制终止", _config.Name);
            try { _process.Kill(entireProcessTree: true); } catch { }
        }

        _logger?.LogInformation("MCP Stdio server [{Name}] 已关闭 (exit={Code})", _config.Name, _process.ExitCode);
    }

    public ValueTask DisposeAsync()
    {
        try { _process?.Dispose(); } catch { }
        return ValueTask.CompletedTask;
    }
}
```

> **注**：`McpServerConfig` 在 11c 定义。为避免 11a 对 11c 的依赖，`StdioTransport` 接收的 `McpServerConfig` 可在 11a 中先定义一个最小版本（仅含 `Name` / `Command` / `Args` / `WorkingDir` / `Env` 字段），11c 扩展为完整版本（增加 `Transport` / `Url` / `ApiKey`）。或者 11a 直接定义完整的 `McpServerConfig`（反正 record 可扩展），11c 只增加 `McpConfig`。**推荐方案**：11a 定义完整的 `McpServerConfig` record（所有字段），11c 定义 `McpConfig` + 接入 `AppConfig`。这样 11a 自包含。

**修正**：`McpServerConfig` 在 11a 定义：

```csharp
// Mcp/Protocol/McpMethods.cs 追加（或单独文件 Mcp/McpServerConfig.cs）

/// <summary>
/// 单个 MCP server 配置。
/// transport=stdio 时需要 command + args；
/// transport=http 时需要 url；
/// 两者都需要 name。
/// </summary>
public sealed record McpServerConfig
{
    public string Name { get; init; } = string.Empty;
    public string Transport { get; init; } = "stdio";
    public string? Command { get; init; }
    public string? Args { get; init; }
    public string? WorkingDir { get; init; }
    public IReadOnlyDictionary<string, string>? Env { get; init; }
    public string? Url { get; init; }
    public string? ApiKey { get; init; }
}
```

### 3.6 MockTransport（测试用）

```csharp
// Mcp/Transport/MockTransport.cs
using System.Threading.Channels;

namespace ParrotCode;

/// <summary>
/// 内存传输 mock：写入的消息排队，可预设响应。
/// 供 11b/11c 的 McpClient / McpConnectionManager 测试用，不启动真实进程/网络。
/// 
/// 用法：
///   var mock = new MockTransport();
///   mock.EnqueueResponse(@"{""jsonrpc"":""2.0"",""id"":1,""result"":{...}}");
///   await mock.SendAsync(requestJson, ct);  // Client 发送
///   var sent = await mock.GetLastSentAsync(ct);  // 测试验证发送内容
///   var received = await mock.ReceiveAsync(ct);  // Client 接收预设响应
/// </summary>
internal sealed class MockTransport : ITransport
{
    private readonly Channel<string> _sendChannel = Channel.CreateUnbounded<string>();
    private readonly Channel<string> _receiveChannel = Channel.CreateUnbounded<string>();

    public Task ConnectAsync(CancellationToken ct) => Task.CompletedTask;

    public async Task SendAsync(string json, CancellationToken ct)
        => await _sendChannel.Writer.WriteAsync(json, ct);

    public async Task<string?> ReceiveAsync(CancellationToken ct)
    {
        if (await _receiveChannel.Reader.WaitToReadAsync(ct) && _receiveChannel.Reader.TryRead(out var msg))
            return msg;
        return null;
    }

    public Task CloseAsync(CancellationToken ct)
    {
        _sendChannel.Writer.TryComplete();
        _receiveChannel.Writer.TryComplete();
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        _sendChannel.Writer.TryComplete();
        _receiveChannel.Writer.TryComplete();
        return ValueTask.CompletedTask;
    }

    // ===== 测试辅助方法 =====

    /// <summary>获取 Client 发送的最近一条消息（阻塞等待）。</summary>
    public async Task<string> GetLastSentAsync(CancellationToken ct)
        => await _sendChannel.Reader.ReadAsync(ct);

    /// <summary>预设响应（Client ReceiveAsync 会读到）。</summary>
    public void EnqueueResponse(string json)
        => _receiveChannel.Writer.TryWrite(json);

    /// <summary>关闭接收通道，模拟连接断开（ReceiveAsync 返回 null）。</summary>
    public void SimulateDisconnect()
        => _receiveChannel.Writer.TryComplete();
}
```

---

## 四、验收标准

### 4.1 编译与测试

| 编号 | 标准 | 验证方式 |
|------|------|---------|
| 11a-01 | `dotnet build` 0 错误 0 警告 | 编译 |
| 11a-02 | 全量测试全绿（含 11a 新增） | `dotnet test` |
| 11a-03 | `McpProtocolTests` 全绿 | `dotnet test` |
| 11a-04 | `McpTransportTests` 全绿 | `dotnet test` |
| 11a-05 | 既有测试不回归 | `dotnet test` |

### 4.2 JsonRpc 编解码

| 编号 | 标准 | 验证方式 |
|------|------|---------|
| 11a-10 | `CreateRequest` 生成合法 JSON-RPC 请求（含 jsonrpc/id/method/params） | 单测 |
| 11a-11 | `CreateRequest` 生成的 id 自增且不重复 | 单测 |
| 11a-12 | `CreateRequest` params=null 时不包含 params 字段或为 null | 单测 |
| 11a-13 | `CreateNotification` 生成无 id 的通知 | 单测 |
| 11a-14 | `CreateNotification` 不返回 Task（无响应等待） | 单测 |

### 4.3 JsonRpc id 匹配

| 编号 | 标准 | 验证方式 |
|------|------|---------|
| 11a-20 | `HandleMessage` 按 id 匹配到 pending TCS 并 SetResult | 单测 |
| 11a-21 | 响应含 error 时 TCS 抛 `JsonRpcException`（含 code 和 message） | 单测 |
| 11a-22 | 响应缺 result 和 error 时 TCS 抛异常 | 单测 |
| 11a-23 | 收到未知 id 的响应时记日志不崩溃 | 单测 |
| 11a-24 | 并发请求（多 id）正确匹配各自响应 | 单测 |
| 11a-25 | `CancelAllPending` 取消所有等待中的请求（TCS 抛异常） | 单测 |
| 11a-26 | `CancelAllPending` 后 _pending 字典为空 | 单测 |

### 4.4 JsonRpc 通知处理

| 编号 | 标准 | 验证方式 |
|------|------|---------|
| 11a-30 | 收到通知（有 method 无 id）时记日志不崩溃 | 单测 |
| 11a-31 | 通知不触发任何 TCS | 单测 |

### 4.5 StdioTransport

| 编号 | 标准 | 验证方式 |
|------|------|---------|
| 11a-40 | `ConnectAsync` 启动子进程并重定向 stdin/stdout/stderr | 单测（用 `cmd /c echo` mock） |
| 11a-41 | `SendAsync` 向 stdin 写入一行 JSON | 单测 |
| 11a-42 | `ReceiveAsync` 从 stdout 读取一行 JSON | 单测 |
| 11a-43 | stderr 输出被收集到日志，不混入 JSON-RPC 通道 | 单测 |
| 11a-44 | `CloseAsync` 关闭 stdin，等待进程退出 | 单测 |
| 11a-45 | 进程未在 3 秒内退出时 `CloseAsync` 强制 Kill | 单测（用 `cmd /c timeout 10` mock） |
| 11a-46 | 进程已退出时 `ReceiveAsync` 返回 null | 单测 |
| 11a-47 | 环境变量正确传递给子进程 | 单测（子进程 echo 环境变量） |
| 11a-48 | 工作目录正确设置 | 单测 |
| 11a-49 | `ConnectAsync` 进程启动失败时抛 `InvalidOperationException` | 单测（不存在的命令） |

### 4.6 MockTransport

| 编号 | 标准 | 验证方式 |
|------|------|---------|
| 11a-55 | `SendAsync` 写入的消息可通过 `GetLastSentAsync` 读取 | 单测 |
| 11a-56 | `EnqueueResponse` 预设的响应可通过 `ReceiveAsync` 读取 | 单测 |
| 11a-57 | `SimulateDisconnect` 后 `ReceiveAsync` 返回 null | 单测 |
| 11a-58 | `CloseAsync` 后通道完成 | 单测 |

---

## 五、测试计划

| 测试文件 | 覆盖范围 | 预估用例数 |
|---------|---------|-----------|
| `McpProtocolTests.cs` | JsonRpc CreateRequest/CreateNotification/HandleMessage/并发/CancelAllPending/JsonRpcException | ~13 |
| `McpTransportTests.cs` | StdioTransport 子进程管理 + MockTransport | ~10 |

**StdioTransport 测试策略**：

StdioTransport 测试需要启动真实子进程。用 Windows 的 `cmd.exe` 或跨平台的 `echo` / `cat` 命令 mock MCP server：

```csharp
// 示例：测试 SendAsync + ReceiveAsync
[Fact]
public async Task SendAsync_WriteJsonToStdin_ReceiveAsync_ReadsFromStdout()
{
    // 用 cmd /c echo mock：子进程 echo 一行 JSON 到 stdout
    var config = new McpServerConfig
    {
        Name = "test",
        Command = "cmd",
        Args = "/c echo {\"jsonrpc\":\"2.0\",\"id\":1,\"result\":{}}"
    };
    var transport = new StdioTransport(config);

    await transport.ConnectAsync(CancellationToken.None);
    var received = await transport.ReceiveAsync(CancellationToken.None);

    received.Should().Contain("\"id\":1");
    await transport.CloseAsync(CancellationToken.None);
}
```

---

## 六、实施步骤

1. 新建 `Mcp/Protocol/JsonRpc.cs`（含 `JsonRpcException`）
2. 新建 `Mcp/Protocol/McpMethods.cs`（方法常量 + record + `McpServerConfig`）
3. 新建 `Mcp/Transport/ITransport.cs`
4. 新建 `Mcp/Transport/StdioTransport.cs`
5. 新建 `Mcp/Transport/MockTransport.cs`
6. 新建 `McpProtocolTests.cs`（~13 用例）
7. 新建 `McpTransportTests.cs`（~10 用例）
8. 验证：`dotnet build` 0 警告 + `dotnet test` 全绿
9. 标记迭代 11a [已完成]

---

## 七、风险与对策

| 风险 | 概率 | 影响 | 对策 |
|------|------|------|------|
| Stdio 传输 stdout 缓冲导致消息截断 | 低 | 高 | 子进程 stdin 设 AutoFlush；server 端应行缓冲（每条 JSON-RPC 消息一行） |
| Windows 上 `cmd /c` 测试跨平台兼容性 | 中 | 低 | 测试用 `OperatingSystem.IsWindows()` 条件跳过，或用跨平台 mock 命令 |
| `ReadLineAsync` 在进程退出时阻塞 | 低 | 中 | 进程退出时 stdout 流关闭，`ReadLineAsync` 返回 null |
| stderr 读取线程未正确退出 | 低 | 低 | `OperationCanceledException` 捕获 + `EndOfStream` 检查 |
| `ConcurrentDictionary` 遍历时并发修改 | 低 | 低 | `CancelAllPending` 先遍历再 Clear，ConcurrentDictionary 枚举是快照 |

---

## 八、关键设计决策

### Q1：为什么 id 用 `int` 自增而非 `string` UUID？

- **简单**：自增 int 足够唯一，无需 UUID 复杂度
- **JSON 紧凑**：`"id":1` 比 `"id":"550e8400-e29b-41d4-a716-446655440000"` 省字节
- **MCP spec 允许**：JSON-RPC 2.0 的 id 可以是 string / number / null，int 属于 number
- **`Interlocked.Increment` 保证原子性**：无需锁

### Q2：为什么用 `TaskCompletionSource<JsonElement>` 而非 `Task<JsonElement>`？

- `TaskCompletionSource` 允许外部 `SetResult` / `SetException`——`HandleMessage` 在收到响应时完成它
- `Task<JsonElement>` 不能从外部完成
- `RunContinuationsAsynchronously` 避免同步回调导致的重入问题

### Q3：为什么 stderr 独立线程读取？

- **不污染 JSON-RPC 通道**：stdout 只走 JSON-RPC 消息，stderr 只走日志
- **MCP server 通常用 stderr 输出日志**（如 npx 的安装进度、Node.js 的 console.error）
- **独立线程避免阻塞**：stderr 可能有大量输出，不能阻塞 JSON-RPC 接收循环

### Q4：为什么 MockTransport 放在主项目而非测试项目？

- `MockTransport` 标记为 `internal`，但需要在 11b/11c 的测试中使用
- 放在主项目的 `Mcp/Transport/` 下，`InternalsVisibleTo` 让测试项目可见
- 替代方案：放在测试项目——但那样 11b/11c 测试需要重复定义

---

**文档结束**。状态：[设计完成，待实现]
