# 迭代 7a：TUI 展示层（Spectre.Console Live + 状态栏 + Tab 补全）— 详细设计

> 状态：[已完成]
> 拆分自 `iter-07-design.md`（保留为整体参考，不删除）。
> 7a 聚焦**展示层**——把迭代 6 的 `Console.Write` 行模式升级为 Spectre.Console `Live` 流式渲染 + 状态栏 + Tab 补全 + 降级路径。
> 7b（HITL 交互层：`IHitlGate` + `HitlPrompt` + `BatchToolExecutor` 改造 + `AgentLoop` 转发）待 7a 完成并验收后，由用户发起再写设计。
>
> 前置：迭代 6 已交付 ReAct 闭环——`AgentLoop` + `IAgentEventSink`/`ChannelEventSink`（无界 `Channel<AgentEvent>`）+ `BatchToolExecutor`（Read 并发 / Write 串行）+ `ChatChunk` + `AgentEvent`（12 种事件）+ 6 个内置工具 + `MockProvider.EnqueueScript`。App 当前用 `Console.Write` + `AnsiConsole.MarkupLine` 行模式消费事件流。
>
> **7a 的核心原则：零 Agent 层改动**。`BatchToolExecutor` / `AgentLoop` / `AgentEvent` / `ChannelEventSink` / `ChatChunk` / `ToolCallAccumulator` 全部不变。7a 只改"展示层"——把迭代 6 App.cs 内联的 `RenderEventsAsync` 抽取为独立的 `EventRenderer` + `TuiApp` + `ConsoleEventRenderer`（降级），并补齐状态栏与 Tab 补全。`ToolBlockedEvent` 在 7a 仍不产生（`BatchToolExecutor` 无 HITL），但 `EventRenderer` 预留其渲染分支为 7b 准备。

## 一、概述

迭代 6 让 ReAct 闭环跑通，但展示层仍是 `Console.Write` 逐字打印——没有状态栏、没有工具调用卡片化展示、没有 Tab 补全，且渲染逻辑内联在 `App.RenderEventsAsync` 中无法复用/测试。迭代 7a 把"展示层"这一件事补齐：**Live 流式渲染**让 Agent 过程可视且不闪烁，**状态栏**让用户随时看到 Agent 状态，**Tab 补全**让斜杠命令输入更友好，**降级路径**保证非交互终端不崩。

1. **`Live` 流式渲染**：用 Spectre.Console 的 `Live(IRenderable)` 显示器把"当前轮活跃区"（正在流的文本 + 进行中的工具调用）原地刷新，token 到达即追加，避免整段重绘闪烁。一轮结束后把活跃区内容"提交"到滚动历史（`AnsiConsole.Write`），清空活跃区进入下一轮。这是 Spectre.Console 流式 Agent 渲染的常见模式——Live 显示"进行中"，完成后提交。
2. **状态栏**：顶部固定一行（`Panel` + 灰色边框）显示 `Provider` / `Model` / `安全等级` / `上下文占比` / `当前轮次` / `工具数`。状态栏作为 Live 渲染目标的一部分，每次刷新重绘。上下文占比来自 `ConversationHistory.EstimatedTokens` 与配置的 `context_window_tokens`（如 DeepSeek 64K），超 70% 黄色、超 90% 红色。
3. **`EventRenderer`**：把 `AgentEvent` 翻译成 `IRenderable`（Spectre 渲染单元）。`TextDeltaEvent` → 累积到 `StringBuilder`；`ToolCallStartEvent` → `Panel` 卡片化展示工具名+参数；`ToolResultEvent` → 成功绿 `✓` 失败红 `✗`；`RoundStartEvent` → 灰色轮次标记。纯渲染逻辑无副作用，可单测。**预留 `SetTransient` 扩展点**为 7b HITL 提示接入做准备（7a 不使用，始终 null）。
4. **`InputReader` + Tab 补全**：用户输入行不用 Live（Live 与 `Console.ReadLine` 互斥）。本迭代手写一个带 Tab 补全的 `ReadLineWithCompletionAsync`——遇到 `/` 开头按 Tab 补全硬编码命令列表（`/clear` / `/exit` / `/quit` / `/help` / `/status`）。命令系统在迭代 10 完善，本迭代只做补全与硬编码分发。
5. **`ConsoleEventRenderer`（降级）**：抽取迭代 6 `App.RenderEventsAsync` 的渲染逻辑成独立类。`tui.mode: console` 配置时回退到行模式渲染。Live 在重定向/非交互终端（`Console.IsOutputRedirected` 或 `!Environment.UserInteractive`）自动降级。保证 CI/管道场景不崩。
6. **`IUiControl`**：UI 抽象接口（`PrintMessageAsync` / `SetStatus` / `RequestHitlAsync`），7a 最小定义，让迭代 10 命令系统能通过它调用 UI 而不直接耦合 `TuiApp`。7a 只定义接口，`TuiApp` 不实现（7a 无命令系统调用方）。
7. **`SecurityLevel` 占位**：状态栏显示"安全等级"，但安全层在迭代 8。7a 引入 `SecurityLevel` 枚举（`Strict` / `Normal` / `Permissive`），状态栏显示等级名（默认 `Normal`，硬编码无配置项）。7a 不做真实拦截——`BatchToolExecutor` 完全不变。配置项 `SecurityConfig` 留 7b/迭代 8。
8. **`TuiApp`（无 HITL 接线简化版）**：主 TUI 应用，装配 Live + 状态栏 + 事件消费 + 输入循环。7a 的 `TuiApp` 构造 `BatchToolExecutor` 时**不传** `IHitlGate`（等价迭代 6 行为，所有工具直接执行）。7b 接入时在此处加 `HitlPrompt` 装配与 `render`/`readKey` 回调注入。

本迭代**刻意保持**：
- **不动 Agent 层**：`BatchToolExecutor` / `AgentLoop` / `AgentEvent` / `ChannelEventSink` / `ChatChunk` / `ToolCallAccumulator` 全部不变。7a 是纯展示层升级，零回归风险。
- **不做 HITL**：`IHitlGate` / `HitlDecision` / `HitlPrompt` 不引入（7b）。`BatchToolExecutor` 不注入 `IHitlGate`，`write_file` / `run_command` 等有副作用的工具仍直接执行（与迭代 6 一致）。
- **不做安全层**：黑名单 / 沙箱 / 三档权限的真实拦截在迭代 8。`SecurityLevel` 仅状态栏显示占位。
- **不做命令系统**：`/clear` / `/exit` / `/help` / `/status` 硬编码分发，完整斜杠命令注册中心在迭代 10。`IUiControl` 预留接口供迭代 10。
- **不做会话持久化**：TUI 渲染的是内存历史，退出即丢失。JSONL 在迭代 10。
- **不做上下文截断/摘要**：状态栏显示占比，超 70% 黄色警告，但不自动压缩。压缩在迭代 9。
- **不做 AlternateScreen 全屏**：用"Live 活跃区 + 滚动历史"半全屏模式，降低终端兼容风险。
- **不做多行输入 / Syntax Highlight**：单行输入，多行与高亮在可选扩展。
- **不接 Anthropic Provider**：TUI 协议无关，Anthropic wire format 后续迭代。
- **`ToolBlockedEvent` 仍不产生**：7a 的 `BatchToolExecutor` 无 HITL，`AgentLoop` 不改。`EventRenderer` 预留 `ToolBlockedEvent` 渲染分支（7b 接入时不用改 `EventRenderer`），但 7a 主路径不触发。

> **与 `iter-07-design.md` 的关系**：`iter-07-design.md` 是迭代 7 的整体设计（含 7a + 7b），保留作为整体参考。本文档（`iter-07a-design.md`）是 7a 的独立设计，删除了 HITL 相关内容，明确 7b 待补部分。实现 7a 时以本文档为准；7a 验收后再写 `iter-07b-design.md` 补齐 HITL 交互层。

## 二、学习目标

1. **Spectre.Console `Live` 渲染**：理解 `Live(IRenderable)` 显示器的工作机制——它持有"当前渲染目标"，`Update(IRenderable)` 原地刷新，避免整段重绘闪烁。掌握"Live 显示进行中 + 完成后 `AnsiConsole.Write` 提交到滚动历史"的半全屏模式，这是 Spectre.Console 流式 Agent 的标准姿势。
2. **`IRenderable` 组合**：Spectre.Console 的渲染单元是组合模式——`Text` / `Markup` / `Panel` / `Rows` / `Columns` / `Rule` / `Table` 都是 `IRenderable`，可嵌套组合成复杂布局。理解"事件 → `IRenderable`"的翻译是渲染层的核心职责，与事件流解耦。
3. **终端能力检测与降级**：`Console.IsOutputRedirected` / `Environment.UserInteractive` / `AnsiConsole.Console.Profile.Capabilities.Interactive` 检测终端是否支持 Live。不支持时降级到行模式（`ConsoleEventRenderer`），保证 CI/管道/重定向场景不崩。理解"渐进增强"——能力全时用 Live，能力弱时降级。
4. **Tab 补全实现**：手写 `ReadLineWithCompletionAsync`——`Console.ReadKey` 逐键读，Tab 触发补全（前缀匹配命令列表，唯一匹配则填充，多匹配列选项）。理解 Spectre.Console 的 `TextPrompt<T>` 不原生支持 Tab 补全，需自行实现读行循环。
5. **状态栏与上下文占比**：`ConversationHistory.EstimatedTokens`（迭代 4 的字符数/3 估算）与配置的 `context_window_tokens` 计算占比，超 70% 黄色、超 90% 红色。理解占比是"软提示"——7a 只警告不压缩（压缩在迭代 9）。
6. **`IUiControl` 抽象（预留）**：预留 UI 接口让迭代 10 命令系统通过抽象调用 UI（`PrintMessage` / `SetStatus`），不直接耦合 `TuiApp`。理解"依赖倒置"——命令系统依赖 `IUiControl` 抽象，TUI 是其实现。
7. **渲染层与 Agent 层解耦**：7a 的核心教训是"展示层可独立于 Agent 层演进"。通过抽取 `EventRenderer` + `TuiApp` + `ConsoleEventRenderer`，把迭代 6 内联在 `App.RenderEventsAsync` 的渲染逻辑模块化，让 7b 的 HITL 接入只需扩展 `EventRenderer`（加临时渲染项）而不用改 Agent 层。
8. **扩展点预留**：`EventRenderer.SetTransient` 是为 7b HITL 提示预留的扩展点。7a 不使用（始终 null），但 `BuildActive` 已包含"若有临时项则加入活跃区"的逻辑。7b 的 `HitlPrompt` 调用 `SetTransient(prompt)` 渲染 HITL 提示到活跃区，不用改 `EventRenderer` 核心结构。理解"为未来留口子但不提前实现"的设计权衡。

## 三、范围

### 3.1 本迭代包含（In Scope）

| 项 | 说明 |
| --- | --- |
| `Tui/TuiApp.cs` | 主 TUI 应用（无 HITL 接线简化版）：装配 Live + 状态栏 + 事件消费 + 输入循环 |
| `Tui/EventRenderer.cs` | `AgentEvent` → `IRenderable` 翻译器（纯渲染逻辑，可单测）+ `SetTransient` 扩展点为 7b 预留 |
| `Tui/StatusBar.cs` | 状态栏组件：`Provider` / `Model` / `安全等级` / `上下文占比` / `当前轮次` / `工具数` |
| `Tui/InputReader.cs` | 带 Tab 补全的输入读取：`/` 开头补全硬编码命令列表 |
| `Tui/ConsoleEventRenderer.cs` | 降级渲染器：迭代 6 行模式渲染抽取成类（`tui.mode: console` 或非交互终端时用） |
| `Tui/IUiControl.cs` | UI 抽象接口（预留，7a 只定义不实现）：`PrintMessageAsync` / `SetStatus` / `RequestHitlAsync` |
| `Tui/SecurityLevel.cs` | `SecurityLevel` 枚举占位（`Strict` / `Normal` / `Permissive`），7a 仅状态栏显示，默认 `Normal` |
| `App/App.cs` | 改：主循环委托 `TuiApp`（或降级到 `ConsoleEventRenderer`）；移除内联 `RenderEventsAsync` |
| `Config/Models.cs` | 扩展：`AppConfig` 加 `Tui` 节（`Mode` / `ShowStatusBar` / `ContextWindowTokens`） |
| `example.parrotcode.yaml` | 加 `tui:` 配置节示例（不含 `enable_hitl` / `security`） |
| `Program.cs` | 改：装配 `TuiApp` / `SecurityLevel`；终端能力检测决定 Live/降级 |
| 单元测试 | `EventRendererTests` / `StatusBarTests` / `InputReaderTests` / `ConsoleEventRendererTests` / `TuiAppIntegrationTests`（端到端，用 MockProvider 脚本，无 HITL 场景） |

