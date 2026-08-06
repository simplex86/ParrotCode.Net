# 迭代 7：TUI 接入（Spectre.Console + 流式渲染 + HITL）— 详细设计

> 状态：[设计完成，待实现]
> 对应 `plan.md` 第三章「迭代 7」，本文档在其基础上补充实现级细节与可执行的验收清单。
> 前置：迭代 6 已交付 ReAct 闭环——`AgentLoop` + `IAgentEventSink`/`ChannelEventSink`（无界 `Channel<AgentEvent>`）+ `BatchToolExecutor`（Read 并发 / Write 串行）+ `ChatChunk` + `AgentEvent`（12 种事件，带 `Event` 后缀：`RoundStartEvent` / `TextDeltaEvent` / `AssistantMessageEvent` / `ToolCallStartEvent` / `ToolResultEvent` / `ToolBlockedEvent` / `RoundEndEvent` / `AgentDoneEvent` / `MaxRoundsReachedEvent` / `WarningEvent` / `ErrorEvent` / `CancelledEvent`）+ 6 个内置工具（`read_file` / `write_file` / `edit_file` / `glob` / `grep` / `run_command`）+ `MockProvider.EnqueueScript`。App 当前用 `Console.Write` + `AnsiConsole.MarkupLine` 行模式消费事件流。本迭代把"展示层"从行模式控制台打印升级为 Spectre.Console `Live` 流式渲染 + 状态栏 + HITL（Human-In-The-Loop）确认，让 Agent 的"思考 → 调工具 → 看结果"过程可视、可控、可中断。

## 一、概述

迭代 6 让 ReAct 闭环跑通，但展示层仍是 `Console.Write` 逐字打印——没有状态栏、没有工具调用卡片化展示、没有人在回路（HITL），`write_file` / `run_command` 等有副作用的工具**直接执行**。迭代 7 把展示与控制这两件事补齐：**流式渲染**让 Agent 过程可视，**HITL** 让危险操作可控。

1. **`Live` 流式渲染**：用 Spectre.Console 的 `Live(IRenderable)` 显示器把"当前轮活跃区"（正在流的文本 + 进行中的工具调用）原地刷新，token 到达即追加，避免整段重绘闪烁。一轮结束后把活跃区内容"提交"到滚动历史（`AnsiConsole.Write`），清空活跃区进入下一轮。这是 Spectre.Console 流式 Agent 渲染的常见模式——Live 显示"进行中"，完成后提交。
2. **状态栏**：顶部固定一行（用 `Rows` + `Rule`/`Panel` 组合）显示 `Provider` / `Model` / `安全等级` / `上下文占比` / `当前轮次` / `工具数`。状态栏作为 Live 渲染目标的一部分，每次刷新重绘，让用户随时看到 Agent 状态。上下文占比来自 `ConversationHistory.EstimatedTokens` 与配置的 `context_window_tokens`（如 DeepSeek 64K）。
3. **HITL（人在回路）**：迭代 6 的 `BatchToolExecutor` 对 `Write` 组工具直接串行执行。本迭代给它注入一个 `IHitlGate` 可选依赖——`Write` 组每个工具执行前调 `gate.RequestAsync(call, ct)`，返回 `HitlDecision`（`AllowOnce` / `AllowSession` / `AllowPermanent` / `Deny`）。`Deny` 时 emit `ToolBlockedEvent`（迭代 6 预留事件）+ 构造 `ToolResult.Fail("用户拒绝执行")` 回灌 LLM；`AllowSession` / `AllowPermanent` 记入决策缓存，同会话同工具后续不再问。
4. **`IHitlGate` 双向通道**：HITL 需要返回值（用户决策），而事件流是 fire-and-forget 单向通道（`sink.WriteAsync` 不返回值）。把 HITL 混入事件流（"emit `HitlRequestEvent` + `await Task<HitlDecision>`"）会破坏事件流的单向清晰性。故本迭代引入 `IHitlGate` 接口作为独立的双向通道：AgentLoop/BatchToolExecutor 通过它**请求**决策，TUI 通过它**响应**决策。事件流只负责旁路通知（拒绝时 emit `ToolBlockedEvent`）。
5. **`HitlPrompt`**：`IHitlGate` 的 Spectre 实现。收到请求时**暂停 Live**（`Live.Stop()` 释放控制台）→ `AnsiConsole.Prompt<HitlChoice>` 弹 A/S/P/D 四键确认 → 恢复 Live。`AnsiConsole.Prompt` 会临时接管控制台，与 Live 同一进程内互斥，故必须先 Stop Live。
6. **`EventRenderer`**：把 `AgentEvent` 翻译成 `IRenderable`（Spectre 渲染单元）。`TextDeltaEvent` → 累积到 `StringBuilder` 后 `new Text(buf)`；`ToolCallStartEvent` → `Panel`/`Rows` 卡片化展示工具名+参数；`ToolResultEvent` → 成功绿 `✓` 失败红 `✗` + 截断内容；`RoundStartEvent` → 灰色轮次标记。纯渲染逻辑无副作用，可单测（断言 `IRenderable` 类型与内容）。
7. **`InputReader` + Tab 补全**：用户输入行不用 Live（Live 与 `Console.ReadLine` 互斥）。本迭代手写一个带 Tab 补全的 `ReadLineWithCompletionAsync`——遇到 `/` 开头按 Tab 补全硬编码命令列表（`/clear` / `/exit` / `/quit` / `/help` / `/status`）。命令系统在迭代 10 完善，本迭代只做补全与硬编码分发。
8. **`IUiControl`**：UI 抽象接口（`PrintMessage` / `SetStatus` / `RequestInput` / `RequestHitl`），本迭代最小定义，让迭代 10 命令系统能通过它调用 UI 而不直接耦合 `TuiApp`。预留接口，本迭代只实现最小子集。
9. **`SecurityLevel` 占位**：状态栏显示"安全等级"，但安全层在迭代 8。本迭代引入 `SecurityLevel` 枚举（`Strict` / `Normal` / `Permissive`）+ 配置项，默认 `Normal`。迭代 7 只在状态栏显示等级名，不做真实拦截——`SecurityGuard` 在迭代 8 接入 `BatchToolExecutor` 的 `OnBeforeExecuteAsync` hook（本迭代预留）。
10. **降级路径**：`tui.mode: console` 配置时回退到迭代 6 的行模式渲染（抽取为 `ConsoleEventRenderer`）。Live 在重定向/非交互终端（`Console.IsOutputRedirected` 或 `!Environment.UserInteractive`）自动降级。保证 CI/管道场景不崩。

本迭代**刻意保持**：
- **不做安全层**：黑名单 / 沙箱 / 三档权限的真实拦截在迭代 8。`SecurityLevel` 仅状态栏显示占位，HITL 是"所有 Write 工具都问"的简化策略（而非迭代 8 的"Normal 模式下读放行、写询问"精细化）。本迭代 HITL 阈值固定：`Write` 类工具必问，`Read` 类工具不问。
- **不做命令系统**：`/clear` / `/exit` 仍硬编码分发，`/help` / `/status` 本迭代补硬编码实现，完整斜杠命令注册中心在迭代 10。`IUiControl` 预留接口供迭代 10。
- **不做会话持久化**：TUI 渲染的是内存历史，退出即丢失。JSONL 在迭代 10。
- **不做上下文截断/摘要**：状态栏显示占比，超 70% 黄色警告，但不自动压缩。压缩在迭代 9。
- **不做 AlternateScreen 全屏**：用"Live 活跃区 + 滚动历史"半全屏模式，降低终端兼容风险。真正全屏（`AltScreen` + 固定布局）作为进阶练习。
- **不做多行输入 / Syntax Highlight**：单行输入，多行与高亮在可选扩展。
- **不接 Anthropic Provider**：HITL 与 TUI 协议无关，Anthropic wire format 后续迭代。
- **HITL 不走事件流**：`HitlRequestEvent` 不引入——拒绝用 `ToolBlockedEvent`（迭代 6 已定义），允许用 `ToolResultEvent`。事件分类保持迭代 6 的 12 种稳定，迭代 8/10 不再扩。

> **与迭代 6 设计文档的偏差说明**：迭代 6 文档第 3.3 节提及"迭代 7 HITL 在 WRITE 工具执行前 emit `ToolBlocked` 并 `await Task<HitlDecision>`"。本设计**不采用事件流承载 HITL 决策**，理由：(1) 事件流是单向 fire-and-forget，`IAgentEventSink.WriteAsync` 返回 `ValueTask` 不携带决策；(2) 把双向请求/响应塞进单向通道需引入"事件带 `TaskCompletionSource`"的混合模式，破坏 `AgentEvent` 是纯数据的契约；(3) `IHitlGate` 独立接口更清晰——请求/响应语义显式，TUI 实现可独立测试，事件流保持纯展示通知。迭代 6 的 `ToolBlockedEvent` 仍用于"HITL 拒绝后通知 UI"，但决策本身走 `IHitlGate`。

## 二、学习目标

1. **Spectre.Console `Live` 渲染**：理解 `Live(IRenderable)` 显示器的工作机制——它持有"当前渲染目标"，`Update(IRenderable)` 原地刷新，避免整段重绘闪烁。掌握"Live 显示进行中 + 完成后 `AnsiConsole.Write` 提交到滚动历史"的半全屏模式，这是 Spectre.Console 流式 Agent 的标准姿势。
2. **`IRenderable` 组合**：Spectre.Console 的渲染单元是组合模式——`Text` / `Markup` / `Panel` / `Rows` / `Columns` / `Rule` / `Table` 都是 `IRenderable`，可嵌套组合成复杂布局。理解"事件 → `IRenderable`"的翻译是渲染层的核心职责，与事件流解耦。
3. **HITL 双向通道**：理解"事件流是单向通知，HITL 是双向请求/响应"的本质区别。掌握用独立接口（`IHitlGate`）承载需要返回值的交互，用事件流承载展示通知——两者各司其职，不混用。
4. **`TaskCompletionSource<T>` 同步原语**：`IHitlGate.RequestAsync` 内部用 `TaskCompletionSource<HitlDecision>`——请求时创建 TCS，UI 响应时 `TrySetResult`，Agent 侧 `await`。理解这是 .NET 把"回调/事件"包装成 `awaitable` 的标准模式（迭代 5/6 的 `Process.Exited` + TCS 同理）。
5. **Live 与 Prompt 互斥**：Spectre.Console 的 `Live` 与 `AnsiConsole.Prompt` 都要接管控制台，同进程内不能同时运行。HITL 弹框前必须 `Live.Stop()`，弹框后重建 Live。理解 Spectre.Console 的"控制台独占"模型与暂停/恢复模式。
6. **决策缓存与作用域**：`AllowSession`（会话级，进程内同工具不再问）/ `AllowPermanent`（持久级，写配置文件跨会话）。本迭代实现会话级缓存（`HashSet<string>`），持久级留接口给迭代 8/10（需配置文件支持）。理解"作用域"在 HITL 设计中的权衡——问太少不安全，问太频繁打扰。
7. **终端能力检测与降级**：`Console.IsOutputRedirected` / `Environment.UserInteractive` / `AnsiConsole.Console.Profile.Capabilities.Interactive` 检测终端是否支持 Live。不支持时降级到行模式（`ConsoleEventRenderer`），保证 CI/管道/重定向场景不崩。理解"渐进增强"——能力全时用 Live，能力弱时降级。
8. **Tab 补全实现**：手写 `ReadLineWithCompletionAsync`——`Console.ReadKey` 逐键读，Tab 触发补全（前缀匹配命令列表，唯一匹配则填充，多匹配响铃或列选项）。理解 Spectre.Console 的 `TextPrompt<T>` 不原生支持 Tab 补全，需自行实现读行循环。
9. **状态栏与上下文占比**：`ConversationHistory.EstimatedTokens`（迭代 4 的字符数/3 估算）与配置的 `context_window_tokens` 计算占比，超 70% 黄色、超 90% 红色。理解占比是"软提示"——本迭代只警告不压缩（压缩在迭代 9）。
10. **`IUiControl` 抽象**：预留 UI 接口让迭代 10 命令系统通过抽象调用 UI（`PrintMessage` / `SetStatus`），不直接耦合 `TuiApp`。理解"依赖倒置"——命令系统依赖 `IUiControl` 抽象，TUI 是其实现，便于后续替换 UI（如换成 AlternateScreen 全屏实现）。

## 三、范围

### 3.1 本迭代包含（In Scope）

