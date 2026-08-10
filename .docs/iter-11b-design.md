# 迭代 11b：MCP 客户端 + 工具适配器

> **状态**：[设计完成，待实现]
> **前置迭代**：11a [已完成]（JSON-RPC 协议层 + Stdio 传输）
> **父文档**：[iter-11-design.md](iter-11-design.md)（总览）
> **后续子迭代**：11c（HTTP SSE 传输 + 连接管理器 + 端到端装配）
>
> **目标**：交付 MCP 客户端生命周期管理（initialize → initialized → tools/list → tools/call）和 MCP 工具到 IBaseTool 的适配器。本迭代用 11a 的 MockTransport 测试完整 MCP 流程，不依赖真实 MCP server。

---

## 一、迭代目标

### 1.1 核心目标

1. **MCP 客户端**：`McpClient` 管理单个 MCP server 的完整生命周期
   - `ConnectAsync`：transport.Connect → 启动接收循环 → initialize 握手 → initialized 通知 → tools/list
   - `RefreshToolsAsync`：刷新工具列表
   - `CallToolAsync`：调用 MCP 工具
   - `CloseAsync`：停止接收循环 + 关闭 transport + 取消 pending
2. **接收循环**：后台 Task 持续从 transport 读取消息，分发给 `JsonRpc.HandleMessage`
3. **MCP 工具适配器**：`McpToolAdapter : IBaseTool`，把 MCP 工具包装成内置工具接口
   - 工具名前缀：`{serverName}/{toolName}` 防多 server 冲突
   - Category 判定：根据 `annotations.readOnlyHint`，无注解默认 Write（安全优先）
   - 参数解析：从 MCP InputSchema（JSON Schema）解析为 `ToolParameter` 列表
4. **工具结果转换**：`McpToolCallResult` → `ToolResult`（拼接 text content，处理 isError）

### 1.2 本迭代要验证的关键问题

| 问题 | 验证方式 | 失败对策 |
|------|---------|---------|
| initialize 握手流程是否正确 | 单测：mock transport 预设 initialize 响应 | 检查 protocolVersion / capabilities 解析 |
| initialized 通知是否发送 | 单测：验证 mock transport 收到通知 | CreateNotification + SendAsync |
| tools/list 响应是否正确解析为 McpToolInfo | 单测：预设含 tools 数组的响应 | ParseToolsList 解析逻辑 |
| tools/call 是否正确发送请求并解析响应 | 单测：预设 call 响应 | ParseToolCallResult 解析逻辑 |
| 接收循环是否正确分发消息到 JsonRpc | 单测：mock transport 入队响应，验证 TCS 完成 | ReceiveLoopAsync 后台 Task |
| 接收循环异常时是否取消所有 pending | 单测：模拟 transport 异常 | CancelAllPending in finally |
| initialize 超时是否处理 | 单测：mock transport 不响应 | WaitAsync(30s) 超时 |
| tools/call 超时是否处理 | 单测：mock transport 不响应 | WaitAsync(60s) 超时 |
| 工具名前缀格式是否正确 | 单测：`{server}/{tool}` | Name 属性 |
| Category 判定（有 readOnlyHint / 无注解） | 单测：两种情况 | annotations.readOnlyHint 检查 |
| Parameters 从 InputSchema 解析 | 单测：含 properties / required 的 schema | ParseParameters |
| ToOpenAiSchema / ToAnthropicSchema 生成 | 单测：验证 schema 结构 | 透传 InputSchema |
| ExecuteAsync 成功/失败转换 | 单测：isError=true / 正常 | ToolResult.Ok / ToolResult.Fail |

### 1.3 非目标（明确不做）

- ❌ 不做 HTTP SSE 传输——11c
- ❌ 不做连接管理器（并行连接多 server）——11c
- ❌ 不做配置 / App 装配 / TerminalApp 集成——11c
- ❌ 不做真实 MCP server 端到端测试——11c
- ❌ 不处理 `tools/list_changed` 通知——可选扩展

---

## 二、文件改动清单

### 2.1 新增文件（2 个）

```
Mcp/
├── McpClient.cs                # MCP 客户端（initialize → tools/list → tools/call + 接收循环）
└── McpToolAdapter.cs           # MCP 工具 → IBaseTool 适配
```

测试文件（1 个）：

```
ParrotCode.Net-xUnit/
└── McpClientTests.cs           # McpClient 生命周期 + McpToolAdapter 适配
```

