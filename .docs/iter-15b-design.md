# 迭代 15b：Hook 集成接入（执行层 + 装配）

> **状态**：[设计完成，待实现]
> **前置迭代**：15a [已完成]（Hook 核心引擎）、14a [已完成]（角色系统）、14b [已完成]（SubAgentRunner + sub_agent 工具）
> **总览文档**：[iter-15-design.md](./iter-15-design.md)
> **关联文档**：[iter-15a-design.md](./iter-15a-design.md)

---

## 一、子迭代目标

### 1.1 核心目标

将 15a 交付的 Hook 核心引擎接入 Agent 运行时——实现 4 种动作（追加 sub_agent）+ tool_pre_exec 拦截 + 生命周期 fire 调用 + 条件装配。

1. **Actions 追加 sub_agent 动作**：填充 `SetSubAgentRunner` 实现 + `ExecSubAgentAsync` 实现。依赖迭代 14b 的 `SubAgentRunner`，通过 setter 注入解决时序问题

2. **SecureBatchToolExecutor 改动**：构造函数加 `HookEngine?` 参数，`OnBeforeExecuteAsync` 安全检查后追加 `tool_pre_exec` 触发。安全层先于 Hook

3. **AgentLoop 最小改动**：构造函数加 `HookEngine?` 参数，`RunCoreAsync` 生命周期节点追加 fire 调用。null 时行为等价改动前

4. **TerminalApp 改动**：传参 + session_start/end 触发 + SetSubAgentRunner 注入

5. **App 装配**：构造 HookLoader → HookEngine + 条件注入（`enable: false` 时传 null）+ system_startup/shutdown

6. **Config + 示例**：HooksConfig + example.parrotcode.yaml + hooks.yaml.example

### 1.2 本子迭代要验证的关键问题

| 问题 | 验证方式 | 失败对策 |
|------|---------|---------|
| `tool_pre_exec` 能否拦截工具执行 | 端到端 E2：配置 Hook 拦截写系统目录 | `SecureBatchToolExecutor.OnBeforeExecuteAsync` 安全检查后调 `HookEngine.FireAsync(ToolPreExec)`，拒绝时返回 `ToolResult.Fail` |
| 安全层先于 Hook | 单测：黑名单命令 + Hook 同时配置 | 安全层先拦截，Hook 不触发 |
| HookEngine=null 时 AgentLoop 行为不变 | 单测：不传 HookEngine | 所有 fire 调用前 `if (_hookEngine is not null)` 保护 |
| `enable: false` 旁路 | 端到端 E6：配置 false | App.cs 不构造 HookEngine，传 null |
| `sub_agent` 动作能起子 Agent | 端到端 E4：session_end + sub_agent | `ActionExecutor.SetSubAgentRunner` 注入后调 `SubAgentRunner.RunAsync` |
| Hook 失败不中断主循环 | 单测：mock 动作抛异常 | `ActionExecutor.ExecuteAsync` try-catch（15a 已实现） |
| 拦截原因回灌 LLM | 端到端 E2：检查 LLM 收到 `[Hook 拦截]` | `ToolResult.Fail($"[Hook 拦截] {rejection}")` |

### 1.3 非目标（15b 明确不做）

- ❌ 不做 Hook 规则的热重载——规则在启动时加载
- ❌ 不做 `message_pre_send` 的消息修改——只发通知，不改消息内容
- ❌ 不做 `prompt_inject` 在非拦截事件中的实际注入——文本渲染后丢弃
- ❌ 不做 `/hook` 斜杠命令
- ❌ 不做 Hook 执行结果的 TUI 展示

### 1.4 与 15a 的衔接

15b 直接消费 15a 交付的 6 个文件：

- **HookEngine**：15b 注入到 AgentLoop + SecureBatchToolExecutor
- **ActionExecutor**：15b 填充 `SetSubAgentRunner` 实现 + `ExecSubAgentAsync` 实现（15a 预留空壳）
- **HookRule / HookEvent / HookAction 等**：15b 的 Config / 装配直接引用
- **HookLoader**：15b 中 App.cs 调用 `Load()` 加载规则
- **HookConfigException**：15b 的 Loader 校验复用（15a 已实现校验逻辑）

---

## 二、文件改动清单

### 2.1 修改文件（6 个 + 1 个示例）

| 文件 | 改动 |
|------|------|
| `Hooks/Actions.cs` | 取消 `SetSubAgentRunner` 注释填充实现 + 取消 `ExecSubAgentAsync` 注释填充实现 |
| `Config/Models.cs` | 新增 `HooksConfig` + `AppConfig.Hooks` 字段 |
| `Security/SecureBatchToolExecutor.cs` | 构造函数加 `HookEngine?` 参数；`OnBeforeExecuteAsync` 安全检查后追加 `tool_pre_exec` 触发 |
| `Agent/AgentLoop.cs` | 构造函数加 `HookEngine?` 参数；`RunCoreAsync` 生命周期节点追加 fire 调用 |
| `App/App.cs` | 构造 `HookLoader` → `HookEngine`；条件注入；`system_startup`/`system_shutdown` 触发 |
| `Tui/TerminalApp.cs` | 构造函数加 `HookEngine?` 参数；`StartAgentRound` 传给 AgentLoop + SecureBatchToolExecutor；`session_start`/`session_end` 触发；`ActionExecutor.SetSubAgentRunner` 注入 |
| `example.parrotcode.yaml` | 新增 `hooks:` 配置节 |

### 2.2 新增文件（1 个示例）

- `.parrotcode/hooks.yaml.example`——Hook 规则示例文件

### 2.3 不变文件

- `Agent/AgentEvent.cs`——**零改动**
- `Agent/BatchToolExecutor.cs`——**零改动**（OnBeforeExecuteAsync 虚方法在迭代 8 已预留）
- `Agent/IAgentEventSink.cs`——**零改动**
- `Security/SecurityGuard.cs`——**零改动**
- `Security/Blacklist.cs` / `PathSandbox.cs` / `SecurityPolicy.cs`——**零改动**
- `Tools/` 全部——**零改动**
- `Conversation/` 全部——**零改动**
- `SubAgent/` 全部——**零改动**（sub_agent 动作复用 SubAgentRunner 公开 API）
- `Skills/` 全部——**零改动**
- `Commands/` 全部——**零改动**
- `Mcp/` 全部——**零改动**
- `Hooks/Models.cs` / `Conditions.cs` / `Templates.cs` / `Loader.cs` / `Engine.cs`——**零改动**（15a 已交付）

---

## 三、详细设计

### 3.1 Actions.cs 改动（填充 sub_agent 实现）

取消 15a 中 `SetSubAgentRunner` 和 `ExecSubAgentAsync` 的注释，填充实现：