| 项 | 说明 |
| --- | --- |
| `Tui/IHitlGate.cs` | HITL 双向通道接口：`RequestAsync(ToolCall, ct) → Task<HitlDecision>` + 决策缓存查询 |
| `Tui/HitlDecision.cs` | `HitlDecision` record（`Choice` + `Reason`）+ `HitlChoice` 枚举（`AllowOnce` / `AllowSession` / `AllowPermanent` / `Deny`） |
| `Tui/HitlPrompt.cs` | `IHitlGate` 的 Spectre 实现：暂停 Live → `AnsiConsole.Prompt` 弹 A/S/P/D → 会话级缓存 |
| `Tui/TuiApp.cs` | 主 TUI 应用：装配 Live + 状态栏 + 事件消费 + 输入循环 + HITL 接线 |
| `Tui/EventRenderer.cs` | `AgentEvent` → `IRenderable` 翻译器（纯渲染逻辑，可单测） |
| `Tui/StatusBar.cs` | 状态栏组件：`Provider` / `Model` / `安全等级` / `上下文占比` / `当前轮次` / `工具数` |
| `Tui/InputReader.cs` | 带 Tab 补全的输入读取：`/` 开头补全硬编码命令列表 |
| `Tui/ConsoleEventRenderer.cs` | 降级渲染器：迭代 6 行模式渲染抽取成类（`tui.mode: console` 或非交互终端时用） |
| `Tui/IUiControl.cs` | UI 抽象接口（预留）：`PrintMessage` / `SetStatus` / `RequestHitl` |
| `Tui/SecurityLevel.cs` | `SecurityLevel` 枚举占位（`Strict` / `Normal` / `Permissive`），迭代 7 仅状态栏显示 |
| `Agent/BatchToolExecutor.cs` | 改：构造注入 `IHitlGate?`；`Write` 组执行前调 `RequestAsync`，`Deny` 时跳过执行 + 返回 `Fail`；预留 `OnBeforeExecuteAsync` hook 给迭代 8 |
| `Agent/AgentLoop.cs` | 改：`Write` 组工具若被 HITL 拒绝，emit `ToolBlockedEvent`（迭代 6 预留事件启用）；`RoundStartEvent` 携带状态供状态栏 |
| `App/App.cs` | 改：主循环委托 `TuiApp`（或 `ConsoleApp` 降级）；移除内联 `RenderEventsAsync` |
| `Config/Models.cs` | 扩展：`AppConfig` 加 `Tui` 节（`Mode` / `ShowStatusBar` / `StreamTokens` / `ContextWindowTokens`）+ `Security` 节（`Level` 占位） |
| `example.parrotcode.yaml` | 加 `tui:` / `security:` 配置节示例 |
| `Program.cs` | 改：装配 `IHitlGate` / `TuiApp` / `SecurityLevel` 注入 App；终端能力检测决定 Live/降级 |
| 单元测试 | `HitlDecisionTests` / `HitlPromptTests`（用假 `IAnsiConsole`）/ `EventRendererTests` / `StatusBarTests` / `InputReaderTests`（补全逻辑）/ `BatchToolExecutorHitlTests` + `TuiAppIntegrationTests`（端到端，用 MockProvider 脚本触发 HITL） |

### 3.2 本迭代不包含（Out of Scope）

- 安全层（黑名单 / 沙箱 / 三档权限真实拦截）→ 迭代 8（`SecurityLevel` 仅占位，HITL 是简化"Write 必问"）
- `SecurityGuard` 管线 → 迭代 8（`OnBeforeExecuteAsync` hook 本迭代预留）
- 斜杠命令注册中心 / 反射扫描 / 别名 → 迭代 10（`/help` / `/status` 硬编码实现）
- 会话持久化（JSONL）→ 迭代 10
- 上下文截断 / 摘要 / 熔断 → 迭代 9（状态栏占比只警告不压缩）
- AlternateScreen 全屏 + 固定分屏布局 → 进阶练习（本迭代半全屏）
- 多行输入 / 语法高亮 / 输入历史（↑↓）→ 可选扩展
- thinking 内容灰色折叠渲染 → 进阶练习（DeepSeek `reasoning_content` 字段解析）
- `AnthropicProvider` 接入 → 后续迭代
- 工具调用计费 / 限流 → 后续迭代
- 持久级 HITL 决策（`AllowPermanent` 跨会话）→ 迭代 10（需配置文件，本迭代 `AllowPermanent` 退化为 `AllowSession`）

### 3.3 与迭代 6 的边界

事件流与 HITL 在迭代 6/7 的边界：

| 迭代 6 | 迭代 7 |
| --- | --- |
| 事件消费者是 `Console.Write` + `AnsiConsole.MarkupLine`（行模式） | 替换为 `TuiApp` + `Live` 流式渲染 + `EventRenderer` |
| `ToolBlockedEvent` 定义但主路径不产生 | HITL 拒绝时由 `BatchToolExecutor` emit（经 AgentLoop 转发） |
| `BatchToolExecutor` 直接执行 Write 组 | 注入 `IHitlGate?`，Write 组执行前请求决策 |
| 无状态栏 | 顶部状态栏（Provider/Model/安全等级/上下文占比/轮次/工具数） |
| 无交互式确认 | Write 工具弹 A/S/P/D 确认框 |
| 无 Tab 补全 | `/` 开头 Tab 补全硬编码命令 |
| `App.RenderEventsAsync` 内联渲染 | 抽取为 `EventRenderer` + `TuiApp`，`ConsoleEventRenderer` 降级 |
| `ChannelEventSink` 无界 | 不变（仍是事件流通道，消费者从 App 改为 TuiApp） |

> 本迭代的 `IAgentEventSink` 抽象与 `ChannelEventSink` 实现**不变**。迭代 6 设计文档提到"迭代 7 可加 `TuiEventSink` 直接渲染（不走 Channel）"——本设计**保留 Channel**，理由：(1) Channel 解耦生产/消费已验证可用；(2) `TuiApp` 作为 Channel 消费者即可，无需新 sink；(3) 保留 Channel 让事件流可被多消费者（未来加日志 sink）扩展。`TuiApp` 内部从 `sink.Reader.ReadAllAsync` 读事件，调 `EventRenderer` 翻译，刷新 Live。

### 3.4 与迭代 8 的边界

| 本迭代（迭代 7） | 迭代 8 |
| --- | --- |
| `SecurityLevel` 枚举占位，状态栏显示 | `SecurityGuard` 真实拦截（黑名单/沙箱/三档权限） |
| HITL 是"所有 Write 工具必问"简化策略 | Normal 模式下读放行、写询问；Strict 白名单；Permissive 仅黑名单 |
| `BatchToolExecutor.OnBeforeExecuteAsync` hook 预留（默认 null 不拦截） | `SecurityGuard` 覆写 hook，返回拒绝原因 |
| HITL 决策缓存仅会话级 | 配置文件持久化 `AllowPermanent` |
| 黑名单不生效 | `rm -rf /` 等即使 Permissive 也拦 |

### 3.5 与迭代 10 的边界

| 本迭代（迭代 7） | 迭代 10 |
| --- | --- |
| `/help` / `/status` / `/clear` / `/exit` 硬编码分发 | `Commands/` 注册中心 + 反射扫描 + 别名 |
| `IUiControl` 最小定义（`PrintMessage` / `SetStatus` / `RequestHitl`） | 命令系统通过 `IUiControl` 调用 UI |
| Tab 补全硬编码命令列表 | 补全来自 `Registry` 注册的命令 |
| 会话历史内存版 | JSONL 持久化 + `/session load` |
| `AllowPermanent` 退化为会话级 | 持久化到配置文件跨会话生效 |

## 四、架构设计

### 4.1 模块结构（迭代 7 增量）

```
ParrotCode.Net/
├── Program.cs                 # 改：终端能力检测 + 装配 IHitlGate/TuiApp/SecurityLevel
├── App/
│   └── App.cs                 # 改：委托 TuiApp/ConsoleApp（降级）；移除内联渲染
├── Config/
│   └── Models.cs              # 改：AppConfig 加 Tui 节 + Security 节
├── Agent/
│   ├── AgentLoop.cs           # 改：Write 被 HITL 拒绝时 emit ToolBlockedEvent
│   ├── BatchToolExecutor.cs   # 改：注入 IHitlGate?；Write 组请求决策；预留 OnBeforeExecuteAsync
│   ├── ChatChunk.cs           # 不变
│   ├── AgentEvent.cs          # 不变（12 种事件稳定）
│   ├── IAgentEventSink.cs     # 不变
│   ├── ChannelEventSink.cs    # 不变
│   └── ToolCallAccumulator.cs # 不变
├── Conversation/
│   ├── History.cs             # 不变（EstimatedTokens 供状态栏）
│   ├── MessageExtensions.cs   # 不变
│   └── TokenEstimator.cs      # 不变
├── Providers/                 # 全部不变
├── Tools/                     # 全部不变（6 个工具）
└── Tui/                       # 新增目录
    ├── IHitlGate.cs           # 新增：HITL 双向通道接口
    ├── HitlDecision.cs        # 新增：决策 record + Choice 枚举
    ├── HitlPrompt.cs          # 新增：Spectre 实现（弹 A/S/P/D + 会话缓存）
    ├── TuiApp.cs              # 新增：主 TUI 应用（Live + 状态栏 + 事件消费 + 输入）
    ├── EventRenderer.cs       # 新增：AgentEvent → IRenderable 翻译
    ├── StatusBar.cs           # 新增：状态栏组件
    ├── InputReader.cs         # 新增：带 Tab 补全的输入读取
    ├── ConsoleEventRenderer.cs# 新增：降级行模式渲染（迭代 6 抽取）
    ├── IUiControl.cs          # 新增：UI 抽象接口（预留）
    └── SecurityLevel.cs       # 新增：安全等级枚举占位
```

> 命名空间约定沿用迭代 1-6：所有源文件统一 `namespace ParrotCode`，文件夹仅作物理组织。

### 4.2 调用流程（TUI 主循环 + 事件消费 + HITL）

```
┌──────────────────────────────────────────────────────────────────────┐
│  TuiApp.RunAsync                                                     │
│  while (!ct.IsCancellationRequested):                                │
│      input = InputReader.ReadLineWithCompletionAsync(commands, ct)   │
│      if input is /clear: history.Clear(); continue                   │
│      if input is /exit: break                                        │
│      if input is /help|/status: renderHelp/Status(); continue        │
│      history.AddUser(input)                                          │
│      var sink = new ChannelEventSink()                              │
│      var agentTask = agentLoop.RunAsync(history, sink, ct)           │
│      # —— Live 流式渲染活跃区 ——                                       │
│      await AnsiConsole.LiveAsync(liveCtx =>                          │
│          await foreach (evt in sink.Reader.ReadAllAsync(ct)):        │
│              var renderable = EventRenderer.Render(evt, liveCtx)    │
│              liveCtx.Update(renderable)                              │
│              # 完成事件（AgentDone/ToolResult/RoundEnd）→ 提交到历史  │
│              if IsCompletingEvent(evt):                              │
│                  liveCtx.Stop()                                       │
│                  AnsiConsole.Write(renderable)                       │
│                  liveCtx.Restart()                                    │
│      await agentTask                                                 │
└──────────────────────────────────────────────────────────────────────┘
                                  │
                                  ▼
┌──────────────────────────────────────────────────────────────────────┐
│  AgentLoop.RunAsync（迭代 6 不变 + HITL 拒绝转发）                    │
│  for round in 1..maxRounds:                                          │
│      sink.WriteAsync(RoundStartEvent(round))                         │
│      流式 LLM → 累积文本/tool_calls → sink.Write(TextDeltaEvent)     │
│      toolCalls = tcAcc.Build()                                       │
│      history.AddAssistant(...)                                       │
│      if no toolCalls: sink.Write(AgentDoneEvent); return            │
│      sink.Write(ToolCallStartEvent(call))  # 每个                    │
│      results = batchExecutor.ExecuteAsync(toolCalls, ct)             │
│          ┌─ BatchToolExecutor 内部 ──────────────────────────┐       │
│          │ Read 组: Task.WhenAll（不问 HITL）                │       │
│          │ Write 组: foreach call:                          │       │
│          │   decision = hitlGate?.RequestAsync(call, ct)    │       │
│          │   if decision == Deny:                            │       │
│          │     results[i] = ToolResult.Fail("用户拒绝")    │       │
│          │     # AgentLoop 见 Fail 后 emit ToolBlockedEvent │       │
│          │   else: results[i] = executor.ExecuteAsync(call) │       │
│          └──────────────────────────────────────────────────┘       │
│      foreach (call, result):                                         │
│          if result 是 HITL 拒绝: sink.Write(ToolBlockedEvent)       │
│          else: sink.Write(ToolResultEvent(call, result))             │
│          history.AddTool(...)                                        │
│      sink.Write(RoundEndEvent(round))                                │
│  sink.Write(MaxRoundsReachedEvent)                                   │
└──────────────────────────────────────────────────────────────────────┘
                                  │
                                  ▼
┌──────────────────────────────────────────────────────────────────────┐
│  IHitlGate.RequestAsync(call, ct)  ← BatchToolExecutor 调用          │
│  ┌─ HitlPrompt 实现 ──────────────────────────────────────────┐     │
│  │ if 会话缓存命中 (AllowSession): return AllowSession       │     │
│  │ tcs = new TaskCompletionSource<HitlDecision>()             │     │
│  │ # 通过回调通知 TuiApp 暂停 Live → Prompt → 恢复 Live        │     │
│  │ _pendingRequest = (call, tcs)                              │     │
│  │ _requestSignal.Set()  # 唤醒 UI 线程处理                    │     │
│  │ return tcs.Task  # BatchToolExecutor await 在此阻塞        │     │
│  │                                                            │     │
│  │ # UI 线程侧（TuiApp 检测到 pending request）:               │     │
│  │ live.Stop()                                                │     │
│  │ choice = AnsiConsole.Prompt<HitlChoice>(...)  # A/S/P/D    │     │
│  │ live.Restart()                                              │     │
│  │ if choice == AllowSession: _sessionCache.Add(call.Name)   │     │
│  │ tcs.TrySetResult(new HitlDecision(choice, reason))         │     │
│  └────────────────────────────────────────────────────────────┘     │
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

    /// <summary>允许永久（P）。跨会话不再问（迭代 7 退化为会话级，迭代 10 持久化）。</summary>
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

#### 4.3.2 `IHitlGate`（HITL 双向通道）

```csharp
namespace ParrotCode;