### 3.2 本迭代不包含（Out of Scope）

- **HITL（人在回路）** → 7b：`IHitlGate` / `HitlDecision` / `HitlPrompt` 不引入；`BatchToolExecutor` 不注入 `IHitlGate`；`write_file` / `run_command` 仍直接执行（与迭代 6 一致）
- **`BatchToolExecutor` 改造** → 7b：不注入 `IHitlGate`，不加 `OnBeforeExecuteAsync` hook
- **`AgentLoop` 改造** → 7b：不改 `ToolBlockedEvent` 转发逻辑（7a 主路径不产生此事件）
- **`TuiConfig.EnableHitl`** → 7b：7a 的 `TuiConfig` 只有 `Mode` / `ShowStatusBar` / `ContextWindowTokens`
- **`SecurityConfig` 配置项** → 7b/迭代 8：7a 的 `SecurityLevel` 硬编码 `Normal`，无配置项
- 安全层（黑名单 / 沙箱 / 三档权限真实拦截）→ 迭代 8
- 斜杠命令注册中心 / 反射扫描 / 别名 → 迭代 10（`/help` / `/status` 硬编码实现）
- 会话持久化（JSONL）→ 迭代 10
- 上下文截断 / 摘要 / 熔断 → 迭代 9（状态栏占比只警告不压缩）
- AlternateScreen 全屏 + 固定分屏布局 → 进阶练习（7a 半全屏）
- 多行输入 / 语法高亮 / 输入历史（↑↓）→ 可选扩展
- thinking 内容灰色折叠渲染 → 进阶练习（DeepSeek `reasoning_content` 字段解析）
- `AnthropicProvider` 接入 → 后续迭代

### 3.3 与迭代 6 的边界

| 迭代 6 | 迭代 7a |
| --- | --- |
| 事件消费者是 `App.RenderEventsAsync`（内联 `Console.Write` + `AnsiConsole.MarkupLine`） | 抽取为 `EventRenderer` + `TuiApp`（Live）+ `ConsoleEventRenderer`（降级） |
| `ToolBlockedEvent` 定义但主路径不产生 | **仍不产生**（`BatchToolExecutor` 无 HITL），`EventRenderer` 预留渲染分支 |
| 无状态栏 | 顶部状态栏（Provider/Model/安全等级/上下文占比/轮次/工具数） |
| 无 Tab 补全 | `/` 开头 Tab 补全硬编码命令 |
| `BatchToolExecutor` 直接执行 Write 组 | **不变**（7a 不动 `BatchToolExecutor`） |
| `AgentLoop` 工具结果统一 `ToolResultEvent` | **不变**（7a 不动 `AgentLoop`） |
| `ChannelEventSink` 无界 | 不变（仍是事件流通道，消费者从 App 改为 TuiApp） |
| `App` 直接装配工具 + 内联渲染 | TuiApp 装配工具（与迭代 6 一致）+ 委托渲染 |

> 7a 的 `IAgentEventSink` 抽象与 `ChannelEventSink` 实现**不变**。`TuiApp` 内部从 `sink.Reader.ReadAllAsync` 读事件，调 `EventRenderer` 翻译，刷新 Live。7a 的 `App.cs` 委托 `TuiApp`，旧 `RenderEventsAsync` 逻辑迁移到 `ConsoleEventRenderer`（降级路径）。

### 3.4 与迭代 7b 的边界

| 迭代 7a | 迭代 7b |
| --- | --- |
| `EventRenderer` 预留 `SetTransient` 扩展点（7a 不使用） | `HitlPrompt` 调 `SetTransient(prompt)` 渲染 HITL 提示到活跃区 |
| `TuiApp` 构造 `BatchToolExecutor` 不传 `IHitlGate` | 加 `HitlPrompt` 装配 + `render`/`readKey` 回调注入 |
| `BatchToolExecutor` 不变 | 注入 `IHitlGate?` + `OnBeforeExecuteAsync` hook 预留 |
| `AgentLoop` 不变 | HITL 拒绝时 emit `ToolBlockedEvent` |
| `ToolBlockedEvent` 不产生 | HITL 拒绝时产生 |
| `TuiConfig` 无 `EnableHitl` | 加 `EnableHitl` 字段 |
| `SecurityLevel` 硬编码 `Normal` | 加 `SecurityConfig` 配置项 |
| `/help` / `/status` 硬编码分发 | 不变（7b 不动命令分发） |

### 3.5 与迭代 8/10 的边界

| 迭代 7a | 迭代 8 | 迭代 10 |
| --- | --- | --- |
| `SecurityLevel` 枚举占位，状态栏显示 | `SecurityGuard` 真实拦截 | `/mode` 命令切换等级 |
| `/help` / `/status` / `/clear` / `/exit` 硬编码 | — | `Commands/` 注册中心 + 反射扫描 |
| `IUiControl` 最小定义（不实现） | — | 命令系统通过 `IUiControl` 调用 UI |
| Tab 补全硬编码命令列表 | — | 补全来自 `Registry` 注册的命令 |
| 会话历史内存版 | — | JSONL 持久化 |

## 四、架构设计

### 4.1 模块结构（迭代 7a 增量）

```
ParrotCode.Net/
├── Program.cs                 # 改：终端能力检测 + 装配 TuiApp/SecurityLevel
├── App/
│   └── App.cs                 # 改：委托 TuiApp 或降级到 ConsoleEventRenderer
├── Config/
│   └── Models.cs              # 改：AppConfig 加 Tui 节（Mode/ShowStatusBar/ContextWindowTokens）
├── Agent/                     # 【全部不变】
│   ├── AgentLoop.cs           # 不变
│   ├── BatchToolExecutor.cs   # 不变（不注入 IHitlGate）
│   ├── ChatChunk.cs           # 不变
│   ├── AgentEvent.cs          # 不变（12 种事件稳定）
│   ├── IAgentEventSink.cs     # 不变
│   ├── ChannelEventSink.cs    # 不变
│   └── ToolCallAccumulator.cs # 不变
├── Conversation/              # 全部不变（EstimatedTokens 供状态栏）
├── Providers/                 # 全部不变
├── Tools/                     # 全部不变（6 个工具）
└── Tui/                       # 新增目录
    ├── TuiApp.cs              # 新增：主 TUI 应用（无 HITL 接线，Live + 状态栏 + 事件消费 + 输入）
    ├── EventRenderer.cs       # 新增：AgentEvent → IRenderable 翻译 + SetTransient 扩展点（7b 预留）
    ├── StatusBar.cs           # 新增：状态栏组件
    ├── InputReader.cs         # 新增：带 Tab 补全的输入读取
    ├── ConsoleEventRenderer.cs# 新增：降级行模式渲染（迭代 6 抽取）
    ├── IUiControl.cs          # 新增：UI 抽象接口（预留，7a 不实现）
    └── SecurityLevel.cs       # 新增：安全等级枚举占位
```

> 命名空间约定沿用迭代 1-6：所有源文件统一 `namespace ParrotCode`，文件夹仅作物理组织。

### 4.2 调用流程（TUI 主循环 + 事件消费，无 HITL）

```
┌──────────────────────────────────────────────────────────────────────┐
│  TuiApp.RunAsync                                                     │
│  while (!ct.IsCancellationRequested):                                │
│      input = InputReader.ReadLineWithCompletionAsync(commands, ct)   │
│      if input is /clear: history.Clear(); continue                   │
│      if input is /exit|/quit: break                                  │
│      if input is /help|/status: renderHelp/Status(); continue        │
│      history.AddUser(input)                                          │
│      var sink = new ChannelEventSink()                              │
│      var agentTask = agentLoop.RunAsync(history, sink, ct)           │
│      # —— Live 流式渲染活跃区 ——                                       │
│      await AnsiConsole.Live(new Text("")).StartAsync(async ctx => {  │
│          await foreach (evt in sink.Reader.ReadAllAsync(ct)):        │
│              if evt is RoundStartEvent(r): statusBar.CurrentRound=r │
│              renderer.Render(evt)                                    │
│              ctx.Update(renderer.BuildActive(statusBar))             │
│              if IsCompletingEvent(evt):                               │
│                  # 提交到滚动历史，清空活跃区                            │
│                  AnsiConsole.WriteLine()                             │
│                  AnsiConsole.Write(renderer.BuildCommitted())        │
│                  renderer.Reset()                                    │
│      })                                                              │
│      await agentTask                                                 │
└──────────────────────────────────────────────────────────────────────┘
                                  │
                                  ▼
┌──────────────────────────────────────────────────────────────────────┐
│  AgentLoop.RunAsync（迭代 6 完全不变）                                  │
│  for round in 1..maxRounds:                                          │
│      sink.WriteAsync(RoundStartEvent(round))                         │
│      流式 LLM → 累积文本/tool_calls → sink.Write(TextDeltaEvent)     │
│      toolCalls = tcAcc.Build()                                       │
│      history.AddAssistant(...)                                       │
│      if no toolCalls: sink.Write(AgentDoneEvent); return            │
│      sink.Write(ToolCallStartEvent(call))  # 每个                    │
│      results = batchExecutor.ExecuteAsync(toolCalls, ct)             │
│          ┌─ BatchToolExecutor（迭代 6 不变）──────────────────┐      │
│          │ Read 组: Task.WhenAll（不问 HITL）                │      │
│          │ Write 组: foreach call: executor.ExecuteAsync     │      │
│          │   （7a 无 HITL，直接执行——与迭代 6 一致）          │      │
│          └──────────────────────────────────────────────────┘      │
│      foreach (call, result):                                         │
│          sink.Write(ToolResultEvent(call, result))                   │
│          history.AddTool(...)                                        │
│      sink.Write(RoundEndEvent(round))                                │
│  sink.Write(MaxRoundsReachedEvent)                                   │
└──────────────────────────────────────────────────────────────────────┘
```

> **7a 与迭代 6 的唯一区别在消费侧**：`App.RenderEventsAsync`（`Console.Write` 行模式）替换为 `TuiApp` + `Live` 流式渲染。`AgentLoop` / `BatchToolExecutor` 完全不变。降级时 `App` 用 `ConsoleEventRenderer`（逻辑与迭代 6 `RenderEventsAsync` 一致）。

### 4.3 关键类型设计

#### 4.3.1 `EventRenderer`（事件 → IRenderable 翻译）