```csharp
// ===== SetSubAgentRunner（15b 填充）=====

/// <summary>
/// 注入 SubAgentRunner（TerminalApp.RunAsync 中 _history 创建后调用）。
/// 注入前 sub_agent 动作会记录警告并跳过。
/// </summary>
public void SetSubAgentRunner(SubAgentRunner? runner,
                               BackgroundTaskManager? backgroundManager = null,
                               ConversationHistory? parentHistory = null)
{
    _subAgentRunner = runner;
    _backgroundTaskManager = backgroundManager;
    _parentHistory = parentHistory;
}

// ===== ExecSubAgentAsync（15b 填充）=====

private async Task<string?> ExecSubAgentAsync(
    HookAction action, Dictionary<string, object?> context, CancellationToken ct)
{
    if (_subAgentRunner is null)
    {
        _logger?.LogWarning("Hook sub_agent 动作未注入 SubAgentRunner，跳过");
        return null;
    }

    var task = _templates.Render(action.Task, context);
    if (string.IsNullOrWhiteSpace(task))
        return null;

    // 解析 mode
    if (!Enum.TryParse<SubAgentMode>(action.Mode, ignoreCase: true, out var mode))
        mode = SubAgentMode.Definitional;

    var request = new SubAgentRequest
    {
        Task = task,
        Role = string.IsNullOrWhiteSpace(action.Role) ? "general" : action.Role,
        Mode = mode
    };

    // Fork 模式传父历史
    var parentHistory = mode == SubAgentMode.Fork ? _parentHistory : null;

    // 同步执行（拦截事件不允许 async，sub_agent 动作一般在非拦截事件中）
    var result = await _subAgentRunner.RunAsync(request, parentHistory, ct);
    return result.Success ? result.Report : $"[sub_agent 失败] {result.Error}";
}
```

### 3.2 SecureBatchToolExecutor 改动

构造函数加 `HookEngine?` 参数，`OnBeforeExecuteAsync` 安全检查后追加 `tool_pre_exec` 触发：

```csharp
public sealed class SecureBatchToolExecutor : BatchToolExecutor
{
    private readonly SecurityGuard _guard;
    private readonly HookEngine? _hookEngine;  // 迭代 15b 新增

    public SecureBatchToolExecutor(
        ToolExecutor executor,
        ToolRegistry registry,
        SecurityGuard guard,
        int maxParallelism = 5,
        IHitlGate? hitlGate = null,
        HookEngine? hookEngine = null,          // 迭代 15b 新增
        ILogger? logger = null)
        : base(executor, registry, maxParallelism, hitlGate, logger)
    {
        _guard = guard ?? throw new ArgumentNullException(nameof(guard));
        _hookEngine = hookEngine;
    }

    /// <summary>
    /// 安全检查 → Hook tool_pre_exec。
    /// 安全层先于 Hook（安全是硬约束，Hook 是用户定制）。
    /// 安全层拦截时不触发 Hook（避免打扰已拦截的操作）。
    /// Hook 拒绝时返回 ToolResult.Fail，拒绝原因回灌 LLM。
    /// </summary>
    protected override async Task<ToolResult?> OnBeforeExecuteAsync(ToolCall call, CancellationToken ct)
    {
        // ① 安全层（既有）
        var blocked = await _guard.CheckAsync(call, ct);
        if (blocked is not null)
            return blocked;  // 安全层拦截，不触发 Hook

        // ② Hook tool_pre_exec（迭代 15b 新增）
        if (_hookEngine is not null)
        {
            var context = new Dictionary<string, object?>
            {
                ["tool_name"] = call.Name,
                ["tool_call_id"] = call.Id,
                ["params"] = ParseToolParams(call.Input)
            };

            var rejection = await _hookEngine.FireAsync(HookEvent.ToolPreExec, context, ct);
            if (rejection is not null)
            {
                _logger?.LogInformation("Hook 拦截工具 {Name}：{Reason}", call.Name, rejection);
                return ToolResult.Fail($"[Hook 拦截] {rejection}");
            }
        }

        return null;  // 放行
    }

    /// <summary>
    /// 将 ToolCall.Input（JsonElement）解析为 Dictionary 供条件匹配。
    /// 顶层属性 → key:value；嵌套对象递归解析。
    /// </summary>
    private static Dictionary<string, object?> ParseToolParams(JsonElement input)
    {
        var result = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (input.ValueKind != JsonValueKind.Object)
            return result;

        foreach (var prop in input.EnumerateObject())
        {
            result[prop.Name] = prop.Value.ValueKind switch
            {
                JsonValueKind.String => (string?)prop.Value.GetString(),
                JsonValueKind.Number => prop.Value.GetRawText(),
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                JsonValueKind.Null => null,
                JsonValueKind.Object => ParseToolParams(prop.Value),  // 递归
                _ => prop.Value.GetRawText()
            };
        }
        return result;
    }
}
```

**关键设计点**：

1. **安全层先于 Hook**：`_guard.CheckAsync` 先跑，拦截时不触发 Hook。安全是硬约束，Hook 是用户定制
2. **Hook 拒绝原因回灌 LLM**：`ToolResult.Fail($"[Hook 拦截] {rejection}")`——LLM 看到 `[Hook 拦截] <原因>` 会调整策略
3. **params 递归解析**：`ToolCall.Input` 是 `JsonElement`，条件匹配需要 dot-path（如 `params.path`）。`ParseToolParams` 递归解析嵌套对象
4. **HookEngine 为 null 时行为不变**：`if (_hookEngine is not null)` 保护

### 3.3 AgentLoop 改动

**最小改动**：构造函数加 `HookEngine?` 可选参数，`RunCoreAsync` 生命周期节点追加 fire 调用。所有 fire 调用前检查 null。

```csharp
// 构造函数新增参数（加在 compressor 之后）
public AgentLoop(IBaseProvider provider,
                 ToolRegistry registry,
                 BatchToolExecutor batchExecutor,
                 int maxRounds = 10,
                 string toolChoice = "auto",
                 string? systemPrompt = null,
                 ContextCompressor? compressor = null,
                 HookEngine? hookEngine = null,    // 迭代 15b 新增
                 ILogger? logger = null)
{
    // ... 既有赋值 ...
    _hookEngine = hookEngine;
}

private readonly HookEngine? _hookEngine;
```

`RunCoreAsync` 中的触发点：