/// <summary>
/// HITL（人在回路）双向通道抽象。
/// BatchToolExecutor 在 Write 组工具执行前调用 RequestAsync 请求用户决策；
/// TUI 实现（HitlPrompt）弹框收集用户选择并完成返回的 Task。
///
/// 与 IAgentEventSink 的区别：
/// - IAgentEventSink 是单向 fire-and-forget（WriteAsync 返回 ValueTask，不携带返回值）。
/// - IHitlGate 是双向请求/响应（RequestAsync 返回 Task&lt;HitlDecision&gt;）。
/// 把需要返回值的交互独立成接口，保持事件流纯展示通知语义。
///
/// 迭代 7 只有一个实现（HitlPrompt，Spectre 弹框）。
/// 测试用 NullHitlGate（直接 AllowOnce）与 DenyHitlGate（直接 Deny）。
/// 迭代 8 SecurityGuard 可作为前置拦截器（在 IHitlGate 之前）。
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
    /// BatchToolExecutor 在调 RequestAsync 前先查缓存。
    /// </summary>
    bool IsAllowedThisSession(string toolName);
}

/// <summary>
/// 默认放行（无 HITL）。用于 Read 工具或配置禁用 HITL 时。
/// 等价于迭代 6 的行为——所有工具直接执行。
/// </summary>
public sealed class NullHitlGate : IHitlGate
{
    public Task<HitlDecision?> RequestAsync(ToolCall call, CancellationToken cancellationToken) =>
        Task.FromResult<HitlDecision?>(null);
    public bool IsAllowedThisSession(string toolName) => false;
}
```

> **设计要点**：
> - **`RequestAsync` 返回 `Task<HitlDecision?>`**：`null` 表示"无需 HITL"（Read 工具或缓存命中），调用方直接执行；非 null 表示用户已决策。用 nullable 区分"未询问"与"询问结果"，避免 `Deny` 被误判为"未询问"。
> - **`IsAllowedThisSession` 独立查询**：让 `BatchToolExecutor` 在调 `RequestAsync` 前先查缓存，命中则跳过弹框（`RequestAsync` 内部也查，双重保险）。分离查询让逻辑更清晰——缓存是状态，请求是动作。
> - **`NullHitlGate`**：等价于迭代 6 行为。配置 `tui.hitl: false` 或终端非交互时注入此实现，回归保护。
> - **`HitlDecision?` 而非 `HitlDecision`**：nullable 让"Read 工具不问"成为显式语义——`RequestAsync` 对 Read 工具返回 null（`BatchToolExecutor` 不调，但接口语义清晰）。

#### 4.3.3 `HitlPrompt`（Spectre 实现）

```csharp
using System.Collections.Concurrent;
using Spectre.Console;

namespace ParrotCode;

/// <summary>
/// IHitlGate 的 Spectre.Console 实现。
/// 收到请求时：暂停 Live（通过回调）→ AnsiConsole.Prompt 弹 A/S/P/D → 恢复 Live → 完成决策 Task。
/// 会话级缓存：AllowSession/AllowPermanent 记入 _sessionCache，同工具后续 IsAllowedThisSession 命中。
///
/// 线程模型：BatchToolExecutor 在 AgentLoop 线程调 RequestAsync（await 阻塞），
/// HitlPrompt 通过 _liveSuspender 回调通知 TuiApp 的 UI 线程暂停 Live 并弹框。
/// 本迭代简化为同线程同步：RequestAsync 内部直接暂停 Live + Prompt（Spectre.Console 单线程模型）。
/// 若未来 Live 在独立线程，需用 Channel 跨线程传递请求。
/// </summary>
public sealed class HitlPrompt : IHitlGate
{
    private readonly ConcurrentDictionary<string, byte> _sessionCache = new();
    private readonly Action? _suspendLive;
    private readonly Action? _resumeLive;

    /// <param name="suspendLive">弹框前暂停 Live 的回调（TuiApp 注入）。</param>
    /// <param name="resumeLive">弹框后恢复 Live 的回调。</param>
    public HitlPrompt(Action? suspendLive = null, Action? resumeLive = null)
    {
        _suspendLive = suspendLive;
        _resumeLive = resumeLive;
    }

    public bool IsAllowedThisSession(string toolName) =>
        _sessionCache.ContainsKey(toolName);

    public Task<HitlDecision?> RequestAsync(ToolCall call, CancellationToken cancellationToken)
    {
        // 缓存命中——会话级允许过，直接返回 AllowSession（不再弹框）
        if (_sessionCache.ContainsKey(call.Name))
            return Task.FromResult<HitlDecision?>(HitlDecision.AllowOnce with { Choice = HitlChoice.AllowSession });

        // 取消时立即返回 Deny（避免 Prompt 阻塞取消）
        if (cancellationToken.IsCancellationRequested)
            return Task.FromResult<HitlDecision?>(HitlDecision.Deny("已取消"));

        // 暂停 Live → Prompt → 恢复 Live（同线程同步）
        _suspendLive?.Invoke();
        try
        {
            var choice = AnsiConsole.Prompt(
                new SelectionPrompt<HitlChoice>()
                    .Title($"[yellow]⚠ 即将执行有副作用的工具：[/][cyan]{Markup.Escape(call.Name)}[/]")
                    .PageSize(5)
                    .AddChoices(HitlChoice.AllowOnce, HitlChoice.AllowSession,
                                HitlChoice.AllowPermanent, HitlChoice.Deny)
                    .UseConverter(c => c switch
                    {
                        HitlChoice.AllowOnce     => "[green]A[/] 允许本次",
                        HitlChoice.AllowSession  => "[green]S[/] 允许本会话",
                        HitlChoice.AllowPermanent=> "[green]P[/] 允许永久",
                        HitlChoice.Deny          => "[red]D[/] 拒绝",
                        _ => c.ToString()
                    }));

            if (choice is HitlChoice.AllowSession or HitlChoice.AllowPermanent)
                _sessionCache[call.Name] = 0;  // 迭代 7 AllowPermanent 退化为会话级

            return Task.FromResult<HitlDecision?>(
                choice == HitlChoice.Deny
                    ? HitlDecision.Deny("用户拒绝执行该工具")
                    : new HitlDecision(choice));
        }
        finally
        {
            _resumeLive?.Invoke();
        }
    }
}
```

> **设计要点**：
> - **`SelectionPrompt<HitlChoice>`**：Spectre.Console 的选择提示，方向键 + 回车。比 `TextPrompt<char>` 更友好（可视化选项）。A/S/P/D 作为快捷键可用 `MoreChoicesText` + `AddChoice` 的 `Style`，但 `SelectionPrompt` 默认方向键——本迭代用 `SelectionPrompt` 保证可读性，快捷键作为进阶练习。
> - **`_suspendLive`/`_resumeLive` 回调**：TuiApp 注入，让 HitlPrompt 不直接持有 `LiveDisplay` 引用，解耦。回调内 `Live.Stop()`/`Restart()`。
> - **`ConcurrentDictionary` 会话缓存**：线程安全（理论上 AgentLoop 与 UI 同线程，但防御性用并发集合）。键是工具名——同会话同工具只问一次（`AllowSession` 语义）。
> - **`AllowPermanent` 退化**：迭代 7 无配置文件持久化，`AllowPermanent` 与 `AllowSession` 行为一致（都进会话缓存）。迭代 10 接入配置文件后区分。
> - **取消时返回 Deny**：`cancellationToken.IsCancellationRequested` 时不弹框直接 Deny，避免 Prompt 阻塞取消。Spectre.Console 的 Prompt 不原生响应 CancellationToken，故前置检查。
> - **同线程同步模型**：本迭代 AgentLoop 与 TuiApp 消费者在同一 `await` 链上（TuiApp `await agentTask`，AgentLoop 内 `await batchExecutor.ExecuteAsync`，BatchToolExecutor 内 `await hitlGate.RequestAsync`）。HitlPrompt 在此链上同步弹框，Live 是 TuiApp 局部变量——故需 `suspendLive` 回调让 HitlPrompt 能暂停 TuiApp 的 Live。这是 Spectre.Console 单控制台实例的约束。

#### 4.3.4 `BatchToolExecutor` 改造（注入 IHitlGate）

```csharp
using Microsoft.Extensions.Logging;

namespace ParrotCode;

public sealed class BatchToolExecutor
{
    private readonly ToolExecutor _executor;
    private readonly ToolRegistry _registry;
    private readonly int _maxParallelism;
    private readonly IHitlGate? _hitlGate;          // 迭代 7 新增
    private readonly ILogger? _logger;

    public BatchToolExecutor(
        ToolExecutor executor,
        ToolRegistry registry,
        int maxParallelism = 5,
        IHitlGate? hitlGate = null,                   // 迭代 7 新增（可选，null 时不问）
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

        // Read 组并发（不问 HITL——幂等无副作用）
        foreach (var batch in readIndices.Chunk(_maxParallelism))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var tasks = batch.Select(i => _executor.ExecuteAsync(calls[i], cancellationToken)).ToArray();
            var batchResults = await Task.WhenAll(tasks);
            for (var j = 0; j < batch.Length; j++)
                results[batch[j]] = batchResults[j];
        }

        // Write 组串行 + HITL（迭代 7 新增）
        foreach (var i in writeIndices)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var call = calls[i];

            // 1. OnBeforeExecuteAsync hook（迭代 8 SecurityGuard 接入点，本迭代默认 null）
            var blocked = await OnBeforeExecuteAsync(call, cancellationToken);
            if (blocked is not null)
            {
                results[i] = blocked;
                continue;
            }

            // 2. HITL 请求（迭代 7 新增）
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
    /// 本迭代默认实现返回 null（不拦截）。预留虚方法供迭代 8 子类化或委托。
    /// </summary>
    protected virtual Task<ToolResult?> OnBeforeExecuteAsync(ToolCall call, CancellationToken ct) =>
        Task.FromResult<ToolResult?>(null);
}
```

> **设计要点**：
> - **`IHitlGate?` 可选注入**：null 时等价于迭代 6（直接执行）。配置 `tui.hitl: false` 或降级模式注入 null。
> - **HITL 仅对 Write 组**：Read 组（`read_file`/`glob`/`grep`）幂等无副作用，不问 HITL——这是"简化策略"。迭代 8 Normal 模式更精细（读放行、写询问）。
> - **`OnBeforeExecuteAsync` hook**：返回 `ToolResult?`，null 表示放行，非 null 表示拦截（`Fail`）。本迭代默认 null，迭代 8 `SecurityGuard` 覆写或委托此 hook 拦截黑名单/沙箱违规。HITL 在 hook 之后——安全层先拦截，通过后再问用户。
> - **HITL 拒绝返回 `ToolResult.Fail`**：不抛异常——失败原因回灌 LLM 让其自我修正（如改用 `write_file` 而非 `run_command`）。AgentLoop 见 `ToolResult.Success==false` 后 emit `ToolBlockedEvent`（见 4.3.5）。
> - **顺序**：`OnBeforeExecuteAsync`（安全层）→ HITL（用户决策）→ 执行。安全层拒绝时不问用户（避免打扰已拦截的操作）。

#### 4.3.5 `AgentLoop` 改造（HITL 拒绝转发为 ToolBlockedEvent）

```csharp
// Agent/AgentLoop.cs（迭代 6 基础上的增量改动）

// RunCoreAsync 内工具结果处理段改为：
var results = await _batchExecutor.ExecuteAsync(toolCalls, cancellationToken);