```csharp
using System.Text;
using Spectre.Console;

namespace ParrotCode;

/// <summary>
/// 把 AgentEvent 翻译成 Spectre.Console IRenderable。
/// 纯渲染逻辑无副作用——输入事件，更新内部累积状态，输出 IRenderable。
/// 可单测：断言返回的 IRenderable 类型与内容。
///
/// 渲染策略：
/// - TextDeltaEvent: 累积到 _textBuf，由 BuildActive 一起输出
/// - ToolCallStartEvent: 加 Panel 到 _pending
/// - ToolResultEvent: 成功绿 ✓ + 截断内容；失败红 ✗ + 错误，加 Panel 到 _pending
/// - ToolBlockedEvent: 红色 Panel "被拦截"（7a 预留分支，主路径不触发——7b HITL 接入后产生）
/// - RoundStartEvent: 灰色 [Round N]，清空 _textBuf 与 _pending
/// - AgentDoneEvent: 灰色 [完成]
/// - MaxRoundsReachedEvent: 黄色 ⚠
/// - ErrorEvent: 红色 ✗ 错误
/// - CancelledEvent: 灰色 [已取消]
///
/// SetTransient 扩展点（7b 预留）：设置一个临时 IRenderable 加入活跃区。
/// 7a 不使用（始终 null）。7b 的 HitlPrompt 调 SetTransient(prompt) 渲染 HITL 提示。
/// </summary>
public sealed class EventRenderer
{
    private readonly StringBuilder _textBuf = new();
    private readonly List<IRenderable> _pending = new();  // 本轮已完成项（工具卡片等）
    private IRenderable? _transient;  // 7b 预留：临时渲染项（HITL 提示），7a 始终 null
    private int _currentRound;

    /// <summary>当前轮活跃区文本（供状态栏或调试查看）。</summary>
    public string CurrentText => _textBuf.ToString();

    /// <summary>当前轮次号。</summary>
    public int CurrentRound => _currentRound;

    /// <summary>重置渲染器（新一轮开始或提交后调）。</summary>
    public void Reset()
    {
        _textBuf.Clear();
        _pending.Clear();
        _transient = null;
        _currentRound = 0;
    }

    /// <summary>
    /// 设置临时渲染项（7b 预留扩展点）。
    /// 7a 不调用此方法。7b 的 HitlPrompt 调用此方法把 HITL 提示 Panel 加入活跃区。
    /// 传 null 清除临时项。
    /// </summary>
    public void SetTransient(IRenderable? renderable) => _transient = renderable;

    /// <summary>
    /// 渲染单个事件为 IRenderable，并更新内部累积状态。
    /// 返回 null 表示该事件不产生独立 IRenderable（如 TextDelta 累积到 _textBuf，由 BuildActive 一起输出）。
    /// </summary>
    public IRenderable? Render(AgentEvent evt)
    {
        switch (evt)
        {
            case AgentEvent.RoundStartEvent(var round):
                _currentRound = round;
                _textBuf.Clear();
                _pending.Clear();
                _transient = null;
                return new Markup($"[grey]── Round {round} ──[/]");

            case AgentEvent.TextDeltaEvent(var text):
                _textBuf.Append(text);
                return null;  // 累积，由 BuildActive 统一输出

            case AgentEvent.AssistantMessageEvent:
                return null;  // 文本已在 TextDelta 实时展示

            case AgentEvent.ToolCallStartEvent(var call):
                var callPanel = new Panel(new Markup(
                    $"[cyan]{Markup.Escape(call.Name)}[/]([grey]{Markup.Escape(Truncate(call.Input.GetRawText(), 100))}[/])"))
                {
                    Header = new PanelHeader("[cyan]→ 工具调用[/]"),
                    BorderStyle = new Style(foreground: Color.Cyan1),
                    Padding = new Padding(2, 0, 2, 0)
                };
                _pending.Add(callPanel);
                return null;  // 由 BuildActive 统一输出

            case AgentEvent.ToolResultEvent(_, var result):
                var (icon, color, content) = result.Success
                    ? ("✓", Color.Green, Truncate(result.Content, 200))
                    : ("✗", Color.Red, Markup.Escape(result.Error ?? "未知错误"));
                var resultPanel = new Panel(new Markup($"[{color}]{icon}[/] {content}"))
                {
                    Header = new PanelHeader(result.Success ? "[green]✓ 结果[/]" : "[red]✗ 失败[/]"),
                    BorderStyle = new Style(foreground: color),
                    Padding = new Padding(2, 0, 2, 0)
                };
                _pending.Add(resultPanel);
                return resultPanel;

            case AgentEvent.ToolBlockedEvent(var call, var reason):
                // 7a 预留分支：主路径不触发（BatchToolExecutor 无 HITL）。
                // 7b HITL 拒绝时由 AgentLoop 产生此事件，此处渲染红色拦截卡片。
                var blockedPanel = new Panel(new Markup(
                    $"[red]✗ 被拦截[/] [cyan]{Markup.Escape(call.Name)}[/]\n[red]{Markup.Escape(reason)}[/]"))
                {
                    Header = new PanelHeader("[red]⛔ 拦截[/]"),
                    BorderStyle = new Style(foreground: Color.Red),
                    Padding = new Padding(2, 0, 2, 0)
                };
                _pending.Add(blockedPanel);
                return blockedPanel;

            case AgentEvent.RoundEndEvent:
                return null;  // 不渲染（RoundStart 已标记）

            case AgentEvent.AgentDoneEvent:
                return new Markup("[grey]── 完成 ──[/]");

            case AgentEvent.MaxRoundsReachedEvent(var rounds):
                return new Markup($"[yellow]⚠ 已达最大轮次 {rounds}[/]");

            case AgentEvent.WarningEvent(var msg):
                return new Markup($"[yellow]⚠[/] {Markup.Escape(msg)}");

            case AgentEvent.ErrorEvent(var msg, _):
                return new Markup($"[red]✗ 错误：[/]{Markup.Escape(msg)}");

            case AgentEvent.CancelledEvent:
                return new Markup("[grey]── 已取消 ──[/]");

            default:
                return null;
        }
    }

    /// <summary>
    /// 构建当前 Live 活跃区的 IRenderable（状态栏 + 轮次 + 文本 + 进行中工具卡片 + 临时项）。
    /// 每次 Live 刷新时调此方法得到最新渲染目标。
    /// _transient 非 null 时加入活跃区尾部（7b HITL 提示用）。
    /// </summary>
    public IRenderable BuildActive(StatusBar statusBar)
    {
        var rows = new List<IRenderable>();

        // 1. 状态栏（顶部固定）
        if (statusBar is not null)
            rows.Add(statusBar.Render());

        // 2. 轮次标记
        if (_currentRound > 0)
            rows.Add(new Markup($"[grey]Round {_currentRound}[/]"));

        // 3. 当前流式文本（如果有）
        if (_textBuf.Length > 0)
            rows.Add(new Text(_textBuf.ToString()));

        // 4. 进行中/已完成的工具卡片（限制最近 5 个，避免撑满屏）
        if (_pending.Count > 5)
        {
            rows.Add(new Markup($"[grey]...还有 {_pending.Count - 5} 个更早的工具卡片[/]"));
            rows.AddRange(_pending.Skip(_pending.Count - 5));
        }
        else
        {
            rows.AddRange(_pending);
        }

        // 5. 临时渲染项（7b 预留：HITL 提示；7a 始终 null 不渲染）
        if (_transient is not null)
            rows.Add(_transient);

        return new Rows(rows);
    }

    /// <summary>
    /// 提取已完成的内容作为滚动历史提交（AgentDone 后调）。
    /// 返回的 IRenderable 不含状态栏（状态栏是 Live 专属）与临时项（HITL 提示是 Live 内的）。
    /// </summary>
    public IRenderable BuildCommitted()
    {
        var rows = new List<IRenderable>();
        if (_currentRound > 0)
            rows.Add(new Markup($"[grey]Round {_currentRound}[/]"));
        if (_textBuf.Length > 0)
            rows.Add(new Text(_textBuf.ToString()));
        rows.AddRange(_pending);
        return new Rows(rows);
    }

    private static string Truncate(string s, int max) =>
        s.Length <= max ? s : s[..(max - 3)] + "...";
}
```

> **设计要点**：
> - **`Render` 返回 `IRenderable?`**：`TextDeltaEvent` 累积到 `_textBuf` 返回 null（由 `BuildActive` 统一输出），完成事件（`ToolResult`/`AgentDone`）返回独立 IRenderable 便于即时反馈。双轨：累积态 + 即时态。
> - **`_pending` 列表**：本轮已完成的工具卡片，`BuildActive` 时与文本一起渲染。`RoundStartEvent` 清空（新一轮）。
> - **`_pending` 限制最近 5 个**：一轮内 10+ 工具调用时，显示最近 5 个 + "...还有 N 个"，避免撑满屏。
> - **`SetTransient` 扩展点（7b 预留）**：7a 不调用（`_transient` 始终 null），`BuildActive` 的"5. 临时渲染项"分支不触发。7b 的 `HitlPrompt` 调 `SetTransient(prompt)` 渲染 HITL 提示，决策后 `SetTransient(result)` 刷新，最后 `SetTransient(null)` 清除。7b 不用改 `EventRenderer` 核心结构。
> - **`BuildActive` vs `BuildCommitted`**：前者含状态栏 + 临时项（Live 刷新用），后者不含（提交到滚动历史用，因为状态栏是 Live 专属不应进历史）。
> - **`ToolBlockedEvent` 预留分支**：7a 主路径不触发（`BatchToolExecutor` 无 HITL），但 `EventRenderer.Render` 已处理此事件类型。7b 接入后由 `AgentLoop` 产生此事件，`EventRenderer` 不用改。单测可构造 `ToolBlockedEvent` 验证渲染。
> - **`Panel` 卡片化**：工具调用/结果用 `Panel` 包裹，带颜色边框 + Header，视觉区分。`Markup.Escape` 防止工具内容含 Spectre 标记字符（`[` 等）破坏渲染。
> - **纯逻辑可测**：`Render` 与 `BuildActive` 是纯函数（输入事件，输出 IRenderable + 更新内部状态），单测可断言 `_textBuf.ToString()`、`_pending.Count`、返回的 `Panel.Header` 文本等。

#### 4.3.2 `StatusBar`

```csharp
using Spectre.Console;

namespace ParrotCode;

/// <summary>
/// 状态栏组件。显示 Provider/Model/安全等级/上下文占比/当前轮次/工具数。
/// 作为 Live 渲染目标的一部分，每次刷新调 Render() 返回最新 IRenderable。
/// 7a 安全等级硬编码 Normal（无配置项）；7b/迭代 8 加配置项后可切换。
/// </summary>
public sealed class StatusBar
{
    public string Provider { get; set; } = string.Empty;
    public string Model { get; set; } = string.Empty;
    public SecurityLevel SecurityLevel { get; set; } = SecurityLevel.Normal;  // 7a 硬编码 Normal
    public int EstimatedTokens { get; set; }
    public int ContextWindowTokens { get; set; } = 64000;  // 默认 64K（DeepSeek）
    public int CurrentRound { get; set; }
    public int ToolCount { get; set; }

    /// <summary>上下文占比（0-1）。超 0.7 黄色，超 0.9 红色。</summary>
    public double ContextRatio =>
        ContextWindowTokens > 0 ? (double)EstimatedTokens / ContextWindowTokens : 0;

    public IRenderable Render()
    {
        var ratio = ContextRatio;
        var ratioColor = ratio >= 0.9 ? "red" : ratio >= 0.7 ? "yellow" : "green";
        var pct = (int)(ratio * 100);
        var securityColor = SecurityLevel switch
        {
            SecurityLevel.Strict      => "red",
            SecurityLevel.Normal      => "yellow",
            SecurityLevel.Permisive  => "green",
            _ => "grey"
        };

        var provider = Truncate(Provider, 20);
        var model = Truncate(Model, 20);

        var markup =
            $"[grey]provider=[/][cyan]{Markup.Escape(provider)}[/] " +
            $"[grey]model=[/][cyan]{Markup.Escape(model)}[/] " +
            $"[grey]security=[/][{securityColor}]{SecurityLevel}[/] " +
            $"[grey]ctx=[/][{ratioColor}]{pct}%[/]({EstimatedTokens}/{ContextWindowTokens}) " +
            $"[grey]round=[/][cyan]{CurrentRound}[/] " +
            $"[grey]tools=[/][cyan]{ToolCount}[/]";

        return new Panel(new Markup(markup))
        {
            BorderStyle = new Style(foreground: Color.Grey50),
            Padding = new Padding(1, 0, 1, 0)
        };
    }

    private static string Truncate(string s, int max) =>
        s.Length <= max ? s : s[..(max - 3)] + "...";
}
```