```csharp
private async Task RunCoreAsync(ConversationHistory history, IAgentEventSink sink, CancellationToken cancellationToken)
{
    var tools = _registry.GetAll().Count > 0 ? _registry.ToOpenAiSchemas() : (JsonElement?)null;

    for (var round = 1; round <= _maxRounds; round++)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await sink.WriteAsync(new AgentEvent.RoundStartEvent(round), cancellationToken);

        // 【迭代 15b】round_start Hook
        if (_hookEngine is not null)
            await _hookEngine.FireAsync(HookEvent.RoundStart,
                new() { ["round"] = round },
                cancellationToken);

        // 迭代 9：压缩检查（既有）
        if (_compressor is not null)
        {
            var compression = await _compressor.TryCompressAsync(history, cancellationToken);
            if (compression.WasCompressed)
            {
                await sink.WriteAsync(new AgentEvent.ContextCompressedEvent(
                    compression.MessagesCompressed, compression.EstimatedTokensSaved), cancellationToken);

                // 【迭代 15b】system_compress Hook
                if (_hookEngine is not null)
                    await _hookEngine.FireAsync(HookEvent.SystemCompress,
                        new() { ["messages_compressed"] = compression.MessagesCompressed,
                                ["tokens_saved"] = compression.EstimatedTokensSaved },
                        cancellationToken);
            }
        }

        var messages = BuildMessagesWithSystem(history);

        // 【迭代 15b】message_pre_send Hook
        if (_hookEngine is not null)
            await _hookEngine.FireAsync(HookEvent.MessagePreSend,
                new() { ["messages_count"] = messages.Count, ["round"] = round },
                cancellationToken);

        // 流式调用 LLM（既有）
        var textBuf = new StringBuilder();
        var tcAcc = new ToolCallAccumulator();
        await foreach (var chunk in _provider.ChatStreamAsync(messages, tools, _toolChoice, cancellationToken))
        { /* ... 既有 ... */ }

        var assistantText = textBuf.ToString();
        var toolCalls = tcAcc.Build();

        // assistant 消息入历史（既有）
        if (toolCalls.Count > 0)
            history.AddAssistant(assistantText, toolCalls);
        else
            history.AddAssistant(assistantText);

        if (!string.IsNullOrEmpty(assistantText))
        {
            await sink.WriteAsync(new AgentEvent.AssistantMessageEvent(assistantText), cancellationToken);

            // 【迭代 15b】message_post_receive Hook
            if (_hookEngine is not null)
                await _hookEngine.FireAsync(HookEvent.MessagePostReceive,
                    new() { ["content"] = assistantText,
                            ["tool_calls_count"] = toolCalls.Count,
                            ["round"] = round },
                    cancellationToken);
        }

        // 无工具调用 → Agent 完成（既有）
        if (toolCalls.Count == 0)
        {
            await sink.WriteAsync(new AgentEvent.AgentDoneEvent(assistantText), cancellationToken);
            _logger?.LogInformation("Agent 完成，共 {Rounds} 轮", round);
            return;
        }

        // 有工具调用 → 通知开始 + 分批执行（既有）
        foreach (var call in toolCalls)
            await sink.WriteAsync(new AgentEvent.ToolCallStartEvent(call), cancellationToken);

        var results = await _batchExecutor.ExecuteAsync(toolCalls, cancellationToken);

        // 【迭代 15b】tool_post_exec Hook（每个工具结果触发一次）
        if (_hookEngine is not null)
        {
            for (var i = 0; i < toolCalls.Count; i++)
            {
                await _hookEngine.FireAsync(HookEvent.ToolPostExec,
                    new()
                    {
                        ["tool_name"] = toolCalls[i].Name,
                        ["tool_call_id"] = toolCalls[i].Id,
                        ["success"] = results[i].Success,
                        ["content_length"] = results[i].Content?.Length ?? 0,
                        ["error"] = results[i].Error
                    },
                    cancellationToken);
            }
        }

        // 迭代 9：截断 + 入历史（既有）
        /* ... 既有 ... */

        await sink.WriteAsync(new AgentEvent.RoundEndEvent(round), cancellationToken);

        // 【迭代 15b】round_end Hook
        if (_hookEngine is not null)
            await _hookEngine.FireAsync(HookEvent.RoundEnd,
                new() { ["round"] = round },
                cancellationToken);
    }

    await sink.WriteAsync(new AgentEvent.MaxRoundsReachedEvent(_maxRounds), cancellationToken);
}
```

`RunAsync` 中的错误路径追加 `system_error` Hook：

```csharp
public async Task RunAsync(ConversationHistory history, IAgentEventSink sink, CancellationToken cancellationToken)
{
    try
    {
        await RunCoreAsync(history, sink, cancellationToken);
    }
    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
    {
        await sink.WriteAsync(new AgentEvent.CancelledEvent(), CancellationToken.None);
    }
    catch (Exception ex)
    {
        _logger?.LogError(ex, "AgentLoop 致命错误");

        // 【迭代 15b】system_error Hook
        if (_hookEngine is not null)
            await _hookEngine.FireAsync(HookEvent.SystemError,
                new() { ["message"] = ex.Message, ["exception_type"] = ex.GetType().Name },
                CancellationToken.None);

        await sink.WriteAsync(new AgentEvent.ErrorEvent(ex.Message, ex), CancellationToken.None);
    }
    finally
    {
        sink.Complete();
    }
}
```

**关键设计点**：

1. **所有 fire 调用前检查 null**：`if (_hookEngine is not null)` 保证 HookEngine 为 null 时行为等价改动前
2. **system_error 用 CancellationToken.None**：错误路径中 cancellationToken 可能已取消，Hook 触发不应被取消
3. **tool_pre_exec 不在 AgentLoop 中触发**：由 SecureBatchToolExecutor.OnBeforeExecuteAsync 触发（拦截事件需要在工具执行前返回拒绝原因）

### 3.4 HooksConfig（Config/Models.cs 新增）

```csharp
/// <summary>
/// Hook 引擎配置（迭代 15b 新增）。所有字段可选，缺省用默认值。
/// </summary>
public sealed record HooksConfig
{
    /// <summary>
    /// 是否启用 Hook 引擎。默认 false（保守——Hook 是可选增强，需用户显式开启）。
    /// false 时不加载 hooks.yaml，不构造 HookEngine，所有 fire 调用 no-op。
    /// </summary>
    public bool? Enable { get; init; }
}
```

`AppConfig` 新增字段：

```csharp
/// <summary>
/// Hook 引擎配置（迭代 15b 新增）。null 时用默认值。
/// </summary>
public HooksConfig? Hooks { get; init; }
```

### 3.5 App.cs 装配

```csharp
// 【迭代 15b】构造 Hook 引擎（两级 YAML 加载 + HookEngine）
var hooksConfig = _config.Hooks ?? new HooksConfig();
HookEngine? hookEngine = null;
if (hooksConfig.Enable ?? false)  // 默认 false
{
    var hookLoader = new HookLoader(projectRoot: projectRoot, logger: _logger);
    var hookRules = hookLoader.Load();
    if (hookRules.Count > 0)
    {
        hookEngine = new HookEngine(hookRules, logger: _logger);
        _logger.LogInformation("已启用 Hook 引擎，{Count} 条规则", hookRules.Count);
    }
    else
    {
        _logger.LogInformation("Hook 引擎已启用，但无规则加载（hooks.yaml 为空或不存在）");
    }
}

// system_startup Hook
if (hookEngine is not null)
    await hookEngine.FireAsync(HookEvent.SystemStartup,
        new() { ["project_root"] = projectRoot,
                ["provider"] = _providerConfig.Name,
                ["model"] = _providerConfig.Model },
        _ct);

using var terminalApp = new TerminalApp(/* ... 既有参数 ... */,
                                        subAgentConfig,
                                        hookEngine,            // 迭代 15b 新增
                                        _logger,
                                        _ct);
await terminalApp.RunAsync();

// system_shutdown Hook
if (hookEngine is not null)
    await hookEngine.FireAsync(HookEvent.SystemShutdown,
        new() { ["project_root"] = projectRoot },
        _ct);
```

### 3.6 TerminalApp 改动

```csharp
private readonly HookEngine? _hookEngine;  // 迭代 15b 新增
private SubAgentRunner? _subAgentRunner;   // 迭代 15b 新增（缓存引用供 HookEngine 注入）

// 构造函数签名加参数（加在 subAgentConfig 之后）
public TerminalApp(/* ... 既有参数 ... */,
                   SubAgentConfig? subAgentConfig,
                   HookEngine? hookEngine,        // 迭代 15b 新增
                   ILogger? logger,
                   CancellationToken ct)
{
    // ... 既有赋值 ...
    _hookEngine = hookEngine;
}
```

`RunAsync` 中 sub_agent 注册块缓存 runner 引用 + 注入到 HookEngine：

```csharp
if (_roleRegistry is not null && (_subAgentConfig.Enable ?? true))
{
    _subAgentRunner = new SubAgentRunner(_provider, _registry!, _securityGuard,
                                         _roleRegistry, _subAgentConfig, logger: null);
    _registry.Register(new SubAgentTool(_subAgentRunner, _history!));

    // 【迭代 15b】注入到 HookEngine（sub_agent 动作用）
    _hookEngine?.Actions.SetSubAgentRunner(_subAgentRunner, parentHistory: _history);
}
```