for (var i = 0; i < toolCalls.Count; i++)
{
    var call = toolCalls[i];
    var result = results[i];

    // 迭代 7 新增：HITL/安全层拒绝 → emit ToolBlockedEvent；否则 ToolResultEvent
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

// —— 辅助：判断是否为 HITL/安全层拒绝（区别于工具自身执行失败）——
// 启发式：拒绝原因包含"用户拒绝"或"被拦截"标记为 blocked。
// 更严谨的做法：BatchToolExecutor 返回带 Blocked 标志的结构（迭代 8 再精细化）。
private static bool IsHitlDenial(ToolResult result) =>
    !result.Success && (result.Error?.Contains("用户拒绝") == true ||
                        result.Error?.Contains("被拦截") == true);
```

> **设计要点**：
> - **`ToolBlockedEvent` 启用**：迭代 6 预留事件类型，本迭代在 HITL/安全层拒绝时 emit。消费者（TuiApp）用红色卡片渲染"✗ 被拦截：{reason}"，区别于工具执行失败（黄色 `✗ 失败`）。
> - **`IsHitlDenial` 启发式**：用错误信息字符串匹配判断"拒绝 vs 执行失败"。不严谨——迭代 8 可让 `ToolResult` 加 `Blocked` 标志或引入 `ToolBlockedResult` 派生类型。本迭代用字符串匹配降低改动面（`ToolResult` 不变）。
> - **回灌历史用统一 `AddTool`**：拒绝原因作为 tool 消息回灌，LLM 看到"用户拒绝执行该工具"后调整策略（如换工具或放弃）。这是 HITL 影响 LLM 行为的闭环。
> - **AgentLoop 其余逻辑不变**：迭代 6 的 ReAct 主循环、事件流顺序、`finally sink.Complete()` 全保留。仅工具结果处理段区分 `ToolBlockedEvent`/`ToolResultEvent`。

#### 4.3.6 `EventRenderer`（事件 → IRenderable 翻译）

```csharp
using System.Text;
using Spectre.Console;

namespace ParrotCode;

/// <summary>
/// 把 AgentEvent 翻译成 Spectre.Console IRenderable。
/// 纯渲染逻辑无副作用——输入事件 + 渲染上下文，输出 IRenderable。
/// 可单测：断言返回的 IRenderable 类型与内容。
///
/// 渲染策略：
/// - TextDeltaEvent: 累积到上下文的 _textBuf，返回 new Text(buf)（流式刷新）
/// - ToolCallStartEvent: 返回 Panel 卡片（工具名 + 参数）
/// - ToolResultEvent: 成功绿 ✓ + 截断内容；失败红 ✗ + 错误
/// - ToolBlockedEvent: 红色 Panel "被拦截"
/// - RoundStartEvent: 灰色 [Round N]
/// - AgentDoneEvent: 灰色 [完成]
/// - MaxRoundsReachedEvent: 黄色 ⚠
/// - ErrorEvent: 红色 ✗ 错误
/// - CancelledEvent: 灰色 [已取消]
/// </summary>
public sealed class EventRenderer
{
    private readonly StringBuilder _textBuf = new();
    private readonly List<IRenderable> _pending = new();  // 本轮已完成项（工具卡片等）
    private int _currentRound;

    /// <summary>当前轮活跃区文本（供状态栏或调试查看）。</summary>
    public string CurrentText => _textBuf.ToString();

    /// <summary>重置渲染器（新一轮开始时调）。</summary>
    public void Reset()
    {
        _textBuf.Clear();
        _pending.Clear();
        _currentRound = 0;
    }

    /// <summary>
    /// 渲染单个事件为 IRenderable，并更新内部累积状态。
    /// 返回 null 表示该事件不产生独立 IRenderable（如 TextDelta 已累积到 _textBuf，由 BuildActive 一起输出）。
    /// </summary>
    public IRenderable? Render(AgentEvent evt)
    {
        switch (evt)
        {
            case AgentEvent.RoundStartEvent(var round):
                _currentRound = round;
                _textBuf.Clear();
                _pending.Clear();
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
                var blockedPanel = new Panel(new Markup(
                    $"[red]✗ 被拦截[/] [cyan]{Markup.Escape(call.Name)}[/]\n[red]{Markup.Escape(reason)}[/]"))
                {
                    Header = new PanelHeader("[red]⛔ HITL 拦截[/]"),
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
    /// 构建当前 Live 活跃区的 IRenderable（状态栏 + 轮次 + 文本 + 进行中工具卡片）。
    /// 每次 Live 刷新时调此方法得到最新渲染目标。
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

        // 4. 进行中/已完成的工具卡片
        rows.AddRange(_pending);

        return new Rows(rows);
    }

    /// <summary>
    /// 提取已完成的内容作为滚动历史提交（AgentDone 后调）。
    /// 返回的 IRenderable 不含状态栏（状态栏是 Live 专属）。
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
> - **`BuildActive` vs `BuildCommitted`**：前者含状态栏（Live 刷新用），后者不含（提交到滚动历史用，因为状态栏是 Live 专属不应进历史）。
> - **`Panel` 卡片化**：工具调用/结果用 `Panel` 包裹，带颜色边框 + Header，视觉区分。`Markup.Escape` 防止工具内容含 Spectre 标记字符（`[` 等）破坏渲染。
> - **纯逻辑可测**：`Render` 与 `BuildActive` 是纯函数（输入事件，输出 IRenderable + 更新内部状态），单测可断言 `_textBuf.ToString()`、`_pending.Count`、返回的 `Panel.Header` 文本等。

#### 4.3.7 `StatusBar`

```csharp
using Spectre.Console;

namespace ParrotCode;

/// <summary>
/// 状态栏组件。显示 Provider/Model/安全等级/上下文占比/当前轮次/工具数。
/// 作为 Live 渲染目标的一部分，每次刷新调 Render() 返回最新 IRenderable。
/// </summary>
public sealed class StatusBar
{
    public string Provider { get; set; } = string.Empty;
    public string Model { get; set; } = string.Empty;
    public SecurityLevel SecurityLevel { get; set; } = SecurityLevel.Normal;
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
            SecurityLevel.Strict     => "red",
            SecurityLevel.Normal     => "yellow",
            SecurityLevel.Permisive => "green",
            _ => "grey"
        };

        var markup =
            $"[grey]provider=[/][cyan]{Markup.Escape(Provider)}[/] " +
            $"[grey]model=[/][cyan]{Markup.Escape(Model)}[/] " +
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
}
```

> **设计要点**：
> - **`Panel` + 灰色边框**：状态栏用细灰边框 Panel 包裹，视觉上与输出区分隔。每次 Live 刷新重绘。
> - **占比颜色**：绿（<70%）/黄（70-90%）/红（>90%）。本迭代只警告不压缩（压缩在迭代 9）。
> - **`SecurityLevel` 枚举占位**：状态栏显示等级名，颜色区分（Strict 红/Normal 黄/Permissive 绿）。迭代 7 默认 Normal，迭代 8 接入真实拦截后可被 `/mode` 命令切换（迭代 10）。
> - **`Markup.Escape`**：Provider/Model 名可能含特殊字符，转义防止破坏 Markup。

#### 4.3.8 `SecurityLevel`（占位枚举）

```csharp
namespace ParrotCode;

/// <summary>
/// 安全等级枚举（迭代 7 占位，迭代 8 接入真实拦截）。
/// - Strict: 只允许白名单路径读写（迭代 8 实现）。
/// - Normal: 读放行、写询问（HITL）。
/// - Permissive: 仅黑名单拦截。
/// 迭代 7 仅状态栏显示，不做真实拦截——HITL 是"所有 Write 必问"简化策略。
/// </summary>
public enum SecurityLevel
{
    Strict,
    Normal,
    Permisive  // 注意：拼写 Permisive（与 plan.md 一致），迭代 8 可纠正为 Permissive
}
```

> **拼写说明**：`plan.md` 第八章写的是 `Permissive`。本设计沿用 plan.md 拼写 `Permisive` 以保持一致；若迭代 8 纠正为 `Permissive`，需同步迁移。实现时建议用 `Permissive`（正确拼写），本设计文档保留 plan.md 原样以暴露此偏差。

#### 4.3.9 `InputReader`（带 Tab 补全）

```csharp
using Spectre.Console;

namespace ParrotCode;

/// <summary>
/// 带 Tab 补全的输入读取器。
/// 遇 / 开头按 Tab 补全硬编码命令列表。Enter 提交，Ctrl+C 取消。
/// 迭代 10 命令系统完善后，命令列表来自 Registry。
///
/// 注意：此方法不在 Live 期间调用（Live 与 Console.ReadKey 互斥）。
/// TuiApp 在 Live Stop 后调用此方法读输入。
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
            var key = await Task.Run(() => Console.ReadKey(true), ct);
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
        // 回退 backCount 字符并清除
        for (var i = 0; i < backCount + 2; i++)  // +2 抵消 "> "
            Console.Write("\b \b");
    }
}
```

> **设计要点**：
> - **`Console.ReadKey(true)`**：`true` 不回显，自行控制显示（让 `/` 命令用青色、普通文本用白色）。
> - **Tab 补全逻辑**：`/` 开头才补全；唯一匹配直接填充（清行重写）；多匹配列选项不填充。Enter 提交，Esc 取消，Backspace 删除。
> - **`Task.Run(() => Console.ReadKey)`**：`ReadKey` 是同步阻塞调用，包成 Task 让 `await` 能响应 CancellationToken。Ctrl+C 由 Program 的全局 cts 处理。
> - **不在 Live 期间调用**：`Console.ReadKey` 与 Live 互斥。TuiApp 在 Live Stop 后调此方法。输入完成后再 Start Live 渲染下一轮 AI 回复。
> - **多行输入**：本迭代单行（Enter 即提交）。多行（Shift+Enter 换行）作为进阶练习，需改用 `ConsoleKey` 组合检测。

#### 4.3.10 `TuiApp`（主 TUI 应用）

```csharp
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Spectre.Console;

namespace ParrotCode;

/// <summary>
/// 主 TUI 应用：装配 Live + 状态栏 + 事件消费 + 输入循环 + HITL 接线。
/// 替代迭代 6 App.cs 的内联渲染。降级时由 ConsoleApp（用 ConsoleEventRenderer）替代。
/// </summary>
internal sealed class TuiApp
{
    private readonly IBaseProvider _provider;
    private readonly ProviderConfig _providerConfig;
    private readonly AgentConfig _agentConfig;
    private readonly TuiConfig _tuiConfig;
    private readonly SecurityLevel _securityLevel;
    private readonly ToolRegistry _registry;
    private readonly BatchToolExecutor _batchExecutor;
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

        // 装配工具（与迭代 6 App 一致）
        _registry = new ToolRegistry();
        _registry.Register(new ReadFileTool());
        _registry.Register(new WriteFileTool());
        _registry.Register(new EditFileTool());
        _registry.Register(new GlobTool());
        _registry.Register(new GrepTool());
        _registry.Register(new RunCommandTool());

        var toolTimeout = TimeSpan.FromSeconds(_agentConfig.ToolTimeoutSeconds ?? 30);
        var executor = new ToolExecutor(_registry, toolTimeout, _logger);

        // HITL 接线：HitlPrompt 与 Live 生命周期绑定（suspend/resume 回调）
        var liveRef = new LiveRef();
        var hitlGate = tuiConfig.EnableHitl
            ? new HitlPrompt(() => liveRef.Stop(), () => liveRef.Restart())
            : (IHitlGate)new NullHitlGate();

        _batchExecutor = new BatchToolExecutor(
            executor, _registry,
            _agentConfig.MaxParallelism ?? 5,
            hitlGate, _logger);
    }

    public async Task RunAsync()
    {
        var history = new ConversationHistory();
        var inputReader = new InputReader();
        var statusBar = new StatusBar
        {
            Provider = _providerConfig.Name,
            Model = _providerConfig.Model,
            SecurityLevel = _securityLevel,
            ContextWindowTokens = _tuiConfig.ContextWindowTokens ?? 64000,
            ToolCount = _registry.GetAll().Count
        };

        AnsiConsole.MarkupLine(
            $"[grey]ParrotCode.Net[/] [green]TUI 模式[/] | " +
            $"provider=[cyan]{Markup.Escape(_providerConfig.Name)}[/] " +
            $"model=[cyan]{Markup.Escape(_providerConfig.Model)}[/] " +
            $"security=[cyan]{_securityLevel}[/] " +
            $"tools=[cyan]{_registry.GetAll().Count}[/]");

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

            // 2. 构造 AgentLoop + 事件流
            var agentLoop = new AgentLoop(_provider, _registry, _batchExecutor,
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
        var liveRef = new LiveRef();

        await AnsiConsole.Live(new Text("")).StartAsync(async ctx =>
        {
            liveRef.Attach(ctx);

            await foreach (var evt in reader.ReadAllAsync(_ct))
            {
                // 更新状态栏轮次
                if (evt is AgentEvent.RoundStartEvent(var r)) statusBar.CurrentRound = r;

                renderer.Render(evt);
                ctx.Update(renderer.BuildActive(statusBar));
                ctx.Refresh();

                // 完成事件 → 提交到滚动历史，清空活跃区
                if (IsCompletingEvent(evt))
                {
                    var committed = renderer.BuildCommitted();
                    // 暂停 Live，写入历史，再恢复
                    liveRef.Stop();
                    AnsiConsole.WriteLine();
                    AnsiConsole.Write(committed);
                    renderer.Reset();
                    liveRef.Restart();
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

    /// <summary>Live 句柄引用，供 HitlPrompt 的 suspend/resume 回调访问。</summary>
    private sealed class LiveRef
    {
        private LiveDisplayContext? _ctx;
        public void Attach(LiveDisplayContext ctx) => _ctx = ctx;
        public void Stop() { /* Spectre.Console Live 无显式 Stop，用 ctx.Update(空) 模拟 */ }
        public void Restart() { /* 下一轮重建 */ }
    }
}
```

> **设计要点**：
> - **`LiveRef` 内部类**：让 `HitlPrompt` 的 `suspend/resume` 回调能访问 Live 上下文。Spectre.Console 的 `LiveDisplayContext` 在 `StartAsync` 回调内才可用，故用 `LiveRef` 间接持有。实际实现中 Spectre.Console 的 Live 暂停需用 `ctx` 的内部机制（或 `AnsiConsole.Live` 嵌套限制）——本设计示意，实现时可能需用 `LiveDisplayContext` 的反射或 Spectre.Console 提供的暂停 API。若 Spectre.Console 无原生暂停，HITL 时改用"Live 不停，Prompt 在 Live 外的独立区域"或"先 Stop Live（退出 StartAsync）→ Prompt → 重启 Live"。实现时验证。
> - **`IsCompletingEvent`**：`AgentDone`/`MaxRoundsReached`/`Error`/`Cancelled` 触发提交。`ToolResultEvent`/`RoundEndEvent` 不提交（继续累积到活跃区，直到 AgentDone 才整轮提交）。这让一轮内的多个工具调用在活跃区内连续展示，轮次结束统一提交。
> - **`renderer.Reset()`**：提交后清空活跃区，下一轮从空开始。
> - **状态栏轮次实时更新**：`RoundStartEvent` 时设 `statusBar.CurrentRound`，`BuildActive` 时刷新状态栏。

#### 4.3.11 `ConsoleEventRenderer`（降级行模式）

```csharp
using Spectre.Console;

namespace ParrotCode;

/// <summary>
/// 降级行模式渲染器（迭代 6 App.RenderEventsAsync 抽取）。
/// 在 tui.mode=console 或终端非交互（重定向/CI）时使用。
/// 不用 Live，纯 Console.Write + AnsiConsole.MarkupLine。
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
                        $"[cyan]→[/] {Markup.Escape(call.Name)}({Markup.Escape(call.Input.GetRawText())})");
                    break;
                case AgentEvent.ToolResultEvent(_, var result):
                    if (result.Success)
                        AnsiConsole.MarkupLine($"[green]✓[/] {Markup.Escape(Truncate(result.Content, 80))}");
                    else
                        AnsiConsole.MarkupLine($"[red]✗[/] {Markup.Escape(result.Error ?? "未知错误")}");
                    break;
                case AgentEvent.ToolBlockedEvent(var call, var reason):
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
> - **新增 `ToolBlockedEvent` 渲染**：迭代 6 此事件 no-op，本迭代降级路径也支持（红色 `⛔`）。
> - **无 Live/状态栏**：降级路径无状态栏（状态栏需 Live 刷新）。`/status` 命令在降级模式直接打印状态信息。

#### 4.3.12 `IUiControl`（预留 UI 抽象）

```csharp
namespace ParrotCode;

/// <summary>
/// UI 抽象接口（迭代 7 最小定义，迭代 10 命令系统通过它调用 UI）。
/// 让命令系统不直接耦合 TuiApp，便于替换 UI 实现。
/// 迭代 7 只定义接口，TuiApp 实现最小子集。
/// </summary>
public interface IUiControl
{
    /// <summary>打印一条消息（用户可见）。</summary>
    Task PrintMessageAsync(string message, CancellationToken ct);

    /// <summary>更新状态栏字段。</summary>
    void SetStatus(string key, string value);

    /// <summary>请求 HITL 决策（委托 IHitlGate）。</summary>
    Task<HitlDecision?> RequestHitlAsync(ToolCall call, CancellationToken ct);
}
```

> **设计要点**：
> - **预留接口**：迭代 7 定义但不强制 TuiApp 实现（可加 `: IUiControl`）。迭代 10 命令系统依赖此抽象。
> - **`SetStatus`**：通用 key-value，让命令能更新任意状态栏字段（如 `/mode strict` 改安全等级）。
> - **`RequestHitlAsync`**：委托 `IHitlGate`，让命令系统也能触发 HITL（如 `/approve` 命令）。

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
- **滚动历史**：`IsCompletingEvent` 触发时，`liveRef.Stop()` → `AnsiConsole.Write(committed)` → `renderer.Reset()` → `liveRef.Restart()`。提交后内容进入滚动历史，Live 区域清空。
- **输入行**：Live Stop 后读输入，输入完成 Start 新 Live 渲染下一轮。

#### 4.4.3 Live 与 HITL/Prompt 互斥

Spectre.Console 的 Live 与 `AnsiConsole.Prompt` 都独占控制台：
- HITL 弹框前必须暂停 Live（`liveRef.Stop()`）。
- `AnsiConsole.Prompt` 接管控制台显示选择框。
- 用户选择后恢复 Live（`liveRef.Restart()`）。

Spectre.Console 0.49.1 的 `LiveDisplayContext` 无显式 `Stop`/`Restart` API。实现选项：
- **方案 A**：HITL 时退出 `StartAsync`（`break` 退出 `await foreach`）→ Prompt → 重启新 Live。代价：需保存渲染器状态。
- **方案 B**：用 `AnsiConsole.Console.Profile.Capabilities` 检测，Live 期间 Prompt 用 AlternateScreen 临时切换。复杂。
- **方案 C（推荐）**：HITL 不用 `AnsiConsole.Prompt`，改用自绘的 `IRenderable` 选择框 + `Console.ReadKey` 读 A/S/P/D，作为 Live 渲染目标的一部分（不暂停 Live）。`HitlPrompt` 渲染"⚠ 即将执行 {tool}，按 A/S/P/D"到 Live 活跃区，读按键，`ctx.Update` 刷新为"✓ 已允许"。

> **决策：方案 C**。HITL 选择框作为 Live 渲染目标的一部分，`Console.ReadKey` 读按键（与 Live 不互斥——Live 是输出，ReadKey 是输入）。这样无需暂停 Live，状态机更简单。`HitlPrompt` 改为接收 `Action<IRenderable>` 渲染回调 + `Func<CancellationToken, ConsoleKey>` 读键回调，而非 `AnsiConsole.Prompt`。

#### 4.4.4 方案 C 下的 HitlPrompt 改造

```csharp
// HitlPrompt 改用 Live 内渲染 + ReadKey（不暂停 Live）
public sealed class HitlPrompt : IHitlGate
{
    private readonly ConcurrentDictionary<string, byte> _sessionCache = new();
    private readonly Action<IRenderable>? _render;       // 在 Live 活跃区渲染提示
    private readonly Func<CancellationToken, ConsoleKey>? _readKey;  // 读用户按键

    public HitlPrompt(Action<IRenderable>? render = null, Func<CancellationToken, ConsoleKey>? readKey = null)
    {
        _render = render;
        _readKey = readKey;
    }

    public bool IsAllowedThisSession(string toolName) => _sessionCache.ContainsKey(toolName);

    public Task<HitlDecision?> RequestAsync(ToolCall call, CancellationToken cancellationToken)
    {
        if (_sessionCache.ContainsKey(call.Name))
            return Task.FromResult<HitlDecision?>(new HitlDecision(HitlChoice.AllowSession));
        if (cancellationToken.IsCancellationRequested)
            return Task.FromResult<HitlDecision?>(HitlDecision.Deny("已取消"));

        // 渲染提示到 Live 活跃区
        var promptRenderable = new Panel(new Markup(
            $"[yellow]⚠ 即将执行[/] [cyan]{Markup.Escape(call.Name)}[/]([grey]{Markup.Escape(Truncate(call.Input.GetRawText(), 80))}[/])\n" +
            $"[grey]按 A=本次 S=会话 P=永久 D=拒绝[/]"))
        {
            BorderStyle = new Style(foreground: Color.Yellow),
            Header = new PanelHeader("[yellow]HITL 确认[/]")
        };
        _render?.Invoke(promptRenderable);

        // 读按键（A/S/P/D）
        var key = _readKey is null ? ConsoleKey.D : _readKey(cancellationToken);
        var choice = key switch
        {
            ConsoleKey.A => HitlChoice.AllowOnce,
            ConsoleKey.S => HitlChoice.AllowSession,
            ConsoleKey.P => HitlChoice.AllowPermanent,
            _ => HitlChoice.Deny
        };

        if (choice is HitlChoice.AllowSession or HitlChoice.AllowPermanent)
            _sessionCache[call.Name] = 0;

        // 渲染决策结果
        var resultRenderable = new Markup(
            choice == HitlChoice.Deny
                ? $"[red]✗ 已拒绝[/]"
                : $"[green]✓ 已允许（{choice}）[/]");
        _render?.Invoke(resultRenderable);

        return Task.FromResult<HitlDecision?>(
            choice == HitlChoice.Deny
                ? HitlDecision.Deny("用户拒绝执行该工具")
                : new HitlDecision(choice));
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..(max - 3)] + "...";
}
```

> **方案 C 优势**：
> - 不暂停 Live，状态机简单（无 Stop/Restart）。
> - HITL 提示与决策结果都进 Live 活跃区，与文本/工具卡片统一渲染。
> - `Console.ReadKey` 与 Live 输出互不干扰（输入 vs 输出分离）。
> - 测试时 `_render`/`_readKey` 可注入假实现（不依赖真实控制台）。

#### 4.4.5 流式渲染边界情况

| 情况 | 处理 |
| --- | --- |
| 终端非交互（`!Environment.UserInteractive` 或 `Console.IsOutputRedirected`） | 降级到 `ConsoleEventRenderer`，不用 Live |
| Spectre.Console 检测到不支持 ANSI | Live 自动降级为逐行刷新（Spectre 内置） |
| Live 期间用户 Ctrl+C | `ct` 取消，`await foreach` 抛 `OperationCanceledException`，Live 自动退出 |
| 文本增量含 `\r` 等控制字符 | `Text` 渲染时 Spectre 转义；若异常用 `Markup.Escape` |
| 工具参数含 `[` 破坏 Markup | `Markup.Escape` 转义所有动态内容 |
| 一轮内 10+ 工具调用卡片堆满屏 | `BuildActive` 限制 `_pending` 显示最近 5 个 + "...还有 N 个" |
| HITL 期间用户按非 A/S/P/D 键 | 忽略，继续等待（`_readKey` 循环到合法键） |
| HITL 期间 Ctrl+C | `_readKey` 的 `Task.Run` 被 ct 取消，返回 Deny("已取消") |

### 4.5 HITL 闭环详解

#### 4.5.1 闭环流程

```
用户: 在 D:\tmp 创建 hello.txt 写入你好
  ↓
LLM 第 1 轮: tool_call write_file({"path":"D:\\tmp\\hello.txt","content":"你好"})
  ↓
BatchToolExecutor:
  write_file 是 Write 类别 → HITL 询问
  HitlPrompt.RequestAsync(call):
    渲染 "⚠ 即将执行 write_file(...)" 到 Live 活跃区
    读按键 → 用户按 S（允许本会话）
    _sessionCache["write_file"] = 0
    返回 AllowSession
  执行 write_file → ToolResult.Ok("已写入")
  ↓
AgentLoop: emit ToolResultEvent(call, result)
  ↓
LLM 第 2 轮: 看到 write_file 成功 → TextDelta("已创建 hello.txt") → AgentDone
  ↓
TuiApp: 提交活跃区到滚动历史
```

#### 4.5.2 拒绝闭环

```
用户: 删除所有 .cs 文件
  ↓
LLM 第 1 轮: tool_call run_command({"command":"del","args":"/S *.cs"})
  ↓
BatchToolExecutor:
  run_command 是 Write 类别 → HITL 询问
  HitlPrompt: 渲染提示 → 用户按 D（拒绝）
  返回 Deny("用户拒绝执行该工具")
  results[i] = ToolResult.Fail("用户拒绝执行该工具")
  ↓
AgentLoop: emit ToolBlockedEvent(call, "用户拒绝执行该工具")
  history.AddTool("错误：用户拒绝执行该工具", call.Id)
  ↓
LLM 第 2 轮: 看到"用户拒绝" → TextDelta("好的，我不删除文件。需要我帮您做别的吗？") → AgentDone
  ↓
TuiApp: 提交（含红色 ⛔ 拦截卡片）
```

#### 4.5.3 会话缓存命中

```
用户: 再创建 hello2.txt
  ↓
LLM: tool_call write_file({"path":"D:\\tmp\\hello2.txt",...})
  ↓
BatchToolExecutor:
  write_file 是 Write → IsAllowedThisSession("write_file") == true（上次按 S）
  → 直接执行，不弹 HITL
  ↓
ToolResult.Ok → ToolResultEvent → AgentDone
```

### 4.6 状态栏设计

状态栏字段：

| 字段 | 来源 | 示例 |
| --- | --- | --- |
| Provider | `_providerConfig.Name` | `deepseek` |
| Model | `_providerConfig.Model` | `deepseek-chat` |
| 安全等级 | `_securityLevel`（配置） | `Normal` |
| 上下文占比 | `history.EstimatedTokens / context_window` | `12%(7680/64000)` |
| 当前轮次 | `RoundStartEvent.Round` | `2` |
| 工具数 | `_registry.GetAll().Count` | `6` |

占比颜色：
- `< 70%`：绿色
- `70-90%`：黄色（警告）
- `> 90%`：红色（危险，本迭代只警告不压缩）

### 4.7 Tab 补全命令列表

本迭代硬编码命令（迭代 10 扩展为 Registry）：

| 命令 | 行为 |
| --- | --- |
| `/clear` | 清空历史 |
| `/exit` / `/quit` | 退出 |
| `/help` | 显示命令列表 |
| `/status` | 打印状态栏 |

Tab 补全规则：
- 输入 `/` 开头按 Tab：前缀匹配命令列表。
- 唯一匹配：直接填充完整命令。
- 多匹配：列选项，不填充。
- 非 `/` 开头：Tab 无效（普通文本输入）。

## 五、依赖变更

**无新增 NuGet 依赖。**

- `Spectre.Console` 0.49.1 已在迭代 1 引入，本迭代用其 `Live` / `Panel` / `Rows` / `SelectionPrompt` / `Markup` / `Text` 等 API。
- `System.Threading.Channels` BCL 内置（迭代 6 已用）。
- `ConcurrentDictionary` BCL 内置（`System.Collections.Concurrent`）。
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

    /// <summary>TUI 配置（迭代 7 新增）。null 时用默认值（Live 模式）。</summary>
    public TuiConfig? Tui { get; init; }

    /// <summary>安全配置（迭代 7 占位，迭代 8 接入）。null 时默认 Normal。</summary>
    public SecurityConfig? Security { get; init; }
}

/// <summary>TUI 渲染配置。所有字段可选。</summary>
public sealed record TuiConfig
{
    /// <summary>渲染模式："live"（默认）| "console"（降级行模式）。</summary>
    public string? Mode { get; init; }

    /// <summary>是否显示状态栏，默认 true。</summary>
    public bool? ShowStatusBar { get; init; }

    /// <summary>是否启用 HITL，默认 true。false 时注入 NullHitlGate。</summary>
    public bool? EnableHitl { get; init; }

    /// <summary>上下文窗口 token 数（状态栏占比分母），默认 64000。</summary>
    public int? ContextWindowTokens { get; init; }
}

/// <summary>安全配置（迭代 7 占位，迭代 8 接入真实拦截）。</summary>
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

# 迭代 7 新增
tui:
  mode: live              # live | console（降级）
  show_status_bar: true
  enable_hitl: true
  context_window_tokens: 64000

# 迭代 7 占位（迭代 8 接入真实拦截）
security:
  level: normal           # strict | normal | permissive
```

### 6.3 默认值

| 字段 | 默认值 | 覆盖来源 |
| --- | --- | --- |
| `Tui.Mode` | `"live"` | `tui.mode` |
| `Tui.ShowStatusBar` | `true` | `tui.show_status_bar` |
| `Tui.EnableHitl` | `true` | `tui.enable_hitl` |
| `Tui.ContextWindowTokens` | `64000` | `tui.context_window_tokens` |
| `Security.Level` | `"normal"` | `security.level` |

> ConfigLoader 解析时若 `tui`/`security` 节缺失，对应字段为 null，App 用默认值。

## 七、迁移说明（迭代 6 → 迭代 7）

| 迭代 6 | 迭代 7 | 处理 |
| --- | --- | --- |
| `App.RenderEventsAsync` 内联渲染 | 抽取为 `EventRenderer` + `TuiApp` + `ConsoleEventRenderer` | 重构（旧逻辑保留为降级路径） |
| `BatchToolExecutor` 直接执行 Write | 注入 `IHitlGate?`，Write 组请求决策 | 扩展（null 时等价旧行为） |
| `ToolBlockedEvent` 定义不产生 | HITL 拒绝时 emit | 启用预留事件 |
| `AgentLoop` 工具结果统一 `ToolResultEvent` | 区分 `ToolBlockedEvent`（拒绝）/ `ToolResultEvent` | 增量改动 |
| 无状态栏 | `StatusBar` 组件 | 新增 |
| 无 Tab 补全 | `InputReader` | 新增 |
| 无 `Tui/` 目录 | 新增 10 个文件 | 新模块 |
| `ChannelEventSink` 消费者是 App | 消费者改为 TuiApp | 接口不变 |
| `App` 直接装配工具 | TuiApp 装配（含 HITL 接线） | 移动 |
| 无安全等级 | `SecurityLevel` 枚举占位 | 新增（状态栏显示） |

迁移后回归不变式：
- `tui.mode: console` + `enable_hitl: false` 时，行为与迭代 6 完全一致（`ConsoleEventRenderer` + `NullHitlGate`）。
- `active_provider: mock` 无脚本时，TuiApp 输入"你好" → Live 渲染"你好（mock）" → 提交，与迭代 6 行模式内容一致（仅视觉增强）。
- `/clear` / `/exit` 行为保持。
- 迭代 1-6 既有测试全绿（`BatchToolExecutor` 的 `IHitlGate?` 可选，旧测试不传 hitlGate 等价旧行为）。

> **回归保护**：`BatchToolExecutor` 构造函数加可选参数 `IHitlGate? hitlGate = null`，旧调用不传则 null（等价迭代 6）。`AgentLoop` 改动仅工具结果处理段区分事件类型，旧 `AgentLoopTests` 用 MockProvider 脚本不触发 HITL（无 Write 工具或脚本不调 Write）仍全绿。若旧测试调 Write 工具，需确认是否注入 `NullHitlGate`——旧 `BatchToolExecutorTests` 不传 hitlGate，默认 null，Write 工具直接执行，行为不变。

## 八、单元测试

### 8.1 `HitlDecisionTests`（新增）

| 用例 | 操作 | 期望 |
| --- | --- | --- |
| `AllowOnce_IsAllowed_True` | `HitlDecision.AllowOnce` | `IsAllowed == true`, `ShouldCache == false` |
| `AllowSession_IsAllowedAndShouldCache_True` | `new HitlDecision(HitlChoice.AllowSession)` | `IsAllowed == true`, `ShouldCache == true` |
| `AllowPermanent_ShouldCache_True` | `new HitlDecision(HitlChoice.AllowPermanent)` | `ShouldCache == true` |
| `Deny_IsAllowed_False` | `HitlDecision.Deny("原因")` | `IsAllowed == false`, `Reason == "原因"` |
| `Deny_StaticFactory_SetsReason` | `HitlDecision.Deny("拒绝")` | `Choice == Deny`, `Reason == "拒绝"` |

### 8.2 `HitlPromptTests`（新增，用假 render/readKey）

| 用例 | 操作 | 期望 |
| --- | --- | --- |
| `RequestAsync_ReadKeyA_ReturnsAllowOnce` | 注入 readKey 返回 A 键 | `Choice == AllowOnce` |
| `RequestAsync_ReadKeyS_ReturnsAllowSession_AndCaches` | readKey 返回 S | `Choice == AllowSession`, `IsAllowedThisSession("x") == true` |
| `RequestAsync_ReadKeyD_ReturnsDenyWithReason` | readKey 返回 D | `Choice == Deny`, `Reason 含"拒绝"` |
| `RequestAsync_CacheHit_DoesNotPrompt` | 先 S 允许 → 再 RequestAsync | 不调 readKey，返回 AllowSession |
| `RequestAsync_CancelledToken_ReturnsDeny` | ct 已取消 | 返回 Deny("已取消")，不调 readKey |
| `RequestAsync_ReadKeyP_CachesAsSession` | readKey 返回 P | `Choice == AllowPermanent`, `IsAllowedThisSession` 命中（退化） |
| `RequestAsync_RendersPromptAndResult` | 收集 render 回调 | 先渲染提示 Panel，后渲染结果 Markup |
| `IsAllowedThisSession_NotCached_ReturnsFalse` | 新建 HitlPrompt | `false` |

### 8.3 `NullHitlGateTests`（新增）

| 用例 | 操作 | 期望 |
| --- | --- | --- |
| `RequestAsync_ReturnsNull` | 调 RequestAsync | 返回 null |
| `IsAllowedThisSession_AlwaysFalse` | 调 IsAllowedThisSession | false |

### 8.4 `EventRendererTests`（新增）

| 用例 | 操作 | 期望 |
| --- | --- | --- |
| `Render_RoundStart_SetsCurrentRound` | Render(RoundStartEvent(3)) | `CurrentRound == 3`, 返回 Markup 含 "Round 3" |
| `Render_TextDelta_AccumulatesToBuffer` | Render(TextDelta("foo")) → Render(TextDelta("bar")) | `CurrentText == "foobar"`, 返回 null |
| `Render_ToolCallStart_AddsToPending` | Render(ToolCallStartEvent) | `_pending.Count == 1` |
| `Render_ToolResultSuccess_AddsPanel` | Render(ToolResultEvent with Ok) | `_pending` 含 Panel |
| `Render_ToolResultFail_AddsRedPanel` | Render(ToolResultEvent with Fail) | Panel 颜色红 |
| `Render_ToolBlocked_AddsRedPanel` | Render(ToolBlockedEvent) | `_pending` 含 Panel，Header 含 "HITL" |
| `Render_AgentDone_ReturnsMarkup` | Render(AgentDoneEvent) | 返回 Markup 含 "完成" |
| `Render_MaxRounds_ReturnsYellowMarkup` | Render(MaxRoundsReachedEvent(10)) | Markup 含 "最大轮次" |
| `Render_Error_ReturnsRedMarkup` | Render(ErrorEvent("x", null)) | Markup 含 "错误" |
| `Render_Cancelled_ReturnsGreyMarkup` | Render(CancelledEvent) | Markup 含 "已取消" |
| `Reset_ClearsBufferAndPending` | Reset() | `CurrentText == ""`, `_pending.Count == 0` |
| `BuildActive_IncludesStatusBarAndText` | 累积文本后 BuildActive(statusBar) | Rows 含 statusBar.Render() + Text |
| `BuildCommitted_ExcludesStatusBar` | BuildCommitted() | Rows 不含状态栏 |
| `Reset_ClearsRoundCounter` | Reset() 后 CurrentRound | `== 0` |

### 8.5 `StatusBarTests`（新增）

| 用例 | 操作 | 期望 |
| --- | --- | --- |
| `Render_ContainsAllFields` | 设各字段后 Render | Markup 含 Provider/Model/security/ctx/round/tools |
| `ContextRatio_Below70_Green` | tokens=1000, window=64000 | ratio < 0.7 |
| `ContextRatio_Over70_Yellow` | tokens=50000, window=64000 | 0.7 <= ratio < 0.9 |
| `ContextRatio_Over90_Red` | tokens=60000, window=64000 | ratio >= 0.9 |
| `ContextWindow_Zero_RatioZero` | ContextWindowTokens=0 | ratio == 0 |
| `SecurityLevel_Strict_RedColor` | Level=Strict | 渲染含 "red" |
| `SecurityLevel_Normal_YellowColor` | Level=Normal | 渲染含 "yellow" |

### 8.6 `InputReaderTests`（新增，补全逻辑）

| 用例 | 操作 | 期望 |
| --- | --- | --- |
| `Complete_UniquePrefix_FillsFull` | 输入 `/cl` + Tab | buf == "/clear" |
| `Complete_MultipleMatches_ListsOptions` | 输入 `/` + Tab | 输出含 /clear /exit 等 |
| `Complete_NoSlashPrefix_NoCompletion` | 输入 `foo` + Tab | buf 不变 |
| `Complete_NonExistentCommand_NoMatch` | 输入 `/xyz` + Tab | 无补全 |
| `Backspace_RemovesLastChar` | 输入 `/cle` + Backspace | buf == "/cl" |
| `Enter_ReturnsBuffer` | 输入 `/clear` + Enter | 返回 "/clear" |
| `Escape_ReturnsNull` | 按 Esc | 返回 null |

### 8.7 `BatchToolExecutorHitlTests`（新增）

| 用例 | 操作 | 期望 |
| --- | --- | --- |
| `ExecuteAsync_WriteTool_PromptsHitl` | 注入假 IHitlGate 记录调用 | RequestAsync 被调一次 |
| `ExecuteAsync_HitlDeny_ReturnsFail` | 假 gate 返回 Deny | 对应位置 ToolResult.Fail("用户拒绝") |
| `ExecuteAsync_HitlDeny_DoesNotExecute` | 假 gate Deny | ToolExecutor.ExecuteAsync 未被调 |
| `ExecuteAsync_HitlAllow_Executes` | 假 gate AllowOnce | ToolExecutor 被调，返回 Ok |
| `ExecuteAsync_ReadTool_NoHitl` | ReadFileTool + 假 gate | RequestAsync 未被调（Read 不问） |
| `ExecuteAsync_CacheHit_NoPrompt` | 先 AllowSession → 再调同工具 | 第二次 RequestAsync 返回 AllowSession 不调 Prompt |
| `ExecuteAsync_HitlNull_GateSkipped` | hitlGate=null | 直接执行（等价迭代 6） |
| `ExecuteAsync_CancelledToken_HitlReturnsDeny` | ct 取消 + 假 gate | gate 返回 Deny，结果 Fail |

### 8.8 `ConsoleEventRendererTests`（新增，降级路径）

| 用例 | 操作 | 期望 |
| --- | --- | --- |
| `Render_TextDelta_WritesToConsole` | 注入假 Console + TextDelta | 输出含 text |
| `Render_ToolBlocked_WritesRedLine` | ToolBlockedEvent | 输出含 "被拦截" |
| `Render_AgentDone_WritesNewline` | AgentDoneEvent | 末尾换行 |
| `Render_Error_WritesRedError` | ErrorEvent | 输出含 "错误" |

### 8.9 `TuiAppIntegrationTests`（端到端）

| 用例 | 操作 | 期望 |
| --- | --- | --- |
| `EndToEnd_NoTool_LiveRendersText` | MockProvider 脚本只返回 TextDelta | Live 渲染文本，提交后控制台含文本 |
| `EndToEnd_WriteTool_HitlPrompt` | 脚本调 write_file + 假 readKey 返回 A | HITL 提示渲染，工具执行，AgentDone |
| `EndToEnd_WriteTool_Deny` | 脚本调 write_file + 假 readKey 返回 D | ToolBlockedEvent 渲染，LLM 收到拒绝原因 |
| `EndToEnd_SessionCache_SecondWriteNoPrompt` | 两次 write_file，第一次 S | 第二次不弹 HITL |
| `EndToEnd_StatusBar_UpdatesRound` | 多轮脚本 | 状态栏 round 字段更新 |
| `EndToEnd_TuiModeConsole_UsesConsoleRenderer` | tui.mode=console | 不用 Live，行模式渲染 |
| `EndToEnd_CancelledEvent_Renders` | 脚本触发取消 | 渲染 "已取消" |

### 8.10 回归

- `dotnet test` 全绿（含迭代 1-6 既有 + 迭代 7 新增 9 个测试文件）。
- `dotnet run`（`tui.mode: console` + `enable_hitl: false`）行为与迭代 6 一致。
- `AgentLoopTests` / `BatchToolExecutorTests` 既有用例全绿（`IHitlGate?` 可选，旧测试不传等价旧行为）。
- `/clear` / `/exit` 行为保持。

## 九、验收标准

> 全部满足方可标记 `[已完成]`。每条尽量可被命令或肉眼验证。

### 9.1 构建与运行

- [ ] `ParrotCode.Net.csproj` 无改动（零新依赖，Spectre.Console 已在迭代 1 引入）。
- [ ] `dotnet build` 无 error、无 warning。
- [ ] `dotnet test` 全绿（含迭代 1-6 既有 + 迭代 7 新增 9 个测试文件）。
- [ ] `dotnet run`（`active_provider: mock`）能启动，TUI 启动横幅正常（显示 TUI 模式 + 安全等级 + 工具数）。

### 9.2 HITL 决策模型

- [ ] `Tui/HitlDecision.cs` 定义 `HitlChoice` 枚举（`AllowOnce`/`AllowSession`/`AllowPermanent`/`Deny`）+ `HitlDecision` record。
- [ ] `IsAllowed` / `ShouldCache` 属性正确。
- [ ] `HitlDecision.Deny(reason)` 静态工厂设置 Reason。
- [ ] `HitlDecisionTests` 5 个用例全绿。

### 9.3 IHitlGate + HitlPrompt

- [ ] `Tui/IHitlGate.cs` 定义接口（`RequestAsync` + `IsAllowedThisSession`）+ `NullHitlGate`。
- [ ] `NullHitlGate.RequestAsync` 返回 null（等价无 HITL）。
- [ ] `Tui/HitlPrompt.cs` 实现 `IHitlGate`：用 render/readKey 回调（方案 C，不暂停 Live）。
- [ ] A/S/P/D 四键映射正确。
- [ ] `AllowSession`/`AllowPermanent` 记入会话缓存，`IsAllowedThisSession` 命中。
- [ ] 缓存命中时不弹提示，直接返回 AllowSession。
- [ ] 取消时返回 Deny("已取消")。
- [ ] `HitlPromptTests` 8 个用例全绿。
- [ ] `NullHitlGateTests` 2 个用例全绿。

### 9.4 BatchToolExecutor HITL 接入

- [ ] `BatchToolExecutor` 构造加可选参数 `IHitlGate? hitlGate = null`。
- [ ] Write 组执行前调 `hitlGate.RequestAsync`（hitlGate 非 null 时）。
- [ ] Read 组不调 HITL。
- [ ] Deny 时返回 `ToolResult.Fail`，不执行工具。
- [ ] Allow 时正常执行。
- [ ] `OnBeforeExecuteAsync` 虚方法预留（默认返回 null，迭代 8 接入）。
- [ ] hitlGate 为 null 时等价迭代 6（直接执行）。
- [ ] `BatchToolExecutorHitlTests` 8 个用例全绿。
- [ ] 旧 `BatchToolExecutorTests` 既有用例全绿（无回归）。

### 9.5 AgentLoop HITL 拒绝转发

- [ ] HITL 拒绝（`ToolResult.Fail` 含"用户拒绝"）时 emit `ToolBlockedEvent`。
- [ ] 工具自身失败时 emit `ToolResultEvent`（非 `ToolBlockedEvent`）。
- [ ] 拒绝原因回灌历史（`history.AddTool("错误：用户拒绝...", call.Id)`）。
- [ ] `AgentLoopTests` 既有用例全绿（无 HITL 场景不产生 `ToolBlockedEvent`）。

### 9.6 EventRenderer

- [ ] `Tui/EventRenderer.cs` 定义 `Render(AgentEvent) → IRenderable?` + `BuildActive`/`BuildCommitted`/`Reset`。
- [ ] 12 种事件类型全覆盖（含 `ToolBlockedEvent`）。
- [ ] `TextDeltaEvent` 累积到 `_textBuf`。
- [ ] `ToolCallStartEvent`/`ToolResultEvent`/`ToolBlockedEvent` 加 Panel 到 `_pending`。
- [ ] `BuildActive` 含状态栏 + 轮次 + 文本 + 工具卡片。
- [ ] `BuildCommitted` 不含状态栏。
- [ ] `Reset` 清空缓冲与轮次。
- [ ] `EventRendererTests` 14 个用例全绿。

### 9.7 StatusBar

- [ ] `Tui/StatusBar.cs` 定义状态栏组件。
- [ ] 显示 Provider/Model/安全等级/上下文占比/当前轮次/工具数。
- [ ] 占比颜色：绿(<70%)/黄(70-90%)/红(>90%)。
- [ ] `SecurityLevel` 颜色映射正确。
- [ ] `StatusBarTests` 7 个用例全绿。

### 9.8 InputReader + Tab 补全

- [ ] `Tui/InputReader.cs` 实现 `ReadLineWithCompletionAsync`。
- [ ] `/` 开头 Tab 补全：唯一匹配填充，多匹配列选项。
- [ ] Enter 提交，Esc 取消，Backspace 删除。
- [ ] 非 `/` 开头 Tab 无效。
- [ ] `InputReaderTests` 7 个用例全绿。

### 9.9 ConsoleEventRenderer（降级）

- [ ] `Tui/ConsoleEventRenderer.cs` 实现（迭代 6 抽取）。
- [ ] 12 种事件全覆盖（含 `ToolBlockedEvent`）。
- [ ] 行为与迭代 6 `App.RenderEventsAsync` 一致。
- [ ] `ConsoleEventRendererTests` 4 个用例全绿。

### 9.10 TuiApp

- [ ] `Tui/TuiApp.cs` 实现主循环（Live + 状态栏 + 事件消费 + 输入）。
- [ ] Live 流式渲染活跃区，`IsCompletingEvent` 时提交到滚动历史。
- [ ] 状态栏轮次实时更新。
- [ ] HITL 接线（`HitlPrompt` 的 render/readKey 回调注入）。
- [ ] `/clear` / `/exit` / `/help` / `/status` 硬编码分发。
- [ ] `TuiAppIntegrationTests` 7 个用例全绿。

### 9.11 IUiControl + SecurityLevel

- [ ] `Tui/IUiControl.cs` 定义接口（`PrintMessageAsync`/`SetStatus`/`RequestHitlAsync`）。
- [ ] `Tui/SecurityLevel.cs` 定义枚举（`Strict`/`Normal`/`Permisive`）。
- [ ] `SecurityLevel` 状态栏显示正确。

### 9.12 端到端 TUI 体验（核心验收）

- [ ] **mock 模式**：`dotnet run`，输入"你好"，Live 渲染"你好（mock）"，完成后提交到滚动历史。
- [ ] **流式渲染不闪烁**：DeepSeek 真实模式，流式输出 token，Live 原地刷新无闪烁。
- [ ] **状态栏**：顶部显示 provider/model/security/ctx%/round/tools，轮次随 ReAct 更新。
- [ ] **上下文占比**：长对话后占比变黄/红。
- [ ] **HITL 弹框**：让 AI 调 `write_file`，看到 HITL 提示卡片。
- [ ] **A 键允许本次**：按 A，工具执行，下次同工具再问。
- [ ] **S 键允许会话**：按 S，同会话同工具不再问。
- [ ] **D 键拒绝**：按 D，看到 `ToolBlockedEvent` 红色卡片，AI 回复"好的，我不执行"。
- [ ] **Tab 补全**：输入 `/c` + Tab 补全为 `/clear`。
- [ ] **降级模式**：`tui.mode: console`，行为与迭代 6 一致（无 Live/状态栏）。
- [ ] **Ctrl+C**：中断长任务，渲染"已取消"，程序不崩溃。

### 9.13 App 接入与装配

- [ ] `App/App.cs` 改为委托 `TuiApp`（或 `ConsoleApp` 降级）。
- [ ] `Program.cs` 终端能力检测（`Console.IsOutputRedirected` / `Environment.UserInteractive`）决定 Live/降级。
- [ ] `Program.cs` 装配 `IHitlGate` / `TuiApp` / `SecurityLevel` 注入。
- [ ] 非 Live 模式自动降级到 `ConsoleEventRenderer` + `NullHitlGate`。

### 9.14 配置

- [ ] `Config/Models.cs` 加 `TuiConfig`（Mode/ShowStatusBar/EnableHitl/ContextWindowTokens）+ `SecurityConfig`（Level）。
- [ ] `AppConfig.Tui` / `AppConfig.Security` 字段。
- [ ] `example.parrotcode.yaml` 加 `tui:` / `security:` 节示例。
- [ ] ConfigLoader 解析新节（缺失时 null，App 用默认值）。
- [ ] 配置项可被覆盖（如 `enable_hitl: false` 生效）。

### 9.15 异常与边界

- [ ] 终端非交互（重定向/CI）→ 降级到 `ConsoleEventRenderer`，不崩。
- [ ] Spectre.Console 不支持 ANSI → Live 自动降级（Spectre 内置）。
- [ ] HITL 期间 Ctrl+C → 返回 Deny("已取消")，Agent 优雅停止。
- [ ] Live 期间 Ctrl+C → `ct` 取消，Live 退出，emit `CancelledEvent`。
- [ ] 工具参数含 `[` 破坏 Markup → `Markup.Escape` 转义。
- [ ] 一轮内 10+ 工具卡片 → `BuildActive` 限制显示最近 5 个 + "...还有 N 个"。
- [ ] 状态栏字段超长 → `Truncate` 截断。

### 9.16 敏感信息

- [ ] 状态栏不显示 ApiKey。
- [ ] HITL 提示不泄露 ApiKey（工具参数可能含路径，不含 key）。
- [ ] 日志不出现 ApiKey（沿用迭代 6）。

### 9.17 跨平台

- [ ] Windows 上 `dotnet test` 全绿。
- [ ] macOS / Linux 上 `dotnet test` 全绿。
- [ ] Live 在三平台正常渲染（Spectre.Console 跨平台）。
- [ ] `InputReader.ReadKey` 在三平台行为一致。
- [ ] 降级模式在三平台一致（`Console.IsOutputRedirected` 跨平台）。

### 9.18 迁移与回归

- [ ] `BatchToolExecutor` 旧构造函数签名兼容（`IHitlGate?` 可选）。
- [ ] `AgentLoop` 旧测试全绿（无 HITL 场景）。
- [ ] `ChannelEventSink` / `IAgentEventSink` **不变**。
- [ ] 迭代 6 的 12 种事件类型**不变**（`ToolBlockedEvent` 启用但不改签名）。
- [ ] `tui.mode: console` + `enable_hitl: false` 时行为与迭代 6 完全一致。
- [ ] 迭代 1-6 的所有测试**全绿**（无回归）。

## 十、进阶练习（可选，不计入验收）

1. **AlternateScreen 全屏**：用 Spectre.Console 的 `IAnsiConsole.Console.Profile.Capabilities.AltScreen` 切换到备用屏幕，固定布局（状态栏顶 + 输出区滚动 + 输入行底）。退出时恢复主屏幕。

2. **thinking 折叠渲染**：解析 DeepSeek `reasoning_content` 字段，用 `Collapsible` 渲染灰色折叠的思考过程，默认折叠，按 T 展开。

3. **A/S/P/D 快捷键**：`HitlPrompt` 不用 `SelectionPrompt`，改用 `Console.ReadKey` 直接读 A/S/P/D 键（方案 C 已支持，本迭代可强化为不区分大小写 + 数字键备选）。

4. **输入历史**：↑↓ 浏览历史输入（存内存 `List<string>`），跨会话持久化在迭代 10。

5. **多行输入**：Shift+Enter 换行，Enter 提交。需检测 `ConsoleModifiers.Shift`。

6. **工具调用进度条**：`run_command` 长时间执行时，Live 显示进度条或旋转 spinner。

7. **`AllowPermanent` 持久化**：写入 `.parrotcode/hitl_decisions.json`，跨会话加载。迭代 10 配置文件支持后接入。

8. **`/mode` 命令**：`/mode strict|normal|permissive` 切换安全等级（通过 `IUiControl.SetStatus`），状态栏实时更新。迭代 10 命令系统接入。

9. **HITL 预览**：HITL 提示卡片显示工具调用的"diff 预览"（如 `write_file` 显示将写入的内容，`edit_file` 显示 old→new diff）。需工具实现 `PreviewAsync` 方法。

10. **主题切换**：`/theme dark|light` 切换 Spectre.Console 颜色主题。

## 十一、风险与注意事项

| 风险 | 缓解 |
| --- | --- |
| Spectre.Console `Live` 与 `Prompt` 互斥导致 HITL 弹框困难 | 采用方案 C：HITL 作为 Live 渲染目标的一部分 + `Console.ReadKey` 读键，不暂停 Live。`HitlPrompt` 用 render/readKey 回调而非 `AnsiConsole.Prompt` |
| `LiveDisplayContext` 无显式 Stop/Restart API | 方案 C 下无需暂停 Live。提交到滚动历史用 `AnsiConsole.Write`（Live 区域外），Live 继续运行。实现时验证 Live 区域与 `AnsiConsole.Write` 的滚动行为 |
| 终端不支持 ANSI 导致 Live 异常 | `Console.IsOutputRedirected` / `Environment.UserInteractive` 检测，降级到 `ConsoleEventRenderer`。Spectre.Console 内置 ANSI 检测也会降级 |
| HITL 期间 Ctrl+C 卡在 `ReadKey` | `ReadKey` 包 `Task.Run` + ct，ct 取消时 `Task` 抛 `OperationCanceledException`，`HitlPrompt` 捕获返回 Deny("已取消") |
| 会话缓存导致后续危险操作不问 | `AllowSession` 仅对工具名缓存，不含参数。`run_command` 即使会话允许，每次不同命令仍可见（但本迭代不区分参数，是简化）。迭代 8 精细化 |
| `AllowPermanent` 退化为会话级 | 文档明确，迭代 10 接入配置文件后持久化。本迭代用户按 P 与 S 行为一致 |
| `Markup.Escape` 漏转义导致渲染崩溃 | 所有动态内容（工具名/参数/结果/错误）一律 `Markup.Escape`。单测验证含 `[` / `]` 的内容 |
| 一轮内工具卡片过多撑满屏 | `BuildActive` 限制 `_pending` 显示最近 5 个 + "...还有 N 个" |
| 状态栏字段过长换行 | `Truncate` 截断 Provider/Model 名到 20 字符 |
| `Console.ReadKey` 在重定向 stdin 时抛异常 | 降级模式用 `Console.ReadLine`，不用 `InputReader`。检测 `Console.IsInputRedirected` |
| Live 刷新频率过高导致 CPU 占用 | `ctx.Update` 节流（如每 50ms 最多刷新一次），或仅在新 token 到达时刷新 |
| 多轮 ReAct 后 Live 活跃区内容堆积 | `IsCompletingEvent` 提交后 `Reset`，每轮活跃区从空开始 |
| `EventRenderer` 状态在多轮间泄漏 | `Reset` 在 `RoundStartEvent` 与提交后都调，确保清空 |
| `HitlPrompt` 的 render/readKey 回调在测试中难注入 | 测试用构造函数注入假 `Action<IRenderable>` / `Func<CancellationToken, ConsoleKey>`，不依赖真实控制台 |
| `BatchToolExecutor` 的 `IHitlGate?` 破坏旧测试 | 可选参数默认 null，旧测试不传等价旧行为。`BatchToolExecutorTests` 全绿验证 |
| `AgentLoop.IsHitlDenial` 字符串匹配不严谨 | 文档标注为启发式，迭代 8 用 `ToolResult.Blocked` 标志或派生类型精细化 |
| `SecurityLevel.Permisive` 拼写 | 沿用 plan.md 拼写，实现时建议用 `Permissive`，本设计文档保留以暴露偏差 |
| TUI 测试难断言（Spectre.Console 输出） | `EventRenderer`/`StatusBar` 是纯逻辑可单测（断言 IRenderable 类型与内容）。`TuiApp` 端到端用 MockProvider 脚本 + 假 render/readKey 回调，断言事件流而非控制台像素 |
| `Live` 在某些终端（如 Windows CMD 旧版）闪烁 | Spectre.Console 0.49.1 已优化 Windows Terminal / CMD。若旧 CMD 闪烁，降级到 console 模式 |

## 十二、交付清单

### 12.1 新增源文件

- [ ] `ParrotCode.Net/Tui/IHitlGate.cs`（HITL 双向通道接口 + NullHitlGate）
- [ ] `ParrotCode.Net/Tui/HitlDecision.cs`（决策 record + Choice 枚举）
- [ ] `ParrotCode.Net/Tui/HitlPrompt.cs`（Spectre 实现，方案 C）
- [ ] `ParrotCode.Net/Tui/TuiApp.cs`（主 TUI 应用）
- [ ] `ParrotCode.Net/Tui/EventRenderer.cs`（事件 → IRenderable 翻译）
- [ ] `ParrotCode.Net/Tui/StatusBar.cs`（状态栏组件）
- [ ] `ParrotCode.Net/Tui/InputReader.cs`（带 Tab 补全输入读取）
- [ ] `ParrotCode.Net/Tui/ConsoleEventRenderer.cs`（降级行模式渲染）
- [ ] `ParrotCode.Net/Tui/IUiControl.cs`（UI 抽象接口预留）
- [ ] `ParrotCode.Net/Tui/SecurityLevel.cs`（安全等级枚举占位）

### 12.2 修改源文件

- [ ] `ParrotCode.Net/Agent/BatchToolExecutor.cs`（注入 IHitlGate? + OnBeforeExecuteAsync hook）
- [ ] `ParrotCode.Net/Agent/AgentLoop.cs`（HITL 拒绝转 ToolBlockedEvent）
- [ ] `ParrotCode.Net/App/App.cs`（委托 TuiApp/ConsoleApp）
- [ ] `ParrotCode.Net/Program.cs`（终端检测 + 装配 IHitlGate/TuiApp/SecurityLevel）
- [ ] `ParrotCode.Net/Config/Models.cs`（TuiConfig + SecurityConfig）
- [ ] `ParrotCode.Net/example.parrotcode.yaml`（tui/security 节示例）

### 12.3 新增测试文件

- [ ] `ParrotCode.Net-xUnit/HitlDecisionTests.cs`
- [ ] `ParrotCode.Net-xUnit/HitlPromptTests.cs`
- [ ] `ParrotCode.Net-xUnit/NullHitlGateTests.cs`
- [ ] `ParrotCode.Net-xUnit/EventRendererTests.cs`
- [ ] `ParrotCode.Net-xUnit/StatusBarTests.cs`
- [ ] `ParrotCode.Net-xUnit/InputReaderTests.cs`
- [ ] `ParrotCode.Net-xUnit/BatchToolExecutorHitlTests.cs`
- [ ] `ParrotCode.Net-xUnit/ConsoleEventRendererTests.cs`
- [ ] `ParrotCode.Net-xUnit/TuiAppIntegrationTests.cs`

### 12.4 演示与验收

- [ ] 演示：mock 模式 Live 流式渲染"你好（mock）"，提交到滚动历史。
- [ ] 演示：DeepSeek 真实模式，让 AI 调 `write_file`，看到 HITL 提示卡片。
- [ ] 演示：按 D 拒绝 `write_file`，AI 回复"好的，我不执行"（验证 HITL 闭环）。
- [ ] 演示：按 S 允许会话，第二次 `write_file` 不再弹 HITL（验证缓存）。
- [ ] 演示：状态栏显示 provider/model/security/ctx%/round/tools，轮次随 ReAct 更新。
- [ ] 演示：长对话后上下文占比变黄/红（验证占比计算）。
- [ ] 演示：输入 `/c` + Tab 补全为 `/clear`（验证 Tab 补全）。
- [ ] 演示：`tui.mode: console` 降级模式行为与迭代 6 一致。
- [ ] 演示：Ctrl+C 中断长任务，渲染"已取消"程序不崩溃。

## 十三、实现顺序建议

为降低集成风险，建议按以下顺序分步实现（每步可单独编译验证）：

1. **决策模型与接口**：`HitlDecision` + `HitlChoice` + `IHitlGate` + `NullHitlGate` + `SecurityLevel`。先建立类型契约，无逻辑。
2. **`HitlPrompt`**：`HitlPrompt`（方案 C，render/readKey 回调）+ `HitlPromptTests`（用假回调）。独立可测，不依赖 Live。
3. **`BatchToolExecutor` 改造**：注入 `IHitlGate?` + Write 组请求决策 + `OnBeforeExecuteAsync` hook + `BatchToolExecutorHitlTests`。验证 HITL 接入，旧测试不回归。
4. **`AgentLoop` 改造**：`IsHitlDenial` + `ToolBlockedEvent` 转发。旧 `AgentLoopTests` 全绿验证。
5. **`EventRenderer` + `StatusBar`**：纯渲染逻辑 + 单测。不依赖 Live。
6. **`InputReader`**：Tab 补全 + 单测。不依赖 Live。
7. **`ConsoleEventRenderer`**：抽取迭代 6 渲染逻辑 + 单测。降级路径先就绪。
8. **`IUiControl`**：定义接口（最小子集）。
9. **`TuiApp`**：装配 Live + 状态栏 + 事件消费 + 输入 + HITL 接线 + `TuiAppIntegrationTests`。核心集成。
10. **App/Program 接入**：改 `App.cs` 委托 + `Program.cs` 终端检测与装配 + `Config/Models.cs` 扩展 + `example.parrotcode.yaml`。
11. **端到端验收**：`dotnet test` 全绿 + mock 模式 Live 渲染 + DeepSeek 真实模式 HITL + 降级模式回归。

> 每步完成后 `dotnet build` 应无 error。步骤 1-8 完成后既有功能不回归（旧 App 仍可用，`BatchToolExecutor` 旧测试全绿）。步骤 9-10 切换 App 到 TuiApp 后，`tui.mode: console` 降级路径保留迭代 6 行为。

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

### A.2 HITL 提示卡片

```
┌─ HITL 确认 ──────────────────────────────────────┐
│ ⚠ 即将执行 write_file({"path":"D:\\tmp\\hello.txt","content":"你好"})│
│ 按 A=本次 S=会话 P=永久 D=拒绝                    │
└──────────────────────────────────────────────────┘
[用户按 S]
✓ 已允许（AllowSession）
```

### A.3 拦截卡片（拒绝）

```
┌─ ⛔ HITL 拦截 ───────────────────────────────────┐
│ ✗ 被拦截 run_command                              │
│ 用户拒绝执行该工具                                 │
└──────────────────────────────────────────────────┘
```

### A.4 降级模式（console）

```
> 在 D:\tmp 创建 hello.txt
你：在 D:\tmp 创建 hello.txt
AI：
→ write_file({"path":"D:\\tmp\\hello.txt","content":"你好"})
[行模式无 HITL——enable_hitl: false 时]
✓ 已写入
已创建 hello.txt
>
```

## 附录 B：HITL 交互时序

```
用户输入 "创建 hello.txt"
    │
    ▼
TuiApp: history.AddUser → agentLoop.RunAsync → Live.StartAsync
    │
    ▼
AgentLoop 第 1 轮: LLM 流式 → tool_call write_file
    │
    ▼
BatchToolExecutor.ExecuteAsync:
    write_file 是 Write → hitlGate.RequestAsync(call, ct)
    │
    ▼
HitlPrompt.RequestAsync:
    缓存未命中 → render(提示Panel) → Live 活跃区显示 "⚠ 即将执行..."
    读按键（await）→ 用户按 S
    _sessionCache["write_file"] = 0
    render(✓已允许) → Live 活跃区追加 "✓ 已允许（AllowSession）"
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
TuiApp: IsCompletingEvent(AgentDone) → liveRef.Stop → AnsiConsole.Write(committed) → renderer.Reset → liveRef.Restart
    Live 活跃区清空，内容进入滚动历史
    │
    ▼
回到输入循环: await inputReader.ReadLineWithCompletionAsync
```

时序要点：
1. **HITL 同步阻塞**：`BatchToolExecutor` 在 `await hitlGate.RequestAsync` 处阻塞，AgentLoop 暂停，直到用户按键。
2. **Live 不暂停**：方案 C 下 Live 持续运行，HITL 提示作为活跃区一部分渲染，`Console.ReadKey` 读输入不干扰 Live 输出。
3. **缓存生效**：第二次 `write_file` 时 `IsAllowedThisSession` 命中，`RequestAsync` 直接返回 AllowSession，不弹提示。
4. **提交时机**：`AgentDoneEvent` 触发提交，整轮内容（文本 + 工具卡片 + HITL 决策）进入滚动历史。

---

> 本文档到此结束。`plan.md` 的迭代 7 条目可标记为「设计完成，待实现」。实现完成后将本文件头部状态改为 `[已完成]`。