### 2.2 修改文件

无。本迭代纯新增，依赖 11a 的 `JsonRpc` / `ITransport` / `MockTransport` / `McpMethods` / `McpServerConfig`。

### 2.3 不变文件

- `Tools/IBaseTool.cs` / `ToolRegistry.cs`——`McpToolAdapter` 实现 `IBaseTool`，不改接口
- `Agent/AgentLoop.cs`——11c 才接入
- `App/App.cs` / `Tui/TerminalApp.cs`——11c 才接入

---

## 三、详细设计

### 3.1 MCP 客户端生命周期概述

```
ConnectAsync 流程：
  ┌─────────────────────────────────────────────────────┐
  │ 1. transport.ConnectAsync (启动子进程/HTTP连接)      │
  │ 2. 启动接收循环 (后台 Task)                          │
  │ 3. initialize 请求 → 等待响应 (30s 超时)             │
  │ 4. initialized 通知 (无响应)                         │
  │ 5. tools/list 请求 → 等待响应 (10s 超时)             │
  │ 6. 解析工具列表 → _tools                             │
  │ 7. IsInitialized = true                             │
  └─────────────────────────────────────────────────────┘

CallToolAsync 流程：
  ┌─────────────────────────────────────────────────────┐
  │ 1. CreateRequest(tools/call, {name, arguments})     │
  │ 2. transport.SendAsync (发送 JSON)                  │
  │ 3. await responseTask (60s 超时)                    │
  │ 4. 解析响应 → McpToolCallResult                     │
  └─────────────────────────────────────────────────────┘

CloseAsync 流程：
  ┌─────────────────────────────────────────────────────┐
  │ 1. 取消接收循环 (CancellationToken)                  │
  │ 2. transport.CloseAsync (关闭子进程/HTTP连接)        │
  │ 3. 等待接收循环结束                                  │
  │ 4. CancelAllPending (取消未完成的请求)               │
  │ 5. IsInitialized = false                            │
  └─────────────────────────────────────────────────────┘
```

### 3.2 McpClient 类

```csharp
// Mcp/McpClient.cs
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace ParrotCode;

/// <summary>
/// MCP 客户端：管理单个 MCP server 的完整生命周期。
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

    /// <summary>已发现的工具列表。</summary>
    public IReadOnlyList<McpToolInfo> Tools => _tools;

    /// <summary>Server 名称。</summary>
    public string ServerName => _serverName;

    /// <summary>是否已连接并初始化。</summary>
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

    /// <summary>刷新工具列表（调用 tools/list）。</summary>
    public async Task RefreshToolsAsync(CancellationToken cancellationToken)
    {
        var (json, task) = _rpc.CreateRequest(McpMethods.ToolsList);
        await _transport.SendAsync(json, cancellationToken);

        var result = await task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
        _tools = ParseToolsList(result);
    }

    /// <summary>调用 MCP 工具。</summary>
    public async Task<McpToolCallResult> CallToolAsync(string toolName, JsonElement arguments, CancellationToken cancellationToken)
    {
        var @params = new McpToolCallParams { Name = toolName, Arguments = arguments };
        var (json, task) = _rpc.CreateRequest(McpMethods.ToolsCall, @params);
        await _transport.SendAsync(json, cancellationToken);

        var result = await task.WaitAsync(TimeSpan.FromSeconds(60), cancellationToken);
        return ParseToolCallResult(result);
    }

    /// <summary>关闭 MCP 客户端。</summary>
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

    /// <summary>接收循环：从 transport 读取消息，分发给 JsonRpc 处理。</summary>
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

    /// <summary>解析 tools/list 响应。</summary>
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
                    ReadOnlyHint = a.TryGetProperty("readOnlyHint", out var ro) ? (bool?)ro.GetBoolean() : null,
                    DestructiveHint = a.TryGetProperty("destructiveHint", out var dh) ? (bool?)dh.GetBoolean() : null
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

    /// <summary>解析 tools/call 响应。</summary>
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
```

### 3.3 McpToolAdapter 类

