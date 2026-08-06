# 迭代 7b：HITL 交互层（IHitlGate + HitlPrompt + Agent 改造）— 详细设计

> 状态：[已完成]
> 拆分自 `iter-07-design.md`（保留为整体参考，不删除）。
> 7b 聚焦**HITL 交互层**——给 7a 的展示层接入"人在回路"确认：`IHitlGate` 双向通道 + `HitlPrompt`（方案 C：Live 内渲染 + `ReadKey`）+ `BatchToolExecutor` 改造 + `AgentLoop` 拒绝转发 + `TuiApp` 接线。
>
> 前置：迭代 7a 已交付展示层——`TuiApp`（Live 流式渲染 + 状态栏 + 输入循环）+ `EventRenderer`（含 `SetTransient` 扩展点预留）+ `StatusBar` + `InputReader` + `ConsoleEventRenderer`（降级）+ `IConsole` 抽象 + `SecurityLevel` 占位 + `TuiConfig`（Mode/ShowStatusBar/ContextWindowTokens）。Agent 层完全不变（`BatchToolExecutor` 不注入 `IHitlGate`，`AgentLoop` 不产生 `ToolBlockedEvent`）。`ToolBlockedEvent` 在 `AgentEvent` 已定义但 7a 主路径不产生，`EventRenderer` 预留其渲染分支。
>
> **7b 的核心原则：最小化 Agent 层改动 + 复用 7a 扩展点**。`BatchToolExecutor` 加可选 `IHitlGate?` 参数（null 时等价 7a）；`AgentLoop` 仅工具结果处理段区分 `ToolBlockedEvent`/`ToolResultEvent`；`EventRenderer.SetTransient`（7a 已预留）承载 HITL 提示渲染；`HitlPrompt` 用方案 C（Live 内渲染 + `ReadKey`）不暂停 Live，规避 7a 发现的"Live 与 `AnsiConsole.Write` 互斥"陷阱。

## 一、概述

迭代 7a 让 Agent 过程"可视"——Live 流式渲染 + 状态栏 + Tab 补全。但 Agent "调工具"仍不可控——`write_file` / `run_command` 等有副作用的工具**直接执行**，用户无法在执行前干预。迭代 7b 把"可控"这件事补齐：**HITL（人在回路）** 让危险操作执行前先经用户确认。

1. **`IHitlGate` 双向通道**：HITL 需要返回值（用户决策），而事件流是 fire-and-forget 单向通道（`sink.WriteAsync` 返回 `ValueTask` 不携带决策）。把 HITL 混入事件流（"emit `HitlRequestEvent` + `await Task<HitlDecision>`"）会破坏事件流的单向清晰性。故本迭代引入 `IHitlGate` 接口作为独立的双向通道：`BatchToolExecutor` 通过它**请求**决策，TUI 通过它**响应**决策。事件流只负责旁路通知（拒绝时 emit `ToolBlockedEvent`）。

2. **`HitlDecision` + `HitlChoice`**：决策四选项——`AllowOnce`（本次允许，下次同工具再问）/ `AllowSession`（会话级允许，进程内同工具不再问）/ `AllowPermanent`（永久允许，7b 退化为会话级，迭代 10 接入配置文件持久化）/ `Deny`（拒绝，`ToolResult.Fail` 回灌 LLM）。四键对应 A/S/P/D。

3. **`HitlPrompt`（方案 C）**：`IHitlGate` 的 Spectre 实现。**不暂停 Live**——HITL 提示作为 Live 活跃区一部分渲染（通过 7a 预留的 `EventRenderer.SetTransient`），`Console.ReadKey` 读 A/S/P/D（输入与 Live 输出互不干扰）。决策结果也通过 `SetTransient` 追加到活跃区（"✓ 已允许" / "✗ 已拒绝"）。规避 7a 发现的"Live 期间 `AnsiConsole.Prompt` 会与 `AnsiConsole.Write` 字节交错"陷阱——全程不调 `AnsiConsole.Prompt`，只调 `ctx.UpdateTarget` + `Console.ReadKey`。

4. **`BatchToolExecutor` 改造**：构造加可选参数 `IHitlGate? hitlGate = null`。Write 组每个工具执行前调 `hitlGate.RequestAsync(call, ct)`；`null` 时等价 7a（直接执行，回归保护）；`Deny` 时返回 `ToolResult.Fail("用户拒绝执行")`，不执行工具。预留 `OnBeforeExecuteAsync` 虚方法给迭代 8 `SecurityGuard` 接入。

5. **`AgentLoop` 改造**：工具结果处理段区分 `ToolBlockedEvent`（HITL 拒绝）/ `ToolResultEvent`（执行成功或失败）。启发式判断 `IsHitlDenial(result)`——错误信息含"用户拒绝"或"被拦截"标记为 blocked。拒绝原因回灌历史（`history.AddTool("错误：用户拒绝...", call.Id)`），让 LLM 自我修正（如换工具或放弃）。

6. **`TuiApp` 接线**：装配 `HitlPrompt`，注入 `render`/`readKey` 回调。`render` 回调调 `EventRenderer.SetTransient` + `ctx.UpdateTarget`（刷新 Live 活跃区显示 HITL 提示）；`readKey` 回调调 `IConsole.ReadKey`（读 A/S/P/D）。`TuiApp` 用字段持有 `LiveDisplayContext?` 与 `EventRenderer`，让回调能跨 `StartAsync` 边界访问 Live 上下文。

7. **`TuiConfig.EnableHitl` + `SecurityConfig` 占位**：配置加 `enable_hitl`（默认 true）控制是否启用 HITL；`security.level`（默认 normal）作为迭代 8 占位——7b 的 `SecurityLevel` 仍只是状态栏显示，真实拦截在迭代 8。`enable_hitl: false` 时注入 `NullHitlGate`，等价 7a 行为。

本迭代**刻意保持**：
- **不引入安全层**：黑名单 / 沙箱 / 三档权限的真实拦截在迭代 8。`SecurityLevel` 仍仅状态栏显示；HITL 是"所有 Write 工具必问"的简化策略（而非迭代 8 的"Normal 模式下读放行、写询问"精细化）。`OnBeforeExecuteAsync` hook 默认返回 null（不拦截），迭代 8 `SecurityGuard` 覆写。
- **不持久化 `AllowPermanent`**：7b 无配置文件持久化，`AllowPermanent` 与 `AllowSession` 行为一致（都进会话缓存）。迭代 10 接入配置文件后区分。
- **不做命令系统**：`/clear` / `/exit` 仍硬编码分发（7a 已实现），7b 不动。`IUiControl` 加 `RequestHitlAsync` 方法签名（预留），7b 的 `TuiApp` 不实现此接口（无命令系统调用方）。
- **不改事件类型**：`AgentEvent` 的 12 种类型**不变**。`ToolBlockedEvent`（7a 已定义）在 7b 启用产生，`EventRenderer` 的渲染分支（7a 已预留）启用。迭代 8/10 不再扩事件类型。
- **不改 `ChannelEventSink` / `IAgentEventSink`**：事件流通道不变，消费者仍是 `TuiApp`。
- **HITL 不走事件流**：`HitlRequestEvent` 不引入——拒绝用 `ToolBlockedEvent`（已有），允许用 `ToolResultEvent`（已有）。决策本身走 `IHitlGate`，保持事件流纯展示通知语义。
- **不暂停 Live**：方案 C（`iter-07-design.md` 4.4.3 决策）——HITL 提示作为 Live 活跃区一部分渲染，`Console.ReadKey` 读按键。规避 7a 发现的 Live 与 `AnsiConsole.Write`/`Prompt` 互斥陷阱。

> **与 7a 实现的关键约束**：7a 实现中发现：
> 1. Live 期间不能调 `AnsiConsole.Write`——会与 ANSI 重绘序列字节交错（`security=Normal` 变成 `??rity=Normal`）。
> 2. Live 期间不能让 logger 写 stderr——会卡在 Live 区域中间破坏布局（7a 已通过 Live 模式下不给 AgentLoop 传 logger 修复）。
> 3. Live 最后一帧留在屏幕作为本轮输出（不提交到滚动历史），简化状态机。
> 4. Live 初始 target 用状态栏而非空 Text——避免从 1 行扩展到多行时的重绘残留。
>
> 7b 的 `HitlPrompt` 设计严格遵循这些约束：**全程不调 `AnsiConsole.Prompt` / `AnsiConsole.Write`**，只用 `ctx.UpdateTarget`（Live 内部刷新）+ `Console.ReadKey`（输入与输出分离）。

## 二、学习目标

1. **HITL 双向通道与单向事件流的区别**：理解"事件流是单向通知（`WriteAsync` 返回 `ValueTask`），HITL 是双向请求/响应（`RequestAsync` 返回 `Task<HitlDecision?>`）"的本质区别。把需要返回值的交互独立成接口（`IHitlGate`），保持事件流纯展示通知——两者各司其职，不混用。理解"通道分离"设计原则。

2. **`TaskCompletionSource<T>` 同步原语**：`IHitlGate.RequestAsync` 内部用 `TaskCompletionSource<HitlDecision>`——请求时创建 TCS，UI 响应时 `TrySetResult`，Agent 侧 `await`。理解这是 .NET 把"回调/事件"包装成 `awaitable` 的标准模式（与迭代 5/6 的 `Process.Exited` + TCS 同理）。**7b 简化版**：因 AgentLoop 与 TuiApp 在同一 `await` 链上（同线程同步），`RequestAsync` 内部直接 `render` + `readKey` + 返回 `Task.FromResult`，无需 TCS 跨线程。若未来 Live 独立线程，需用 TCS 跨线程传递。

3. **方案 C：Live 内渲染 + ReadKey（不暂停 Live）**：理解 7a 发现的"Live 与 `AnsiConsole.Prompt`/`Write` 互斥"陷阱——Prompt 会写 ANSI 序列与 Live 重绘序列交错。方案 C 的解法：HITL 提示作为 Live 渲染目标的一部分（通过 `SetTransient` + `ctx.UpdateTarget`），`Console.ReadKey` 读按键（输入与 Live 输出分离）。这样 Live 持续运行，状态机简单，无需 Stop/Restart。

4. **决策缓存与作用域**：`AllowSession`（会话级，进程内同工具不再问）/ `AllowPermanent`（持久级，7b 退化为会话级）。用 `ConcurrentDictionary<string, byte>` 存会话缓存，键是工具名——同会话同工具只问一次。理解"作用域"在 HITL 设计中的权衡——问太少不安全，问太频繁打扰。

5. **Agent 层最小化改动 + 回归保护**：`BatchToolExecutor` 加可选参数 `IHitlGate? = null`，旧调用不传则 null（等价 7a）。`AgentLoop` 仅工具结果处理段加 `if (IsHitlDenial) emit ToolBlockedEvent else emit ToolResultEvent`。理解"可选依赖 + 启发式判断"如何让 Agent 层改动最小化、可回归。

6. **启发式判断的局限与未来改进**：`IsHitlDenial(result)` 用错误信息字符串匹配（含"用户拒绝"或"被拦截"）判断"拒绝 vs 执行失败"。不严谨——迭代 8 可让 `ToolResult` 加 `Blocked` 标志或引入 `ToolBlockedResult` 派生类型。理解"启发式作为过渡方案"的权衡——降低改动面 vs 严谨性。

7. **扩展点复用**：7a 预留的 `EventRenderer.SetTransient` 在 7b 被复用承载 HITL 提示。理解"为未来留口子但不提前实现"的设计——7a 定义 `SetTransient` + `BuildActive` 含 transient 逻辑但不调用，7b 的 `HitlPrompt` 调用它，**不用改 `EventRenderer` 核心结构**。

8. **回调注入与跨 Live 上下文通信**：`HitlPrompt` 的 `render`/`readKey` 回调由 `TuiApp` 注入。`render` 需访问 `EventRenderer` 与 `LiveDisplayContext`（`StartAsync` 回调内局部变量），`TuiApp` 用字段持有它们让回调跨边界访问。理解"用字段持有闭包外部状态"的常见模式。