> **设计要点**：
> - **`Panel` + 灰色边框**：状态栏用细灰边框 Panel 包裹，视觉上与输出区分隔。每次 Live 刷新重绘。
> - **占比颜色**：绿（<70%）/黄（70-90%）/红（>90%）。7a 只警告不压缩（压缩在迭代 9）。
> - **`SecurityLevel` 枚举占位**：状态栏显示等级名，颜色区分（Strict 红/Normal 黄/Permissive 绿）。7a 默认 `Normal`，迭代 8 接入真实拦截后可被 `/mode` 命令切换（迭代 10）。
> - **`Markup.Escape`**：Provider/Model 名可能含特殊字符，转义防止破坏 Markup。
> - **`Truncate` 截断**：Provider/Model 名截断到 20 字符，防止状态栏过长换行。

#### 4.3.3 `SecurityLevel`（占位枚举）

```csharp
namespace ParrotCode;

/// <summary>
/// 安全等级枚举（7a 占位，迭代 8 接入真实拦截）。
/// - Strict: 只允许白名单路径读写（迭代 8 实现）。
/// - Normal: 读放行、写询问（HITL，7b 接入）。
/// - Permissive: 仅黑名单拦截（迭代 8）。
/// 7a 仅状态栏显示，不做真实拦截。7a 硬编码 Normal，无配置项。
/// </summary>
public enum SecurityLevel
{
    Strict,
    Normal,
    Permisive  // 注意：沿用 plan.md 拼写；迭代 8 可纠正为 Permissive
}
```

> **拼写说明**：`plan.md` 第八章写的是 `Permissive`。本设计沿用 plan.md 拼写 `Permisive` 以保持一致；若迭代 8 纠正为 `Permissive`，需同步迁移。实现时建议用 `Permissive`（正确拼写），本设计文档保留 plan.md 原样以暴露此偏差。

#### 4.3.4 `InputReader`（带 Tab 补全）

```csharp
using System.Text;
using Spectre.Console;

namespace ParrotCode;

/// <summary>
/// 带 Tab 补全的输入读取器。
/// 遇 / 开头按 Tab 补全硬编码命令列表。Enter 提交，Esc 取消，Backspace 删除。
/// 迭代 10 命令系统完善后，命令列表来自 Registry。
///
/// 注意：此方法不在 Live 期间调用（Live 与 Console.ReadKey 互斥）。
/// TuiApp 在 Live 结束后（IsCompletingEvent 提交后）调用此方法读输入。
/// </summary>
public sealed class InputReader
{
    private readonly string[] _commands;

    public InputReader(string[]? commands = null)
    {
        _commands = commands ?? new[] { "/clear", "/exit", "/quit", "/help", "/status" };
    }

    public async Task<string?> ReadLineWithCompletionAsync(CancellationToken ct)
    {
        var buf = new StringBuilder();
        AnsiConsole.Markup("[bold blue]> [/]");

        while (!ct.IsCancellationRequested)
        {
            ConsoleKeyInfo key;
            try
            {
                key = await Task.Run(() => Console.ReadKey(true), ct);
            }
            catch (OperationCanceledException)
            {
                return null;
            }

            if (key.Key == ConsoleKey.Enter)
            {
                AnsiConsole.WriteLine();
                return buf.ToString();
            }
            if (key.Key == ConsoleKey.Escape)
            {
                AnsiConsole.WriteLine();
                return null;  // 取消
            }
            if (key.Key == ConsoleKey.Tab && buf.Length > 0 && buf[0] == '/')
            {
                var prefix = buf.ToString();
                var matches = _commands.Where(c => c.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ToArray();
                if (matches.Length == 1)
                {
                    // 唯一匹配——填充
                    ClearLine(buf.Length);
                    buf.Clear();
                    buf.Append(matches[0]);
                    AnsiConsole.Markup($"[cyan]{Markup.Escape(matches[0])}[/]");
                }
                else if (matches.Length > 1)
                {
                    // 多匹配——显示选项（不填充）
                    AnsiConsole.WriteLine();
                    AnsiConsole.MarkupLine(string.Join("  ", matches.Select(m => $"[cyan]{Markup.Escape(m)}[/]")));
                    AnsiConsole.Markup("[bold blue]> [/]");
                    AnsiConsole.Markup($"[cyan]{Markup.Escape(buf.ToString())}[/]");
                }
                continue;
            }
            if (key.Key == ConsoleKey.Backspace && buf.Length > 0)
            {
                buf.Remove(buf.Length - 1, 1);
                ClearLine(buf.Length + 1);
                AnsiConsole.Markup($"[bold blue]> [/]{Markup.Escape(buf.ToString())}");
                continue;
            }
            if (!char.IsControl(key.KeyChar))
            {
                buf.Append(key.KeyChar);
                var escaped = Markup.Escape(key.KeyChar.ToString());
                var color = buf[0] == '/' ? "cyan" : "white";
                AnsiConsole.Markup($"[{color}]{escaped}[/]");
            }
        }
        return null;
    }

    private static void ClearLine(int backCount)
    {
        // 回退 backCount 字符并清除（+2 抵消 "> "）
        for (var i = 0; i < backCount + 2; i++)
            Console.Write("\b \b");
    }
}
```

> **设计要点**：
> - **`Console.ReadKey(true)`**：`true` 不回显，自行控制显示（让 `/` 命令用青色、普通文本用白色）。
> - **Tab 补全逻辑**：`/` 开头才补全；唯一匹配直接填充（清行重写）；多匹配列选项不填充。Enter 提交，Esc 取消，Backspace 删除。
> - **`Task.Run(() => Console.ReadKey)`**：`ReadKey` 是同步阻塞调用，包成 Task 让 `await` 能响应 CancellationToken。Ctrl+C 由 Program 的全局 cts 处理。ct 取消时 `Task.Run` 抛 `OperationCanceledException`，捕获返回 null。
> - **不在 Live 期间调用**：`Console.ReadKey` 与 Live 互斥。TuiApp 在 Live `StartAsync` 回调结束后（`IsCompletingEvent` 提交 + Live 自然退出）调此方法。输入完成后再 Start 新 Live 渲染下一轮。
> - **多行输入**：7a 单行（Enter 即提交）。多行（Shift+Enter 换行）作为进阶练习。

#### 4.3.5 `ConsoleEventRenderer`（降级行模式）

```csharp
using System.Threading.Channels;
using Spectre.Console;

namespace ParrotCode;

/// <summary>
/// 降级行模式渲染器（迭代 6 App.RenderEventsAsync 抽取）。
/// 在 tui.mode=console 或终端非交互（重定向/CI）时使用。
/// 不用 Live，纯 Console.Write + AnsiConsole.MarkupLine。
/// 7a 新增 ToolBlockedEvent 渲染分支（7b 产生此事件时降级路径也支持）。
/// </summary>
internal sealed class ConsoleEventRenderer
{
    public async Task RenderAsync(ChannelReader<AgentEvent> reader, CancellationToken ct)
    {
        AnsiConsole.Markup("[green]AI：[/]");
        await foreach (var evt in reader.ReadAllAsync(ct))
        {
            switch (evt)
            {
                case AgentEvent.TextDeltaEvent(var text):
                    Console.Write(text);
                    break;
                case AgentEvent.ToolCallStartEvent(var call):
                    Console.WriteLine();
                    AnsiConsole.MarkupLine(
                        $"[cyan]→[/] {Markup.Escape(call.Name)}({Markup.Escape(Truncate(call.Input.GetRawText(), 80))})");
                    break;
                case AgentEvent.ToolResultEvent(_, var result):
                    if (result.Success)
                        AnsiConsole.MarkupLine($"[green]✓[/] {Markup.Escape(Truncate(result.Content, 80))}");
                    else
                        AnsiConsole.MarkupLine($"[red]✗[/] {Markup.Escape(result.Error ?? "未知错误")}");
                    break;
                case AgentEvent.ToolBlockedEvent(var call, var reason):
                    // 7a 预留分支：主路径不触发。7b HITL 拒绝时产生此事件，降级路径也渲染。
                    AnsiConsole.MarkupLine($"[red]⛔ {Markup.Escape(call.Name)} 被拦截：[/]{Markup.Escape(reason)}");
                    break;
                case AgentEvent.AgentDoneEvent:
                    Console.WriteLine();
                    break;
                case AgentEvent.MaxRoundsReachedEvent(var rounds):
                    Console.WriteLine();
                    AnsiConsole.MarkupLine($"[yellow]⚠ 已达最大轮次 {rounds}[/]");
                    break;
                case AgentEvent.WarningEvent(var msg):
                    AnsiConsole.MarkupLine($"[yellow]⚠[/] {Markup.Escape(msg)}");
                    break;
                case AgentEvent.ErrorEvent(var msg, _):
                    Console.WriteLine();
                    AnsiConsole.MarkupLine($"[red]✗ 错误：[/]{Markup.Escape(msg)}");
                    break;
                case AgentEvent.CancelledEvent:
                    AnsiConsole.MarkupLine("\n[grey]已取消。[/]");
                    break;
                case AgentEvent.RoundStartEvent:
                case AgentEvent.RoundEndEvent:
                case AgentEvent.AssistantMessageEvent:
                    break;
            }
        }
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..(max - 3)] + "...";
}
```

> **设计要点**：
> - **抽取自迭代 6 `App.RenderEventsAsync`**：行为一致，回归保护。降级时 App 用此类渲染。
> - **新增 `ToolBlockedEvent` 渲染分支**：迭代 6 此事件 no-op，7a 预留分支（红色 `⛔`）。7b 产生此事件时降级路径也支持。
> - **无 Live/状态栏**：降级路径无状态栏（状态栏需 Live 刷新）。`/status` 命令在降级模式直接打印状态信息。

#### 4.3.6 `TuiApp`（主 TUI 应用，无 HITL 接线）