`StartAgentRound` 传 HookEngine 给 AgentLoop + SecureBatchToolExecutor：

```csharp
private void StartAgentRound()
{
    var executor = new ToolExecutor(_registry!, TimeSpan.FromSeconds(_agentConfig.ToolTimeoutSeconds ?? 30), _logger);

    IHitlGate? hitlGate = _hitlPrompt is null ? new NullHitlGate()
                                              : (IHitlGate)_hitlPrompt;

    _securityGuard.Level = _securityLevel;

    var batchExecutor = new SecureBatchToolExecutor(executor,
                                                    _registry!,
                                                    _securityGuard,
                                                    _agentConfig.MaxParallelism ?? 5,
                                                    hitlGate: hitlGate,
                                                    hookEngine: _hookEngine,   // 迭代 15b 新增
                                                    _logger);

    _sink = new ChannelEventSink();
    var agentLoop = new AgentLoop(_provider,
                                  _registry!,
                                  batchExecutor,
                                  _agentConfig.MaxRounds ?? 10,
                                  _agentConfig.ToolChoice ?? "auto",
                                  _systemPromptWithInstructions,
                                  compressor: _compressor,
                                  hookEngine: _hookEngine,   // 迭代 15b 新增
                                  logger: null);

    _agentTask = RunAgentWithHooksAsync(agentLoop);
}

/// <summary>
/// 包装 AgentLoop.RunAsync，在前后触发 session_start/session_end Hook。
/// </summary>
private async Task RunAgentWithHooksAsync(AgentLoop agentLoop)
{
    // session_start Hook
    if (_hookEngine is not null)
    {
        _hookEngine.ResetOnce();  // 新会话清除 once 跟踪
        await _hookEngine.FireAsync(HookEvent.SessionStart,
            new() { ["messages_count"] = _history!.Count },
            _ct);
    }

    try
    {
        await agentLoop.RunAsync(_history!, _sink!, _ct);
    }
    finally
    {
        // session_end Hook
        if (_hookEngine is not null)
            await _hookEngine.FireAsync(HookEvent.SessionEnd,
                new() { ["messages_count"] = _history!.Count },
                _ct);
    }
}
```

### 3.7 example.parrotcode.yaml 新增

```yaml
# 迭代 15 新增：Hook 引擎（生命周期事件钩子）
# 默认 false——Hook 能执行 shell 命令和 HTTP 请求，是安全敏感特性，需显式开启。
# 开启后从 .parrotcode/hooks.yaml（项目）和 ~/.parrotcode/hooks.yaml（全局）加载规则。
hooks:
  enable: false                      # 是否启用 Hook 引擎（默认 false）
```

### 3.8 .parrotcode/hooks.yaml.example 新增

```yaml
# ParrotCode Hook 规则示例。复制为 .parrotcode/hooks.yaml 后按需修改。
# 规则文件被 .gitignore 忽略（可能含敏感 webhook URL）。
#
# 全局规则放 ~/.parrotcode/hooks.yaml，项目规则放 ./.parrotcode/hooks.yaml。
# 两者合并执行（不覆盖）。

hooks:
  # 示例 1：write_file 前自动 git stash（保护工作区）
  - name: git-stash-before-write
    event: tool_pre_exec
    condition:
      match: ALL
      rules:
        - field: tool_name
          operator: exact
          value: write_file
    actions:
      - type: shell
        command: "git stash"
    control:
      once: false
      async: false
      timeout: 10

  # 示例 2：拦截项目外的写操作（prompt_inject 返回拒绝原因）
  - name: block-external-write
    event: tool_pre_exec
    condition:
      match: ALL
      rules:
        - field: tool_name
          operator: glob
          value: "*_file"
        - field: params.path
          operator: regex
          value: "^/etc/|^/usr/"
    actions:
      - type: prompt_inject
        text: "禁止写系统目录 {{params.path}}"
    control:
      once: false
      async: false

  # 示例 3：每次工具调用后 POST 到 webhook 做审计
  - name: audit-tool-call
    event: tool_post_exec
    actions:
      - type: http
        url: "https://audit.example.com/hooks/tool"
        method: POST
        headers:
          Content-Type: application/json
        body: '{"tool":"{{tool_name}}","success":{{success}}}'
    control:
      async: true       # 异步 fire-and-forget，不阻塞 Agent
      timeout: 5

  # 示例 4：会话结束时起子 Agent 做总结
  - name: session-summary
    event: session_end
    actions:
      - type: sub_agent
        task: "总结本次会话的要点和产出"
        role: general
        mode: fork
    control:
      async: false      # 同步等待子 Agent 完成
      timeout: 60

  # 示例 5：上下文压缩时通知（once=true 只通知一次）
  - name: compress-notice
    event: system_compress
    actions:
      - type: shell
        command: "echo '上下文已压缩' >> .parrotcode/compress.log"
    control:
      once: false
      async: true
```

---

## 四、运行时序

### 4.1 tool_pre_exec 拦截时序

```
LLM 决定调用 write_file(path="/etc/passwd", content="...")
  └─ AgentLoop → BatchToolExecutor.ExecuteAsync
       └─ OnBeforeExecuteAsync（SecureBatchToolExecutor 覆写）
            ├─ ① SecurityGuard.CheckAsync
            │    └─ PathSandbox 检查 /etc/passwd
            │       └─ 如果安全层拦截 → return ToolResult.Fail("[路径沙箱] ...")
            │       └─ 如果安全层放行 → 继续
            ├─ ② HookEngine.FireAsync(ToolPreExec, {tool_name:"write_file", params:{path:"/etc/passwd",...}})
            │    └─ 遍历规则：
            │         rule "block-external-write":
            │           condition: tool_name glob "*_file" ✓ AND params.path regex "^/etc/" ✓
            │           action: prompt_inject text="禁止写系统目录 {{params.path}}"
            │           → 渲染 → "禁止写系统目录 /etc/passwd"
            │           → 返回拒绝原因
            └─ return ToolResult.Fail("[Hook 拦截] 禁止写系统目录 /etc/passwd")

  AgentLoop 收到 ToolResult.Fail → emit ToolBlockedEvent → 回灌 LLM
  LLM 看到 "[Hook 拦截] 禁止写系统目录 /etc/passwd" → 调整策略（换路径或换工具）
```

### 4.2 sub_agent 动作时序

```
SessionEnd 事件触发：
  TerminalApp.RunAgentWithHooksAsync → _hookEngine.FireAsync(SessionEnd, {messages_count: N})
    └─ rule "session-summary":
         action: sub_agent task="总结本次会话的要点和产出" role="general" mode="fork"
         → ActionExecutor.ExecSubAgentAsync
           ├─ _subAgentRunner 非 null（TerminalApp.RunAsync 中已注入）
           ├─ 解析 mode=fork → SubAgentMode.Fork
           ├─ 构造 SubAgentRequest
           ├─ 调 _subAgentRunner.RunAsync(request, parentHistory: _history)
           └─ 返回 result.Report（子 Agent 的总结文本）
    └─ FireAsync 返回 null（SessionEnd 非拦截事件）
  TerminalApp 继续退出
```

---

## 五、验收标准

### 5.1 功能验收（单测）

#### SecureBatchToolExecutorHook（9 项）