## 三、范围

### 3.1 本迭代包含（In Scope）

| 项 | 说明 |
| --- | --- |
| `Tui/HitlDecision.cs` | `HitlChoice` 枚举（`AllowOnce`/`AllowSession`/`AllowPermanent`/`Deny`）+ `HitlDecision` record（`Choice` + `Reason` + `IsAllowed`/`ShouldCache` 属性 + 静态工厂） |
| `Tui/IHitlGate.cs` | HITL 双向通道接口（`RequestAsync` + `IsAllowedThisSession`）+ `NullHitlGate`（默认放行，等价 7a） |
| `Tui/HitlPrompt.cs` | `IHitlGate` 的 Spectre 实现（方案 C：`render`/`readKey` 回调 + 会话缓存） |
| `Agent/BatchToolExecutor.cs` | 改：构造加可选 `IHitlGate? hitlGate = null`；Write 组执行前调 `RequestAsync`，`Deny` 时返回 `Fail` 不执行；预留 `OnBeforeExecuteAsync` 虚方法给迭代 8 |
| `Agent/AgentLoop.cs` | 改：工具结果处理段区分 `ToolBlockedEvent`（HITL 拒绝）/ `ToolResultEvent`；`IsHitlDenial` 启发式判断 |
| `Tui/TuiApp.cs` | 改：装配 `HitlPrompt` + `render`/`readKey` 回调注入；字段持有 `LiveDisplayContext?` 与 `EventRenderer` 供回调访问；`enable_hitl: false` 时注入 `NullHitlGate` |
| `Tui/IUiControl.cs` | 改：加 `RequestHitlAsync` 方法签名（预留，7b 不实现） |
| `Config/Models.cs` | 改：`TuiConfig` 加 `EnableHitl`（默认 true）；`AppConfig` 加 `SecurityConfig`（占位，`Level` 字段，默认 normal） |
| `example.parrotcode.yaml` | 改：`tui:` 节加 `enable_hitl: true`；加 `security:` 节示例（`level: normal`） |
| `Program.cs` | 改：从 `SecurityConfig.Level` 解析 `SecurityLevel` 传给 `TuiApp`（7b 仅状态栏显示，不拦截） |
| 单元测试 | `HitlDecisionTests` / `HitlPromptTests`（用假 render/readKey 回调）/ `NullHitlGateTests` / `BatchToolExecutorHitlTests`（注入假 IHitlGate）/ `AgentLoopHitlTests`（MockProvider 脚本触发拒绝→ToolBlockedEvent）/ `TuiAppHitlIntegrationTests`（端到端，MockProvider 脚本 + 假 readKey） |

### 3.2 本迭代不包含（Out of Scope）

- **安全层（黑名单/沙箱/三档权限真实拦截）** → 迭代 8：`SecurityLevel` 仍仅状态栏显示；`OnBeforeExecuteAsync` hook 默认返回 null（不拦截）；HITL 是"所有 Write 必问"简化策略
- **`SecurityGuard` 管线** → 迭代 8：`OnBeforeExecuteAsync` 虚方法预留，迭代 8 子类化或委托覆写
- **`AllowPermanent` 持久化** → 迭代 10：7b 退化为会话级（与 `AllowSession` 行为一致），需配置文件支持
- **斜杠命令注册中心** → 迭代 10：`/help` / `/status` / `/clear` / `/exit` 仍 7a 硬编码分发，7b 不动
- **`IUiControl` 实现** → 迭代 10：7b 只加 `RequestHitlAsync` 方法签名，`TuiApp` 不实现此接口（无调用方）
- **会话持久化（JSONL）** → 迭代 10
- **上下文截断/摘要** → 迭代 9
- **AlternateScreen 全屏** → 进阶练习
- **多行输入/语法高亮/输入历史** → 可选扩展
- **thinking 折叠渲染** → 进阶练习
- **AnthropicProvider** → 后续迭代
- **HITL 预览（diff 显示）** → 进阶练习

### 3.3 与迭代 7a 的边界

| 迭代 7a | 迭代 7b |
| --- | --- |
| `EventRenderer.SetTransient` 预留（不调用） | `HitlPrompt` 调 `SetTransient(prompt)` 渲染 HITL 提示到活跃区 |
| `TuiApp` 构造 `BatchToolExecutor` 不传 `IHitlGate` | 加 `HitlPrompt` 装配 + `render`/`readKey` 回调注入 |
| `BatchToolExecutor` 不变 | 注入 `IHitlGate?`（可选，null 时等价 7a）+ `OnBeforeExecuteAsync` 虚方法预留 |
| `AgentLoop` 工具结果统一 `ToolResultEvent` | 区分 `ToolBlockedEvent`（HITL 拒绝）/ `ToolResultEvent` |
| `ToolBlockedEvent` 不产生 | HITL 拒绝时产生（`AgentLoop` emit） |
| `EventRenderer` 的 `ToolBlockedEvent` 渲染分支预留 | 启用（红色 ⛔ Panel） |
| `TuiConfig` 无 `EnableHitl` | 加 `EnableHitl` 字段（默认 true） |
| `SecurityLevel` 硬编码 `Normal` | 加 `SecurityConfig.Level` 配置项（7b 仅解析与状态栏显示，不拦截） |
| `IUiControl` 无 `RequestHitlAsync` | 加方法签名（预留，不实现） |
| `/help` / `/status` / `/clear` / `/exit` 硬编码 | 不变（7b 不动命令分发） |
| Live 最后一帧留下（不提交历史） | 不变（HITL 提示随最后一帧留下） |

### 3.4 与迭代 8 的边界

| 迭代 7b | 迭代 8 |
| --- | --- |
| `SecurityLevel` 枚举，状态栏显示 + 配置解析 | `SecurityGuard` 真实拦截（黑名单/沙箱/三档权限） |
| HITL 是"所有 Write 工具必问"简化策略 | Normal 模式下读放行、写询问；Strict 白名单；Permissive 仅黑名单 |
| `OnBeforeExecuteAsync` 虚方法默认返回 null（不拦截） | `SecurityGuard` 覆写 hook 返回拒绝原因 |
| HITL 决策缓存仅会话级 | 配置文件持久化 `AllowPermanent` |
| 黑名单不生效 | `rm -rf /` 等即使 Permissive 也拦 |

### 3.5 与迭代 10 的边界

| 迭代 7b | 迭代 10 |
| --- | --- |
| `IUiControl.RequestHitlAsync` 方法签名（预留） | 命令系统通过 `IUiControl` 调用 UI |
| `AllowPermanent` 退化为会话级 | 持久化到配置文件跨会话生效 |
| `/help` / `/status` 硬编码 | `Commands/` 注册中心 + 反射扫描 + 别名 |

## 四、架构设计

### 4.1 模块结构（迭代 7b 增量）

```
ParrotCode.Net/
├── Program.cs                 # 改：解析 SecurityConfig.Level → SecurityLevel 传 TuiApp
├── App/
│   └── App.cs                 # 不变（7a 已委托 TuiApp）
├── Config/
│   └── Models.cs              # 改：TuiConfig 加 EnableHitl；AppConfig 加 SecurityConfig
├── Agent/
│   ├── AgentLoop.cs           # 改：IsHitlDenial + ToolBlockedEvent 转发
│   ├── BatchToolExecutor.cs   # 改：注入 IHitlGate? + OnBeforeExecuteAsync 虚方法
│   ├── ChatChunk.cs           # 不变
│   ├── AgentEvent.cs          # 不变（12 种事件稳定，ToolBlockedEvent 启用产生）
│   ├── IAgentEventSink.cs     # 不变
│   ├── ChannelEventSink.cs    # 不变
│   └── ToolCallAccumulator.cs # 不变
├── Conversation/              # 全部不变
├── Providers/                 # 全部不变
├── Tools/                     # 全部不变（6 个工具）
└── Tui/
    ├── HitlDecision.cs        # 新增：Choice 枚举 + Decision record
    ├── IHitlGate.cs           # 新增：HITL 双向通道接口 + NullHitlGate
    ├── HitlPrompt.cs           # 新增：方案 C 实现（render/readKey 回调 + 会话缓存）
    ├── TuiApp.cs               # 改：装配 HitlPrompt + render/readKey 回调接线
    ├── EventRenderer.cs       # 不变（SetTransient 7a 已预留，7b 复用）
    ├── StatusBar.cs           # 不变
    ├── InputReader.cs          # 不变
    ├── ConsoleEventRenderer.cs# 不变（ToolBlockedEvent 渲染分支 7a 已预留）
    ├── IConsole.cs             # 不变
    ├── IUiControl.cs          # 改：加 RequestHitlAsync 方法签名（预留）
    └── SecurityLevel.cs       # 不变（7a 已定义枚举）
```

> 命名空间约定沿用迭代 1-7a：所有源文件统一 `namespace ParrotCode`，文件夹仅作物理组织。

### 4.2 调用流程（TUI 主循环 + 事件消费 + HITL）

```
┌──────────────────────────────────────────────────────────────────────┐
│  TuiApp.RunAsync（7a 基础 + 7b HITL 接线）                            │
│  while (!ct.IsCancellationRequested):                                │
│      input = InputReader.ReadLineWithCompletionAsync(ct)             │
│      if input is /clear|/exit|/help|/status: 硬编码分发; continue     │
│      history.AddUser(input)                                          │
│      # —— 装配 HitlPrompt（7b 新增）——                                 │
│      # render 回调：SetTransient + ctx.UpdateTarget（刷新 Live 活跃区）│
│      # readKey 回调：IConsole.ReadKey（读 A/S/P/D，与 Live 输出分离）  │
│      var hitlPrompt = new HitlPrompt(                                 │
│          render: r => { _renderer.SetTransient(r); _liveCtx?.UpdateTarget(...) }, │
│          readKey: ct => _console.ReadKey(true).Key);                  │
│      var batchExecutor = new BatchToolExecutor(executor, registry,    │
│          maxParallelism, hitlPrompt, logger);                         │
│      var agentLoop = new AgentLoop(provider, registry, batchExecutor, ...)│
│      var sink = new ChannelEventSink()                               │
│      var agentTask = agentLoop.RunAsync(history, sink, ct)            │
│      # —— Live 流式渲染活跃区（7a 不变 + SetTransient 由 HitlPrompt 触发）── │
│      await AnsiConsole.Live(statusBar.Render()).StartAsync(async ctx => { │
│          _liveCtx = ctx;  # 7b：字段持有供 render 回调访问              │
│          await foreach (evt in sink.Reader.ReadAllAsync(ct)):         │
│              if evt is RoundStartEvent(r): statusBar.CurrentRound = r │
│              _renderer.Render(evt)                                    │
│              ctx.UpdateTarget(_renderer.BuildActive(statusBar))       │
│      })                                                              │
│      await agentTask                                                  │
└──────────────────────────────────────────────────────────────────────┘
                                  │
                                  ▼
┌──────────────────────────────────────────────────────────────────────┐
│  AgentLoop.RunAsync（7a 基础 + 7b ToolBlockedEvent 转发）             │
│  for round in 1..maxRounds:                                          │
│      sink.WriteAsync(RoundStartEvent(round))                         │
│      流式 LLM → 累积文本/tool_calls → sink.Write(TextDeltaEvent)     │
│      toolCalls = tcAcc.Build()                                       │
│      history.AddAssistant(...)                                       │
│      if no toolCalls: sink.Write(AgentDoneEvent); return            │
│      sink.Write(ToolCallStartEvent(call))  # 每个                    │
│      results = batchExecutor.ExecuteAsync(toolCalls, ct)             │
│          ┌─ BatchToolExecutor 内部（7b 改造）─────────────────┐     │
│          │ Read 组: Task.WhenAll（不问 HITL，与 7a 一致）       │     │
│          │ Write 组: foreach call:                          │       │
│          │   blocked = await OnBeforeExecuteAsync(call, ct)  │       │
│          │   if blocked is not null: results[i] = blocked    │       │
│          │     continue  # 迭代 8 SecurityGuard 拦截          │       │
│          │   if hitlGate is not null:                        │       │
│          │     decision = await hitlGate.RequestAsync(call)  │       │
│          │     if decision is { IsAllowed: false }:          │       │
│          │       results[i] = ToolResult.Fail(decision.Reason)│      │
│          │       continue  # 不执行工具                       │       │
│          │   results[i] = await executor.ExecuteAsync(call)  │       │
│          └──────────────────────────────────────────────────┘       │
│      foreach (call, result):                                         │
│          # 7b 新增：区分 ToolBlockedEvent / ToolResultEvent           │
│          if !result.Success && IsHitlDenial(result):                 │
│              sink.Write(ToolBlockedEvent(call, result.Error))        │
│          else:                                                       │
│              sink.Write(ToolResultEvent(call, result))              │
│          history.AddTool(result.Success ? result.Content :          │
│              $"错误：{result.Error}", call.Id)  # 拒绝原因回灌 LLM  │
│      sink.Write(RoundEndEvent(round))                                │
│  sink.Write(MaxRoundsReachedEvent)                                   │
└──────────────────────────────────────────────────────────────────────┘
                                  │
                                  ▼
┌──────────────────────────────────────────────────────────────────────┐
│  IHitlGate.RequestAsync(call, ct)  ← BatchToolExecutor 调用（7b）    │
│  ┌─ HitlPrompt 实现（方案 C：不暂停 Live）──────────────────┐       │
│  │ if 会话缓存命中: return AllowSession（不弹提示）          │     │
│  │ if ct.IsCancellationRequested: return Deny("已取消")     │     │
│  │ # 1. 渲染 HITL 提示 Panel 到 Live 活跃区                  │     │
│  │ promptPanel = Panel("⚠ 即将执行 {call.Name}(...)         │     │
│  │               按 A=本次 S=会话 P=永久 D=拒绝")           │     │
│  │ _render(promptPanel)  # → SetTransient + ctx.UpdateTarget │     │
│  │ # 2. 读按键（A/S/P/D），与 Live 输出分离                  │     │
│  │ key = _readKey(ct)  # → IConsole.ReadKey(true).Key       │     │
│  │ choice = key switch { A→AllowOnce, S→AllowSession,       │     │
│  │                       P→AllowPermanent, _→Deny }         │     │
│  │ # 3. 会话缓存（AllowSession/AllowPermanent）              │     │
│  │ if choice is AllowSession or AllowPermanent:             │     │
│  │     _sessionCache[call.Name] = 0                         │     │
│  │ # 4. 渲染决策结果到 Live 活跃区                            │     │
│  │ resultMarkup = choice == Deny ? "✗ 已拒绝" : "✓ 已允许"  │     │
│  │ _render(resultMarkup)  # → SetTransient + ctx.UpdateTarget │     │
│  │ return new HitlDecision(choice, reason)                  │     │
│  └────────────────────────────────────────────────────────┘       │
└──────────────────────────────────────────────────────────────────────┘
```