```csharp
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Spectre.Console;

namespace ParrotCode;

/// <summary>
/// 主 TUI 应用（7a 简化版，无 HITL 接线）。
/// 装配 Live + 状态栏 + 事件消费 + 输入循环。
/// 替代迭代 6 App.cs 的内联渲染。降级时由 App 用 ConsoleEventRenderer 替代。
///
/// 7a 的 BatchToolExecutor 不注入 IHitlGate（等价迭代 6 行为，所有工具直接执行）。
/// 7b 接入时：在此处加 HitlPrompt 装配 + render/readKey 回调注入 EventRenderer.SetTransient。
/// </summary>
internal sealed class TuiApp
{
    private readonly IBaseProvider _provider;
    private readonly ProviderConfig _providerConfig;
    private readonly AgentConfig _agentConfig;
    private readonly TuiConfig _tuiConfig;
    private readonly SecurityLevel _securityLevel;
    private readonly ILogger _logger;
    private readonly CancellationToken _ct;

    public TuiApp(
        IBaseProvider provider,
        ProviderConfig providerConfig,
        AgentConfig agentConfig,
        TuiConfig tuiConfig,
        SecurityLevel securityLevel,
        ILogger logger,
        CancellationToken ct)
    {
        _provider = provider;
        _providerConfig = providerConfig;
        _agentConfig = agentConfig;
        _tuiConfig = tuiConfig;
        _securityLevel = securityLevel;
        _logger = logger;
        _ct = ct;
    }

    public async Task RunAsync()
    {
        var history = new ConversationHistory();
        var inputReader = new InputReader();

        // 装配工具注册中心（与迭代 6 App 一致）
        var registry = new ToolRegistry();
        registry.Register(new ReadFileTool());
        registry.Register(new WriteFileTool());
        registry.Register(new EditFileTool());
        registry.Register(new GlobTool());
        registry.Register(new GrepTool());
        registry.Register(new RunCommandTool());

        var toolTimeout = TimeSpan.FromSeconds(_agentConfig.ToolTimeoutSeconds ?? 30);
        var executor = new ToolExecutor(registry, toolTimeout, _logger);

        // 7a：不注入 IHitlGate（等价迭代 6 行为）
        // 7b 接入时：var hitlGate = tuiConfig.EnableHitl ? new HitlPrompt(...) : NullHitlGate;
        //           并传入 BatchToolExecutor 构造函数
        var batchExecutor = new BatchToolExecutor(
            executor, registry,
            _agentConfig.MaxParallelism ?? 5,
            _logger);

        var statusBar = new StatusBar
        {
            Provider = _providerConfig.Name,
            Model = _providerConfig.Model,
            SecurityLevel = _securityLevel,
            ContextWindowTokens = _tuiConfig.ContextWindowTokens ?? 64000,
            ToolCount = registry.GetAll().Count
        };

        AnsiConsole.MarkupLine(
            $"[grey]ParrotCode.Net[/] [green]TUI 模式[/] | " +
            $"provider=[cyan]{Markup.Escape(_providerConfig.Name)}[/] " +
            $"model=[cyan]{Markup.Escape(_providerConfig.Model)}[/] " +
            $"security=[cyan]{_securityLevel}[/] " +
            $"tools=[cyan]{registry.GetAll().Count}[/]");

        while (!_ct.IsCancellationRequested)
        {
            statusBar.EstimatedTokens = history.EstimatedTokens;
            statusBar.CurrentRound = 0;

            // 1. 读输入（不在 Live 期间）
            var line = await inputReader.ReadLineWithCompletionAsync(_ct);
            if (line is null) break;

            if (line is "/exit" or "/quit") break;
            if (line is "/clear")
            {
                history.Clear();
                AnsiConsole.MarkupLine("[grey]已清空对话历史。[/]");
                continue;
            }
            if (line is "/help")
            {
                RenderHelp();
                continue;
            }
            if (line is "/status")
            {
                AnsiConsole.Write(statusBar.Render());
                AnsiConsole.WriteLine();
                continue;
            }
            if (string.IsNullOrWhiteSpace(line)) continue;

            AnsiConsole.MarkupLine($"[grey]你：[/]{Markup.Escape(line)}");
            history.AddUser(line);

            // 2. 构造 AgentLoop + 事件流（迭代 6 不变）
            var agentLoop = new AgentLoop(_provider, registry, batchExecutor,
                _agentConfig.MaxRounds ?? 10, _agentConfig.ToolChoice ?? "auto",
                _agentConfig.SystemPrompt, _logger);
            var sink = new ChannelEventSink();
            var agentTask = agentLoop.RunAsync(history, sink, _ct);

            // 3. Live 流式渲染活跃区
            await RenderLiveAsync(sink.Reader, statusBar);

            await agentTask;
        }

        _logger.LogInformation("程序退出");
    }

    private async Task RenderLiveAsync(ChannelReader<AgentEvent> reader, StatusBar statusBar)
    {
        var renderer = new EventRenderer();

        await AnsiConsole.Live(new Text("")).StartAsync(async ctx =>
        {
            await foreach (var evt in reader.ReadAllAsync(_ct))
            {
                // 更新状态栏轮次
                if (evt is AgentEvent.RoundStartEvent(var r))
                    statusBar.CurrentRound = r;

                renderer.Render(evt);
                ctx.Update(renderer.BuildActive(statusBar));
                ctx.Refresh();

                // 完成事件 → 提交到滚动历史，清空活跃区
                if (IsCompletingEvent(evt))
                {
                    var committed = renderer.BuildCommitted();
                    // Live 区域外写入会滚动（AnsiConsole.Write 在 Live 期间写到 Live 区域外）
                    AnsiConsole.WriteLine();
                    AnsiConsole.Write(committed);
                    renderer.Reset();
                }
            }
        });
    }

    private static bool IsCompletingEvent(AgentEvent evt) =>
        evt is AgentEvent.AgentDoneEvent
            or AgentEvent.MaxRoundsReachedEvent
            or AgentEvent.ErrorEvent
            or AgentEvent.CancelledEvent;

    private static void RenderHelp()
    {
        AnsiConsole.MarkupLine("[grey]可用命令：[/]");
        AnsiConsole.MarkupLine("  [cyan]/clear[/]  清空对话历史");
        AnsiConsole.MarkupLine("  [cyan]/status[/] 显示状态栏");
        AnsiConsole.MarkupLine("  [cyan]/help[/]   显示帮助");
        AnsiConsole.MarkupLine("  [cyan]/exit[/]   退出");
    }
}
```

> **设计要点**：
> - **`BatchToolExecutor` 不注入 `IHitlGate`**：7a 用迭代 6 的四参数构造函数（`executor` / `registry` / `maxParallelism` / `logger`），等价迭代 6 行为。7b 接入时改为五参数构造函数加 `hitlGate`。
> - **`Live(new Text("")).StartAsync`**：启动时渲染目标为空 `Text`，随后 `ctx.Update(renderer.BuildActive(statusBar))` 刷新为实际内容。Live 在 `StartAsync` 回调返回时自然结束（事件流读完）。
> - **`IsCompletingEvent`**：`AgentDone`/`MaxRoundsReached`/`Error`/`Cancelled` 触发提交。`ToolResultEvent`/`RoundEndEvent` 不提交（继续累积到活跃区，直到 AgentDone 才整轮提交）。这让一轮内的多个工具调用在活跃区内连续展示，轮次结束统一提交。
> - **`renderer.Reset()`**：提交后清空活跃区，下一轮从空开始。
> - **状态栏轮次实时更新**：`RoundStartEvent` 时设 `statusBar.CurrentRound`，`BuildActive` 时刷新状态栏。
> - **输入与 Live 分离**：`InputReader.ReadLineWithCompletionAsync` 在 Live `StartAsync` 回调结束后调用（Live 已退出），`Console.ReadKey` 不与 Live 冲突。
> - **7b 接入点（注释标注）**：7b 在 `batchExecutor` 构造处加 `HitlPrompt` 装配，并把 `render`/`readKey` 回调连接到 `EventRenderer.SetTransient`。7a 的 `TuiApp` 结构已为此预留（`renderer` 是 `RenderLiveAsync` 的局部变量，7b 可在回调中调用 `renderer.SetTransient`）。

#### 4.3.7 `IUiControl`（预留 UI 抽象，7a 只定义不实现）

```csharp
namespace ParrotCode;

/// <summary>
/// UI 抽象接口（7a 最小定义，迭代 10 命令系统通过它调用 UI）。
/// 让命令系统不直接耦合 TuiApp，便于替换 UI 实现。
/// 7a 只定义接口，TuiApp 不实现（7a 无命令系统调用方）。
/// 7b/迭代 10 让 TuiApp 实现此接口。
/// </summary>
public interface IUiControl
{
    /// <summary>打印一条消息（用户可见）。</summary>
    Task PrintMessageAsync(string message, CancellationToken ct);

    /// <summary>更新状态栏字段。</summary>
    void SetStatus(string key, string value);

    /// <summary>请求 HITL 决策（委托 IHitlGate，7b 接入）。</summary>
    Task<HitlDecision?> RequestHitlAsync(ToolCall call, CancellationToken ct);
}
```

> **设计要点**：
> - **预留接口**：7a 定义但不实现（`TuiApp` 不加 `: IUiControl`）。`RequestHitlAsync` 引用 `HitlDecision` 类型——7a 未定义此类型，故 7a 此接口**编译依赖 `HitlDecision`**。
> - **编译依赖处理**：7a 有两个选择：(a) 7a 只定义 `PrintMessageAsync` + `SetStatus`，`RequestHitlAsync` 留 7b 加（避免依赖 `HitlDecision`）；(b) 7a 同时定义 `HitlDecision` 占位 record（但 7a 不使用）。
> - **决策：方案 (a)**——7a 的 `IUiControl` 只含 `PrintMessageAsync` + `SetStatus`，`RequestHitlAsync` 留 7b 加（7b 定义 `HitlDecision` 时一并加此方法）。这样 7a 不引入 `HitlDecision` 类型，保持 7a 纯展示层。修正后的接口如下：

```csharp
namespace ParrotCode;

/// <summary>
/// UI 抽象接口（7a 最小定义，迭代 10 命令系统通过它调用 UI）。
/// 7a 只含 PrintMessageAsync + SetStatus。RequestHitlAsync 留 7b 加（依赖 HitlDecision）。
/// </summary>
public interface IUiControl
{
    /// <summary>打印一条消息（用户可见）。</summary>
    Task PrintMessageAsync(string message, CancellationToken ct);

    /// <summary>更新状态栏字段。</summary>
    void SetStatus(string key, string value);
}
```

### 4.4 Live 流式渲染详解

#### 4.4.1 Live 工作机制

Spectre.Console 的 `Live(IRenderable)` 显示器：
1. `AnsiConsole.Live(target).StartAsync(async ctx => { ... })` 启动 Live。
2. `ctx.Update(newTarget)` 原地刷新渲染目标（清除旧内容，绘制新内容）。
3. `ctx.Refresh()` 强制重绘（通常 `Update` 已隐含刷新）。
4. Live 区域之外的 `AnsiConsole.Write` 会滚动（Live 区域固定）。

#### 4.4.2 半全屏模式（活跃区 + 滚动历史）

```
┌─────────────────────────────────────────┐
│ [状态栏] provider=deepseek model=...   │  ← Live 区域顶部（固定）
│ ── Round 1 ──                            │  ← Live 区域（活跃区）
│ 我来帮你读取 README                       │  ← 流式文本累积
│ → read_file({"path":"README.md"})        │  ← 工具卡片
│ ✓ # ParrotCode.Net ...                  │  ← 结果卡片
│ ── 完成 ──                               │
└─────────────────────────────────────────┘
[滚动历史] 之前的轮次已提交到此处           ← Live 区域外（滚动）
> 用户输入_                                 ← 输入行（Live 外）
```

- **Live 区域**：当前轮活跃内容（状态栏 + 文本 + 工具卡片）。`ctx.Update` 刷新。
- **滚动历史**：`IsCompletingEvent` 触发时，`AnsiConsole.WriteLine()` + `AnsiConsole.Write(committed)` → `renderer.Reset()`。提交后内容进入滚动历史，Live 区域清空（下一轮从空 `Text` 开始）。
- **输入行**：Live `StartAsync` 回调结束后（事件流读完），`InputReader.ReadLineWithCompletionAsync` 读输入。输入完成后再 Start 新 Live 渲染下一轮。

#### 4.4.3 流式渲染边界情况

| 情况 | 处理 |
| --- | --- |
| 终端非交互（`!Environment.UserInteractive` 或 `Console.IsOutputRedirected`） | 降级到 `ConsoleEventRenderer`，不用 Live |
| Spectre.Console 检测到不支持 ANSI | Live 自动降级为逐行刷新（Spectre 内置） |
| Live 期间用户 Ctrl+C | `ct` 取消，`await foreach` 抛 `OperationCanceledException`，Live 自动退出 |
| 文本增量含 `\r` 等控制字符 | `Text` 渲染时 Spectre 转义；若异常用 `Markup.Escape` |
| 工具参数含 `[` 破坏 Markup | `Markup.Escape` 转义所有动态内容 |
| 一轮内 10+ 工具调用卡片堆满屏 | `BuildActive` 限制 `_pending` 显示最近 5 个 + "...还有 N 个" |
| `Console.ReadKey` 在重定向 stdin 时抛异常 | 降级模式用 `Console.ReadLine`，不用 `InputReader`。检测 `Console.IsInputRedirected` |
| Live 刷新频率过高导致 CPU 占用 | `ctx.Update` 仅在新 token 到达时调用（事件驱动而非轮询），自然节流 |
| 多轮 ReAct 后 Live 活跃区内容堆积 | `IsCompletingEvent` 提交后 `Reset`，每轮活跃区从空开始 |
| `EventRenderer` 状态在多轮间泄漏 | `Reset` 在 `RoundStartEvent`（Render 内部）与提交后（TuiApp 外部）都调，确保清空 |

### 4.5 状态栏设计

状态栏字段：

| 字段 | 来源 | 示例 |
| --- | --- | --- |
| Provider | `_providerConfig.Name` | `deepseek` |
| Model | `_providerConfig.Model` | `deepseek-chat` |
| 安全等级 | `_securityLevel`（7a 硬编码 `Normal`） | `Normal` |
| 上下文占比 | `history.EstimatedTokens / context_window` | `12%(7680/64000)` |
| 当前轮次 | `RoundStartEvent.Round` | `2` |
| 工具数 | `registry.GetAll().Count` | `6` |