```csharp
// Mcp/McpToolAdapter.cs
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace ParrotCode;

/// <summary>
/// MCP 工具 → IBaseTool 适配器。
/// 把 MCP server 暴露的单个工具包装成 IBaseTool，注册到 ToolRegistry，
/// AgentLoop 和 BatchToolExecutor 透明调用（不感知 MCP vs 内置）。
/// 
/// 工具名前缀：{serverName}/{toolName}，防多 server 冲突。
/// Category 判定：根据 MCP annotations.readOnlyHint，无注解默认 Write（安全优先）。
/// </summary>
public sealed class McpToolAdapter : IBaseTool
{
    private readonly McpClient _client;
    private readonly McpToolInfo _toolInfo;
    private readonly ILogger? _logger;

    /// <summary>全局工具名（含 server 前缀）：{serverName}/{toolName}。</summary>
    public string Name => $"{_client.ServerName}/{_toolInfo.Name}";

    public string Description => _toolInfo.Description;

    /// <summary>
    /// 工具分类：annotations.readOnlyHint=true → Read；否则 Write（安全优先）。
    /// MCP 工具副作用不确定，无注解时默认 Write，让安全层和 HITL 覆盖。
    /// </summary>
    public ToolCategory Category =>
        _toolInfo.Annotations?.ReadOnlyHint == true ? ToolCategory.Read : ToolCategory.Write;

    /// <summary>
    /// 参数列表：从 MCP InputSchema（JSON Schema）解析。
    /// 仅提取顶层 properties 的 name/type/description/required，
    /// 不做嵌套 object 递归（MCP 工具参数通常是扁平结构）。
    /// </summary>
    public IReadOnlyList<ToolParameter> Parameters => _parameters ??= ParseParameters();

    private IReadOnlyList<ToolParameter>? _parameters;

    public McpToolAdapter(McpClient client, McpToolInfo toolInfo, ILogger? logger = null)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _toolInfo = toolInfo ?? throw new ArgumentNullException(nameof(toolInfo));
        _logger = logger;
    }

    /// <summary>
    /// 执行 MCP 工具调用：委托 McpClient.CallToolAsync，将结果转为 ToolResult。
    /// </summary>
    public async Task<ToolResult> ExecuteAsync(JsonElement input, CancellationToken cancellationToken)
    {
        try
        {
            var result = await _client.CallToolAsync(_toolInfo.Name, input, cancellationToken);

            if (result.IsError)
            {
                var errorText = string.Join("\n", result.Content.Where(c => c.Type == "text").Select(c => c.Text));
                return ToolResult.Fail(string.IsNullOrWhiteSpace(errorText) ? "MCP 工具调用失败" : errorText);
            }

            var contentText = string.Join("\n", result.Content.Where(c => c.Type == "text").Select(c => c.Text));
            return ToolResult.Ok(contentText);
        }
        catch (JsonRpcException ex)
        {
            _logger?.LogWarning(ex, "MCP 工具 {Name} 调用失败", Name);
            return ToolResult.Fail($"MCP 工具 {Name} 调用失败：{ex.Message}");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;  // 外部取消透传
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "MCP 工具 {Name} 执行异常", Name);
            return ToolResult.Fail($"MCP 工具 {Name} 执行失败：{ex.Message}");
        }
    }

    public JsonElement ToOpenAiSchema()
    {
        var schema = new
        {
            type = "function",
            function = new
            {
                name = Name,
                description = Description,
                parameters = _toolInfo.InputSchema.ValueKind != JsonValueKind.Undefined
                    ? (object)JsonSerializer.Deserialize<JsonElement>(_toolInfo.InputSchema.GetRawText())
                    : new { type = "object", properties = new { } }
            }
        };
        return JsonSerializer.SerializeToElement(schema);
    }

    public JsonElement ToAnthropicSchema()
    {
        var schema = new
        {
            name = Name,
            description = Description,
            input_schema = _toolInfo.InputSchema.ValueKind != JsonValueKind.Undefined
                ? (object)JsonSerializer.Deserialize<JsonElement>(_toolInfo.InputSchema.GetRawText())
                : new { type = "object", properties = new { } }
        };
        return JsonSerializer.SerializeToElement(schema);
    }

    /// <summary>从 MCP InputSchema 解析参数列表（仅顶层 properties）。</summary>
    private IReadOnlyList<ToolParameter> ParseParameters()
    {
        if (_toolInfo.InputSchema.ValueKind != JsonValueKind.Object) return Array.Empty<ToolParameter>();
        if (!_toolInfo.InputSchema.TryGetProperty("properties", out var props)) return Array.Empty<ToolParameter>();
        if (props.ValueKind != JsonValueKind.Object) return Array.Empty<ToolParameter>();

        var required = new HashSet<string>();
        if (_toolInfo.InputSchema.TryGetProperty("required", out var reqEl) && reqEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var r in reqEl.EnumerateArray())
                if (r.ValueKind == JsonValueKind.String) required.Add(r.GetString()!);
        }

        var parameters = new List<ToolParameter>();
        foreach (var prop in props.EnumerateObject())
        {
            var type = prop.Value.TryGetProperty("type", out var t) ? t.GetString() ?? "string" : "string";
            var desc = prop.Value.TryGetProperty("description", out var d) ? d.GetString() ?? "" : "";
            parameters.Add(new ToolParameter(prop.Name, type, desc, required.Contains(prop.Name)));
        }
        return parameters;
    }
}
```