### 4.3 关键类型设计

#### 4.3.1 `HitlDecision` + `HitlChoice`

```csharp
namespace ParrotCode;

/// <summary>
/// HITL 决策选项。四键对应 A/S/P/D。
/// </summary>
public enum HitlChoice
{
    /// <summary>允许本次（A）。下次同工具再问。</summary>
    AllowOnce,

    /// <summary>允许本会话（S）。进程内同工具不再问。</summary>
    AllowSession,

    /// <summary>允许永久（P）。跨会话不再问（7b 退化为会话级，迭代 10 持久化）。</summary>
    AllowPermanent,

    /// <summary>拒绝（D）。ToolResult.Fail 回灌 LLM。</summary>
    Deny
}

/// <summary>
/// HITL 决策结果。Reason 仅 Deny 时填充（回灌给 LLM 的拒绝原因）。
/// </summary>
public sealed record HitlDecision(HitlChoice Choice, string? Reason = null)
{
    /// <summary>是否允许执行。</summary>
    public bool IsAllowed => Choice != HitlChoice.Deny;

    /// <summary>是否应缓存（会话或持久级）。</summary>
    public bool ShouldCache => Choice is HitlChoice.AllowSession or HitlChoice.AllowPermanent;

    public static HitlDecision Deny(string reason) => new(HitlChoice.Deny, reason);
    public static HitlDecision AllowOnce => new(HitlChoice.AllowOnce);
}
```

> **设计要点**：
> - **`IsAllowed` / `ShouldCache` 派生属性**：`BatchToolExecutor` 用 `IsAllowed` 判断是否执行，`HitlPrompt` 用 `ShouldCache` 判断是否进缓存。语义清晰，调用方不用重复 `switch`。
> - **`Deny(reason)` 静态工厂**：拒绝时必须带原因（回灌 LLM），工厂强制约束。`AllowOnce` 无参工厂因无 Reason。
> - **`Reason` nullable**：仅 `Deny` 时有值，`Allow*` 时 null。避免"允许也带原因"的语义混淆。

#### 4.3.2 `IHitlGate`（HITL 双向通道）+ `NullHitlGate`

```csharp
namespace ParrotCode;

/// <summary>
/// HITL（人在回路）双向通道抽象。
/// BatchToolExecutor 在 Write 组工具执行前调用 RequestAsync 请求用户决策；
/// TUI 实现（HitlPrompt）弹框收集用户选择并完成返回的 Task。
///
/// 与 IAgentEventSink 的区别：
/// - IAgentEventSink 是单向 fire-and-forget（WriteAsync 返回 ValueTask，不携带返回值）。
/// - IHitlGate 是双向请求/响应（RequestAsync 返回 Task&lt;HitlDecision?&gt;）。
/// 把需要返回值的交互独立成接口，保持事件流纯展示通知语义。
///
/// 返回 nullable HitlDecision?：
/// - null 表示"无需 HITL"（Read 工具或缓存命中），调用方直接执行。
/// - 非 null 表示用户已决策（AllowOnce/AllowSession/AllowPermanent/Deny）。
/// 用 nullable 区分"未询问"与"询问结果"，避免 Deny 被误判为"未询问"。
///
/// 7b 只有一个实现（HitlPrompt，方案 C）。
/// 测试用 NullHitlGate（直接 null）与假 IHitlGate（返回预设 Decision）。
/// 迭代 8 SecurityGuard 可作为前置拦截器（在 IHitlGate 之前，通过 OnBeforeExecuteAsync hook）。
/// </summary>
public interface IHitlGate
{
    /// <summary>
    /// 请求用户对某次工具调用的决策。
    /// 实现应阻塞（await）直到用户响应；null 表示无需询问（如 Read 工具或缓存命中）。
    /// 调用方（BatchToolExecutor）await 此方法，期间 AgentLoop 暂停。
    /// </summary>
    /// <param name="call">待执行的工具调用。</param>
    /// <param name="cancellationToken">取消令牌（用户 Ctrl+C 时取消等待）。</param>
    /// <returns>用户决策；null 表示无需 HITL（直接执行）。</returns>
    Task<HitlDecision?> RequestAsync(ToolCall call, CancellationToken cancellationToken);

    /// <summary>
    /// 查询某工具是否已被会话级允许（避免重复弹框）。
    /// BatchToolExecutor 在调 RequestAsync 前可先查缓存（RequestAsync 内部也查，双重保险）。
    /// </summary>
    bool IsAllowedThisSession(string toolName);
}

/// <summary>
/// 默认放行（无 HITL）。用于配置 enable_hitl: false 或终端非交互时。
/// 等价于迭代 7a 的行为——所有工具直接执行。
/// </summary>
public sealed class NullHitlGate : IHitlGate
{
    public Task<HitlDecision?> RequestAsync(ToolCall call, CancellationToken cancellationToken) =>
        Task.FromResult<HitlDecision?>(null);
    public bool IsAllowedThisSession(string toolName) => false;
}
```

> **设计要点**：
> - **`RequestAsync` 返回 `Task<HitlDecision?>`**：`null` 表示"无需 HITL"（Read 工具或缓存命中），调用方直接执行；非 null 表示用户已决策。nullable 区分"未询问"与"询问结果"。
> - **`IsAllowedThisSession` 独立查询**：让 `BatchToolExecutor` 在调 `RequestAsync` 前先查缓存，命中则跳过弹框（`RequestAsync` 内部也查，双重保险）。分离查询让逻辑更清晰——缓存是状态，请求是动作。
> - **`NullHitlGate`**：等价于 7a 行为。配置 `enable_hitl: false` 或降级模式注入此实现，回归保护。

#### 4.3.3 `HitlPrompt`（方案 C：Live 内渲染 + ReadKey）

```csharp
using System.Collections.Concurrent;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace ParrotCode;

/// <summary>
/// IHitlGate 的 Spectre.Console 实现（方案 C：不暂停 Live）。
///
/// 收到请求时：
/// 1. 会话缓存命中 → 直接返回 AllowSession（不弹提示）
/// 2. 取消 → 返回 Deny("已取消")
/// 3. 渲染 HITL 提示 Panel 到 Live 活跃区（通过 _render 回调调 SetTransient + ctx.UpdateTarget）
/// 4. 读按键（A/S/P/D），通过 _readKey 回调（Console.ReadKey，与 Live 输出分离）
/// 5. AllowSession/AllowPermanent 记入 _sessionCache
/// 6. 渲染决策结果到 Live 活跃区（"✓ 已允许" / "✗ 已拒绝"）
/// 7. 返回 HitlDecision
///
/// 方案 C 关键：全程不调 AnsiConsole.Prompt / AnsiConsole.Write——
/// 只用 _render 回调（→ ctx.UpdateTarget，Live 内部刷新）+ _readKey 回调（→ Console.ReadKey，输入）。
/// 规避 7a 发现的"Live 与 AnsiConsole.Write/Prompt 字节交错"陷阱。
///
/// 线程模型：7b 简化为同线程同步——AgentLoop 与 TuiApp 在同一 await 链上
/// （TuiApp await agentTask，AgentLoop 内 await batchExecutor.ExecuteAsync，
/// BatchToolExecutor 内 await hitlGate.RequestAsync）。
/// RequestAsync 内部直接调 _render + _readKey，返回 Task.FromResult。
/// 若未来 Live 独立线程，需用 TaskCompletionSource 跨线程传递请求。
/// </summary>
public sealed class HitlPrompt : IHitlGate
{
    private readonly ConcurrentDictionary<string, byte> _sessionCache = new();
    private readonly Action<IRenderable?> _render;
    private readonly Func<CancellationToken, ConsoleKey> _readKey;

    /// <param name="render">渲染回调：把 IRenderable 设为 EventRenderer 的 transient + 触发 Live 刷新。
    /// 传 null 时只读按键不渲染（测试用）。</param>
    /// <param name="readKey">读按键回调：返回 ConsoleKey.A/S/P/D 等。传 null 时默认返回 D（测试用）。</param>
    public HitlPrompt(Action<IRenderable?>? render = null, Func<CancellationToken, ConsoleKey>? readKey = null)
    {
        _render = render ?? (_ => { });
        _readKey = readKey ?? (_ => ConsoleKey.D);
    }

    public bool IsAllowedThisSession(string toolName) =>
        _sessionCache.ContainsKey(toolName);

    public Task<HitlDecision?> RequestAsync(ToolCall call, CancellationToken cancellationToken)
    {
        // 1. 会话缓存命中——直接返回 AllowSession（不弹提示）
        if (_sessionCache.ContainsKey(call.Name))
            return Task.FromResult<HitlDecision?>(new HitlDecision(HitlChoice.AllowSession));

        // 2. 取消时立即返回 Deny（避免 ReadKey 阻塞取消）
        if (cancellationToken.IsCancellationRequested)
            return Task.FromResult<HitlDecision?>(HitlDecision.Deny("已取消"));

        // 3. 渲染 HITL 提示 Panel 到 Live 活跃区
        var promptPanel = new Panel(new Markup(
            $"[yellow]⚠ 即将执行[/] [cyan]{Markup.Escape(call.Name)}[/]" +
            $"([grey]{Markup.Escape(Truncate(call.Input.GetRawText(), 80))}[/])\n" +
            $"[grey]按 A=本次 S=会话 P=永久 D=拒绝[/]"))
        {
            Header = new PanelHeader("[yellow]HITL 确认[/]"),
            BorderStyle = new Style(foreground: Color.Yellow),
            Padding = new Padding(2, 0, 2, 0)
        };
        _render(promptPanel);

        // 4. 读按键（A/S/P/D），与 Live 输出分离
        var key = _readKey(cancellationToken);
        var choice = key switch
        {
            ConsoleKey.A => HitlChoice.AllowOnce,
            ConsoleKey.S => HitlChoice.AllowSession,
            ConsoleKey.P => HitlChoice.AllowPermanent,
            _ => HitlChoice.Deny  // 非 A/S/P/D 一律拒绝（安全默认）
        };

        // 5. 会话缓存（AllowSession/AllowPermanent）
        if (choice is HitlChoice.AllowSession or HitlChoice.AllowPermanent)
            _sessionCache[call.Name] = 0;  // 7b AllowPermanent 退化为会话级

        // 6. 渲染决策结果到 Live 活跃区
        var resultMarkup = new Markup(
            choice == HitlChoice.Deny
                ? "[red]✗ 已拒绝[/]"
                : $"[green]✓ 已允许（{choice}）[/]");
        _render(resultMarkup);

        // 7. 返回决策
        return Task.FromResult<HitlDecision?>(
            choice == HitlChoice.Deny
                ? HitlDecision.Deny("用户拒绝执行该工具")
                : new HitlDecision(choice));
    }

    private static string Truncate(string s, int max) =>
        s.Length <= max ? s : s[..(max - 3)] + "...";
}
```