| # | 验收项 | 验证方法 | 通过标准 |
|---|--------|---------|---------|
| 1 | HookEngine=null 时行为等价改动前 | 不传 HookEngine 跑工具执行 | 安全层正常工作，无 Hook 触发 |
| 2 | tool_pre_exec 放行（无匹配规则） | 配置无关的 Hook | 工具正常执行 |
| 3 | tool_pre_exec 拦截（返回 ToolResult.Fail） | 配置拦截 write_file 的 Hook | ToolResult.Fail 含拒绝原因 |
| 4 | 拦截原因含 `[Hook 拦截]` 前缀 | 检查 ToolResult.Error | Error 以 `[Hook 拦截]` 开头 |
| 5 | 安全层先于 Hook | 黑名单命令 + Hook 同时配置 | 安全层先拦截，Hook 不触发 |
| 6 | 安全层放行 + Hook 拦截 | 安全放行 + Hook 配置拦截 | Hook 拦截成功 |
| 7 | 安全层放行 + Hook 放行 → 正常执行 | 两者都放行 | 工具正常执行 |
| 8 | params dot-path 条件匹配 | write_file(path=/etc/passwd) + condition params.path | 条件匹配成功 |
| 9 | 多个工具调用各自独立触发 Hook | 2 个工具调用 | 各自独立触发 |

#### AgentLoopHook（11 项）

| # | 验收项 | 验证方法 | 通过标准 |
|---|--------|---------|---------|
| 10 | HookEngine=null 时行为等价改动前 | 不传 HookEngine | 行为等价改动前 |
| 11 | round_start 每轮触发 | AgentLoop 跑 1 轮 | FireAsync(RoundStart) 被调用 1 次 |
| 12 | round_end 每轮触发 | AgentLoop 跑 1 轮 | FireAsync(RoundEnd) 被调用 1 次 |
| 13 | message_pre_send 每轮触发 | LLM 调用前 | FireAsync(MessagePreSend) 被调用 |
| 14 | message_post_receive 每轮触发 | LLM 回复后 | FireAsync(MessagePostReceive) 被调用 |
| 15 | tool_post_exec 每个工具触发 | 2 个工具调用 | FireAsync(ToolPostExec) 被调用 2 次 |
| 16 | system_error 错误时触发 | mock provider 抛异常 | FireAsync(SystemError) 被调用 |
| 17 | system_compress 压缩时触发 | mock 压缩 | FireAsync(SystemCompress) 被调用 |
| 18 | Hook 动作慢时不阻塞（async=true） | async 动作 | FireAsync 立即返回 |
| 19 | Hook 动作异常不中断 AgentLoop | mock 动作抛异常 | AgentLoop 继续运行 |
| 20 | context 含正确字段 | 检查 context | round / content / tool_name 等 |

#### ActionExecutor sub_agent（4 项）

| # | 验收项 | 验证方法 | 通过标准 |
|---|--------|---------|---------|
| 21 | sub_agent 动作调 SubAgentRunner | mock SubAgentRunner | RunAsync 被调用 |
| 22 | sub_agent 返回报告 | mock runner 返回成功 | 返回 result.Report |
| 23 | sub_agent Fork 模式传父历史 | mode=fork | parentHistory 传入 |
| 24 | sub_agent Definitional 模式传 null | mode=definitional | parentHistory=null |

#### TerminalApp + App 装配（8 项）

| # | 验收项 | 验证方法 | 通过标准 |
|---|--------|---------|---------|
| 25 | session_start 在 AgentLoop 前触发 | 检查调用顺序 | SessionStart 先于 AgentLoop.RunAsync |
| 26 | session_end 在 AgentLoop 后触发 | 检查调用顺序 | SessionEnd 后于 AgentLoop.RunAsync |
| 27 | ResetOnce 在 session_start 前调用 | 新会话 | once 跟踪被清除 |
| 28 | system_startup 在 TerminalApp 前触发 | 检查调用顺序 | SystemStartup 先于 TerminalApp.RunAsync |
| 29 | system_shutdown 在 TerminalApp 后触发 | 检查调用顺序 | SystemShutdown 后于 TerminalApp.RunAsync |
| 30 | SetSubAgentRunner 在 sub_agent 注册后调用 | 检查注入时机 | runner 已注入到 ActionExecutor |
| 31 | enable=false 时不构造 HookEngine | 配置 false | hookEngine=null |
| 32 | enable=true + 无规则文件 | 配置 true + 无 hooks.yaml | hookEngine=null，日志提示 |

#### HooksConfig（4 项）

| # | 验收项 | 验证方法 | 通过标准 |
|---|--------|---------|---------|
| 33 | 默认 Enable=null → false | 不配置 hooks | `Enable ?? false` = false |
| 34 | YAML 加载 enable: true | 配置 hooks.enable: true | Enable=true |
| 35 | YAML 加载 enable: false | 配置 hooks.enable: false | Enable=false |
| 36 | null 配置用默认值 | Hooks=null | `?? new HooksConfig()` |

#### 零改动验证（工程验收）

| # | 验收项 | 通过标准 |
|---|--------|---------|
| 37 | `Agent/AgentEvent.cs` git diff 为空 | 零改动 |
| 38 | `Agent/BatchToolExecutor.cs` git diff 为空 | 零改动（OnBeforeExecuteAsync 虚方法在迭代 8 已预留） |
| 39 | `Agent/IAgentEventSink.cs` git diff 为空 | 零改动 |
| 40 | `Security/SecurityGuard.cs` git diff 为空 | 零改动 |
| 41 | `Tools/` 全部 git diff 为空 | 零改动 |
| 42 | `Conversation/` 全部 git diff 为空 | 零改动 |
| 43 | `SubAgent/` 全部 git diff 为空 | 零改动 |
| 44 | `Skills/` 全部 git diff 为空 | 零改动 |

### 5.2 端到端验收（含环境配置 + 操作步骤）

#### E1：tool_pre_exec + shell 动作（write_file 前自动 git stash）

**验收目标**：验证 `tool_pre_exec` Hook 在工具执行前触发 shell 动作。

**环境配置**：

```
验收目录：d:\cs\ParrotCode.Net\.parrotcode-e2e\          # 专用验收目录（避免污染项目）
├── .parrotcode\
│   └── hooks.yaml                                        # Hook 规则文件
└── test-project\                                         # git 测试项目
    ├── (git 仓库，有未提交的修改)
    └── (LLM 将要求 write_file 到此目录)
```

**步骤 1：创建验收环境**

```bash
# 创建专用验收目录
mkdir -p d:\cs\ParrotCode.Net\.parrotcode-e2e\.parrotcode
mkdir -p d:\cs\ParrotCode.Net\.parrotcode-e2e\test-project

# 初始化 git 测试项目
cd d:\cs\ParrotCode.Net\.parrotcode-e2e\test-project
git init
echo "initial content" > existing.txt
git add . && git commit -m "init"

# 制造未提交的修改
echo "uncommitted change" >> existing.txt
```

**步骤 2：创建 Hook 规则文件**

创建 `d:\cs\ParrotCode.Net\.parrotcode-e2e\.parrotcode\hooks.yaml`：

```yaml
hooks:
  - name: git-stash-before-write
    event: tool_pre_exec
    condition:
      match: ALL
      rules:
        - field: tool_name
          operator: exact
          value: write_file
    actions:
      - type: shell
        command: "cd d:/cs/ParrotCode.Net/.parrotcode-e2e/test-project && git stash"
    control:
      once: false
      async: false
      timeout: 10
```