---

## 四、验收标准

### 4.1 编译与测试

| 编号 | 标准 | 验证方式 |
|------|------|---------|
| 11b-01 | `dotnet build` 0 错误 0 警告 | 编译 |
| 11b-02 | 全量测试全绿（含 11b 新增） | `dotnet test` |
| 11b-03 | `McpClientTests` 全绿 | `dotnet test` |
| 11b-04 | 既有测试不回归（含 11a） | `dotnet test` |

### 4.2 McpClient 生命周期

| 编号 | 标准 | 验证方式 |
|------|------|---------|
| 11b-10 | `ConnectAsync` 完成 initialize → initialized → tools/list 流程 | 单测（MockTransport） |
| 11b-11 | initialize 请求含 protocolVersion "2025-03-26" | 单测（验证发送的 JSON） |
| 11b-12 | initialized 通知在 initialize 响应后发送 | 单测（验证发送顺序） |
| 11b-13 | initialize 超时（30 秒）抛 `TimeoutException` | 单测（MockTransport 不响应） |
| 11b-14 | `ConnectAsync` 后 `IsInitialized=true` | 单测 |
| 11b-15 | `ConnectAsync` 后 `Tools` 含解析的工具列表 | 单测 |
| 11b-16 | `RefreshToolsAsync` 更新 `Tools` 列表 | 单测 |
| 11b-17 | tools/list 超时（10 秒）抛 `TimeoutException` | 单测 |

### 4.3 McpClient 工具调用

| 编号 | 标准 | 验证方式 |
|------|------|---------|
| 11b-20 | `CallToolAsync` 发送 tools/call 请求（含 name + arguments） | 单测（验证发送的 JSON） |
| 11b-21 | `CallToolAsync` 返回解析后的 `McpToolCallResult` | 单测 |
| 11b-22 | tools/call 超时（60 秒）抛 `TimeoutException` | 单测 |
| 11b-23 | 工具调用成功时 `IsError=false` | 单测 |
| 11b-24 | 工具调用失败时 `IsError=true` | 单测 |
| 11b-25 | 响应含多个 content block 时全部解析 | 单测 |

### 4.4 McpClient 接收循环

| 编号 | 标准 | 验证方式 |
|------|------|---------|
| 11b-30 | 接收循环持续读取消息并分发到 JsonRpc | 单测（预设响应后验证 TCS 完成） |
| 11b-31 | 接收循环在 transport 返回 null 时退出 | 单测（SimulateDisconnect） |
| 11b-32 | 接收循环异常时取消所有 pending 请求 | 单测 |
| 11b-33 | 接收循环取消时不抛异常 | 单测 |

### 4.5 McpClient 关闭

| 编号 | 标准 | 验证方式 |
|------|------|---------|
| 11b-40 | `CloseAsync` 停止接收循环 | 单测 |
| 11b-41 | `CloseAsync` 关闭 transport | 单测 |
| 11b-42 | `CloseAsync` 取消所有 pending 请求 | 单测 |
| 11b-43 | `CloseAsync` 后 `IsInitialized=false` | 单测 |
| 11b-44 | transport 关闭异常时不崩溃 | 单测 |

### 4.6 McpToolAdapter