> **设计要点**：
> - **方案 C：不暂停 Live**：`_render` 回调调 `EventRenderer.SetTransient` + `ctx.UpdateTarget`（TuiApp 注入），`_readKey` 回调调 `IConsole.ReadKey`（TuiApp 注入）。全程不调 `AnsiConsole.Prompt`/`Write`，规避 7a 发现的字节交错陷阱。
> - **`Action<IRenderable?>` 渲染回调**：传 `IRenderable`（提示 Panel/结果 Markup）或 `null`（清除 transient）。TuiApp 的回调实现：`r => { _renderer.SetTransient(r); _liveCtx?.UpdateTarget(_renderer.BuildActive(_statusBar)); }`。
> - **`Func<CancellationToken, ConsoleKey>` 读键回调**：返回 `ConsoleKey.A/S/P/D` 等。TuiApp 的回调实现：`ct => _console.ReadKey(true).Key`。`ReadKey(true)` 不回显（Live 负责显示提示）。
> - **`ConcurrentDictionary` 会话缓存**：线程安全（防御性，7b 同线程但保留并发安全）。键是工具名——同会话同工具只问一次。
> - **`AllowPermanent` 退化**：7b 无配置文件持久化，`AllowPermanent` 与 `AllowSession` 行为一致（都进会话缓存）。迭代 10 接入配置文件后区分。
> - **取消时返回 Deny**：`cancellationToken.IsCancellationRequested` 时不弹提示直接 Deny，避免 `ReadKey` 阻塞取消。Spectre.Console 的 `ReadKey` 不原生响应 CancellationToken，故前置检查。
> - **非 A/S/P/D 键一律 Deny**：安全默认——未知按键拒绝执行，避免误触放行。
> - **`Task.FromResult` 而非 `async`**：同线程同步，`RequestAsync` 内部直接调 `_render` + `_readKey`（同步阻塞），返回已完成的 Task。若未来 Live 独立线程，改用 `TaskCompletionSource`。
> - **回调可注入测试**：构造函数接受 `Action<IRenderable?>?` / `Func<CancellationToken, ConsoleKey>?`，测试用假回调断言渲染内容与按键映射，不依赖真实控制台。

#### 4.3.4 `BatchToolExecutor` 改造（注入 IHitlGate + OnBeforeExecuteAsync hook）

```csharp
using Microsoft.Extensions.Logging;

namespace ParrotCode;

/// <summary>
/// 分批工具执行器：按 ToolCategory 分组调度。
/// Read 组（幂等、无副作用）用 Task.WhenAll 并发，限流到 maxParallelism 防止 OOM；
/// Write 组（有副作用）顺序 await 避免竞态，迭代 7b 注入 IHitlGate? 在执行前询问用户。
/// 委托迭代 5 ToolExecutor 做单次执行（超时 + 异常捕获）。
/// 迭代 8 SecurityGuard 作为 OnBeforeExecuteAsync hook 接入（7b 预留默认返回 null）。
/// </summary>
public sealed class BatchToolExecutor
{
    private readonly ToolExecutor _executor;
    private readonly ToolRegistry _registry;
    private readonly int _maxParallelism;
    private readonly IHitlGate? _hitlGate;          // 7b 新增（null 时等价 7a）
    private readonly ILogger? _logger;

    public BatchToolExecutor(
        ToolExecutor executor,
        ToolRegistry registry,
        int maxParallelism = 5,
        IHitlGate? hitlGate = null,                   // 7b 新增（可选，null 时不问）
        ILogger? logger = null)
    {
        _executor = executor ?? throw new ArgumentNullException(nameof(executor));
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        if (maxParallelism < 1) throw new ArgumentOutOfRangeException(nameof(maxParallelism));
        _maxParallelism = maxParallelism;
        _hitlGate = hitlGate;
        _logger = logger;
    }

    public async Task<IReadOnlyList<ToolResult>> ExecuteAsync(
        IReadOnlyList<ToolCall> calls,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(calls);
        if (calls.Count == 0) return Array.Empty<ToolResult>();

        cancellationToken.ThrowIfCancellationRequested();

        var readIndices = new List<int>();
        var writeIndices = new List<int>();
        for (var i = 0; i < calls.Count; i++)
        {
            var tool = _registry.Get(calls[i].Name);
            if (tool is null || tool.Category != ToolCategory.Read)
                writeIndices.Add(i);
            else
                readIndices.Add(i);
        }

        var results = new ToolResult[calls.Count];

        // Read 组并发（不问 HITL——幂等无副作用，与 7a 一致）
        foreach (var batch in readIndices.Chunk(_maxParallelism))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var tasks = batch.Select(i => _executor.ExecuteAsync(calls[i], cancellationToken)).ToArray();
            var batchResults = await Task.WhenAll(tasks);
            for (var j = 0; j < batch.Length; j++)
                results[batch[j]] = batchResults[j];
        }

        // Write 组串行 + HITL（7b 新增）
        foreach (var i in writeIndices)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var call = calls[i];

            // 1. OnBeforeExecuteAsync hook（迭代 8 SecurityGuard 接入点，7b 默认返回 null）
            var blocked = await OnBeforeExecuteAsync(call, cancellationToken);
            if (blocked is not null)
            {
                results[i] = blocked;
                _logger?.LogInformation("工具 {Name} 被安全层拦截", call.Name);
                continue;
            }

            // 2. HITL 请求（7b 新增，hitlGate 非 null 时）
            if (_hitlGate is not null)
            {
                var decision = await _hitlGate.RequestAsync(call, cancellationToken);
                if (decision is { IsAllowed: false })
                {
                    results[i] = ToolResult.Fail(decision.Reason ?? "用户拒绝执行");
                    _logger?.LogInformation("HITL 拒绝工具 {Name}", call.Name);
                    continue;
                }
                // AllowOnce/AllowSession/AllowPermanent → 继续执行
            }

            results[i] = await _executor.ExecuteAsync(call, cancellationToken);
        }

        return results;
    }

    /// <summary>
    /// 迭代 8 接入点：SecurityGuard 覆写此方法返回 ToolResult.Fail 拦截。
    /// 7b 默认实现返回 null（不拦截）。预留虚方法供迭代 8 子类化或委托。
    /// 顺序：OnBeforeExecuteAsync（安全层）→ HITL（用户决策）→ 执行。
    /// 安全层拒绝时不问用户（避免打扰已拦截的操作）。
    /// </summary>
    protected virtual Task<ToolResult?> OnBeforeExecuteAsync(ToolCall call, CancellationToken ct) =>
        Task.FromResult<ToolResult?>(null);
}
```

> **设计要点**：
> - **`IHitlGate?` 可选注入**：null 时等价于 7a（直接执行）。配置 `enable_hitl: false` 或降级模式注入 null。
> - **HITL 仅对 Write 组**：Read 组（`read_file`/`glob`/`grep`）幂等无副作用，不问 HITL——这是"简化策略"。迭代 8 Normal 模式更精细（读放行、写询问）。
> - **`OnBeforeExecuteAsync` hook**：返回 `Task<ToolResult?>`，null 表示放行，非 null 表示拦截（`Fail`）。7b 默认 null，迭代 8 `SecurityGuard` 覆写或委托此 hook 拦截黑名单/沙箱违规。HITL 在 hook 之后——安全层先拦截，通过后再问用户。
> - **HITL 拒绝返回 `ToolResult.Fail`**：不抛异常——失败原因回灌 LLM 让其自我修正（如改用 `write_file` 而非 `run_command`）。AgentLoop 见 `ToolResult.Success==false` 后 emit `ToolBlockedEvent`（见 4.3.5）。
> - **顺序**：`OnBeforeExecuteAsync`（安全层）→ HITL（用户决策）→ 执行。安全层拒绝时不问用户（避免打扰已拦截的操作）。
> - **回归保护**：旧 `BatchToolExecutorTests` 不传 `hitlGate`（默认 null），Write 工具直接执行，行为与 7a 一致。

#### 4.3.5 `AgentLoop` 改造（HITL 拒绝转发为 ToolBlockedEvent）

```csharp
// Agent/AgentLoop.cs（7a 基础上的增量改动，仅工具结果处理段）

// RunCoreAsync 内工具结果处理段改为：
var results = await _batchExecutor.ExecuteAsync(toolCalls, cancellationToken);

for (var i = 0; i < toolCalls.Count; i++)
{
    var call = toolCalls[i];
    var result = results[i];

    // 7b 新增：HITL/安全层拒绝 → emit ToolBlockedEvent；否则 ToolResultEvent
    if (!result.Success && IsHitlDenial(result))
    {
        await sink.WriteAsync(new AgentEvent.ToolBlockedEvent(call, result.Error ?? "被拦截"), cancellationToken);
    }
    else
    {
        await sink.WriteAsync(new AgentEvent.ToolResultEvent(call, result), cancellationToken);
    }

    // 失败原因（含 HITL 拒绝）回灌历史，让 LLM 自我修正
    history.AddTool(
        result.Success ? result.Content : $"错误：{result.Error}",
        call.Id);
}

await sink.WriteAsync(new AgentEvent.RoundEndEvent(round), cancellationToken);

// —— 7b 新增辅助：判断是否为 HITL/安全层拒绝（区别于工具自身执行失败）——
// 启发式：拒绝原因包含"用户拒绝"或"被拦截"标记为 blocked。
// 更严谨的做法：BatchToolExecutor 返回带 Blocked 标志的结构（迭代 8 再精细化）。
private static bool IsHitlDenial(ToolResult result) =>
    !result.Success && (result.Error?.Contains("用户拒绝") == true ||
                        result.Error?.Contains("被拦截") == true);
```

> **设计要点**：
> - **`ToolBlockedEvent` 启用**：7a 预留事件类型，7b 在 HITL/安全层拒绝时 emit。消费者（TuiApp）用红色卡片渲染"✗ 被拦截：{reason}"，区别于工具执行失败（红色 `✗ 失败`）。
> - **`IsHitlDenial` 启发式**：用错误信息字符串匹配判断"拒绝 vs 执行失败"。不严谨——迭代 8 可让 `ToolResult` 加 `Blocked` 标志或引入 `ToolBlockedResult` 派生类型。7b 用字符串匹配降低改动面（`ToolResult` 不变）。
> - **回灌历史用统一 `AddTool`**：拒绝原因作为 tool 消息回灌，LLM 看到"用户拒绝执行该工具"后调整策略（如换工具或放弃）。这是 HITL 影响 LLM 行为的闭环。
> - **AgentLoop 其余逻辑不变**：7a 的 ReAct 主循环、事件流顺序、`finally sink.Complete()` 全保留。仅工具结果处理段区分 `ToolBlockedEvent`/`ToolResultEvent`。