**步骤 3：创建主配置文件**

创建 `d:\cs\ParrotCode.Net\.parrotcode-e2e\.parrotcode.yaml`（启用 Hook）：

```yaml
hooks:
  enable: true
```

**步骤 4：运行 ParrotCode**

```bash
cd d:\cs\ParrotCode.Net\.parrotcode-e2e
dotnet run --project d:\cs\ParrotCode.Net\ParrotCode.Net --no-build
```

**步骤 5：输入触发指令**

在 TUI 中输入：
```
请用 write_file 工具在 d:/cs/ParrotCode.Net/.parrotcode-e2e/test-project/new.txt 写入 "hello"
```

**步骤 6：验证**

| 检查点 | 通过标准 |
|--------|---------|
| git stash 是否执行 | `cd test-project && git stash list` 显示 1 条 stash |
| existing.txt 的未提交修改是否被 stash | `cat existing.txt` 内容恢复为 "initial content"（无 "uncommitted change"） |
| write_file 是否正常执行 | `test-project/new.txt` 存在，内容为 "hello" |
| Hook 执行日志 | 控制台或日志中无 Hook 错误 |
| Agent 行为 | LLM 正常完成写文件操作 |

**清理**：

```bash
cd d:\cs\ParrotCode.Net\.parrotcode-e2e\test-project
git stash pop          # 恢复未提交的修改
cd ..
rm -rf .parrotcode-e2e # 清理验收目录
```

---

#### E2：tool_pre_exec + prompt_inject 拦截（禁止写系统目录）

**验收目标**：验证 `tool_pre_exec` Hook 的拦截能力——prompt_inject 返回拒绝原因回灌 LLM。

**环境配置**：

```
验收目录：d:\cs\ParrotCode.Net\.parrotcode-e2e\
├── .parrotcode\
│   └── hooks.yaml
└── .parrotcode.yaml
```

**步骤 1：创建 Hook 规则文件**

创建 `d:\cs\ParrotCode.Net\.parrotcode-e2e\.parrotcode\hooks.yaml`：

```yaml
hooks:
  - name: block-system-dir-write
    event: tool_pre_exec
    condition:
      match: ALL
      rules:
        - field: tool_name
          operator: glob
          value: "*_file"
        - field: params.path
          operator: regex
          value: "^/etc/|^C:\\\\Windows\\\\"
    actions:
      - type: prompt_inject
        text: "禁止写系统目录 {{params.path}}，请使用项目目录内的路径"
    control:
      once: false
      async: false
```

> **注意**：Windows 路径正则中 `\\\\` 是 YAML 中表示 `\\`（两个反斜杠），正则引擎解析为 `\\`（字面反斜杠）。

**步骤 2：创建主配置文件**

```yaml
hooks:
  enable: true
```

**步骤 3：运行 ParrotCode**

```bash
cd d:\cs\ParrotCode.Net\.parrotcode-e2e
dotnet run --project d:\cs\ParrotCode.Net\ParrotCode.Net --no-build
```

**步骤 4：输入触发指令**

在 TUI 中输入：
```
请用 write_file 工具在 C:\Windows\test_hook.txt 写入 "hello"
```

**步骤 5：验证**

| 检查点 | 通过标准 |
|--------|---------|
| write_file 是否被拦截 | `C:\Windows\test_hook.txt` 不存在 |
| LLM 是否收到拒绝原因 | TUI 中 ToolResult 显示 `[Hook 拦截] 禁止写系统目录 C:\Windows\test_hook.txt，请使用项目目录内的路径` |
| LLM 是否调整策略 | LLM 尝试换路径（如写到项目目录）或告知用户无法写系统目录 |
| 安全层是否先于 Hook | 如果 PathSandbox 也拦截了 C:\Windows\，则安全层先拦截（返回 `[路径沙箱]` 而非 `[Hook 拦截]`）——这是正确行为 |

**边界情况验证**：

- 输入写项目目录内的文件（如 `./test.txt`）→ Hook 不拦截，write_file 正常执行
- 输入写 `/etc/passwd`（Unix）→ Hook 拦截（正则匹配 `^/etc/`）

**清理**：

```bash
rm -rf d:\cs\ParrotCode.Net\.parrotcode-e2e
```

---

#### E3：tool_post_exec + http 审计（webhook）

**验收目标**：验证 `tool_post_exec` Hook 触发 http 动作 POST 到 webhook。

**环境配置**：

需要一个 HTTP 接收端。提供两种方案：

**方案 A：使用在线 webhook 测试服务（推荐）**

1. 访问 https://webhook.site（或 https://pipedream.com）
2. 获取一个唯一的 webhook URL（如 `https://webhook.site/xxxxxxxx-xxxx-xxxx`）

**方案 B：本地启动简易 HTTP 服务器**

```bash
# Python 简易服务器（接收 POST 并打印 body）
python -c "
from http.server import HTTPServer, BaseHTTPRequestHandler
class H(BaseHTTPRequestHandler):
    def do_POST(self):
        l = int(self.headers.get('Content-Length', 0))
        body = self.rfile.read(l).decode()
        print(f'收到 POST: {body}')
        self.send_response(200)
        self.end_headers()
        self.wfile.write(b'OK')
    def do_GET(self):
        self.send_response(200)
        self.end_headers()
        self.wfile.write(b'GET OK')
HTTPServer(('localhost', 9876), H).serve_forever()
"
# 服务在 http://localhost:9876 接收请求
```

**步骤 1：创建 Hook 规则文件**

创建 `d:\cs\ParrotCode.Net\.parrotcode-e2e\.parrotcode\hooks.yaml`：

```yaml
hooks:
  - name: audit-tool-call
    event: tool_post_exec
    actions:
      - type: http
        url: "http://localhost:9876/hooks/tool"    # 方案 B；方案 A 换成 webhook.site URL
        method: POST
        headers:
          Content-Type: application/json
          X-Audit-Source: parrotcode-hook
        body: '{"tool":"{{tool_name}}","success":{{success}},"content_length":{{content_length}}}'
    control:
      async: true       # 异步 fire-and-forget，不阻塞 Agent
      timeout: 5
```

**步骤 2：创建主配置文件**

```yaml
hooks:
  enable: true
```

**步骤 3：启动 HTTP 接收端**

方案 B：在另一个终端启动 Python 简易服务器。

**步骤 4：运行 ParrotCode**

```bash
cd d:\cs\ParrotCode.Net\.parrotcode-e2e
dotnet run --project d:\cs\ParrotCode.Net\ParrotCode.Net --no-build
```

**步骤 5：输入触发指令**

在 TUI 中输入（触发工具调用）：
```
请用 read_file 工具读取 d:/cs/ParrotCode.Net/.docs/plan.md 的前 10 行
```

**步骤 6：验证**

| 检查点 | 通过标准 |
|--------|---------|
| HTTP 服务器收到 POST 请求 | Python 服务器终端打印 `收到 POST: {"tool":"read_file","success":true,"content_length":...}` |
| 请求头正确 | `Content-Type: application/json` + `X-Audit-Source: parrotcode-hook` |
| 请求体模板变量已替换 | `{{tool_name}}` → `read_file`，`{{success}}` → `True` |
| async 不阻塞 Agent | Agent 正常完成 read_file，不等 webhook 响应 |
| 多次工具调用各发一次 | 如果 LLM 调了 2 个工具，服务器收到 2 个 POST |