| 编号 | 标准 | 验证方式 |
|------|------|---------|
| 11b-50 | `Name` 返回 `{serverName}/{toolName}` 格式 | 单测 |
| 11b-51 | `Description` 返回 MCP 工具描述 | 单测 |
| 11b-52 | `Category` 有 `readOnlyHint=true` 时返回 Read | 单测 |
| 11b-53 | `Category` 无 annotations 时返回 Write | 单测 |
| 11b-54 | `Category` 有 `readOnlyHint=false` 时返回 Write | 单测 |
| 11b-55 | `Parameters` 从 InputSchema 正确解析 name/type/description | 单测 |
| 11b-56 | `Parameters` required 列表正确提取 | 单测 |
| 11b-57 | `Parameters` 无 InputSchema 时返回空列表 | 单测 |
| 11b-58 | `ExecuteAsync` 成功调用返回 `ToolResult.Ok`（拼接 text content） | 单测 |
| 11b-59 | `ExecuteAsync` 失败调用（isError）返回 `ToolResult.Fail` | 单测 |
| 11b-60 | `ExecuteAsync` JSON-RPC 异常转为 `ToolResult.Fail` | 单测 |
| 11b-61 | `ExecuteAsync` 外部 CancellationToken 取消透传 OCE | 单测 |
| 11b-62 | `ToOpenAiSchema()` 生成含 server 前缀 name 的 schema | 单测 |
| 11b-63 | `ToAnthropicSchema()` 生成含 server 前缀 name 的 schema | 单测 |
| 11b-64 | InputSchema 为空时生成默认 `{type:"object",properties:{}}` | 单测 |

---

## 五、测试计划

| 测试文件 | 覆盖范围 | 预估用例数 |
|---------|---------|-----------|
| `McpClientTests.cs` | McpClient 生命周期 + 工具调用 + 接收循环 + 关闭 + McpToolAdapter | ~30 |

**测试策略**：用 11a 的 `MockTransport` 模拟 MCP server，预设响应验证客户端行为。

**测试辅助**：

```csharp
/// <summary>构造 MCP 响应 JSON 的辅助方法。</summary>
internal static class McpTestHelpers
{
    public static string InitializeResponse() =>
        """{"jsonrpc":"2.0","id":1,"result":{"protocolVersion":"2025-03-26","capabilities":{"tools":{}},"serverInfo":{"name":"test-server","version":"1.0"}}}""";

    public static string ToolsListResponse(params (string name, string desc)[] tools) =>
        $$"""{"jsonrpc":"2.0","id":2,"result":{"tools":[{{string.Join(",", tools.Select(t => $$"""{"name":"{{t.name}}","description":"{{t.desc}}","inputSchema":{"type":"object","properties":{}}}"""))}}]}}""";

    public static string ToolCallResponse(string text, bool isError = false) =>
        $$"""{"jsonrpc":"2.0","id":3,"result":{"content":[{"type":"text","text":"{{text}}"}],"isError":{{isError.ToString().ToLower()}}}}""";
}

/// <summary>测试用的 McpClient 工厂。</summary>
internal static class McpClientFactory
{
    public static (McpClient client, MockTransport transport) Create(string serverName = "test")
    {
        var transport = new MockTransport();
        var client = new McpClient(serverName, transport);
        return (client, transport);
    }

    public static async Task<(McpClient client, MockTransport transport)> CreateAndConnectAsync(
        string serverName = "test",
        params (string name, string desc)[] tools)
    {
        var (client, transport) = Create(serverName);
        // 预设 initialize 响应
        transport.EnqueueResponse(McpTestHelpers.InitializeResponse());
        // 预设 tools/list 响应
        transport.EnqueueResponse(McpTestHelpers.ToolsListResponse(tools));

        await client.ConnectAsync(CancellationToken.None);
        return (client, transport);
    }
}
```

**典型测试示例**：

```csharp
[Fact]
public async Task ConnectAsync_CompletesInitializeInitializedToolsList()
{
    var (client, transport) = McpClientFactory.Create("fs");
    transport.EnqueueResponse(McpTestHelpers.InitializeResponse());
    transport.EnqueueResponse(McpTestHelpers.ToolsListResponse(("read_file", "Read a file")));

    await client.ConnectAsync(CancellationToken.None);

    client.IsInitialized.Should().BeTrue();
    client.Tools.Should().HaveCount(1);
    client.Tools[0].Name.Should().Be("read_file");

    // 验证发送了 initialize 请求
    var sent1 = await transport.GetLastSentAsync(CancellationToken.None);
    sent1.Should().Contain("\"method\":\"initialize\"");

    await client.CloseAsync(CancellationToken.None);
}

[Fact]
public async Task CallToolAsync_ReturnsParsedResult()
{
    var (client, transport) = await McpClientFactory.CreateAndConnectAsync(("read_file", "Read"));
    transport.EnqueueResponse(McpTestHelpers.ToolCallResponse("file content here"));

    var result = await client.CallToolAsync("read_file", JsonSerializer.SerializeToElement(new { path = "test.txt" }), CancellationToken.None);

    result.IsError.Should().BeFalse();
    result.Content.Should().HaveCount(1);
    result.Content[0].Text.Should().Be("file content here");

    await client.CloseAsync(CancellationToken.None);
}

[Fact]
public void Adapter_Name_HasServerPrefix()
{
    var (client, _) = McpClientFactory.Create("filesystem");
    var toolInfo = new McpToolInfo { Name = "read_file", Description = "Read" };
    var adapter = new McpToolAdapter(client, toolInfo);

    adapter.Name.Should().Be("filesystem/read_file");
}
```