#### 4.3.6 `TuiApp` 改造（装配 HitlPrompt + render/readKey 回调接线）

```csharp
// Tui/TuiApp.cs（7a 基础上的增量改动）

internal sealed class TuiApp
{
    // ... 7a 字段不变 ...
    private readonly bool _enableHitl;  // 7b 新增

    // 7b 新增：字段持有 Live 上下文与 EventRenderer，供 HitlPrompt 的 render 回调访问
    private LiveDisplayContext? _liveCtx;
    private EventRenderer? _renderer;
    private StatusBar? _statusBar;

    public TuiApp(IBaseProvider provider,
                  ProviderConfig providerConfig,
                  AgentConfig? agentConfig,
                  TuiConfig? tuiConfig,
                  SecurityLevel securityLevel,
                  ILogger? logger,
                  CancellationToken ct,
                  IConsole? console = null,
                  bool useLive = true)
    {
        // ... 7a 初始化不变 ...
        _enableHitl = tuiConfig?.EnableHitl ?? true;  // 7b 新增，默认 true
    }

    public async Task RunAsync()
    {
        // ... 7a 装配工具注册中心不变 ...

        // 7b 新增：装配 HitlPrompt（enable_hitl: true 时）
        IHitlGate hitlGate = _enableHitl && _useLive
            ? new HitlPrompt(
                render: r =>  // 把 IRenderable 设为 transient + 触发 Live 刷新
                {
                    _renderer?.SetTransient(r);
                    _liveCtx?.UpdateTarget(_renderer?.BuildActive(_statusBar!) ?? new Text(""));
                },
                readKey: ct => _console.ReadKey(true).Key)  // 读 A/S/P/D
            : new NullHitlGate();  // enable_hitl: false 或降级模式

        var batchExecutor = new BatchToolExecutor(executor, registry,
            _agentConfig.MaxParallelism ?? 5, hitlGate, _logger);

        // ... 7a statusBar 装配不变，赋值字段供回调访问 ...
        _statusBar = statusBar;

        // ... 7a 输入循环不变 ...

        // 7b 改造：RenderWithLiveAsync 内把 ctx 与 renderer 赋值字段
        // ...（见下方 RenderWithLiveAsync 改造）...
    }

    private async Task RenderWithLiveAsync(ChannelReader<AgentEvent> reader, StatusBar statusBar)
    {
        var renderer = new EventRenderer();
        _renderer = renderer;  // 7b：字段持有供 render 回调访问

        await AnsiConsole.Live(statusBar.Render()).StartAsync(async ctx =>
        {
            _liveCtx = ctx;  // 7b：字段持有供 render 回调访问

            await foreach (var evt in reader.ReadAllAsync(_ct))
            {
                if (evt is AgentEvent.RoundStartEvent(var r))
                    statusBar.CurrentRound = r;

                renderer.Render(evt);
                ctx.UpdateTarget(renderer.BuildActive(statusBar));
            }
        });

        _liveCtx = null;  // 7b：Live 结束后清空，避免回调误访问已释放的 ctx
    }

    // ... 其余 7a 方法不变 ...
}
```

> **设计要点**：
> - **字段持有 Live 上下文**：`_liveCtx` / `_renderer` / `_statusBar` 作为字段，让 `HitlPrompt` 的 `render` 回调能跨 `StartAsync` 边界访问。`StartAsync` 内把 `ctx` 赋值 `_liveCtx`，回调内 `_liveCtx?.UpdateTarget(...)`。
> - **`render` 回调实现**：`r => { _renderer?.SetTransient(r); _liveCtx?.UpdateTarget(_renderer?.BuildActive(_statusBar!) ?? new Text("")); }`。先 `SetTransient` 把 HITL 提示 Panel 设为 transient，再 `UpdateTarget` 触发 Live 刷新显示。
> - **`readKey` 回调实现**：`ct => _console.ReadKey(true).Key`。`ReadKey(true)` 不回显（Live 负责显示提示），返回 `ConsoleKey` 给 `HitlPrompt` 映射为 `HitlChoice`。
> - **`_enableHitl && _useLive` 双条件**：只有 Live 模式且配置启用 HITL 时才装配 `HitlPrompt`。降级模式（`_useLive = false`）或 `enable_hitl: false` 时用 `NullHitlGate`（等价 7a）。
> - **降级模式不接 HITL**：降级模式用 `ConsoleEventRenderer`（无 Live），HITL 提示无法渲染到活跃区。故降级时注入 `NullHitlGate`，Write 工具直接执行（与 7a 降级行为一致）。
> - **`_liveCtx` 清空**：Live 结束后置 null，避免回调误访问已释放的 `LiveDisplayContext`。
> - **线程安全**：7b 同线程同步（AgentLoop 与 TuiApp 在同一 await 链），`_liveCtx`/`_renderer` 字段无需锁。若未来 Live 独立线程，需用 `volatile` 或锁保护。

#### 4.3.7 `IUiControl` 扩展（加 RequestHitlAsync 签名）

```csharp
namespace ParrotCode;

public interface IUiControl
{
    // 7a 已定义
    Task PrintMessageAsync(string message, CancellationToken ct);
    void SetStatus(string key, string value);

    /// <summary>
    /// 请求 HITL 决策（委托 IHitlGate）。
    /// 7b 加签名预留，TuiApp 不实现此接口（7b 无命令系统调用方）。
    /// 迭代 10 命令系统通过此方法触发 HITL（如 /approve 命令）。
    /// </summary>
    Task<HitlDecision?> RequestHitlAsync(ToolCall call, CancellationToken ct);
}
```

> **设计要点**：
> - **仅加签名**：7b 的 `TuiApp` 不实现 `IUiControl`（无命令系统调用方）。签名预留供迭代 10。
> - **委托 `IHitlGate`**：迭代 10 的 `TuiApp` 实现此方法时，内部委托 `_hitlGate.RequestAsync`。

### 4.4 HITL 闭环详解

#### 4.4.1 闭环流程（允许）

```
用户: 在 D:\tmp 创建 hello.txt 写入你好
  ↓
LLM 第 1 轮: tool_call write_file({"path":"D:\\tmp\\hello.txt","content":"你好"})
  ↓
BatchToolExecutor:
  write_file 是 Write 类别 → hitlGate.RequestAsync(call, ct)
  ↓
HitlPrompt.RequestAsync:
  缓存未命中 → _render(提示Panel) → Live 活跃区显示:
    ┌─ HITL 确认 ──────────────────────────────┐
    │ ⚠ 即将执行 write_file({"path":"...","content":"你好"}) │
    │ 按 A=本次 S=会话 P=永久 D=拒绝           │
    └──────────────────────────────────────────┘
  _readKey(ct) → 用户按 S
  _sessionCache["write_file"] = 0
  _render(✓已允许) → Live 活跃区追加 "[green]✓ 已允许（AllowSession）[/]"
  返回 HitlDecision(AllowSession)
  ↓
BatchToolExecutor: decision.IsAllowed → executor.ExecuteAsync(call)
  → ToolResult.Ok("已写入")
  ↓
AgentLoop: emit ToolResultEvent(call, result)
  history.AddTool("已写入", call.Id)
  ↓
LLM 第 2 轮: 看到 write_file 成功 → TextDelta("已创建 hello.txt") → AgentDone
  emit AgentDoneEvent
  ↓
TuiApp: Live 自然退出，最后一帧（含 HITL 提示 + ✓已允许 + 工具卡片 + 完成标记）留在屏幕
```

#### 4.4.2 拒绝闭环

```
用户: 删除所有 .cs 文件
  ↓
LLM 第 1 轮: tool_call run_command({"command":"del","args":"/S *.cs"})
  ↓
BatchToolExecutor:
  run_command 是 Write 类别 → hitlGate.RequestAsync(call, ct)
  ↓
HitlPrompt: _render(提示Panel) → 用户按 D
  _render(✗已拒绝)
  返回 Deny("用户拒绝执行该工具")
  results[i] = ToolResult.Fail("用户拒绝执行该工具")
  ↓
AgentLoop: IsHitlDenial(result) == true（含"用户拒绝"）
  emit ToolBlockedEvent(call, "用户拒绝执行该工具")
  history.AddTool("错误：用户拒绝执行该工具", call.Id)
  ↓
EventRenderer: 渲染红色 ⛔ Panel:
  ┌─ ⛔ HITL 拦截 ───────────────────────────┐
  │ ✗ 被拦截 run_command                      │
  │ 用户拒绝执行该工具                         │
  └──────────────────────────────────────────┘
  ↓
LLM 第 2 轮: 看到"用户拒绝" → TextDelta("好的，我不删除文件。需要我帮您做别的吗？") → AgentDone
  ↓
TuiApp: Live 最后一帧（含 HITL 提示 + ✗已拒绝 + ⛔拦截卡片 + AI 回复）留在屏幕
```

#### 4.4.3 会话缓存命中

```
用户: 再创建 hello2.txt
  ↓
LLM: tool_call write_file({"path":"D:\\tmp\\hello2.txt",...})
  ↓
BatchToolExecutor:
  write_file 是 Write → hitlGate.RequestAsync(call, ct)
  ↓
HitlPrompt.RequestAsync:
  _sessionCache.ContainsKey("write_file") == true（上次按 S）
  → 直接返回 AllowSession（不调 _render / _readKey）
  ↓
ToolResult.Ok → ToolResultEvent → AgentDone
（无 HITL 提示渲染，直接执行）
```

#### 4.4.4 Ctrl+C 中断 HITL

```
用户: 删除文件 → AI 调 run_command → HITL 提示弹出
用户按 Ctrl+C:
  ↓
Program 的全局 cts 触发取消
  ↓
HitlPrompt.RequestAsync 前置检查:
  cancellationToken.IsCancellationRequested == true
  → 返回 Deny("已取消")（不调 _readKey，避免阻塞）
  ↓
BatchToolExecutor: results[i] = Fail("已取消")（但此时 AgentLoop 已收到取消）
  ↓
AgentLoop.RunAsync 的 catch (OperationCanceledException):
  emit CancelledEvent
  ↓
TuiApp: Live 渲染 "── 已取消 ──"，程序不崩溃
```

### 4.5 方案 C 的关键约束与 7a 实现教训

7a 实现中发现的关键约束，7b 的 `HitlPrompt` 严格遵循：

| 7a 教训 | 7b 遵循 |
| --- | --- |
| Live 期间不能调 `AnsiConsole.Write`——字节交错（`security` 变 `??rity`） | `HitlPrompt` 全程不调 `AnsiConsole.Write`/`Prompt`，只用 `_render` 回调（→ `ctx.UpdateTarget`） |
| Live 期间不能让 logger 写 stderr——卡在 Live 区域 | 7a 已通过 Live 模式不给 AgentLoop 传 logger 修复，7b 保持 |
| Live 最后一帧留在屏幕（不提交历史） | 7b 不变——HITL 提示随最后一帧留下，作为本轮输出的一部分 |
| Live 初始 target 用状态栏而非空 Text | 7b 不变——`AnsiConsole.Live(statusBar.Render())` |
| `Console.ReadKey` 与 Live 输出互不干扰 | 7b 利用此特性——`_readKey` 回调读按键，Live 持续渲染提示 |

> **方案 C 的核心**：HITL 提示作为 Live 活跃区一部分渲染（通过 `SetTransient`），`Console.ReadKey` 读按键（输入与 Live 输出分离）。这样 Live 持续运行，无需 Stop/Restart，状态机简单，规避所有 Live 与其他控制台操作互斥的陷阱。