**清理**：

```bash
# 停止 Python 服务器（Ctrl+C）
rm -rf d:\cs\ParrotCode.Net\.parrotcode-e2e
```

---

#### E4：session_end + sub_agent 动作（会话总结）

**验收目标**：验证 `session_end` Hook 触发 sub_agent 动作起子 Agent 做总结。

**环境配置**：

```
验收目录：d:\cs\ParrotCode.Net\.parrotcode-e2e\
├── .parrotcode\
│   └── hooks.yaml
├── .parrotcode.yaml
└── (需配置可用的 LLM provider——子 Agent 要调 LLM)
```

**前置条件**：

- `.parrotcode.yaml` 中已配置可用的 provider（如 OpenAI / 本地模型）
- SubAgent 配置已启用（`sub_agent.enable: true`，默认 true）
- 角色系统已加载（`roles/` 目录有 `general.md`，迭代 14a 已交付）

**步骤 1：创建 Hook 规则文件**

创建 `d:\cs\ParrotCode.Net\.parrotcode-e2e\.parrotcode\hooks.yaml`：

```yaml
hooks:
  - name: session-summary
    event: session_end
    actions:
      - type: sub_agent
        task: "总结本次会话讨论的要点和产出，用 3 条要点概括"
        role: general
        mode: fork
    control:
      async: false      # 同步等待子 Agent 完成
      timeout: 60
```

**步骤 2：创建主配置文件**

```yaml
hooks:
  enable: true
provider:
  name: openai           # 或你的 provider
  model: gpt-4o-mini     # 或你的模型
  api_key: "sk-..."      # 你的 API key
sub_agent:
  enable: true
```

**步骤 3：运行 ParrotCode**

```bash
cd d:\cs\ParrotCode.Net\.parrotcode-e2e
dotnet run --project d:\cs\ParrotCode.Net\ParrotCode.Net --no-build
```

**步骤 4：进行一次简短对话**

在 TUI 中输入：
```
请帮我用 C# 写一个冒泡排序函数
```

等 LLM 回复完成后，退出会话（Ctrl+C 或输入 `/exit`）。

**步骤 5：验证**

| 检查点 | 通过标准 |
|--------|---------|
| session_end Hook 触发 | 退出时日志显示 Hook 触发 |
| sub_agent 动作执行 | 子 Agent 被启动（日志可见 SubAgentRunner 调用） |
| 子 Agent 返回总结 | 日志中可见子 Agent 的总结报告（3 条要点） |
| Fork 模式隔离 | 子 Agent 不影响父 Agent 的对话历史（父历史不变） |
| 子 Agent 完成 | 同步等待（async=false），退出前子 Agent 完成 |

**注意事项**：

- 子 Agent 调用 LLM 需要消耗 token——用 gpt-4o-mini 等便宜模型
- 如果 LLM 不可用，sub_agent 动作会失败但不会中断退出（ActionExecutor 错误隔离）

**清理**：

```bash
rm -rf d:\cs\ParrotCode.Net\.parrotcode-e2e
```

---

#### E5：system_compress + shell 通知

**验收目标**：验证 `system_compress` Hook 在上下文压缩时触发 shell 动作写日志。

**环境配置**：

需要触发上下文压缩——配置一个很低的压缩阈值。

**步骤 1：创建 Hook 规则文件**

创建 `d:\cs\ParrotCode.Net\.parrotcode-e2e\.parrotcode\hooks.yaml`：

```yaml
hooks:
  - name: compress-notice
    event: system_compress
    actions:
      - type: shell
        command: "echo '上下文已压缩' >> d:/cs/ParrotCode.Net/.parrotcode-e2e/compress.log"
    control:
      once: false
      async: true
```

**步骤 2：创建主配置文件（低压缩阈值）**

```yaml
hooks:
  enable: true
agent:
  max_rounds: 10
  compress_threshold: 500    # 很低的阈值，几轮对话就会触发压缩
  compress_target: 300
```

> **注意**：`compress_threshold` 和 `compress_target` 的实际字段名以 `AgentConfig` 定义为准——验收时检查 `Config/Models.cs` 中的 `AgentConfig` 确认字段名。

**步骤 3：运行 ParrotCode**

```bash
cd d:\cs\ParrotCode.Net\.parrotcode-e2e
dotnet run --project d:\cs\ParrotCode.Net\ParrotCode.Net --no-build
```

**步骤 4：进行多轮对话触发压缩**

在 TUI 中连续输入多轮长文本对话，直到上下文超过阈值触发压缩：
```
第一轮：请详细解释 RESTful API 的设计原则（长文本回复会快速增加上下文）
第二轮：请详细解释 GraphQL 与 REST 的区别
第三轮：请详细解释微服务架构的优缺点
...
```

**步骤 5：验证**

| 检查点 | 通过标准 |
|--------|---------|
| 压缩是否触发 | TUI 中显示 `[上下文压缩]` 通知（ContextCompressedEvent） |
| compress.log 是否写入 | `cat d:\cs\ParrotCode.Net\.parrotcode-e2e\compress.log` 显示 `上下文已压缩` |
| 每次压缩都写一条 | 如果压缩了 N 次，log 中有 N 条记录 |

**清理**：

```bash
rm -rf d:\cs\ParrotCode.Net\.parrotcode-e2e
```

---

#### E6：enable: false 旁路

**验收目标**：验证 `hooks.enable: false` 时 Hook 不触发，行为等价无 Hook。

**步骤 1：创建配置（enable: false）**

创建 `d:\cs\ParrotCode.Net\.parrotcode-e2e\.parrotcode.yaml`：

```yaml
hooks:
  enable: false
```

创建 `d:\cs\ParrotCode.Net\.parrotcode-e2e\.parrotcode\hooks.yaml`（有规则但不加载）：

```yaml
hooks:
  - name: should-not-fire
    event: round_start
    actions:
      - type: shell
        command: "echo 'HOOK FIRED' >> d:/cs/ParrotCode.Net/.parrotcode-e2e/hook.log"
    control:
      async: false
```

**步骤 2：运行 ParrotCode**

```bash
cd d:\cs\ParrotCode.Net\.parrotcode-e2e
dotnet run --project d:\cs\ParrotCode.Net\ParrotCode.Net --no-build
```

**步骤 3：进行一次对话**

在 TUI 中输入任意对话，等 Agent 完成。

**步骤 4：验证**

| 检查点 | 通过标准 |
|--------|---------|
| hook.log 不存在 | `d:\cs\ParrotCode.Net\.parrotcode-e2e\hook.log` 文件不存在（Hook 未触发） |
| 启动日志 | 日志中无"已启用 Hook 引擎"（HookEngine 为 null） |
| Agent 行为 | 行为等价无 Hook——无性能影响、无异常 |

**清理**：

```bash
rm -rf d:\cs\ParrotCode.Net\.parrotcode-e2e
```

---

#### E7：once: true 只触发一次

**验收目标**：验证 `once: true` 的规则在同一会话内只触发一次。

**步骤 1：创建 Hook 规则文件**

创建 `d:\cs\ParrotCode.Net\.parrotcode-e2e\.parrotcode\hooks.yaml`：

```yaml
hooks:
  - name: init-notice
    event: round_start
    actions:
      - type: shell
        command: "echo 'round started' >> d:/cs/ParrotCode.Net/.parrotcode-e2e/once.log"
    control:
      once: true       # 只触发一次
      async: false
```