---

## 六、实施步骤

1. 新建 `Mcp/McpClient.cs`（生命周期 + 接收循环 + 响应解析）
2. 新建 `Mcp/McpToolAdapter.cs`（IBaseTool 适配 + 参数解析 + schema 生成）
3. 新建 `McpClientTests.cs`（~30 用例，含测试辅助类）
4. 验证：`dotnet build` 0 警告 + `dotnet test` 全绿
5. 标记迭代 11b [已完成]

---

## 七、风险与对策

| 风险 | 概率 | 影响 | 对策 |
|------|------|------|------|
| 接收循环与 SendAsync 并发导致 JSON-RPC 消息交错 | 低 | 中 | Transport 层保证消息原子性（Stdio 按行，HTTP 按请求） |
| WaitAsync 超时后 TCS 仍在 _pending 中 | 中 | 低 | 超时后 TCS 不自动移除，但 CancelAllPending 会清理；下次收到响应时 TryRemove 返回 false |
| 接收循环在 CloseAsync 时阻塞 | 中 | 中 | `_receiveCts.Cancel()` 触发 ReceiveAsync 返回，循环退出 |
| MCP server 返回的 InputSchema 格式不规范 | 低 | 低 | ParseParameters 容错处理（ValueKind 检查） |
| McpToolAdapter 的 ToOpenAiSchema/ToAnthropicSchema 与内置工具格式不一致 | 低 | 中 | 验证 Provider 能正确解析 schema（11c 端到端验证） |

---

## 八、关键设计决策

### Q1：为什么接收循环用独立 Task 而非回调？

- **解耦**：接收循环持续读取，不依赖 SendAsync 触发——支持 server 主动推送通知
- **简单**：一个 while 循环 + ReceiveAsync，无需事件订阅模型
- **可控**：CloseAsync 通过 CancellationToken 停止循环，资源清理明确

### Q2：为什么 initialize 超时 30s 而 tools/list 10s？

- **initialize**：MCP server 首次启动可能需 npm install / 加载依赖，30s 给足余量
- **tools/list**：server 已初始化，tools/list 是轻量查询，10s 足够
- **tools/call**：工具执行时间不确定，60s 给足余量

### Q3：为什么 McpToolAdapter 不缓存 ExecuteAsync 的结果？

- **MCP 工具调用是动态的**：每次调用可能返回不同结果（如 read_file 读不同文件）
- **缓存是 AgentLoop 层的职责**：如需缓存，在 BatchToolExecutor 或更高层处理
- **IBaseTool 契约不包含缓存**：内置工具也不缓存

### Q4：为什么 Parameters 只解析顶层 properties 不递归嵌套？

- **MCP 工具参数通常是扁平结构**：如 `{path: string, content: string}`，不涉及深层嵌套
- **递归解析增加复杂度**：$ref / allOf / oneOf 等 JSON Schema 高级特性处理复杂
- **ToOpenAiSchema 透传原始 InputSchema**：LLM 收到完整 schema，不影响调用
- **Parameters 仅用于 UI 显示 / HITL 提示**：扁平结构足够

### Q5：为什么 ExecuteAsync 捕获 JsonRpcException 转为 ToolResult.Fail 而非抛异常？

- **IBaseTool.ExecuteAsync 的调用方（BatchToolExecutor）期望返回 ToolResult**：抛异常会中断批量执行
- **错误信息透传给 LLM**：LLM 看到 "MCP 工具调用失败：Method not found" 后可自我修正
- **与内置工具行为一致**：内置工具也返回 ToolResult.Fail 而非抛异常

---

**文档结束**。状态：[设计完成，待实现]