## 五、依赖变更

**无新增 NuGet 依赖。**

- `Spectre.Console` 0.49.1（7a 已引入）：用 `Panel`/`Markup`/`Style`/`Color`/`IRenderable` 等 API。
- `System.Collections.Concurrent` BCL 内置（`ConcurrentDictionary`）。
- `ParrotCode.Net.csproj` / `ParrotCode.Net-xUnit.csproj`：**不变**。

## 六、配置文件

### 6.1 `Config/Models.cs` 扩展

```csharp
namespace ParrotCode;

public sealed record AppConfig
{
    public string? ActiveProvider { get; init; }
    public IList<ProviderConfig> Providers { get; init; } = Array.Empty<ProviderConfig>();
    public AgentConfig? Agent { get; init; }
    public TuiConfig? Tui { get; init; }

    /// <summary>安全配置（7b 占位，迭代 8 接入真实拦截）。null 时默认 Normal。</summary>
    public SecurityConfig? Security { get; init; }
}

public sealed record TuiConfig
{
    public string? Mode { get; init; }              // 7a
    public bool? ShowStatusBar { get; init; }       // 7a
    public int? ContextWindowTokens { get; init; }  // 7a

    /// <summary>是否启用 HITL，默认 true。false 时注入 NullHitlGate（7b 新增）。</summary>
    public bool? EnableHitl { get; init; }
}

/// <summary>安全配置（7b 占位，迭代 8 接入真实拦截）。</summary>
public sealed record SecurityConfig
{
    /// <summary>安全等级："strict" | "normal"（默认）| "permissive"。</summary>
    public string? Level { get; init; }
}
```

### 6.2 `example.parrotcode.yaml` 扩展

```yaml
active_provider: deepseek

providers:
  - name: deepseek
    protocol: openai
    model: deepseek-chat
    base_url: https://api.deepseek.com/v1
    api_key: ${DEEPSEEK_API_KEY}

agent:
  max_rounds: 10
  tool_choice: auto
  max_parallelism: 5
  tool_timeout_seconds: 30

# 迭代 7a/7b 配置
tui:
  mode: live              # live | console（降级行模式）
  show_status_bar: true
  context_window_tokens: 64000
  enable_hitl: true       # 7b 新增：是否启用 HITL，默认 true

# 迭代 7b 占位（迭代 8 接入真实拦截）
security:
  level: normal           # strict | normal | permissive
```

### 6.3 默认值

| 字段 | 默认值 | 覆盖来源 |
| --- | --- | --- |
| `Tui.EnableHitl` | `true` | `tui.enable_hitl` |
| `Security.Level` | `"normal"` | `security.level` |

> ConfigLoader 解析时若 `tui.enable_hitl` / `security` 节缺失，对应字段为 null，App 用默认值。

## 七、迁移说明（迭代 7a → 迭代 7b）

| 迭代 7a | 迭代 7b | 处理 |
| --- | --- | --- |
| `BatchToolExecutor` 不注入 `IHitlGate` | 加可选参数 `IHitlGate? hitlGate = null` | 扩展（null 时等价 7a） |
| `AgentLoop` 工具结果统一 `ToolResultEvent` | 区分 `ToolBlockedEvent`（HITL 拒绝）/ `ToolResultEvent` | 增量改动 |
| `ToolBlockedEvent` 不产生 | HITL 拒绝时产生 | 启用预留事件 |
| `EventRenderer.SetTransient` 预留不调用 | `HitlPrompt` 调用渲染 HITL 提示 | 复用扩展点 |
| `TuiApp` 不装配 `HitlPrompt` | 装配 + render/readKey 回调接线 | 增量改动 |
| `TuiConfig` 无 `EnableHitl` | 加 `EnableHitl` 字段 | 扩展 |
| `SecurityLevel` 硬编码 `Normal` | 加 `SecurityConfig.Level` 配置项（仅解析与显示） | 扩展 |
| `IUiControl` 无 `RequestHitlAsync` | 加方法签名（预留） | 扩展 |
| `/help` / `/status` / `/clear` / `/exit` 硬编码 | 不变 | 无改动 |
| Live 最后一帧留下 | 不变 | 无改动 |

迁移后回归不变式：
- `tui.enable_hitl: false` 时，注入 `NullHitlGate`，行为与 7a 完全一致（Write 工具直接执行）。
- 降级模式（`tui.mode: console` 或非交互终端）时，`_useLive = false` → 注入 `NullHitlGate`，行为与 7a 降级一致。
- `active_provider: mock` 无脚本时，TuiApp 输入"你好" → Live 渲染"你好（mock）"，与 7a 一致（无 Write 工具，不触发 HITL）。
- `/clear` / `/exit` / `/help` / `/status` 行为保持。
- 迭代 1-7a 既有测试全绿（`BatchToolExecutor` 的 `IHitlGate?` 可选，旧测试不传等价 7a）。

> **回归保护**：`BatchToolExecutor` 构造函数加可选参数 `IHitlGate? hitlGate = null`，旧调用不传则 null（等价 7a）。`AgentLoop` 改动仅工具结果处理段区分事件类型，旧 `AgentLoopTests` 用 MockProvider 脚本不触发 HITL（无 Write 工具或脚本不调 Write）仍全绿。`EventRenderer` 的 `SetTransient` 在 7a 已有单测验证（`SetTransient_NonNull_BuildActiveIncludesTransient`），7b 复用不改。

## 八、单元测试

### 8.1 `HitlDecisionTests`（新增）

| 用例 | 操作 | 期望 |
| --- | --- | --- |
| `AllowOnce_IsAllowed_True_ShouldCache_False` | `HitlDecision.AllowOnce` | `IsAllowed == true`, `ShouldCache == false` |
| `AllowSession_IsAllowedAndShouldCache_True` | `new HitlDecision(HitlChoice.AllowSession)` | `IsAllowed == true`, `ShouldCache == true` |
| `AllowPermanent_ShouldCache_True` | `new HitlDecision(HitlChoice.AllowPermanent)` | `IsAllowed == true`, `ShouldCache == true` |
| `Deny_IsAllowed_False_ReasonSet` | `HitlDecision.Deny("原因")` | `IsAllowed == false`, `Reason == "原因"` |
| `Deny_StaticFactory_SetsChoiceDeny` | `HitlDecision.Deny("拒绝")` | `Choice == Deny` |

### 8.2 `HitlPromptTests`（新增，用假 render/readKey 回调）

| 用例 | 操作 | 期望 |
| --- | --- | --- |
| `RequestAsync_ReadKeyA_ReturnsAllowOnce` | 注入 readKey 返回 A 键 | `Choice == AllowOnce` |
| `RequestAsync_ReadKeyS_ReturnsAllowSession_AndCaches` | readKey 返回 S | `Choice == AllowSession`, `IsAllowedThisSession("x") == true` |
| `RequestAsync_ReadKeyD_ReturnsDenyWithReason` | readKey 返回 D | `Choice == Deny`, `Reason 含"拒绝"` |
| `RequestAsync_ReadKeyP_CachesAsSession` | readKey 返回 P | `Choice == AllowPermanent`, `IsAllowedThisSession` 命中（退化） |
| `RequestAsync_CacheHit_DoesNotPrompt` | 先 S 允许 → 再 RequestAsync | 不调 readKey，不调 render，返回 AllowSession |
| `RequestAsync_CancelledToken_ReturnsDeny` | ct 已取消 | 返回 Deny("已取消")，不调 readKey/render |
| `RequestAsync_RendersPromptThenResult` | 收集 render 回调 | 先渲染提示 Panel，后渲染结果 Markup（两次调用） |
| `RequestAsync_UnknownKey_ReturnsDeny` | readKey 返回 X 键 | `Choice == Deny`（安全默认） |
| `IsAllowedThisSession_NotCached_ReturnsFalse` | 新建 HitlPrompt | `false` |
| `RequestAsync_NoRenderCallback_DoesNotThrow` | render=null | 不抛异常（只读按键） |

### 8.3 `NullHitlGateTests`（新增）

| 用例 | 操作 | 期望 |
| --- | --- | --- |
| `RequestAsync_ReturnsNull` | 调 RequestAsync | 返回 null |
| `IsAllowedThisSession_AlwaysFalse` | 调 IsAllowedThisSession | false |

### 8.4 `BatchToolExecutorHitlTests`（新增）

| 用例 | 操作 | 期望 |
| --- | --- | --- |
| `ExecuteAsync_WriteTool_PromptsHitl` | 注入假 IHitlGate 记录调用 | RequestAsync 被调一次 |
| `ExecuteAsync_HitlDeny_ReturnsFail` | 假 gate 返回 Deny | 对应位置 ToolResult.Fail("用户拒绝") |
| `ExecuteAsync_HitlDeny_DoesNotExecute` | 假 gate Deny + 假 ToolExecutor 记录 | ToolExecutor.ExecuteAsync 未被调 |
| `ExecuteAsync_HitlAllow_Executes` | 假 gate AllowOnce | ToolExecutor 被调，返回 Ok |
| `ExecuteAsync_ReadTool_NoHitl` | ReadFileTool + 假 gate | RequestAsync 未被调（Read 不问） |
| `ExecuteAsync_CacheHit_NoPrompt` | 先 AllowSession → 再调同工具 | 第二次 RequestAsync 返回 AllowSession 不调 Prompt |
| `ExecuteAsync_HitlNull_GateSkipped` | hitlGate=null | 直接执行（等价 7a） |
| `ExecuteAsync_CancelledToken_HitlReturnsDeny` | ct 取消 + 假 gate | gate 返回 Deny，结果 Fail |
| `ExecuteAsync_OnBeforeExecuteAsync_HookCalled` | 子类覆写返回 Fail | results[i] = Fail，HITL 未问 |

### 8.5 `AgentLoopHitlTests`（新增，MockProvider 脚本触发拒绝）

| 用例 | 操作 | 期望 |
| --- | --- | --- |
| `HitlDeny_EmitsToolBlockedEvent` | 脚本调 write_file + 假 gate Deny | 事件流含 ToolBlockedEvent |
| `HitlDeny_ReasonReflowsToHistory` | 同上 | history 含"错误：用户拒绝" |
| `ToolExecuteFail_EmitsToolResultEvent_NotBlocked` | 脚本调 write_file + gate Allow + 工具抛异常 | 事件流含 ToolResultEvent（非 ToolBlockedEvent） |
| `HitlAllow_EmitsToolResultEvent` | 脚本调 write_file + gate Allow + 工具成功 | 事件流含 ToolResultEvent |
| `NoHitlGate_NoToolBlockedEvent` | hitlGate=null + 脚本调 write_file | 事件流无 ToolBlockedEvent（等价 7a） |

### 8.6 `TuiAppHitlIntegrationTests`（新增，端到端）

| 用例 | 操作 | 期望 |
| --- | --- | --- |
| `EndToEnd_WriteTool_HitlPromptAllow` | MockProvider 脚本调 write_file + 假 readKey 返回 A | HITL 提示渲染，工具执行，AgentDone |
| `EndToEnd_WriteTool_HitlDeny` | 脚本调 write_file + 假 readKey 返回 D | ToolBlockedEvent 渲染，LLM 收到拒绝原因 |
| `EndToEnd_SessionCache_SecondWriteNoPrompt` | 两次 write_file，第一次 S | 第二次不调 readKey（缓存命中） |
| `EndToEnd_EnableHitlFalse_NoPrompt` | tui.enable_hitl: false + 脚本调 write_file | 不弹 HITL，直接执行（NullHitlGate） |
| `EndToEnd_CancelledEvent_Renders` | 脚本触发取消 | 渲染 "已取消" |
| `EndToEnd_StatusBar_UpdatesRound` | 多轮脚本 | 状态栏 round 字段更新 |

### 8.7 回归