占比颜色：
- `< 70%`：绿色
- `70-90%`：黄色（警告）
- `> 90%`：红色（危险，7a 只警告不压缩）

### 4.6 Tab 补全设计

本迭代硬编码命令（迭代 10 扩展为 Registry）：

| 命令 | 行为 |
| --- | --- |
| `/clear` | 清空历史 |
| `/exit` / `/quit` | 退出 |
| `/help` | 显示命令列表 |
| `/status` | 打印状态栏 |

Tab 补全规则：
- 输入 `/` 开头按 Tab：前缀匹配命令列表。
- 唯一匹配：直接填充完整命令（清行重写）。
- 多匹配：列选项，不填充。
- 非 `/` 开头：Tab 无效（普通文本输入）。

## 五、依赖变更

**无新增 NuGet 依赖。**

- `Spectre.Console` 0.49.1 已在迭代 1 引入，7a 用其 `Live` / `Panel` / `Rows` / `Markup` / `Text` 等 API。
- `System.Threading.Channels` BCL 内置（迭代 6 已用）。
- `Console.ReadKey` / `ConsoleKey` BCL 内置。

`ParrotCode.Net.csproj` / `ParrotCode.Net-xUnit.csproj`：**不变**。

## 六、配置文件

### 6.1 `Config/Models.cs` 扩展

```csharp
namespace ParrotCode;

public sealed record AppConfig
{
    public string? ActiveProvider { get; init; }
    public IList<ProviderConfig> Providers { get; init; } = Array.Empty<ProviderConfig>();
    public AgentConfig? Agent { get; init; }

    /// <summary>TUI 配置（7a 新增）。null 时用默认值（Live 模式）。</summary>
    public TuiConfig? Tui { get; init; }

    // 7b/迭代 8 加：public SecurityConfig? Security { get; init; }
}

/// <summary>TUI 渲染配置。所有字段可选。</summary>
public sealed record TuiConfig
{
    /// <summary>渲染模式："live"（默认）| "console"（降级行模式）。</summary>
    public string? Mode { get; init; }

    /// <summary>是否显示状态栏，默认 true。</summary>
    public bool? ShowStatusBar { get; init; }

    /// <summary>上下文窗口 token 数（状态栏占比分母），默认 64000。</summary>
    public int? ContextWindowTokens { get; init; }

    // 7b 加：public bool? EnableHitl { get; init; }
}
```

> **7a 不加 `EnableHitl` / `SecurityConfig`**：7a 的 `TuiConfig` 只有 `Mode` / `ShowStatusBar` / `ContextWindowTokens`。`EnableHitl` 留 7b，`SecurityConfig` 留 7b/迭代 8。

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

# 迭代 7a 新增
tui:
  mode: live              # live | console（降级）
  show_status_bar: true
  context_window_tokens: 64000