**步骤 2：创建主配置文件**

```yaml
hooks:
  enable: true
agent:
  max_rounds: 5
```

**步骤 3：运行 ParrotCode**

```bash
cd d:\cs\ParrotCode.Net\.parrotcode-e2e
dotnet run --project d:\cs\ParrotCode.Net\ParrotCode.Net --no-build
```

**步骤 4：触发多轮对话**

输入一个需要多轮工具调用的任务（如"读取 plan.md 然后总结要点"），让 Agent 跑 2-3 轮。

**步骤 5：验证**

| 检查点 | 通过标准 |
|--------|---------|
| once.log 只有 1 条记录 | `cat once.log` 只显示 1 行 `round started`（第 1 轮触发，第 2-3 轮跳过） |
| Agent 多轮正常运行 | Agent 完成任务，round_start 事件每轮都触发（Hook 只是跳过已 once 的规则） |

**清理**：

```bash
rm -rf d:\cs\ParrotCode.Net\.parrotcode-e2e
```

---

### 5.3 工程验收

- `dotnet build` 0 error 0 warning
- 全部既有测试通过（含 15a 的 73 项测试 + 迭代 12/13/14 的测试）
- 新增单测覆盖 SecureBatchToolExecutorHook / AgentLoopHook / Actions sub_agent / TerminalApp / App 装配 / HooksConfig（约 36 项）
- `Agent/AgentEvent.cs` git diff 为空
- `Agent/BatchToolExecutor.cs` git diff 为空
- `Agent/IAgentEventSink.cs` git diff 为空
- `Security/SecurityGuard.cs` git diff 为空
- `Tools/` / `Conversation/` / `SubAgent/` / `Skills/` / `Commands/` / `Mcp/` 全部 git diff 为空
- `Hooks/Models.cs` / `Conditions.cs` / `Templates.cs` / `Loader.cs` / `Engine.cs` git diff 为空（15a 交付不变）
- `nullable` 引用类型开启，无 warning
- `async` 全链路，无 `.Result` / `.Wait()`
- `CancellationToken` 贯穿

### 5.4 代码质量

- `AgentEvent.cs` 零改动
- `BatchToolExecutor.cs` 零改动（OnBeforeExecuteAsync 虚方法在迭代 8 已预留）
- `SecurityGuard.cs` 零改动（Hook 在安全层之后触发，不修改 SecurityGuard）
- `AgentLoop.cs` 改动全部是 `if (_hookEngine is not null) await _hookEngine.FireAsync(...)` 形式
- `SecureBatchToolExecutor.cs` 改动全部在 `OnBeforeExecuteAsync` 内、安全检查之后

---

## 六、风险与对策

| 风险 | 对策 |
|------|------|
| Hook 动作执行慢阻塞 AgentLoop | 用户应给慢动作配 `async: true`。拦截事件禁止 async（15a 的 Loader 校验） |
| Hook 动作执行 shell 命令有安全风险 | Hook 默认 `enable: false`。shell 动作不经过 SecurityGuard（Hook 是用户主动配置的自动化） |
| `sub_agent` 动作时序问题 | `SetSubAgentRunner` setter 注入。注入前 sub_agent 动作记警告并跳过。`session_start` 事件触发时 runner 已注入 |
| `sub_agent` Fork 模式需要父历史引用 | `SetSubAgentRunner` 同时传入 `parentHistory`。在 `_history` 创建后调用 |
| AgentLoop 改动破坏既有测试 | 所有 fire 调用前 `if (_hookEngine is not null)` 保护。既有测试不传 HookEngine |
| SecureBatchToolExecutor 改动破坏安全层 | Hook 触发在安全检查**之后**。安全层拦截时不触发 Hook |
| Hook 规则文件含敏感信息（webhook URL） | `.parrotcode/hooks.yaml` 被 `.gitignore` 忽略 |
| `system_error` Hook 在 CancellationToken 已取消时触发 | `FireAsync` 传 `CancellationToken.None` |
| 端到端 E4 需要 LLM 可用 | sub_agent 动作失败不中断退出（ActionExecutor 错误隔离）。如果 LLM 不可用，验收改为检查"sub_agent 动作被调用但失败"日志 |
| 端到端 E2 的正则路径在 Windows 上匹配 | Windows 路径用 `\\\\`（YAML 转义后正则为 `\\`）。验收时测试 `C:\Windows\` 路径 |

---

## 七、交付检查清单

### 修改文件

- [ ] `Hooks/Actions.cs`——取消 SetSubAgentRunner 注释 + 取消 ExecSubAgentAsync 注释（填充 sub_agent 实现）
- [ ] `Config/Models.cs`——新增 `HooksConfig` + `AppConfig.Hooks`
- [ ] `Security/SecureBatchToolExecutor.cs`——构造函数加 `HookEngine?` + OnBeforeExecuteAsync 追加 tool_pre_exec
- [ ] `Agent/AgentLoop.cs`——构造函数加 `HookEngine?` + RunCoreAsync 追加 fire 调用（round/message/tool_post/system_error/system_compress）
- [ ] `App/App.cs`——构造 HookLoader → HookEngine + 条件注入 + system_startup/shutdown
- [ ] `Tui/TerminalApp.cs`——构造函数加 `HookEngine?` + StartAgentRound 传参 + session_start/end + SetSubAgentRunner
- [ ] `example.parrotcode.yaml`——新增 `hooks:` 配置节

### 新增文件

- [ ] `.parrotcode/hooks.yaml.example`——Hook 规则示例文件

### 零改动验证

- [ ] `Agent/AgentEvent.cs` git diff 为空
- [ ] `Agent/BatchToolExecutor.cs` git diff 为空
- [ ] `Agent/IAgentEventSink.cs` git diff 为空
- [ ] `Security/SecurityGuard.cs` git diff 为空
- [ ] `Security/Blacklist.cs` / `PathSandbox.cs` / `SecurityPolicy.cs` git diff 为空
- [ ] `Tools/` 全部 git diff 为空
- [ ] `Conversation/` 全部 git diff 为空
- [ ] `SubAgent/` 全部 git diff 为空
- [ ] `Skills/` 全部 git diff 为空
- [ ] `Commands/` 全部 git diff 为空
- [ ] `Mcp/` 全部 git diff 为空
- [ ] `Hooks/Models.cs` / `Conditions.cs` / `Templates.cs` / `Loader.cs` / `Engine.cs` git diff 为空（15a 交付不变）

### 测试

- [ ] 单测：SecureBatchToolExecutorHook（9 项）
- [ ] 单测：AgentLoopHook（11 项）
- [ ] 单测：ActionExecutor sub_agent（4 项）
- [ ] 单测：TerminalApp + App 装配（8 项）
- [ ] 单测：HooksConfig（4 项）
- [ ] `dotnet build` 0 error 0 warning
- [ ] 全部既有测试通过（含 15a 的 73 项测试）
- [ ] 端到端 E1：tool_pre_exec + shell（git stash before write_file）
- [ ] 端到端 E2：tool_pre_exec + prompt_inject（拦截写系统目录）
- [ ] 端到端 E3：tool_post_exec + http（webhook 审计）
- [ ] 端到端 E4：session_end + sub_agent（会话总结）
- [ ] 端到端 E5：system_compress + shell 通知
- [ ] 端到端 E6：enable: false 旁路
- [ ] 端到端 E7：once: true 只触发一次