- `dotnet test` 全绿（含迭代 1-7a 既有 + 迭代 7b 新增 6 个测试文件）。
- `dotnet run`（`tui.enable_hitl: false`）行为与 7a 一致。
- `AgentLoopTests` / `BatchToolExecutorTests` 既有用例全绿（`IHitlGate?` 可选，旧测试不传等价 7a）。
- `EventRendererTests` 既有用例全绿（`SetTransient` 7a 已测，7b 复用不改）。
- `/clear` / `/exit` / `/help` / `/status` 行为保持。

## 九、验收标准

> 全部满足方可标记 `[已完成]`。每条尽量可被命令或肉眼验证。

### 9.1 构建与运行

- [ ] `ParrotCode.Net.csproj` 无改动（零新依赖）。
- [ ] `dotnet build` 无 error、无 warning。
- [ ] `dotnet test` 全绿（含迭代 1-7a 既有 + 迭代 7b 新增 6 个测试文件）。
- [ ] `dotnet run`（`active_provider: mock`，`enable_hitl: true`）能启动，TUI 横幅正常。

### 9.2 HITL 决策模型

- [ ] `Tui/HitlDecision.cs` 定义 `HitlChoice` 枚举（`AllowOnce`/`AllowSession`/`AllowPermanent`/`Deny`）+ `HitlDecision` record。
- [ ] `IsAllowed` / `ShouldCache` 属性正确。
- [ ] `HitlDecision.Deny(reason)` 静态工厂设置 Reason。
- [ ] `HitlDecisionTests` 5 个用例全绿。

### 9.3 IHitlGate + NullHitlGate

- [ ] `Tui/IHitlGate.cs` 定义接口（`RequestAsync` + `IsAllowedThisSession`）+ `NullHitlGate`。
- [ ] `NullHitlGate.RequestAsync` 返回 null（等价无 HITL）。
- [ ] `NullHitlGate.IsAllowedThisSession` 恒 false。
- [ ] `NullHitlGateTests` 2 个用例全绿。

### 9.4 HitlPrompt（方案 C）

- [ ] `Tui/HitlPrompt.cs` 实现 `IHitlGate`：用 `render`/`readKey` 回调（方案 C，不暂停 Live）。
- [ ] A/S/P/D 四键映射正确。
- [ ] `AllowSession`/`AllowPermanent` 记入会话缓存，`IsAllowedThisSession` 命中。
- [ ] 缓存命中时不调 render/readKey，直接返回 AllowSession。
- [ ] 取消时返回 Deny("已取消")。
- [ ] 非 A/S/P/D 键一律 Deny（安全默认）。
- [ ] render 回调被调两次（提示 Panel + 结果 Markup）。
- [ ] `HitlPromptTests` 10 个用例全绿。

### 9.5 BatchToolExecutor HITL 接入

- [ ] `BatchToolExecutor` 构造加可选参数 `IHitlGate? hitlGate = null`。
- [ ] Write 组执行前调 `hitlGate.RequestAsync`（hitlGate 非 null 时）。
- [ ] Read 组不调 HITL。
- [ ] Deny 时返回 `ToolResult.Fail`，不执行工具。
- [ ] Allow 时正常执行。
- [ ] `OnBeforeExecuteAsync` 虚方法预留（默认返回 null，迭代 8 接入）。
- [ ] hitlGate 为 null 时等价 7a（直接执行）。
- [ ] `BatchToolExecutorHitlTests` 9 个用例全绿。
- [ ] 旧 `BatchToolExecutorTests` 既有用例全绿（无回归）。

### 9.6 AgentLoop HITL 拒绝转发

- [ ] HITL 拒绝（`ToolResult.Fail` 含"用户拒绝"）时 emit `ToolBlockedEvent`。
- [ ] 工具自身失败时 emit `ToolResultEvent`（非 `ToolBlockedEvent`）。
- [ ] 拒绝原因回灌历史（`history.AddTool("错误：用户拒绝...", call.Id)`）。
- [ ] `AgentLoopHitlTests` 5 个用例全绿。
- [ ] 旧 `AgentLoopTests` 既有用例全绿（无 HITL 场景不产生 `ToolBlockedEvent`）。

### 9.7 TuiApp HITL 接线

- [ ] `TuiApp` 装配 `HitlPrompt`（`enable_hitl: true` 且 Live 模式时）。
- [ ] `render` 回调调 `SetTransient` + `ctx.UpdateTarget`（刷新 Live 活跃区）。
- [ ] `readKey` 回调调 `IConsole.ReadKey(true).Key`。
- [ ] 字段 `_liveCtx` / `_renderer` / `_statusBar` 持有供回调访问。
- [ ] `enable_hitl: false` 时注入 `NullHitlGate`（等价 7a）。
- [ ] 降级模式（`_useLive = false`）时注入 `NullHitlGate`。
- [ ] `TuiAppHitlIntegrationTests` 6 个用例全绿。

### 9.8 EventRenderer 复用 SetTransient

- [ ] `EventRenderer.SetTransient`（7a 已预留）被 `HitlPrompt` 调用渲染 HITL 提示。
- [ ] `BuildActive` 含 transient（HITL 提示 Panel + 结果 Markup）。
- [ ] `ToolBlockedEvent` 渲染分支（7a 已预留）启用——红色 ⛔ Panel。
- [ ] `EventRendererTests` 既有用例全绿（7a 已测 `SetTransient`，7b 复用不改）。

### 9.9 配置

- [ ] `Config/Models.cs` 加 `TuiConfig.EnableHitl` + `SecurityConfig.Level` + `AppConfig.Security`。
- [ ] `example.parrotcode.yaml` 加 `enable_hitl: true` + `security:` 节示例。
- [ ] ConfigLoader 解析新字段（缺失时 null，App 用默认值）。
- [ ] `Program.cs` 从 `SecurityConfig.Level` 解析 `SecurityLevel` 传给 `TuiApp`。
- [ ] 配置项可被覆盖（如 `enable_hitl: false` 生效）。

### 9.10 IUiControl 扩展

- [ ] `Tui/IUiControl.cs` 加 `RequestHitlAsync` 方法签名（预留）。
- [ ] `TuiApp` 不实现 `IUiControl`（7b 无命令系统调用方）。

### 9.11 端到端 TUI 体验（核心验收）

- [ ] **mock 模式**：`dotnet run`，输入"你好"，Live 渲染"你好（mock）"（无 Write 工具，不触发 HITL）。
- [ ] **HITL 弹框**：让 AI 调 `write_file`，看到 HITL 提示卡片（黄色边框 Panel）。
- [ ] **A 键允许本次**：按 A，工具执行，下次同工具再问。
- [ ] **S 键允许会话**：按 S，同会话同工具不再问（缓存命中，无提示）。
- [ ] **P 键允许永久**：按 P，行为与 S 一致（7b 退化），状态栏无变化。
- [ ] **D 键拒绝**：按 D，看到 `ToolBlockedEvent` 红色 ⛔ 卡片，AI 回复"好的，我不执行"。
- [ ] **Ctrl+C 中断 HITL**：HITL 提示弹出时按 Ctrl+C，渲染"已取消"，程序不崩溃。
- [ ] **enable_hitl: false**：配置关闭 HITL，Write 工具直接执行（无提示）。
- [ ] **降级模式**：`tui.mode: console`，Write 工具直接执行（`NullHitlGate`），行为与 7a 降级一致。

### 9.12 异常与边界

- [ ] HITL 期间 Ctrl+C → `RequestAsync` 前置检查返回 Deny("已取消")，Agent 优雅停止。
- [ ] HITL 期间按非 A/S/P/D 键 → 一律 Deny（安全默认）。
- [ ] 工具参数含 `[` 破坏 Markup → `Markup.Escape` 转义（7a 已有，7b 复用）。
- [ ] 会话缓存导致后续危险操作不问 → `AllowSession` 仅对工具名缓存，不含参数（7b 简化，迭代 8 精细化）。
- [ ] `AllowPermanent` 退化为会话级 → 文档明确，迭代 10 接入配置文件后持久化。
- [ ] 降级模式不接 HITL → `NullHitlGate` 注入，Write 工具直接执行。

### 9.13 敏感信息

- [ ] HITL 提示不泄露 ApiKey（工具参数可能含路径，不含 key）。
- [ ] 状态栏不显示 ApiKey（7a 已有，7b 保持）。
- [ ] 日志不出现 ApiKey（7a 已有，7b 保持）。

### 9.14 跨平台

- [ ] Windows 上 `dotnet test` 全绿。
- [ ] macOS / Linux 上 `dotnet test` 全绿。
- [ ] `Console.ReadKey` 在三平台行为一致（读 A/S/P/D）。
- [ ] Live 模式 HITL 渲染在三平台正常。

### 9.15 迁移与回归

- [ ] `BatchToolExecutor` 旧构造函数签名兼容（`IHitlGate?` 可选）。
- [ ] `AgentLoop` 旧测试全绿（无 HITL 场景）。
- [ ] `ChannelEventSink` / `IAgentEventSink` **不变**。
- [ ] 迭代 7a 的 12 种事件类型**不变**（`ToolBlockedEvent` 启用产生但不改签名）。
- [ ] `EventRenderer` / `StatusBar` / `InputReader` / `ConsoleEventRenderer` **不变**（7a 已预留扩展点）。
- [ ] `tui.enable_hitl: false` 时行为与 7a 完全一致。
- [ ] 降级模式（`tui.mode: console`）行为与 7a 降级一致。
- [ ] 迭代 1-7a 的所有测试**全绿**（无回归）。

## 十、进阶练习（可选，不计入验收）

1. **HITL 预览（diff 显示）**：HITL 提示卡片显示工具调用的"diff 预览"——`write_file` 显示将写入的内容，`edit_file` 显示 old→new diff，`run_command` 显示完整命令。需工具实现 `PreviewAsync` 方法。

2. **A/S/P/D 快捷键强化**：`HitlPrompt` 不区分大小写（a/s/p/d 同 A/S/P/D）+ 数字键备选（1/2/3/4）。

3. **`AllowPermanent` 持久化**：写入 `.parrotcode/hitl_decisions.json`，跨会话加载。迭代 10 配置文件支持后接入。

4. **HITL 超时自动拒绝**：HITL 提示弹出后 30 秒无响应自动 Deny（防止用户离开后无限等待）。

5. **HITL 历史回滚**：允许过的操作记录在会话历史，`/undo` 命令回滚（需工具支持 `UndoAsync`）。

6. **参数级 HITL 缓存**：`AllowSession` 不仅对工具名缓存，还含参数哈希——`run_command` 不同命令仍问（7b 简化为工具名级，迭代 8 精细化）。

7. **HITL 批量确认**：一轮内多个 Write 工具调用，批量确认"允许全部"/"逐个确认"。

8. **HITL 审计日志**：记录每次 HITL 决策（工具/参数/选择/时间戳）到 JSONL，供事后审查。

## 十一、风险与注意事项