# 7b/迭代 8 加：
# security:
#   level: normal           # strict | normal | permissive
```

### 6.3 默认值

| 字段 | 默认值 | 覆盖来源 |
| --- | --- | --- |
| `Tui.Mode` | `"live"` | `tui.mode` |
| `Tui.ShowStatusBar` | `true` | `tui.show_status_bar` |
| `Tui.ContextWindowTokens` | `64000` | `tui.context_window_tokens` |
| `SecurityLevel`（非配置，硬编码） | `Normal` | 7a 硬编码，7b/迭代 8 加配置项 |

> ConfigLoader 解析时若 `tui` 节缺失，对应字段为 null，App 用默认值。

## 七、迁移说明（迭代 6 → 7a）

| 迭代 6 | 迭代 7a | 处理 |
| --- | --- | --- |
| `App.RenderEventsAsync` 内联渲染 | 抽取为 `EventRenderer` + `TuiApp` + `ConsoleEventRenderer` | 重构（旧逻辑保留为降级路径） |
| `App` 装配工具 + 内联渲染 | `TuiApp` 装配工具 + 委托渲染 | 移动（工具装配逻辑一致） |
| `ChannelEventSink` 消费者是 App | 消费者改为 TuiApp | 接口不变 |
| 无状态栏 | `StatusBar` 组件 | 新增 |
| 无 Tab 补全 | `InputReader` | 新增 |
| 无 `Tui/` 目录 | 新增 7 个文件 | 新模块 |
| `BatchToolExecutor` 直接执行 Write | **不变**（7a 不动 Agent 层） | 零改动 |
| `AgentLoop` 工具结果统一 `ToolResultEvent` | **不变**（7a 不动 Agent 层） | 零改动 |
| `ToolBlockedEvent` 定义不产生 | **仍不产生**，`EventRenderer` 预留渲染分支 | 预留 |

迁移后回归不变式：
- `tui.mode: console` 时，行为与迭代 6 完全一致（`ConsoleEventRenderer` + 无 HITL）。
- `active_provider: mock` 无脚本时，TuiApp 输入"你好" → Live 渲染"你好（mock）" → 提交，与迭代 6 行模式内容一致（仅视觉增强）。
- `/clear` / `/exit` 行为保持。
- 迭代 1-6 既有测试全绿（`BatchToolExecutor` / `AgentLoop` 零改动，旧测试不回归）。

> **回归保护**：7a 不动 `BatchToolExecutor` / `AgentLoop` / `AgentEvent` / `ChannelEventSink`，迭代 6 的 `BatchToolExecutorTests` / `AgentLoopTests` 全绿。`App.cs` 改为委托 `TuiApp`，旧 `App.RenderEventsAsync` 逻辑迁移到 `ConsoleEventRenderer`（降级路径），行为一致。

## 八、单元测试

### 8.1 `EventRendererTests`（新增）

| 用例 | 操作 | 期望 |
| --- | --- | --- |
| `Render_RoundStart_SetsCurrentRound` | Render(RoundStartEvent(3)) | `CurrentRound == 3`, 返回 Markup 含 "Round 3" |
| `Render_RoundStart_ClearsBufferAndPending` | 先 Render(TextDelta) + Render(ToolCall) → Render(RoundStart(2)) | `CurrentText == ""`, `_pending.Count == 0` |
| `Render_TextDelta_AccumulatesToBuffer` | Render(TextDelta("foo")) → Render(TextDelta("bar")) | `CurrentText == "foobar"`, 返回 null |
| `Render_ToolCallStart_AddsToPending` | Render(ToolCallStartEvent) | `_pending.Count == 1`, 返回 null |
| `Render_ToolResultSuccess_AddsPanel` | Render(ToolResultEvent with Ok) | `_pending` 含 Panel, 返回 Panel |
| `Render_ToolResultFail_AddsRedPanel` | Render(ToolResultEvent with Fail) | Panel 颜色红 |
| `Render_ToolBlocked_AddsRedPanel` | Render(ToolBlockedEvent)（7a 预留分支验证） | `_pending` 含 Panel, Header 含 "拦截" |
| `Render_AgentDone_ReturnsMarkup` | Render(AgentDoneEvent) | 返回 Markup 含 "完成" |
| `Render_MaxRounds_ReturnsYellowMarkup` | Render(MaxRoundsReachedEvent(10)) | Markup 含 "最大轮次" |
| `Render_Error_ReturnsRedMarkup` | Render(ErrorEvent("x", null)) | Markup 含 "错误" |
| `Render_Cancelled_ReturnsGreyMarkup` | Render(CancelledEvent) | Markup 含 "已取消" |
| `Render_Warning_ReturnsYellowMarkup` | Render(WarningEvent("w")) | Markup 含 "⚠" |
| `Render_RoundEnd_ReturnsNull` | Render(RoundEndEvent(1)) | 返回 null |
| `Render_AssistantMessage_ReturnsNull` | Render(AssistantMessageEvent("x")) | 返回 null |
| `Reset_ClearsBufferAndPending` | 累积后 Reset() | `CurrentText == ""`, `_pending.Count == 0`, `CurrentRound == 0` |
| `BuildActive_IncludesStatusBarAndText` | 累积文本后 BuildActive(statusBar) | Rows 含 statusBar.Render() + Text |
| `BuildActive_LimitPendingTo5` | 加 7 个 ToolCallStart | Rows 含 "...还有 2 个" + 最近 5 个 |
| `BuildCommitted_ExcludesStatusBar` | BuildCommitted() | Rows 不含状态栏 |
| `SetTransient_Null_BuildActiveExcludesTransient` | SetTransient(null) → BuildActive | 不含临时项（7a 默认） |
| `SetTransient_NonNull_BuildActiveIncludesTransient` | SetTransient(markup) → BuildActive | 含临时项（验证 7b 扩展点工作） |

### 8.2 `StatusBarTests`（新增）

| 用例 | 操作 | 期望 |
| --- | --- | --- |
| `Render_ContainsAllFields` | 设各字段后 Render | Markup 含 Provider/Model/security/ctx/round/tools |
| `ContextRatio_Below70_Green` | tokens=1000, window=64000 | ratio < 0.7 |
| `ContextRatio_Over70_Yellow` | tokens=50000, window=64000 | 0.7 <= ratio < 0.9 |
| `ContextRatio_Over90_Red` | tokens=60000, window=64000 | ratio >= 0.9 |
| `ContextWindow_Zero_RatioZero` | ContextWindowTokens=0 | ratio == 0 |
| `SecurityLevel_Strict_RedColor` | Level=Strict | 渲染含 "red" |
| `SecurityLevel_Normal_YellowColor` | Level=Normal | 渲染含 "yellow" |
| `SecurityLevel_Permisive_GreenColor` | Level=Permisive | 渲染含 "green" |
| `Provider_LongName_Truncated` | Provider 25 字符 | 渲染含 "..." |

### 8.3 `InputReaderTests`（新增，补全逻辑）

| 用例 | 操作 | 期望 |
| --- | --- | --- |
| `Complete_UniquePrefix_FillsFull` | 输入 `/cl` + Tab | buf == "/clear" |
| `Complete_MultipleMatches_ListsOptions` | 输入 `/` + Tab | 输出含 /clear /exit 等 |
| `Complete_NoSlashPrefix_NoCompletion` | 输入 `foo` + Tab | buf 不变 |
| `Complete_NonExistentCommand_NoMatch` | 输入 `/xyz` + Tab | 无补全 |
| `Backspace_RemovesLastChar` | 输入 `/cle` + Backspace | buf == "/cl" |
| `Enter_ReturnsBuffer` | 输入 `/clear` + Enter | 返回 "/clear" |
| `Escape_ReturnsNull` | 按 Esc | 返回 null |
| `CancelledToken_ReturnsNull` | ct 已取消 | 返回 null |

> `InputReaderTests` 需 mock `Console.ReadKey`——用 `System.IO.StringReader` + `Console.SetIn` 重定向 stdin，或抽取 `IConsole` 抽象注入。推荐后者（`IConsole.ReadKey()` / `IConsole.Write(string)`），让 `InputReader` 依赖抽象而非静态 `Console`，便于测试。实现时若时间紧，可降级为集成测试（手动验证）。

### 8.4 `ConsoleEventRendererTests`（新增，降级路径）

| 用例 | 操作 | 期望 |
| --- | --- | --- |
| `Render_TextDelta_WritesToConsole` | 注入假 Console + TextDelta | 输出含 text |
| `Render_ToolBlocked_WritesRedLine` | ToolBlockedEvent（7a 预留分支验证） | 输出含 "被拦截" |
| `Render_AgentDone_WritesNewline` | AgentDoneEvent | 末尾换行 |
| `Render_Error_WritesRedError` | ErrorEvent | 输出含 "错误" |
| `Render_ToolResultSuccess_WritesGreenCheck` | ToolResultEvent with Ok | 输出含 "✓" |
| `Render_ToolResultFail_WritesRedCross` | ToolResultEvent with Fail | 输出含 "✗" |

### 8.5 `TuiAppIntegrationTests`（端到端，无 HITL）

| 用例 | 操作 | 期望 |
| --- | --- | --- |
| `EndToEnd_NoTool_LiveRendersText` | MockProvider 脚本只返回 TextDelta | Live 渲染文本，提交后控制台含文本 |
| `EndToEnd_ReadTool_RendersToolCard` | 脚本调 read_file | 活跃区含工具调用 Panel + 结果 Panel |
| `EndToEnd_MultiRound_StatusBarUpdatesRound` | 2 轮 ReAct 脚本 | 状态栏 round 字段从 1 → 2 |
| `EndToEnd_MaxRounds_RendersWarning` | 脚本触发 10 轮 | 渲染 "最大轮次" |
| `EndToEnd_CancelledEvent_Renders` | 脚本触发取消 | 渲染 "已取消" |
| `EndToEnd_SlashClear_ClearsHistory` | 输入 /clear | 历史清空 |
| `EndToEnd_SlashStatus_PrintsStatusBar` | 输入 /status | 输出含状态栏字段 |
| `EndToEnd_SlashHelp_PrintsCommands` | 输入 /help | 输出含 /clear /status 等 |

### 8.6 回归

- `dotnet test` 全绿（含迭代 1-6 既有 + 7a 新增 5 个测试文件）。
- `dotnet run`（`tui.mode: console`）行为与迭代 6 一致。
- `AgentLoopTests` / `BatchToolExecutorTests` 既有用例全绿（7a 不动 Agent 层）。
- `/clear` / `/exit` 行为保持。

## 九、验收标准

> 全部满足方可标记 `[已完成]`。每条尽量可被命令或肉眼验证。

### 9.1 构建与运行

- [ ] `ParrotCode.Net.csproj` 无改动（零新依赖）。
- [ ] `dotnet build` 无 error、无 warning。
- [ ] `dotnet test` 全绿（含迭代 1-6 既有 + 7a 新增 5 个测试文件）。
- [ ] `dotnet run`（`active_provider: mock`）能启动，TUI 启动横幅正常（显示 TUI 模式 + 安全等级 + 工具数）。

### 9.2 EventRenderer

- [ ] `Tui/EventRenderer.cs` 定义 `Render(AgentEvent) → IRenderable?` + `BuildActive`/`BuildCommitted`/`Reset`/`SetTransient`。
- [ ] 12 种事件类型全覆盖（含 `ToolBlockedEvent` 预留分支）。
- [ ] `TextDeltaEvent` 累积到 `_textBuf`。
- [ ] `ToolCallStartEvent`/`ToolResultEvent`/`ToolBlockedEvent` 加 Panel 到 `_pending`。
- [ ] `BuildActive` 含状态栏 + 轮次 + 文本 + 工具卡片 + 临时项（7a 始终 null）。
- [ ] `BuildActive` 限制 `_pending` 最近 5 个 + "...还有 N 个"。
- [ ] `BuildCommitted` 不含状态栏与临时项。
- [ ] `Reset` 清空缓冲、`_pending`、`_transient`、轮次。
- [ ] `SetTransient` 设置 `_transient`（7b 预留扩展点，7a 不使用）。
- [ ] `EventRendererTests` 20 个用例全绿。

### 9.3 StatusBar

- [ ] `Tui/StatusBar.cs` 定义状态栏组件。
- [ ] 显示 Provider/Model/安全等级/上下文占比/当前轮次/工具数。
- [ ] 占比颜色：绿(<70%)/黄(70-90%)/红(>90%)。
- [ ] `SecurityLevel` 颜色映射正确（Strict 红/Normal 黄/Permissive 绿）。
- [ ] Provider/Model 名截断到 20 字符。
- [ ] `StatusBarTests` 9 个用例全绿。

### 9.4 SecurityLevel

- [ ] `Tui/SecurityLevel.cs` 定义枚举（`Strict`/`Normal`/`Permisive`）。
- [ ] 7a 硬编码 `Normal`，无配置项。
- [ ] 状态栏显示正确。

### 9.5 InputReader + Tab 补全

- [ ] `Tui/InputReader.cs` 实现 `ReadLineWithCompletionAsync`。
- [ ] `/` 开头 Tab 补全：唯一匹配填充，多匹配列选项。
- [ ] Enter 提交，Esc 取消，Backspace 删除。
- [ ] 非 `/` 开头 Tab 无效。
- [ ] CancellationToken 取消时返回 null。
- [ ] `InputReaderTests` 8 个用例全绿。

### 9.6 ConsoleEventRenderer（降级）

- [ ] `Tui/ConsoleEventRenderer.cs` 实现（迭代 6 抽取）。
- [ ] 12 种事件全覆盖（含 `ToolBlockedEvent` 预留分支）。
- [ ] 行为与迭代 6 `App.RenderEventsAsync` 一致。
- [ ] `ConsoleEventRendererTests` 6 个用例全绿。

### 9.7 IUiControl

- [ ] `Tui/IUiControl.cs` 定义接口（`PrintMessageAsync`/`SetStatus`，7a 最小子集）。
- [ ] 7a `TuiApp` 不实现此接口（7b/迭代 10 实现）。
- [ ] 不引入 `HitlDecision` 类型依赖。

### 9.8 TuiApp

- [ ] `Tui/TuiApp.cs` 实现主循环（Live + 状态栏 + 事件消费 + 输入）。
- [ ] Live 流式渲染活跃区，`IsCompletingEvent` 时提交到滚动历史。
- [ ] 状态栏轮次实时更新（`RoundStartEvent` → `statusBar.CurrentRound`）。
- [ ] `BatchToolExecutor` 不注入 `IHitlGate`（等价迭代 6）。
- [ ] `/clear` / `/exit` / `/help` / `/status` 硬编码分发。
- [ ] `TuiAppIntegrationTests` 8 个用例全绿。

### 9.9 端到端 TUI 体验（核心验收）

- [ ] **mock 模式**：`dotnet run`，输入"你好"，Live 渲染"你好（mock）"，完成后提交到滚动历史。
- [ ] **流式渲染不闪烁**：DeepSeek 真实模式，流式输出 token，Live 原地刷新无闪烁。
- [ ] **状态栏**：顶部显示 provider/model/security/ctx%/round/tools，轮次随 ReAct 更新。
- [ ] **工具卡片化**：让 AI 调 `read_file`，看到青色工具调用 Panel + 绿色结果 Panel。
- [ ] **上下文占比**：长对话后占比变黄/红。
- [ ] **Tab 补全**：输入 `/c` + Tab 补全为 `/clear`。
- [ ] **降级模式**：`tui.mode: console`，行为与迭代 6 一致（无 Live/状态栏）。
- [ ] **Ctrl+C**：中断长任务，渲染"已取消"，程序不崩溃。

### 9.10 App 接入与装配

- [ ] `App/App.cs` 改为委托 `TuiApp`（或降级到 `ConsoleEventRenderer`）。
- [ ] `Program.cs` 终端能力检测（`Console.IsOutputRedirected` / `Environment.UserInteractive`）决定 Live/降级。
- [ ] `Program.cs` 装配 `TuiApp` / `SecurityLevel`（硬编码 `Normal`）注入 App。
- [ ] 非 Live 模式自动降级到 `ConsoleEventRenderer`。

### 9.11 配置

- [ ] `Config/Models.cs` 加 `TuiConfig`（Mode/ShowStatusBar/ContextWindowTokens）。
- [ ] `AppConfig.Tui` 字段。
- [ ] `example.parrotcode.yaml` 加 `tui:` 节示例。
- [ ] ConfigLoader 解析新节（缺失时 null，App 用默认值）。
- [ ] 配置项可被覆盖（如 `mode: console` 生效）。

### 9.12 异常与边界

- [ ] 终端非交互（重定向/CI）→ 降级到 `ConsoleEventRenderer`，不崩。
- [ ] Spectre.Console 不支持 ANSI → Live 自动降级（Spectre 内置）。
- [ ] Live 期间 Ctrl+C → `ct` 取消，Live 退出，emit `CancelledEvent`。
- [ ] 工具参数含 `[` 破坏 Markup → `Markup.Escape` 转义。
- [ ] 一轮内 10+ 工具卡片 → `BuildActive` 限制显示最近 5 个 + "...还有 N 个"。
- [ ] 状态栏字段超长 → `Truncate` 截断。
- [ ] `Console.ReadKey` 在重定向 stdin 时 → 降级模式用 `Console.ReadLine`。

### 9.13 敏感信息

- [ ] 状态栏不显示 ApiKey。
- [ ] 日志不出现 ApiKey（沿用迭代 6）。

### 9.14 跨平台

- [ ] Windows 上 `dotnet test` 全绿。
- [ ] macOS / Linux 上 `dotnet test` 全绿。
- [ ] Live 在三平台正常渲染（Spectre.Console 跨平台）。
- [ ] `InputReader.ReadKey` 在三平台行为一致。
- [ ] 降级模式在三平台一致（`Console.IsOutputRedirected` 跨平台）。

### 9.15 迁移与回归

- [ ] `BatchToolExecutor` **零改动**（7a 不动 Agent 层）。
- [ ] `AgentLoop` **零改动**（7a 不动 Agent 层）。
- [ ] `AgentEvent` 12 种事件类型**不变**。
- [ ] `ChannelEventSink` / `IAgentEventSink` **不变**。
- [ ] `tui.mode: console` 时行为与迭代 6 完全一致。
- [ ] 迭代 1-6 的所有测试**全绿**（无回归）。

### 9.16 7b 扩展点验证

- [ ] `EventRenderer.SetTransient` 方法存在且可调用（7a 不使用，但验证 7b 可接入）。
- [ ] `EventRenderer.BuildActive` 在 `_transient` 非 null 时加入活跃区（单测 `SetTransient_NonNull_BuildActiveIncludesTransient` 验证）。
- [ ] `EventRenderer.Render` 对 `ToolBlockedEvent` 有渲染分支（单测 `Render_ToolBlocked_AddsRedPanel` 验证）。
- [ ] `ConsoleEventRenderer` 对 `ToolBlockedEvent` 有渲染分支（单测验证）。
- [ ] `TuiApp` 的 `RenderLiveAsync` 中 `renderer` 是局部变量（7b 可在回调中调用 `renderer.SetTransient`）。

## 十、进阶练习（可选，不计入验收）

1. **AlternateScreen 全屏**：用 Spectre.Console 的 `IAnsiConsole.Console.Profile.Capabilities.AltScreen` 切换到备用屏幕，固定布局（状态栏顶 + 输出区滚动 + 输入行底）。退出时恢复主屏幕。

2. **thinking 折叠渲染**：解析 DeepSeek `reasoning_content` 字段，用 `Collapsible` 渲染灰色折叠的思考过程，默认折叠，按 T 展开。

3. **输入历史**：↑↓ 浏览历史输入（存内存 `List<string>`），跨会话持久化在迭代 10。

4. **多行输入**：Shift+Enter 换行，Enter 提交。需检测 `ConsoleModifiers.Shift`。

5. **工具调用进度条**：`run_command` 长时间执行时，Live 显示进度条或旋转 spinner。

6. **`IConsole` 抽象**：把 `Console.ReadKey` / `Console.Write` 抽成 `IConsole` 接口注入 `InputReader`，让 `InputReaderTests` 不依赖真实控制台（7a 可降级为集成测试，此练习提升为纯单测）。

7. **Live 刷新节流**：若 token 到达频率极高（如批量回放），`ctx.Update` 加 50ms 节流避免 CPU 占用过高。

8. **状态栏字段可配置**：`tui.status_bar_fields: [provider, model, ctx, round]` 让用户自定义状态栏显示哪些字段。

## 十一、风险与注意事项

| 风险 | 缓解 |
| --- | --- |
| 终端不支持 ANSI 导致 Live 异常 | `Console.IsOutputRedirected` / `Environment.UserInteractive` 检测，降级到 `ConsoleEventRenderer`。Spectre.Console 内置 ANSI 检测也会降级 |
| Live 期间 `AnsiConsole.Write` 行为不确定 | Spectre.Console 0.49.1 的 Live 区域外 `AnsiConsole.Write` 会滚动。实现时验证提交行为，若不滚动则改用"Live 退出后 `AnsiConsole.Write`"模式（`IsCompletingEvent` 时 break 退出 `StartAsync` → Write → 重启 Live） |
| `Console.ReadKey` 在重定向 stdin 时抛异常 | 降级模式用 `Console.ReadLine`，不用 `InputReader`。检测 `Console.IsInputRedirected` |
| `Markup.Escape` 漏转义导致渲染崩溃 | 所有动态内容（工具名/参数/结果/错误）一律 `Markup.Escape`。单测验证含 `[` / `]` 的内容 |
| 一轮内工具卡片过多撑满屏 | `BuildActive` 限制 `_pending` 显示最近 5 个 + "...还有 N 个" |
| 状态栏字段过长换行 | `Truncate` 截断 Provider/Model 名到 20 字符 |
| Live 刷新频率过高导致 CPU 占用 | `ctx.Update` 仅在新 token 到达时调用（事件驱动），自然节流 |
| 多轮 ReAct 后 Live 活跃区内容堆积 | `IsCompletingEvent` 提交后 `Reset`，每轮活跃区从空开始 |
| `EventRenderer` 状态在多轮间泄漏 | `Reset` 在 `RoundStartEvent`（Render 内部）与提交后（TuiApp 外部）都调，确保清空 |
| `InputReaderTests` 难 mock `Console.ReadKey` | 抽取 `IConsole` 抽象注入，或降级为集成测试（手动验证）。进阶练习 6 提升 |
| `TuiApp` 端到端测试难断言（Spectre.Console 输出） | `EventRenderer`/`StatusBar` 是纯逻辑可单测（断言 IRenderable 类型与内容）。`TuiApp` 端到端用 MockProvider 脚本，断言事件流与状态栏字段，而非控制台像素 |
| `Live` 在某些终端（如 Windows CMD 旧版）闪烁 | Spectre.Console 0.49.1 已优化 Windows Terminal / CMD。若旧 CMD 闪烁，降级到 console 模式 |
| 7b 接入时 `EventRenderer` 扩展点不足 | 7a 已预留 `SetTransient` + `ToolBlockedEvent` 渲染分支。7b 验证扩展点可用，若不足再补（但 7a 设计已覆盖 7b 需求） |

## 十二、交付清单

### 12.1 新增源文件

- [ ] `ParrotCode.Net/Tui/TuiApp.cs`（主 TUI 应用，无 HITL 接线）
- [ ] `ParrotCode.Net/Tui/EventRenderer.cs`（事件 → IRenderable 翻译 + SetTransient 扩展点）
- [ ] `ParrotCode.Net/Tui/StatusBar.cs`（状态栏组件）
- [ ] `ParrotCode.Net/Tui/InputReader.cs`（带 Tab 补全输入读取）
- [ ] `ParrotCode.Net/Tui/ConsoleEventRenderer.cs`（降级行模式渲染）
- [ ] `ParrotCode.Net/Tui/IUiControl.cs`（UI 抽象接口预留，7a 最小子集）
- [ ] `ParrotCode.Net/Tui/SecurityLevel.cs`（安全等级枚举占位）

### 12.2 修改源文件

- [ ] `ParrotCode.Net/App/App.cs`（委托 TuiApp 或降级到 ConsoleEventRenderer）
- [ ] `ParrotCode.Net/Program.cs`（终端检测 + 装配 TuiApp/SecurityLevel）
- [ ] `ParrotCode.Net/Config/Models.cs`（TuiConfig）
- [ ] `ParrotCode.Net/example.parrotcode.yaml`（tui 节示例）

### 12.3 新增测试文件

- [ ] `ParrotCode.Net-xUnit/EventRendererTests.cs`
- [ ] `ParrotCode.Net-xUnit/StatusBarTests.cs`
- [ ] `ParrotCode.Net-xUnit/InputReaderTests.cs`
- [ ] `ParrotCode.Net-xUnit/ConsoleEventRendererTests.cs`
- [ ] `ParrotCode.Net-xUnit/TuiAppIntegrationTests.cs`

### 12.4 演示与验收

- [ ] 演示：mock 模式 Live 流式渲染"你好（mock）"，提交到滚动历史。
- [ ] 演示：DeepSeek 真实模式，让 AI 调 `read_file`，看到工具调用 Panel + 结果 Panel。
- [ ] 演示：状态栏显示 provider/model/security/ctx%/round/tools，轮次随 ReAct 更新。
- [ ] 演示：长对话后上下文占比变黄/红（验证占比计算）。
- [ ] 演示：输入 `/c` + Tab 补全为 `/clear`（验证 Tab 补全）。
- [ ] 演示：`tui.mode: console` 降级模式行为与迭代 6 一致。
- [ ] 演示：Ctrl+C 中断长任务，渲染"已取消"程序不崩溃。

## 十三、实现顺序建议

为降低集成风险，建议按以下顺序分步实现（每步可单独编译验证）：

1. **`SecurityLevel` 枚举**：占位枚举，无逻辑。先建立类型，状态栏依赖它。
2. **`StatusBar`**：状态栏组件 + `StatusBarTests`。纯渲染逻辑，不依赖 Live。
3. **`EventRenderer`**：事件翻译 + `SetTransient` 扩展点 + `EventRendererTests`。纯渲染逻辑，不依赖 Live。
4. **`InputReader`**：Tab 补全 + `InputReaderTests`。不依赖 Live。
5. **`ConsoleEventRenderer`**：抽取迭代 6 渲染逻辑 + `ConsoleEventRendererTests`。降级路径先就绪。
6. **`IUiControl`**：定义接口（7a 最小子集：`PrintMessageAsync` + `SetStatus`）。
7. **`Config/Models.cs` 扩展**：加 `TuiConfig` + `AppConfig.Tui` 字段。
8. **`TuiApp`**：装配 Live + 状态栏 + 事件消费 + 输入 + `TuiAppIntegrationTests`。核心集成。
9. **`App`/`Program` 接入**：改 `App.cs` 委托 + `Program.cs` 终端检测与装配 + `example.parrotcode.yaml`。
10. **端到端验收**：`dotnet test` 全绿 + mock 模式 Live 渲染 + DeepSeek 真实模式 + 降级模式回归。

> 每步完成后 `dotnet build` 应无 error。步骤 1-7 完成后既有功能不回归（旧 App 仍可用，Agent 层零改动）。步骤 8-9 切换 App 到 TuiApp 后，`tui.mode: console` 降级路径保留迭代 6 行为。

---

## 附录 A：TUI 布局与渲染示例

### A.1 Live 活跃区布局

```
┌─ provider=deepseek model=deepseek-chat security=Normal ctx=12%(7680/64000) round=1 tools=6 ─┐
│                                                                                              │
│ ── Round 1 ──                                                                                 │
│ 我来帮你读取 README 并总结                                                                     │
│                                                                                              │
│ ┌─ → 工具调用 ─────────────────────────────────────┐                                          │
│ │ read_file({"path":"README.md"})                   │                                          │
│ └──────────────────────────────────────────────────┘                                          │
│                                                                                              │
│ ┌─ ✓ 结果 ────────────────────────────────────────┐                                          │
│ │ ✓ # ParrotCode.Net ...（截断）                   │                                          │
│ └──────────────────────────────────────────────────┘                                          │
│                                                                                              │
│ ── 完成 ──                                                                                    │
└──────────────────────────────────────────────────────────────┘
[滚动历史] 之前的轮次
> 用户输入_
```

### A.2 多工具调用（限制最近 5 个）

```
┌─ ...还有 2 个更早的工具卡片 ──
│ ┌─ → 工具调用 ───────────────────┐
│ │ grep({"pattern":"TODO"})        │
│ └────────────────────────────────┘
│ ┌─ ✓ 结果 ──────────────────────┐
│ │ ✓ src/Program.cs:10:TODO ...   │
│ └────────────────────────────────┘
│ ...（最近 5 个）
```

### A.3 降级模式（console）

```
> 读取 README
你：读取 README
AI：
→ read_file({"path":"README.md"})
✓ # ParrotCode.Net ...
已读取 README。
>
```

### A.4 状态栏占比警告

```
┌─ provider=deepseek model=deepseek-chat security=Normal ctx=75%(48000/64000) round=3 tools=6 ─┐
                                                              ↑ 黄色（70-90%）
```

```
┌─ provider=deepseek model=deepseek-chat security=Normal ctx=92%(58880/64000) round=5 tools=6 ─┐
                                                              ↑ 红色（>90%）
```

## 附录 B：与 `iter-07-design.md` 的差异说明（7b 待补部分）

本文档（`iter-07a-design.md`）从 `iter-07-design.md` 拆分而来，删除了 HITL 相关内容。7b 待补的部分如下：

### B.1 7b 将引入的类型（7a 不引入）

| 类型 | 用途 | 来源章节 |
| --- | --- | --- |
| `HitlChoice`（枚举） | A/S/P/D 四档决策 | iter-07 §4.3.1 |
| `HitlDecision`（record） | 决策结果 + Reason | iter-07 §4.3.1 |
| `IHitlGate`（接口） | HITL 双向通道 | iter-07 §4.3.2 |
| `NullHitlGate`（类） | 默认放行（无 HITL） | iter-07 §4.3.2 |
| `HitlPrompt`（类） | Spectre 实现（方案 C） | iter-07 §4.3.3 + §4.4.4 |

### B.2 7b 将修改的文件（7a 不改）

| 文件 | 7b 改动 | 7a 状态 |
| --- | --- | --- |
| `BatchToolExecutor.cs` | 注入 `IHitlGate?` + `OnBeforeExecuteAsync` hook | 不变（7a 用迭代 6 四参数构造） |
| `AgentLoop.cs` | HITL 拒绝转 `ToolBlockedEvent` | 不变（7a 主路径不产生此事件） |
| `TuiApp.cs` | 加 `HitlPrompt` 装配 + `render`/`readKey` 回调注入 `EventRenderer.SetTransient` | 7a 简化版（无 HITL 接线） |
| `Config/Models.cs` | 加 `TuiConfig.EnableHitl` + `SecurityConfig` | 7a 只有 `TuiConfig`（3 字段） |
| `example.parrotcode.yaml` | 加 `enable_hitl` / `security` 节 | 7a 只有 `tui` 节（3 字段） |
| `IUiControl.cs` | 加 `RequestHitlAsync` 方法 | 7a 只有 `PrintMessageAsync` + `SetStatus` |

### B.3 7a 预留的扩展点（7b 接入时不用改 7a 代码）

| 扩展点 | 7a 状态 | 7b 用法 |
| --- | --- | --- |
| `EventRenderer.SetTransient(IRenderable?)` | 已定义，7a 不调用（`_transient` 始终 null） | `HitlPrompt` 调 `SetTransient(prompt)` 渲染 HITL 提示到活跃区 |
| `EventRenderer.Render` 的 `ToolBlockedEvent` 分支 | 已定义，7a 不触发 | 7b `AgentLoop` 产生此事件时自动渲染红色拦截 Panel |
| `ConsoleEventRenderer` 的 `ToolBlockedEvent` 分支 | 已定义，7a 不触发 | 7b 降级路径也渲染拦截卡片 |
| `TuiApp.RenderLiveAsync` 的 `renderer` 局部变量 | 7a 是局部变量 | 7b 在回调中调用 `renderer.SetTransient`（需把 `renderer` 提升为字段或闭包捕获） |
| `TuiApp` 的 `batchExecutor` 构造处 | 7a 用四参数（无 `hitlGate`） | 7b 改为五参数加 `hitlGate` |

### B.4 7a 不引入 `HitlDecision` 的连锁影响

7a 的 `IUiControl` 只有 `PrintMessageAsync` + `SetStatus`（不含 `RequestHitlAsync`），因为 `RequestHitlAsync` 依赖 `HitlDecision` 类型（7b 引入）。7b 定义 `HitlDecision` 后，给 `IUiControl` 加 `RequestHitlAsync` 方法（接口加方法是 breaking change，但 7a `TuiApp` 不实现 `IUiControl`，故无影响；7b/迭代 10 实现时一并加）。

---

> 本文档到此结束。7a 实现完成并验收后，由用户发起再写 `iter-07b-design.md` 补齐 HITL 交互层。7a 完成后将本文件头部状态改为 `[已完成]`。