| 风险 | 缓解 |
| --- | --- |
| 7a 发现"Live 与 `AnsiConsole.Write`/`Prompt` 互斥" | 方案 C：`HitlPrompt` 全程不调 `AnsiConsole.Write`/`Prompt`，只用 `_render` 回调（→ `ctx.UpdateTarget`）+ `_readKey` 回调（→ `Console.ReadKey`） |
| `_liveCtx` 字段跨 `StartAsync` 边界访问 | Live 运行期间字段持有 ctx，Live 结束后置 null。7b 同线程同步无需锁，未来多线程需 `volatile` 或锁 |
| `Console.ReadKey` 在重定向 stdin 时抛异常 | 降级模式用 `NullHitlGate`（不调 `ReadKey`）。检测 `Console.IsInputRedirected` 决定是否启用 HITL |
| HITL 期间 Ctrl+C 卡在 `ReadKey` | `RequestAsync` 前置检查 `cancellationToken.IsCancellationRequested`，已取消时直接返回 Deny("已取消")，不调 `_readKey` |
| 会话缓存导致后续危险操作不问 | `AllowSession` 仅对工具名缓存，不含参数。`run_command` 即使会话允许，每次不同命令仍可见（但 7b 不区分参数，是简化）。迭代 8 精细化 |
| `AllowPermanent` 退化为会话级 | 文档明确，迭代 10 接入配置文件后持久化。7b 用户按 P 与 S 行为一致 |
| `IsHitlDenial` 字符串匹配不严谨 | 文档标注为启发式，迭代 8 用 `ToolResult.Blocked` 标志或派生类型精细化 |
| `Markup.Escape` 漏转义导致渲染崩溃 | 所有动态内容（工具名/参数/结果/错误）一律 `Markup.Escape`（7a 已有，7b 复用） |
| `HitlPrompt` 的 render/readKey 回调在测试中难注入 | 测试用构造函数注入假 `Action<IRenderable?>` / `Func<CancellationToken, ConsoleKey>`，不依赖真实控制台 |
| `BatchToolExecutor` 的 `IHitlGate?` 破坏旧测试 | 可选参数默认 null，旧测试不传等价 7a。`BatchToolExecutorTests` 全绿验证 |
| HITL 提示随 Live 最后一帧留下（不单独提交历史） | 7a 设计决策——HITL 提示作为本轮输出的一部分留下，不单独滚动。用户可滚动查看 |
| 降级模式不接 HITL | `NullHitlGate` 注入，Write 工具直接执行。降级模式用于 CI/管道，无需交互确认 |
| `SecurityLevel.Permisive` 拼写 | 沿用 7a 拼写（与 plan.md 一致），迭代 8 可纠正为 `Permissive` |
| TUI 测试难断言（Spectre.Console 输出） | `HitlPrompt` 用假回调断言 render/readKey 调用；`TuiAppHitlIntegrationTests` 用 MockProvider 脚本 + 假 readKey，断言事件流而非控制台像素 |
| 多轮 ReAct 后 HITL 提示堆积 | Live 最后一帧留下后，下一轮 `EventRenderer` 在 `RoundStartEvent` 时 `SetTransient(null)` 清空（7a 已有 `Reset` 逻辑） |

## 十二、交付清单

### 12.1 新增源文件

- [ ] `ParrotCode.Net/Tui/HitlDecision.cs`（Choice 枚举 + Decision record）
- [ ] `ParrotCode.Net/Tui/IHitlGate.cs`（HITL 双向通道接口 + NullHitlGate）
- [ ] `ParrotCode.Net/Tui/HitlPrompt.cs`（方案 C 实现，render/readKey 回调 + 会话缓存）

### 12.2 修改源文件

- [ ] `ParrotCode.Net/Agent/BatchToolExecutor.cs`（注入 IHitlGate? + OnBeforeExecuteAsync hook）
- [ ] `ParrotCode.Net/Agent/AgentLoop.cs`（IsHitlDenial + ToolBlockedEvent 转发）
- [ ] `ParrotCode.Net/Tui/TuiApp.cs`（装配 HitlPrompt + render/readKey 回调接线 + 字段持有 Live 上下文）
- [ ] `ParrotCode.Net/Tui/IUiControl.cs`（加 RequestHitlAsync 方法签名）
- [ ] `ParrotCode.Net/Config/Models.cs`（TuiConfig.EnableHitl + SecurityConfig + AppConfig.Security）
- [ ] `ParrotCode.Net/example.parrotcode.yaml`（enable_hitl + security 节）
- [ ] `ParrotCode.Net/Program.cs`（解析 SecurityConfig.Level → SecurityLevel）

### 12.3 新增测试文件

- [ ] `ParrotCode.Net-xUnit/HitlDecisionTests.cs`
- [ ] `ParrotCode.Net-xUnit/HitlPromptTests.cs`
- [ ] `ParrotCode.Net-xUnit/NullHitlGateTests.cs`
- [ ] `ParrotCode.Net-xUnit/BatchToolExecutorHitlTests.cs`
- [ ] `ParrotCode.Net-xUnit/AgentLoopHitlTests.cs`
- [ ] `ParrotCode.Net-xUnit/TuiAppHitlIntegrationTests.cs`

### 12.4 演示与验收

- [ ] 演示：mock 模式 Live 渲染"你好（mock）"（无 Write 工具，不触发 HITL，与 7a 一致）。
- [ ] 演示：DeepSeek 真实模式，让 AI 调 `write_file`，看到 HITL 提示卡片（黄色边框 Panel）。
- [ ] 演示：按 A 允许本次，工具执行，下次同工具再问。
- [ ] 演示：按 S 允许会话，第二次 `write_file` 不再弹 HITL（验证缓存）。
- [ ] 演示：按 D 拒绝 `write_file`，AI 回复"好的，我不执行"（验证 HITL 闭环）。
- [ ] 演示：HITL 提示弹出时 Ctrl+C，渲染"已取消"程序不崩溃。
- [ ] 演示：`enable_hitl: false` 配置，Write 工具直接执行（无提示，等价 7a）。
- [ ] 演示：降级模式 `tui.mode: console`，Write 工具直接执行（NullHitlGate）。

## 十三、实现顺序建议

为降低集成风险，建议按以下顺序分步实现（每步可单独编译验证）：

1. **决策模型与接口**：`HitlDecision` + `HitlChoice` + `IHitlGate` + `NullHitlGate`。先建立类型契约，无逻辑。配套 `HitlDecisionTests` + `NullHitlGateTests`。

2. **`HitlPrompt`**：方案 C 实现（render/readKey 回调）+ `HitlPromptTests`（用假回调）。独立可测，不依赖 Live/TuiApp。

3. **`BatchToolExecutor` 改造**：注入 `IHitlGate?` + Write 组请求决策 + `OnBeforeExecuteAsync` hook + `BatchToolExecutorHitlTests`。验证 HITL 接入，旧测试不回归。

4. **`AgentLoop` 改造**：`IsHitlDenial` + `ToolBlockedEvent` 转发 + `AgentLoopHitlTests`。旧 `AgentLoopTests` 全绿验证。

5. **`Config/Models.cs` 扩展**：`TuiConfig.EnableHitl` + `SecurityConfig` + `AppConfig.Security`。配套 ConfigLoader 测试（解析新字段）。

6. **`IUiControl` 扩展**：加 `RequestHitlAsync` 方法签名（仅签名，无实现）。

7. **`TuiApp` 改造**：装配 `HitlPrompt` + render/readKey 回调接线 + 字段持有 Live 上下文 + `TuiAppHitlIntegrationTests`。核心集成。

8. **`example.parrotcode.yaml` + `Program.cs`**：加配置节示例 + 解析 `SecurityConfig.Level` → `SecurityLevel`。

9. **端到端验收**：`dotnet test` 全绿 + mock 模式无 HITL + DeepSeek 真实模式 HITL + 降级模式回归 + `enable_hitl: false` 回归。

> 每步完成后 `dotnet build` 应无 error。步骤 1-6 完成后既有功能不回归（旧 `BatchToolExecutor` 旧测试全绿，`TuiApp` 仍用 7a 简化版）。步骤 7 切换 `TuiApp` 到 HITL 接线版后，`enable_hitl: false` 降级路径保留 7a 行为。

---

## 附录 A：HITL 交互时序

```
用户输入 "创建 hello.txt"
    │
    ▼
TuiApp: history.AddUser → agentLoop.RunAsync → Live.StartAsync
    _liveCtx = ctx  # 字段持有供 HitlPrompt 回调访问
    │
    ▼
AgentLoop 第 1 轮: LLM 流式 → tool_call write_file
    emit ToolCallStartEvent → Live 显示 "→ write_file(...)"
    │
    ▼
BatchToolExecutor.ExecuteAsync:
    write_file 是 Write → hitlGate.RequestAsync(call, ct)
    │
    ▼
HitlPrompt.RequestAsync:
    缓存未命中 → _render(提示Panel)
        → SetTransient(提示Panel) + ctx.UpdateTarget(BuildActive)
        → Live 活跃区显示 "⚠ 即将执行 write_file(...) 按 A/S/P/D"
    _readKey(ct) → 用户按 S（Console.ReadKey 输入，与 Live 输出分离）
    _sessionCache["write_file"] = 0
    _render(✓已允许) → SetTransient(✓已允许) + ctx.UpdateTarget
        → Live 活跃区 transient 变为 "✓ 已允许（AllowSession）"
    返回 HitlDecision(AllowSession)
    │
    ▼
BatchToolExecutor: decision.IsAllowed → executor.ExecuteAsync(call)
    → ToolResult.Ok("已写入")
    │
    ▼
AgentLoop: emit ToolResultEvent(call, result)
    history.AddTool("已写入", call.Id)
    │
    ▼
AgentLoop 第 2 轮: LLM 看到 write_file 成功 → TextDelta("已创建 hello.txt") → Done
    emit AgentDoneEvent
    │
    ▼
TuiApp: Live 自然退出，最后一帧留在屏幕（含 HITL 提示 + ✓已允许 + 工具卡片 + 完成标记）
    _liveCtx = null  # 清空字段
    │
    ▼
回到输入循环: await inputReader.ReadLineWithCompletionAsync
```

时序要点：
1. **HITL 同步阻塞**：`BatchToolExecutor` 在 `await hitlGate.RequestAsync` 处阻塞，AgentLoop 暂停，直到用户按键。
2. **Live 不暂停**：方案 C 下 Live 持续运行，HITL 提示作为活跃区一部分渲染（通过 `SetTransient` + `ctx.UpdateTarget`），`Console.ReadKey` 读输入不干扰 Live 输出。
3. **缓存生效**：第二次 `write_file` 时 `IsAllowedThisSession` 命中，`RequestAsync` 直接返回 AllowSession，不调 render/readKey。
4. **最后一帧留下**：Live 自然退出后，最后一帧（含 HITL 提示 + 决策结果 + 工具卡片 + AI 回复）作为本轮输出留在屏幕。

## 附录 B：HITL 提示卡片渲染示例

### B.1 HITL 确认提示（弹出时）

```
┌─ HITL 确认 ──────────────────────────────────────┐
│ ⚠ 即将执行 write_file({"path":"D:\\tmp\\hello.txt","content":"你好"}) │
│ 按 A=本次 S=会话 P=永久 D=拒绝                    │
└──────────────────────────────────────────────────┘
```

### B.2 允许结果（按 S 后）

```
✓ 已允许（AllowSession）
```

### B.3 拒绝结果（按 D 后）

```
✗ 已拒绝
```

### B.4 拦截卡片（AgentLoop emit ToolBlockedEvent 后）

```
┌─ ⛔ HITL 拦截 ───────────────────────────────────┐
│ ✗ 被拦截 run_command                              │
│ 用户拒绝执行该工具                                 │
└──────────────────────────────────────────────────┘
```

### B.5 完整一轮 Live 活跃区布局（含 HITL）

```
┌─ provider=deepseek model=deepseek-chat security=Normal ctx=12%(7680/64000) round=1 tools=6 ─┐
│                                                                                              │
│ ── Round 1 ──                                                                                 │
│ 我来帮你创建 hello.txt                                                                        │
│                                                                                              │
│ ┌─ → 工具调用 ─────────────────────────────────────┐                                          │
│ │ write_file({"path":"D:\\tmp\\hello.txt","content":"你好"}) │                                          │
│ └──────────────────────────────────────────────────┘                                          │
│                                                                                              │
│ ┌─ HITL 确认 ──────────────────────────────────────┐                                          │
│ │ ⚠ 即将执行 write_file(...)                        │                                          │
│ │ 按 A=本次 S=会话 P=永久 D=拒绝                    │                                          │
│ └──────────────────────────────────────────────────┘                                          │
│ ✓ 已允许（AllowSession）                                                                      │
│                                                                                              │
│ ┌─ ✓ 结果 ────────────────────────────────────────┐                                          │
│ │ ✓ 已写入                                          │                                          │
│ └──────────────────────────────────────────────────┘                                          │
│                                                                                              │
│ ── 完成 ──                                                                                    │
└──────────────────────────────────────────────────────────────┘
```

---

> 本文档到此结束。`plan.md` 的迭代 7 条目（含 7a + 7b）可标记为「设计完成，待实现」。实现完成后将本文件头部状态改为 `[已完成]`。
