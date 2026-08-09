# 迭代 10：斜杠命令 + 会话持久化（JSONL）+ 项目指令 — 总览

> **状态**：[设计完成，待实现]
> **前置迭代**：9 [已完成]（上下文管理）、8c [已完成]（安全纵深防御）、7c-3 [已完成]（TUI + HITL）
> **后续迭代**：11（MCP 协议客户端）
> **对应 `plan.md` 第三章「迭代 10」**，本文档为总览，保留用于追溯整体设计与跨子迭代决策。
> **参考**：MewCode `commands/` / `storage/` / `instructions/` 模块。
>
> **本文档为总览**，实施拆分为三个子迭代（各自含独立验收标准）：
> - [iter-10a-design.md](iter-10a-design.md)：命令系统骨架（Registry/Parser/Dispatcher/IUiControl + 6 个内置命令，`/session` 用 stub）
> - [iter-10b-design.md](iter-10b-design.md)：JSONL 会话持久化（SessionStore + MessageDto + `/session` 接入真实 Store）
> - [iter-10c-design.md](iter-10c-design.md)：项目指令（InstructionLoader + `@include` + AgentLoop 注入 + 端到端装配）
>
> **拆分理由**：三大子系统耦合度低（唯一耦合点是 `/session` 命令调用 `SessionStore`），可独立交付价值与验收。10a 完成即可用 `/mode` `/compress` `/status`；10b 验证"退出后恢复"核心目标；10c 验证项目指令注入。详见各子迭代文档。
>
> **子迭代依赖顺序**：10a → 10b（依赖 10a 的 `/session` 命令骨架）→ 10c（独立，但建议最后做以收尾端到端）。

---

## 一、概述

迭代 7c-3 的 `TerminalApp.HandleUserInput` 用 `if (line is "/exit" or "/quit")` 硬编码三个命令（`/exit` `/clear` `/help`），无法扩展。迭代 9 的 `ContextCompressor` 预留了 `ResetWarning` / `ResetCircuit` 供 `/clear` `/compress` 调用，但本迭代才真正接入。会话历史目前是内存版——退出即丢失，无法恢复。Agent 不知道项目约定（编码规范、测试命令、架构约束），每次都从零理解。

迭代 10 构建三大子系统：

1. **斜杠命令系统（`Commands/`）**：注册中心 + 解析器 + 分发器 + 内置命令。`/` 前缀走命令，否则走 AI。命令用反射自动扫描 `ICommand` 实现类注册，避免手写 register 列表。内置 7 个命令：`/help` `/clear` `/compress` `/mode` `/status` `/session` `/exit`。
2. **JSONL 会话持久化（`Storage/SessionStore.cs`）**：每行一条消息的 JSON，文件末尾追加写（O(1)）。Meta 文件存会话概要（ID、时间、消息数、provider）。崩溃恢复：逐行解析，损坏行跳过并记日志；未配对的 `tool_use`（缺 `tool_result`）截断到最后一个完整状态。恢复时距今 > 30 分钟提示"这是 X 小时前的会话"。
3. **项目指令加载（`Instructions/Loader.cs`）**：三级目录扫描（全局 `~/.parrocode/instructions.md` → 项目 `./PARROTCODE.md` → 本地 `./.parrotcode/instructions.md`）+ `@include path/to/file.md` 嵌套（限 3 层防无限递归）。指令注入 system prompt，每轮重新拼装，不受压缩影响。

本迭代**刻意保持**：
- **不做 Skill 系统的 `/skill` 命令**：迭代 12。本迭代 `/help` 列出的命令都是系统内置命令。
- **不做 `AllowPermanent` 跨会话持久化**：HITL 的"永久允许"本迭代仍退化为会话级（`AllowSession`）。跨会话持久化涉及安全配置文件读写，放迭代 12 Hook 引擎或后续。
- **不做会话自动恢复**：启动时不自动加载上次会话，需用户主动 `/session load <id>`。自动恢复涉及"上次会话 ID"的持久化与冲突处理，留作进阶练习。
- **不做多会话并发**：同一时刻只有一个活跃会话。`/session load` 会清空当前历史再加载（提示用户先 `/session save`）。
- **不改 `IBaseProvider` / `AgentLoop` 核心循环**：命令系统在 `TerminalApp` 层拦截，不侵入 Agent。仅 `AgentLoop` 的 system prompt 来源改为可注入（支持项目指令）。
- **不做斜杠命令的 Tab 补全增强**：`InputFieldView` 已有 Tab 补全（硬编码 5 个命令），本迭代改为从 `CommandRegistry` 动态获取命令名列表。

> **拆分决策**：本迭代拆分为三个子迭代（10a/10b/10c），各自含独立验收标准。
> - **拆分理由**：三大子系统耦合度低——项目指令完全独立（只读文件+注入prompt）；SessionStore 只依赖既有 `Message` record；唯一真实耦合点是 `/session` 命令调用 `SessionStore`（10a 用 stub 解耦）。
> - **独立交付价值**：10a 完成即可用 `/mode` `/compress` `/status` 等命令；10b 验证"退出后恢复"核心目标；10c 验证项目指令注入。每个核心目标都有独立验收关卡。
> - **依赖顺序**：10a → 10b（依赖 10a 的 `/session` 命令骨架）→ 10c（独立，建议最后做以收尾端到端）。
> - 详见各子迭代文档：[iter-10a](iter-10a-design.md) / [iter-10b](iter-10b-design.md) / [iter-10c](iter-10c-design.md)。

---

## 二、学习目标

1. **斜杠命令系统设计**：用户与 Agent 的元交互入口。理解"命令 vs 对话"的分发边界——`/` 前缀走命令系统（同步、无 LLM），否则走 AgentLoop（异步、有 LLM）。体会注册中心 + 解析器 + 分发器的三段式架构，以及反射自动注册避免手写 register 列表的好处。
2. **JSONL 持久化模式**：O(1) 追加写（每条消息一行 JSON，`FileStream.Append`）、崩溃恢复（逐行解析、损坏行跳过）、Meta 文件分离（会话概要与消息内容解耦）。理解为什么用 JSONL 而非 JSON 数组——追加写不需要重写整个文件，崩溃时只丢最后一行。
3. **tool_use / tool_result 配对修复**：OpenAI 协议要求 `tool` 消息按 `tool_call_id` 关联到 `assistant` 的 `tool_calls[i].Id`。崩溃或压缩可能导致 `tool_use` 后缺 `tool_result`（LLM 调了工具但结果未入历史）。恢复时检测未配对的 `tool_use`，截断到最后一个完整状态——避免 LLM 收到"孤儿 tool_call"报错。
4. **@include 指令嵌套**：项目指令支持 `@include path/to/file.md` 嵌套引用，限制 3 层防无限递归。体会"指令组合"的威力——主指令文件引用通用规范、项目特定规范、团队约定等子文件，按需组装。
5. **IUiControl 抽象**：命令需要操作 UI（显示输出、清屏、退出、切换模式），但不应该直接依赖 `TerminalApp` 具体类型。用 `IUiControl` 接口抽象 UI 能力，命令依赖抽象而非具体——便于测试（mock UI）和未来替换 UI 实现。
6. **system prompt 的动态注入**：项目指令作为 system prompt 的一部分注入 `AgentLoop`。每轮重新拼装（`BuildMessagesWithSystem`），不受压缩影响——压缩只动历史，不动 system prompt。理解为什么 system prompt 不入历史（避免被摘要丢失关键约定）。

---

## 三、范围

### 3.1 本迭代包含（In Scope）

| 项 | 说明 |
| --- | --- |
| `Commands/CommandType.cs` | 命令类型枚举：`System` / `Hidden`（Hidden 不在 `/help` 显示） |
| `Commands/ICommand.cs` | 命令接口：`Name` / `Description` / `Type` / `Aliases` / `ExecuteAsync` |
| `Commands/CommandContext.cs` | 命令执行上下文：`Args` / `History` / `Compressor` / `SecurityGuard` / `Ui` / `SessionStore` / `Instructions` / `Ct` |
| `Commands/CommandResult.cs` | 命令执行结果：`Handled` / `ExitApp` / `Output` |
| `Commands/CommandRegistry.cs` | 注册中心 + 反射自动扫描 + 别名冲突检测 |
| `Commands/CommandParser.cs` | `/name args` 解析：分离命令名与参数 |
| `Commands/CommandDispatcher.cs` | 分发器：`/` 前缀走命令，否则返回 `NotACommand` |
| `Commands/Builtin/HelpCommand.cs` | `/help`：列出所有非 Hidden 命令 |
| `Commands/Builtin/ClearCommand.cs` | `/clear`：清空历史 + UI + 重置压缩器警告 |
| `Commands/Builtin/CompressCommand.cs` | `/compress`：手动触发压缩（即使熔断器 open 也允许，成功后 reset） |
| `Commands/Builtin/ModeCommand.cs` | `/mode <strict|normal|permissive>`：切换安全等级 |
| `Commands/Builtin/StatusCommand.cs` | `/status`：显示当前配置概要 |
| `Commands/Builtin/SessionCommand.cs` | `/session save|load|list|current`：会话持久化 |
| `Commands/Builtin/ExitCommand.cs` | `/exit` `/quit`：退出应用 |
| `Storage/SessionStore.cs` | JSONL 持久化：追加写 + 逐行读 + 损坏行跳过 + 配对修复 + Meta 文件 |
| `Storage/SessionMeta.cs` | 会话元数据 record：`Id` / `CreatedAt` / `UpdatedAt` / `MessageCount` / `ProviderName` / `ModelName` / `Title` |
| `Storage/SessionSummary.cs` | 会话列表摘要 record（`/session list` 用） |
| `Instructions/InstructionLoader.cs` | 三级目录扫描 + `@include` 嵌套（限 3 层） |
| `Instructions/InstructionResult.cs` | 指令加载结果：`Content` / `Sources`（来源文件列表） |
| `Tui/IUiControl.cs` | UI 抽象接口：命令通过此接口操作 UI |
| `Tui/TerminalApp.cs` | 扩展：实现 `IUiControl`；`HandleUserInput` 改用 `CommandDispatcher`；注入 `SessionStore` / `InstructionLoader` |
| `Tui/InputFieldView.cs` | 扩展：Tab 补全命令名从 `CommandRegistry` 动态获取 |
| `Tui/StatusBarView.cs` | 扩展：`/mode` 切换后更新 `SecurityLevel` 显示 |
| `Agent/AgentLoop.cs` | 扩展：`systemPrompt` 改为可注入（含项目指令）；构造函数加 `instructions` 参数 |
| `App/App.cs` | 扩展：构造 `SessionStore` / `InstructionLoader` / `CommandRegistry` 传入 `TerminalApp` |
| `Config/Models.cs` | 扩展：`SessionConfig` + `InstructionsConfig` + `AppConfig.Session` / `AppConfig.Instructions` |
| `example.parrotcode.yaml` | 加 `session:` / `instructions:` 配置节示例 |
| 单元测试 | `CommandRegistryTests` / `CommandParserTests` / `CommandDispatcherTests` / `SessionStoreTests` / `InstructionLoaderTests` + 各内置命令测试 |

### 3.2 本迭代不包含（Out of Scope）

- Skill 系统 `/skill` 命令 → 迭代 12
- `AllowPermanent` 跨会话持久化（HITL 永久允许） → 迭代 12 或后续
- 会话自动恢复（启动时加载上次会话） → 进阶练习
- 多会话并发 / 会话切换栈 → 后续迭代
- 会话导出为 Markdown / 纯文本 → 进阶练习
- 项目指令的热重载（文件变化时自动重新加载） → 后续迭代
- `@include` 的通配符支持（`@include docs/*.md`） → 后续迭代
- 摘要缓存持久化到 `.parrotcode/summaries/` → 进阶练习（迭代 9 已提及）
- 斜杠命令的参数校验框架（本迭代各命令自行校验） → 后续迭代

---

## 四、现状分析

### 4.1 已预留的接入点

| 位置 | 现状 | 迭代 10 利用方式 |
|------|------|---------------|
| `TerminalApp.HandleUserInput` | 硬编码 `/exit` `/clear` `/help` 三个命令 | 替换为 `CommandDispatcher.DispatchAsync(line)` |
| `InputFieldView._commands` | 硬编码 5 个命令名数组（Tab 补全） | 改为从 `CommandRegistry` 动态获取 |
| `InputFieldView.CompleteCommand` | Tab 补全唯一匹配 | 不变，仅数据源改为动态 |
| `ContextCompressor.ResetWarning` | 已有（迭代 9 预留） | `/clear` 调用 |
| `ContextCompressor.ResetCircuit` | 已有（迭代 9 预留） | `/compress` 手动成功后调用 |
| `ContextCompressor.CheckAndCompressAsync` | 已有（迭代 9） | `/compress` 手动触发调用 |
| `SecurityGuard.Level` | 可变属性（迭代 8b 预留"为迭代 10 /mode 运行时切换预留"） | `/mode` 直接 set |
| `StatusBarView` | 显示 `security={_securityLevel}` | `/mode` 后调 `Update` 刷新 |
| `AgentLoop._systemPrompt` | 构造时固定（`AgentConfig.SystemPrompt` 或默认） | 改为构造时注入项目指令拼接结果 |
| `ConversationHistory.Clear` | 已有 | `/clear` 调用 |
| `ConversationHistory.ToProviderMessages` | 返回数组快照 | `/session save` 序列化用 |
| `ConversationHistory.ReplaceMessages` | 已有（迭代 9） | `/session load` 恢复用 |
| `Message` record | `Role` / `Content` / `ToolCalls` / `ToolCallId` | JSONL 序列化目标 |
| `MessageExtensions.ToOpenAiWire` | 已有（序列化为 OpenAI wire format） | JSONL 不用此方法（用独立 DTO 保留协议中性） |

### 4.2 当前 HandleUserInput 流程（待改造）

```csharp
// 现状（TerminalApp.HandleUserInput 第 254-289 行）
private void HandleUserInput(string line)
{
    // 斜杠命令硬编码分发
    if (line is "/exit" or "/quit") { Application.RequestStop(_top!); return; }
    if (line is "/clear") { _chatView!.ClearMessages(); _history!.Clear(); return; }
    if (line is "/help") { _chatView!.AppendStaticMessage("可用命令：/clear /exit /help"); return; }
    if (string.IsNullOrWhiteSpace(line)) return;

    // Agent 正在运行时忽略新输入
    if (_agentTask is not null && !_agentTask.IsCompleted) return;

    // 显示用户消息 + 启动 AgentLoop
    _chatView!.AppendUserMessage(line);
    _history!.AddUser(line);
    StartAgentRound();
}
```

**问题**：
- 命令硬编码，新增命令需改 `HandleUserInput` + `InputFieldView._commands` 两处。
- 无参数解析（`/mode strict` 无法支持）。
- 无会话持久化，退出即丢失。
- 无项目指令注入，Agent 不知道项目约定。

**改造后**：

```csharp
private async void HandleUserInput(string line)
{
    if (string.IsNullOrWhiteSpace(line)) return;

    // Agent 正在运行时忽略新输入（命令也忽略，避免状态竞争）
    if (_agentTask is not null && !_agentTask.IsCompleted) return;

    // 命令分发
    var dispatchResult = await _commandDispatcher!.DispatchAsync(line, BuildCommandContext(), _ct);
    if (dispatchResult.Handled)
    {
        if (dispatchResult.Output is not null)
            _chatView!.AppendStaticMessage(dispatchResult.Output);
        if (dispatchResult.ExitApp)
            Application.RequestStop(_top!);
        return;
    }

    // 非命令 → 走 AI
    _chatView!.AppendUserMessage(line);
    _history!.AddUser(line);
    _statusBarView!.CurrentRound = 0;
    _statusBarView.EstimatedTokens = _history.EstimatedTokens;
    StartAgentRound();
}
```

### 4.3 当前 AgentLoop system prompt（待改造）

```csharp
// 现状（AgentLoop 构造函数）
_systemPrompt = systemPrompt ?? DefaultSystemPrompt;

// 现状（BuildMessagesWithSystem）
private IReadOnlyList<Message> BuildMessagesWithSystem(ConversationHistory history)
{
    var snapshot = history.ToProviderMessages();
    if (string.IsNullOrEmpty(_systemPrompt)) return snapshot;
    var withSystem = new List<Message>(snapshot.Count + 1)
    {
        new(MessageRole.System, _systemPrompt)
    };
    withSystem.AddRange(snapshot);
    return withSystem;
}
```

**问题**：`systemPrompt` 来自 `AgentConfig.SystemPrompt`（YAML 配置），无法注入项目指令。

**改造后**：`App.RunAsync` 加载项目指令，拼接默认 system prompt + 项目指令，传入 `TerminalApp` → `AgentLoop`。

---

## 五、架构设计

### 5.1 三大子系统总览

```
用户输入
  │
  ▼
TerminalApp.HandleUserInput
  │
  ▼
CommandDispatcher.DispatchAsync
  │
  ├── "/xxx" 前缀 → CommandRegistry 查找 → ICommand.ExecuteAsync
  │                                        │
  │                                        ├── /help     → Ui.AppendStaticMessage(命令列表)
  │                                        ├── /clear    → History.Clear + Ui.ClearMessages + Compressor.ResetWarning
  │                                        ├── /compress → Compressor.CheckAndCompressAsync (手动，熔断器 open 也允许)
  │                                        ├── /mode     → SecurityGuard.Level = xxx + Ui.RefreshStatusBar
  │                                        ├── /status   → Ui.AppendStaticMessage(配置概要)
  │                                        ├── /session  → SessionStore.SaveAsync / LoadAsync / ListAsync
  │                                        └── /exit     → CommandResult.ExitApp
  │
  └── 非命令 → AgentLoop.RunAsync (含项目指令注入的 system prompt)
                    │
                    ▼
              BuildMessagesWithSystem
                    │
                    ▼
              [System: 默认prompt + 项目指令] + History 快照
                    │
                    ▼
              Provider.ChatStreamAsync
```

### 5.2 命令系统分层

```
┌─────────────────────────────────────────────────────┐
│  TUI 层                                              │
│  TerminalApp (实现 IUiControl)                       │  ← HandleUserInput 改用 Dispatcher
│  InputFieldView (Tab 补全数据源改为 Registry)         │
├─────────────────────────────────────────────────────┤
│  命令分发层（本迭代新增）                             │
│  CommandDispatcher / CommandParser                   │  ← / 前缀路由
├─────────────────────────────────────────────────────┤
│  命令注册层（本迭代新增）                             │
│  CommandRegistry (反射自动扫描 ICommand 实现)         │
├─────────────────────────────────────────────────────┤
│  命令实现层（本迭代新增）                             │
│  ICommand 接口 + 7 个内置命令                         │
├─────────────────────────────────────────────────────┤
│  基础设施层                                          │
│  SessionStore (JSONL) / InstructionLoader (@include) │
├─────────────────────────────────────────────────────┤
│  既有层（不变或小改）                                 │
│  AgentLoop (system prompt 注入) / ContextCompressor   │
│  SecurityGuard (Level 可变) / ConversationHistory     │
└─────────────────────────────────────────────────────┘
```

### 5.3 JSONL 会话持久化结构

```
.parrotcode/sessions/
├── {sessionId}.jsonl          ← 每行一条消息 JSON（追加写）
├── {sessionId}.meta.json      ← 会话元数据
├── {sessionId2}.jsonl
├── {sessionId2}.meta.json
└── ...
```

**JSONL 每行格式**（协议中性的 `MessageDto`）：

```json
{"role":"user","content":"你好","toolCalls":null,"toolCallId":null}
{"role":"assistant","content":"","toolCalls":[{"id":"call_1","name":"read_file","input":"{\"path\":\"README.md\"}"}],"toolCallId":null}
{"role":"tool","content":"# README\n...","toolCalls":null,"toolCallId":"call_1"}
```

**Meta 文件格式**（`{sessionId}.meta.json`）：

```json
{
  "id": "20260809_153000_a1b2c3",
  "createdAt": "2026-08-09T15:30:00Z",
  "updatedAt": "2026-08-09T16:45:12Z",
  "messageCount": 24,
  "providerName": "deepseek",
  "modelName": "deepseek-chat",
  "title": "你好"  // 首条用户消息前 50 字符
}
```

### 5.4 项目指令加载流程

```
1. 三级目录扫描（按优先级合并，后者追加）：
   a. ~/.parrocode/instructions.md     （全局用户指令）
   b. ./PARROTCODE.md                  （项目根指令，类似 CLAUDE.md）
   c. ./.parrotcode/instructions.md    （项目本地指令，可被 .gitignore 忽略）

2. @include 嵌套处理（限 3 层）：
   主指令文件内容 → 扫描 @include path/to/file.md → 递归加载子文件内容替换
   
3. 合并结果：
   [全局指令]\n\n[项目指令（含 @include 展开）]\n\n[本地指令（含 @include 展开）]

4. 注入 system prompt：
   AgentLoop 构造时拼接：默认 prompt + "\n\n## 项目指令\n" + 指令内容
   每轮 BuildMessagesWithSystem 重新拼装，不受压缩影响
```

### 5.5 命令上下文（CommandContext）

命令执行时需要的所有依赖通过 `CommandContext` 传入，避免命令直接依赖 `TerminalApp`：

```csharp
public sealed record CommandContext(
    ConversationHistory History,
    ContextCompressor? Compressor,
    SecurityGuard SecurityGuard,
    IUiControl Ui,
    SessionStore? SessionStore,
    string? InstructionSummary,  // 指令加载概要（来源文件数）
    CancellationToken Ct)
{
    /// <summary>当前 Provider 配置（/status 用）。</summary>
    public ProviderConfig ProviderConfig { get; init; } = null!;
    
    /// <summary>当前 TUI 配置（/status 用）。</summary>
    public TuiConfig TuiConfig { get; init; } = null!;
}
```

---

## 六、详细设计

### 6.1 CommandType 枚举

```csharp
// Commands/CommandType.cs
namespace ParrotCode;

/// <summary>
/// 命令类型。决定命令在 /help 中的可见性。
/// </summary>
public enum CommandType
{
    /// <summary>
    /// 系统命令：在 /help 中可见，用户可直接调用。
    /// </summary>
    System,

    /// <summary>
    /// 隐藏命令：不在 /help 中显示，但仍可调用（如内部调试命令）。
    /// </summary>
    Hidden
}
```

### 6.2 ICommand 接口

```csharp
// Commands/ICommand.cs
namespace ParrotCode;

/// <summary>
/// 斜杠命令接口。所有命令实现此接口，由 CommandRegistry 反射自动扫描注册。
/// 命令是同步逻辑（无 LLM 调用），通过 CommandContext 操作 UI/History/Compressor 等。
/// </summary>
public interface ICommand
{
    /// <summary>命令名（不含 / 前缀），如 "help" / "clear" / "session"。</summary>
    string Name { get; }

    /// <summary>命令描述（/help 展示用，简短一句话）。</summary>
    string Description { get; }

    /// <summary>命令类型（System 在 /help 可见，Hidden 不可见）。</summary>
    CommandType Type { get; }

    /// <summary>命令别名（不含 / 前缀），如 exit 的别名 ["quit"]。空列表表示无别名。</summary>
    IReadOnlyList<string> Aliases { get; }

    /// <summary>用法示例（/help 展示用，如 "/session save" / "/mode strict"）。</summary>
    string Usage { get; }

    /// <summary>
    /// 执行命令。返回 CommandResult。
    /// 命令不应抛异常——错误信息通过 CommandResult.Output 返回。
    /// </summary>
    Task<CommandResult> ExecuteAsync(CommandContext context);
}
```

### 6.3 CommandResult

```csharp
// Commands/CommandResult.cs
namespace ParrotCode;

/// <summary>
/// 命令执行结果。
/// </summary>
public sealed record CommandResult
{
    /// <summary>命令是否被处理（true=已处理，false=未识别/未处理，回退到 AI）。</summary>
    public bool Handled { get; init; }

    /// <summary>命令输出文本（显示到 ChatView，null 表示无输出）。</summary>
    public string? Output { get; init; }

    /// <summary>是否请求退出应用（/exit /quit 设置）。</summary>
    public bool ExitApp { get; init; }

    /// <summary>未处理（回退到 AI）的静态工厂。</summary>
    public static CommandResult NotHandled => new() { Handled = false };

    /// <summary>已处理，无输出的静态工厂。</summary>
    public static CommandResult Ok => new() { Handled = true };

    /// <summary>已处理，带输出的静态工厂。</summary>
    public static CommandResult WithOutput(string output) => new() { Handled = true, Output = output };

    /// <summary>退出应用的静态工厂。</summary>
    public static CommandResult Exit => new() { Handled = true, ExitApp = true };
}
```

### 6.4 CommandContext

```csharp
// Commands/CommandContext.cs
namespace ParrotCode;

/// <summary>
/// 命令执行上下文：封装命令执行时需要的所有依赖。
/// 命令通过此上下文操作 UI/History/Compressor/SecurityGuard 等，不直接依赖 TerminalApp。
/// </summary>
public sealed record CommandContext(
    ConversationHistory History,
    ContextCompressor? Compressor,
    SecurityGuard SecurityGuard,
    IUiControl Ui,
    SessionStore? SessionStore,
    CancellationToken Ct)
{
    /// <summary>当前 Provider 配置（/status 用）。必填。</summary>
    public ProviderConfig ProviderConfig { get; init; } = null!;

    /// <summary>当前 TUI 配置（/status 用）。必填。</summary>
    public TuiConfig TuiConfig { get; init; } = null!;

    /// <summary>当前 AgentConfig（/status 用）。必填。</summary>
    public AgentConfig AgentConfig { get; init; } = null!;

    /// <summary>项目指令加载概要（/status 显示来源文件数）。</summary>
    public string? InstructionSummary { get; init; }

    /// <summary>原始输入行（含 / 前缀，便于错误提示引用）。</summary>
    public string RawInput { get; init; } = string.Empty;
}
```

### 6.5 CommandRegistry（注册中心 + 反射自动扫描）

```csharp
// Commands/CommandRegistry.cs
using System.Reflection;

namespace ParrotCode;

/// <summary>
/// 命令注册中心：管理所有已注册的 ICommand。
/// 支持手动注册 + 反射自动扫描程序集中所有 ICommand 实现类。
/// 别名冲突检测：注册时检查 Name 和所有 Aliases 是否已被占用。
/// </summary>
public sealed class CommandRegistry
{
    private readonly Dictionary<string, ICommand> _commands = new(StringComparer.OrdinalIgnoreCase);
    private readonly ILogger? _logger;

    public CommandRegistry(ILogger? logger = null)
    {
        _logger = logger;
    }

    /// <summary>已注册的命令数（含别名不重复计算）。</summary>
    public int Count => _commands.Values.Distinct().Count();

    /// <summary>
    /// 手动注册命令。Name 和所有 Aliases 必须唯一，冲突抛 InvalidOperationException。
    /// </summary>
    public void Register(ICommand command)
    {
        ArgumentNullException.ThrowIfNull(command);

        // 检查 Name 冲突
        if (_commands.ContainsKey(command.Name))
        {
            var existing = _commands[command.Name];
            throw new InvalidOperationException(
                $"命令名 '{command.Name}' 冲突：已由 {existing.GetType().Name} 注册");
        }

        // 检查 Aliases 冲突
        foreach (var alias in command.Aliases)
        {
            if (_commands.ContainsKey(alias))
            {
                var existing = _commands[alias];
                throw new InvalidOperationException(
                    $"别名 '{alias}' 冲突：已由 {existing.GetType().Name} 注册");
            }
        }

        // 注册 Name
        _commands[command.Name] = command;

        // 注册 Aliases（指向同一命令实例）
        foreach (var alias in command.Aliases)
            _commands[alias] = command;
    }

    /// <summary>
    /// 反射自动扫描程序集中所有 ICommand 实现类并注册。
    /// 跳过接口和抽象类，用无参构造函数实例化。
    /// </summary>
    public void AutoRegisterFromAssembly(Assembly? assembly = null)
    {
        assembly ??= Assembly.GetExecutingAssembly();
        var commandTypes = assembly.GetTypes()
            .Where(t => typeof(ICommand).IsAssignableFrom(t)
                        && t is { IsInterface: false, IsAbstract: false }
                        && t.GetConstructor(Type.EmptyTypes) is not null);

        foreach (var type in commandTypes)
        {
            try
            {
                var command = (ICommand)Activator.CreateInstance(type)!;
                Register(command);
                _logger?.LogDebug("自动注册命令 {Name} ({Type})", command.Name, type.Name);
            }
            catch (InvalidOperationException ex)
            {
                // 别名/名称冲突——跳过并记日志（可能是手动注册后再自动扫描）
                _logger?.LogWarning(ex, "自动注册命令 {Type} 失败（可能已手动注册）", type.Name);
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "自动注册命令 {Type} 失败", type.Name);
            }
        }
    }

    /// <summary>按名（或别名）查找命令。未找到返回 null。</summary>
    public ICommand? Find(string nameOrAlias)
    {
        return _commands.TryGetValue(nameOrAlias, out var cmd) ? cmd : null;
    }

    /// <summary>获取所有命令（去重，不含别名重复）。</summary>
    public IReadOnlyList<ICommand> GetAll()
    {
        return _commands.Values.Distinct().ToList();
    }

    /// <summary>获取所有命令名（含别名），供 Tab 补全用。</summary>
    public IReadOnlyList<string> GetAllNamesWithAliases()
    {
        return _commands.Keys.ToList();
    }

    /// <summary>获取所有在 /help 中可见的命令（Type == System）。</summary>
    public IReadOnlyList<ICommand> GetVisibleCommands()
    {
        return GetAll().Where(c => c.Type == CommandType.System).ToList();
    }
}
```

### 6.6 CommandParser（解析器）

```csharp
// Commands/CommandParser.cs
using System.Text.RegularExpressions;

namespace ParrotCode;

/// <summary>
/// 斜杠命令解析器：把用户输入行解析为 (命令名, 参数字符串)。
/// 规则：
/// - 行首必须是 '/' 才视为命令（否则返回 null，走 AI）
/// - 命令名 = '/' 后到第一个空格前的部分（大小写不敏感）
/// - 参数 = 第一个空格后的全部内容（保留原始大小写与空格）
/// - 无参数命令："/clear" → ("clear", "")
/// - 带参数命令："/mode strict" → ("mode", "strict")
/// - 带空格参数："/session save my-session" → ("session", "save my-session")
/// </summary>
public static class CommandParser
{
    /// <summary>
    /// 解析输入行。
    /// 返回 (命令名, 参数) 元组；非命令（不以 / 开头）返回 null。
    /// </summary>
    public static (string Name, string Args)? Parse(string line)
    {
        if (string.IsNullOrEmpty(line) || line[0] != '/')
            return null;

        // 去掉 '/' 前缀
        var body = line[1..];

        // 找第一个空格分隔命令名与参数
        var spaceIdx = body.IndexOf(' ');
        if (spaceIdx < 0)
        {
            // 无空格——纯命令名，无参数
            return (body, string.Empty);
        }

        var name = body[..spaceIdx];
        var args = body[(spaceIdx + 1)..];
        return (name, args);
    }

    /// <summary>
    /// 把参数字符串按空格分割为参数数组（支持引号包裹的含空格参数）。
    /// "/session save my-session" → ["save", "my-session"]
    /// "/mode \"strict mode\"" → ["strict mode"]
    /// 简化版：不处理转义引号，仅支持双引号包裹整体参数。
    /// </summary>
    public static IReadOnlyList<string> SplitArgs(string args)
    {
        if (string.IsNullOrWhiteSpace(args))
            return Array.Empty<string>();

        var result = new List<string>();
        var matches = Regex.Matches(args, @"(?:""([^""]*)""|(\S+))");
        foreach (Match m in matches)
        {
            // 优先取引号内的分组，否则取非空白分组
            result.Add(m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value);
        }
        return result;
    }
}
```

### 6.7 CommandDispatcher（分发器）

```csharp
// Commands/CommandDispatcher.cs
namespace ParrotCode;

/// <summary>
/// 命令分发器：判断输入是否为命令，若是则查找并执行。
/// "/" 前缀 → 查 Registry → 执行 ICommand.ExecuteAsync
/// 非 "/" 前缀 → 返回 CommandResult.NotHandled（回退到 AI）
/// 命令未找到 → 返回 WithOutput("未知命令: xxx，输入 /help 查看可用命令")
/// </summary>
public sealed class CommandDispatcher
{
    private readonly CommandRegistry _registry;

    public CommandDispatcher(CommandRegistry registry)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
    }

    public async Task<CommandResult> DispatchAsync(string line, CommandContext context, CancellationToken ct)
    {
        var parsed = CommandParser.Parse(line);
        if (parsed is null)
            return CommandResult.NotHandled;  // 非命令，走 AI

        var (name, args) = parsed.Value;
        var command = _registry.Find(name);
        if (command is null)
        {
            return CommandResult.WithOutput(
                $"未知命令: /{name}，输入 /help 查看可用命令");
        }

        // 把参数注入 context（RawInput 保留原始行）
        var ctx = context with { RawInput = line, Ct = ct };

        try
        {
            return await command.ExecuteAsync(ctx);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;  // 取消向上传播
        }
        catch (Exception ex)
        {
            // 命令异常不崩溃应用——返回错误输出
            return CommandResult.WithOutput($"[!] 执行命令 /{name} 失败：{ex.Message}");
        }
    }
}
```

### 6.8 IUiControl 抽象接口

```csharp
// Tui/IUiControl.cs
namespace ParrotCode;

/// <summary>
/// UI 抽象接口：命令通过此接口操作 UI，不直接依赖 TerminalApp。
/// 仅暴露命令需要的能力，遵循接口隔离原则。
/// </summary>
public interface IUiControl
{
    /// <summary>追加静态消息到对话区（系统提示/命令输出）。</summary>
    void AppendStaticMessage(string text);

    /// <summary>追加用户消息到对话区。</summary>
    void AppendUserMessage(string text);

    /// <summary>清空对话区所有消息。</summary>
    void ClearMessages();

    /// <summary>刷新状态栏（/mode 切换后调用）。</summary>
    void RefreshStatusBar();

    /// <summary>更新状态栏的 token 估算（/clear /compress /session load 后调用）。</summary>
    void UpdateTokenEstimate(int estimatedTokens);

    /// <summary>更新状态栏的安全等级显示（/mode 后调用）。</summary>
    void UpdateSecurityLevel(SecurityLevel level);

    /// <summary>请求退出应用（/exit /quit 调用）。</summary>
    void RequestExit();
}
```

### 6.9 内置命令实现

#### 6.9.1 HelpCommand

```csharp
// Commands/Builtin/HelpCommand.cs
using System.Text;

namespace ParrotCode.Commands.Builtin;

/// <summary>
/// /help：列出所有可见命令（Type == System）。
/// </summary>
public sealed class HelpCommand : ICommand
{
    public string Name => "help";
    public string Description => "显示可用命令列表";
    public CommandType Type => CommandType.System;
    public IReadOnlyList<string> Aliases => new[] { "?" };
    public string Usage => "/help";

    public Task<CommandResult> ExecuteAsync(CommandContext context)
    {
        // CommandRegistry 通过 context 间接获取——这里需要 context 暴露 Registry
        // 简化：HelpCommand 需要访问 Registry，通过构造函数注入
        var sb = new StringBuilder();
        sb.AppendLine("可用命令：");
        foreach (var cmd in _registry.GetVisibleCommands().OrderBy(c => c.Name))
        {
            sb.AppendLine($"  {cmd.Usage,-20} {cmd.Description}");
        }
        sb.AppendLine();
        sb.AppendLine("提示：输入消息与 AI 对话；/ 开头走命令。");
        return Task.FromResult(CommandResult.WithOutput(sb.ToString()));
    }

    private readonly CommandRegistry _registry;

    public HelpCommand(CommandRegistry registry)
    {
        _registry = registry;
    }
}
```

> **注**：`HelpCommand` 需要访问 `CommandRegistry` 列出命令，通过构造函数注入。这是反射自动注册的一个例外——`HelpCommand` 无参构造无法满足。解决方案：`AutoRegisterFromAssembly` 跳过 `HelpCommand`，手动 `Register(new HelpCommand(registry))`。

#### 6.9.2 ClearCommand

```csharp
// Commands/Builtin/ClearCommand.cs
namespace ParrotCode.Commands.Builtin;

/// <summary>
/// /clear：清空对话历史 + UI + 重置压缩器警告。
/// 不重置熔断器（熔断器是跨轮状态，/clear 只清历史不重置压缩器内部状态）。
/// </summary>
public sealed class ClearCommand : ICommand
{
    public string Name => "clear";
    public string Description => "清空对话历史，重新开始";
    public CommandType Type => CommandType.System;
    public IReadOnlyList<string> Aliases => Array.Empty<string>();
    public string Usage => "/clear";

    public Task<CommandResult> ExecuteAsync(CommandContext context)
    {
        context.History.Clear();
        context.Ui.ClearMessages();
        context.Compressor?.ResetWarning();
        context.Ui.UpdateTokenEstimate(0);
        return Task.FromResult(CommandResult.Ok);
    }
}
```

#### 6.9.3 CompressCommand

```csharp
// Commands/Builtin/CompressCommand.cs
namespace ParrotCode.Commands.Builtin;

/// <summary>
/// /compress：手动触发上下文压缩。
/// 即使熔断器 open 也允许手动触发（与自动触发不同）。
/// 手动触发成功后 ResetCircuit（给用户"再试一次"的机会）。
/// </summary>
public sealed class CompressCommand : ICommand
{
    public string Name => "compress";
    public string Description => "手动触发上下文压缩（摘要历史）";
    public CommandType Type => CommandType.System;
    public IReadOnlyList<string> Aliases => Array.Empty<string>();
    public string Usage => "/compress";

    public async Task<CommandResult> ExecuteAsync(CommandContext context)
    {
        if (context.Compressor is null)
            return CommandResult.WithOutput("[!] 上下文压缩未启用");

        // 熔断器 open 时提示但仍执行（手动触发）
        if (context.Compressor.CircuitOpen)
        {
            // 手动 reset 后再触发
            context.Compressor.ResetCircuit();
        }

        var result = await context.Compressor.CheckAndCompressAsync(context.History, context.Ct);

        if (result.WasCompressed)
        {
            context.Ui.UpdateTokenEstimate(context.History.EstimatedTokens);
            return CommandResult.WithOutput(
                $"[压缩] 已压缩 {result.MessagesCompressed} 条消息，节省约 {result.EstimatedTokensSaved} tokens");
        }

        if (result.CircuitOpen)
            return CommandResult.WithOutput("[!] 压缩失败，熔断器已打开（摘要连续失败）");

        return CommandResult.WithOutput("[i] 当前无需压缩（token 未达阈值）");
    }
}
```

#### 6.9.4 ModeCommand

```csharp
// Commands/Builtin/ModeCommand.cs
namespace ParrotCode.Commands.Builtin;

/// <summary>
/// /mode [strict|normal|permissive]：查看或切换安全等级。
/// 无参数 → 显示当前等级；有参数 → 切换。
/// </summary>
public sealed class ModeCommand : ICommand
{
    public string Name => "mode";
    public string Description => "查看或切换安全等级（strict/normal/permissive）";
    public CommandType Type => CommandType.System;
    public IReadOnlyList<string> Aliases => Array.Empty<string>();
    public string Usage => "/mode [strict|normal|permissive]";

    public Task<CommandResult> ExecuteAsync(CommandContext context)
    {
        var args = CommandParser.SplitArgs(context.RawInput[(context.RawInput.IndexOf(' ') + 1)..]);
        // 简化：直接从 RawInput 取参数
        var parts = context.RawInput.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        var modeArg = parts.Length > 1 ? parts[1].Trim() : null;

        if (string.IsNullOrEmpty(modeArg))
        {
            // 无参数 → 显示当前等级
            return Task.FromResult(CommandResult.WithOutput(
                $"当前安全等级：{context.SecurityGuard.Level}（可选：strict / normal / permissive）"));
        }

        var newLevel = SecurityLevelParser.Parse(modeArg);
        context.SecurityGuard.Level = newLevel;
        context.Ui.UpdateSecurityLevel(newLevel);
        context.Ui.RefreshStatusBar();

        return Task.FromResult(CommandResult.WithOutput(
            $"安全等级已切换为：{newLevel}"));
    }
}
```

#### 6.9.5 StatusCommand

```csharp
// Commands/Builtin/StatusCommand.cs
using System.Text;

namespace ParrotCode.Commands.Builtin;

/// <summary>
/// /status：显示当前配置概要。
/// </summary>
public sealed class StatusCommand : ICommand
{
    public string Name => "status";
    public string Description => "显示当前配置概要";
    public CommandType Type => CommandType.System;
    public IReadOnlyList<string> Aliases => Array.Empty<string>();
    public string Usage => "/status";

    public Task<CommandResult> ExecuteAsync(CommandContext context)
    {
        var sb = new StringBuilder();
        sb.AppendLine("=== 当前配置 ===");
        sb.AppendLine($"Provider: {context.ProviderConfig.Name} ({context.ProviderConfig.Protocol})");
        sb.AppendLine($"Model: {context.ProviderConfig.Model}");
        sb.AppendLine($"安全等级: {context.SecurityGuard.Level}");
        sb.AppendLine($"最大轮次: {context.AgentConfig.MaxRounds ?? 10}");
        sb.AppendLine($"工具并发: {context.AgentConfig.MaxParallelism ?? 5}");
        sb.AppendLine($"上下文窗口: {context.TuiConfig.ContextWindowTokens ?? 64000}");

        if (context.Compressor is not null)
        {
            sb.AppendLine($"历史消息数: {context.History.Count}");
            sb.AppendLine($"估算 tokens: {context.History.EstimatedTokens}");
            sb.AppendLine($"压缩熔断器: {(context.Compressor.CircuitOpen ? "打开（已禁用自动压缩）" : "正常")}");
            sb.AppendLine($"已压缩: {(context.Compressor.CircuitOpen ? "?" : "否")}");
        }

        if (context.SessionStore is not null)
        {
            sb.AppendLine($"会话存储: {context.SessionStore.StorageDir}");
        }

        if (!string.IsNullOrEmpty(context.InstructionSummary))
        {
            sb.AppendLine($"项目指令: {context.InstructionSummary}");
        }

        return Task.FromResult(CommandResult.WithOutput(sb.ToString()));
    }
}
```

#### 6.9.6 SessionCommand

```csharp
// Commands/Builtin/SessionCommand.cs
using System.Text;

namespace ParrotCode.Commands.Builtin;

/// <summary>
/// /session save|load|list|current：会话持久化。
/// - save [title]：保存当前会话，返回会话 ID
/// - load &lt;id&gt;：加载指定会话（清空当前历史）
/// - list：列出最近 10 个会话
/// - current：显示当前会话 ID（如有）
/// </summary>
public sealed class SessionCommand : ICommand
{
    public string Name => "session";
    public string Description => "会话持久化（save/load/list/current）";
    public CommandType Type => CommandType.System;
    public IReadOnlyList<string> Aliases => new[] { "sessions" };
    public string Usage => "/session save|load <id>|list|current";

    public async Task<CommandResult> ExecuteAsync(CommandContext context)
    {
        if (context.SessionStore is null)
            return CommandResult.WithOutput("[!] 会话持久化未启用");

        var parts = context.RawInput.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
        var subcommand = parts.Length > 1 ? parts[1].ToLowerInvariant() : null;

        switch (subcommand)
        {
            case null:
                return CommandResult.WithOutput("用法：/session save|load <id>|list|current");

            case "save":
                return await SaveAsync(context, parts);

            case "load":
                return await LoadAsync(context, parts);

            case "list":
                return await ListAsync(context);

            case "current":
                return Current(context);

            default:
                return CommandResult.WithOutput($"[!] 未知子命令：{subcommand}（可选：save/load/list/current）");
        }
    }

    private async Task<CommandResult> SaveAsync(CommandContext context, string[] parts)
    {
        var title = parts.Length > 2 ? parts[2] : null;
        var messages = context.History.ToProviderMessages();

        if (messages.Count == 0)
            return CommandResult.WithOutput("[!] 历史为空，无需保存");

        var meta = await context.SessionStore!.SaveAsync(
            messages, context.ProviderConfig, title, context.Ct);

        return CommandResult.WithOutput(
            $"[i] 会话已保存\n  ID: {meta.Id}\n  消息数: {meta.MessageCount}\n  标题: {meta.Title}");
    }

    private async Task<CommandResult> LoadAsync(CommandContext context, string[] parts)
    {
        if (parts.Length < 3)
            return CommandResult.WithOutput("[!] 用法：/session load <id>");

        var sessionId = parts[2];

        // 提示当前历史将被覆盖
        if (context.History.Count > 0)
        {
            // 不自动保存——提示用户先 save
            // 简化：直接覆盖（生产环境可加确认）
        }

        var (meta, messages) = await context.SessionStore!.LoadAsync(sessionId, context.Ct);

        if (messages.Count == 0)
            return CommandResult.WithOutput($"[!] 会话 {sessionId} 无消息或不存在");

        // 清空当前历史 + UI
        context.History.Clear();
        context.Ui.ClearMessages();
        context.Compressor?.ResetWarning();

        // 加载消息到历史
        context.History.ReplaceMessages(messages);

        // 时间跨度提醒
        var elapsed = DateTime.UtcNow - meta.UpdatedAt;
        if (elapsed.TotalMinutes > 30)
        {
            context.Ui.AppendStaticMessage(
                $"[i] 这是 {FormatTimeSpan(elapsed)}前的会话（{meta.UpdatedAt:yyyy-MM-dd HH:mm} 保存）");
        }

        // 渲染历史消息到 UI
        foreach (var msg in messages)
        {
            RenderHistoricalMessage(context.Ui, msg);
        }

        context.Ui.UpdateTokenEstimate(context.History.EstimatedTokens);

        return CommandResult.WithOutput(
            $"[i] 已加载会话 {meta.Id}（{messages.Count} 条消息）");
    }

    private async Task<CommandResult> ListAsync(CommandContext context)
    {
        var sessions = await context.SessionStore!.ListAsync(context.Ct);
        if (sessions.Count == 0)
            return CommandResult.WithOutput("[i] 无已保存会话");

        var sb = new StringBuilder();
        sb.AppendLine("最近会话（按更新时间倒序）：");
        foreach (var s in sessions.Take(10))
        {
            sb.AppendLine($"  {s.Id}  {s.UpdatedAt:MM-dd HH:mm}  {s.MessageCount,3}条  {s.Title}");
        }
        return CommandResult.WithOutput(sb.ToString());
    }

    private static CommandResult Current(CommandContext context)
    {
        // 本迭代不跟踪"当前会话 ID"（无自动恢复），始终返回提示
        return CommandResult.WithOutput("[i] 当前会话未持久化（用 /session save 保存）");
    }

    private static string FormatTimeSpan(TimeSpan elapsed)
    {
        if (elapsed.TotalDays >= 1) return $"{(int)elapsed.TotalDays} 天 ";
        if (elapsed.TotalHours >= 1) return $"{(int)elapsed.TotalHours} 小时 ";
        if (elapsed.TotalMinutes >= 1) return $"{(int)elapsed.TotalMinutes} 分钟 ";
        return "刚刚";
    }

    private static void RenderHistoricalMessage(IUiControl ui, Message msg)
    {
        switch (msg.Role)
        {
            case MessageRole.User:
                ui.AppendUserMessage(msg.Content);
                break;
            case MessageRole.Assistant:
                ui.AppendStaticMessage($"⏺ {msg.Content}");
                break;
            case MessageRole.Tool:
                ui.AppendStaticMessage($"  ⎿ [tool] {TruncateForDisplay(msg.Content)}");
                break;
            case MessageRole.System:
                // 压缩摘要等 system 消息不渲染到 UI（避免干扰）
                break;
        }
    }

    private static string TruncateForDisplay(string s, int max = 200) =>
        s.Length <= max ? s : s[..(max - 3)] + "...";
}
```

#### 6.9.7 ExitCommand

```csharp
// Commands/Builtin/ExitCommand.cs
namespace ParrotCode.Commands.Builtin;

/// <summary>
/// /exit /quit：退出应用。
/// </summary>
public sealed class ExitCommand : ICommand
{
    public string Name => "exit";
    public string Description => "退出 ParrotCode";
    public CommandType Type => CommandType.System;
    public IReadOnlyList<string> Aliases => new[] { "quit" };
    public string Usage => "/exit";

    public Task<CommandResult> ExecuteAsync(CommandContext context)
    {
        context.Ui.RequestExit();
        return Task.FromResult(CommandResult.Exit);
    }
}
```

### 6.10 SessionStore（JSONL 持久化）

```csharp
// Storage/SessionStore.cs
using System.IO;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace ParrotCode;

/// <summary>
/// 会话元数据。
/// </summary>
public sealed record SessionMeta
{
    public string Id { get; init; } = string.Empty;
    public DateTime CreatedAt { get; init; }
    public DateTime UpdatedAt { get; init; }
    public int MessageCount { get; init; }
    public string ProviderName { get; init; } = string.Empty;
    public string ModelName { get; init; } = string.Empty;
    public string Title { get; init; } = string.Empty;
}

/// <summary>
/// 会话列表摘要（/session list 用，不含消息内容）。
/// </summary>
public sealed record SessionSummary(
    string Id,
    DateTime UpdatedAt,
    int MessageCount,
    string Title);

/// <summary>
/// JSONL 会话持久化。
/// - 每行一条消息的 JSON（MessageDto），文件末尾追加写（O(1)）。
/// - Meta 文件分离：{sessionId}.meta.json 存会话概要。
/// - 崩溃恢复：逐行解析，损坏行跳过并记日志。
/// - 配对修复：未配对的 tool_use（缺 tool_result）截断到最后一个完整状态。
/// </summary>
public sealed class SessionStore
{
    private readonly string _storageDir;
    private readonly ILogger? _logger;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = false,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public SessionStore(string? storageDir = null, ILogger? logger = null)
    {
        _storageDir = storageDir ?? ".parrotcode/sessions";
        _logger = logger;
    }

    /// <summary>存储目录（绝对路径或相对项目根）。</summary>
    public string StorageDir => _storageDir;

    /// <summary>
    /// 保存会话。生成新 ID，追加写 JSONL + 写 Meta。
    /// 已存在同名 ID 会被覆盖（重新写整个文件）。
    /// </summary>
    public async Task<SessionMeta> SaveAsync(
        IReadOnlyList<Message> messages,
        ProviderConfig provider,
        string? title,
        CancellationToken ct)
    {
        Directory.CreateDirectory(_storageDir);

        var id = GenerateSessionId();
        var jsonlPath = GetJsonlPath(id);
        var metaPath = GetMetaPath(id);

        // 写 JSONL（覆盖模式，重新写整个文件——保存是全量快照）
        await using (var fs = new FileStream(jsonlPath, FileMode.Create, FileAccess.Write, FileShare.None))
        await using (var writer = new StreamWriter(fs, Encoding.UTF8))
        {
            foreach (var msg in messages)
            {
                var dto = MessageDto.FromMessage(msg);
                var line = JsonSerializer.Serialize(dto, JsonOpts);
                await writer.WriteLineAsync(line);
            }
        }

        var now = DateTime.UtcNow;
        var meta = new SessionMeta
        {
            Id = id,
            CreatedAt = now,
            UpdatedAt = now,
            MessageCount = messages.Count,
            ProviderName = provider.Name,
            ModelName = provider.Model,
            Title = title ?? DeriveTitle(messages)
        };

        // 写 Meta
        var metaJson = JsonSerializer.Serialize(meta, JsonOpts);
        await File.WriteAllTextAsync(metaPath, metaJson, Encoding.UTF8, ct);

        _logger?.LogInformation("会话已保存：{Id}（{Count} 条消息）", id, messages.Count);
        return meta;
    }

    /// <summary>
    /// 加载会话。逐行解析 JSONL，损坏行跳过，未配对 tool_use 截断。
    /// </summary>
    public async Task<(SessionMeta Meta, IReadOnlyList<Message> Messages)> LoadAsync(
        string sessionId, CancellationToken ct)
    {
        var jsonlPath = GetJsonlPath(sessionId);
        var metaPath = GetMetaPath(sessionId);

        if (!File.Exists(jsonlPath))
            throw new FileNotFoundException($"会话文件不存在：{jsonlPath}");

        // 读 Meta（不存在时构造默认）
        SessionMeta meta;
        if (File.Exists(metaPath))
        {
            var metaJson = await File.ReadAllTextAsync(metaPath, ct);
            meta = JsonSerializer.Deserialize<SessionMeta>(metaJson) ?? CreateDefaultMeta(sessionId);
        }
        else
        {
            meta = CreateDefaultMeta(sessionId);
        }

        // 读 JSONL
        var messages = new List<Message>();
        var corruptLines = 0;

        await using var fs = new FileStream(jsonlPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var reader = new StreamReader(fs, Encoding.UTF8);
        string? line;
        while ((line = await reader.ReadLineAsync(ct)) is not null)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                var dto = JsonSerializer.Deserialize<MessageDto>(line);
                if (dto is not null)
                    messages.Add(dto.ToMessage());
            }
            catch (JsonException ex)
            {
                corruptLines++;
                _logger?.LogWarning("会话 {Id} 损坏行已跳过：{Error}", sessionId, ex.Message);
            }
        }

        // 配对修复：截断未配对的 tool_use
        var beforeCount = messages.Count;
        messages = RepairToolCallPairing(messages);
        if (messages.Count < beforeCount)
        {
            _logger?.LogWarning("会话 {Id} 配对修复：截断 {Count} 条未配对消息",
                sessionId, beforeCount - messages.Count);
        }

        return (meta with { MessageCount = messages.Count }, messages);
    }

    /// <summary>
    /// 列出所有会话摘要（按 UpdatedAt 倒序）。
    /// 扫描 Meta 文件，不读 JSONL。
    /// </summary>
    public async Task<IReadOnlyList<SessionSummary>> ListAsync(CancellationToken ct)
    {
        if (!Directory.Exists(_storageDir))
            return Array.Empty<SessionSummary>();

        var result = new List<SessionSummary>();
        foreach (var metaFile in Directory.EnumerateFiles(_storageDir, "*.meta.json"))
        {
            try
            {
                var json = await File.ReadAllTextAsync(metaFile, ct);
                var meta = JsonSerializer.Deserialize<SessionMeta>(json);
                if (meta is not null)
                {
                    result.Add(new SessionSummary(
                        meta.Id, meta.UpdatedAt, meta.MessageCount, meta.Title));
                }
            }
            catch (Exception ex)
            {
                _logger?.LogWarning("读取会话 Meta 失败 {File}：{Error}", metaFile, ex.Message);
            }
        }

        return result.OrderByDescending(s => s.UpdatedAt).ToList();
    }

    /// <summary>
    /// 配对修复：检测未配对的 tool_use（assistant 带 ToolCalls 但后续缺对应 tool_result）。
    /// 截断到最后一个完整状态——删除未配对的 assistant(tool_calls) 消息。
    /// </summary>
    private static List<Message> RepairToolCallPairing(List<Message> messages)
    {
        // 收集所有已配对的 tool_call_id
        var pairedToolCallIds = new HashSet<string>();
        foreach (var msg in messages)
        {
            if (msg.Role == MessageRole.Tool && msg.ToolCallId is not null)
                pairedToolCallIds.Add(msg.ToolCallId);
        }

        // 从后往前找第一个未配对的 assistant(tool_calls)
        for (var i = messages.Count - 1; i >= 0; i--)
        {
            var msg = messages[i];
            if (msg.Role == MessageRole.Assistant && msg.ToolCalls is { Count: > 0 })
            {
                // 检查此 assistant 的所有 tool_call 是否都有对应 tool_result
                var allPaired = msg.ToolCalls.All(tc => pairedToolCallIds.Contains(tc.Id));
                if (!allPaired)
                {
                    // 截断到此消息之前（不含）
                    return messages.Take(i).ToList();
                }
            }
        }

        return messages;
    }

    private static string GenerateSessionId()
    {
        var ts = DateTime.Now.ToString("yyyyMMdd_HHmmss", System.Globalization.CultureInfo.InvariantCulture);
        var suffix = Guid.NewGuid().ToString("N")[..6];
        return $"{ts}_{suffix}";
    }

    private static string DeriveTitle(IReadOnlyList<Message> messages)
    {
        var firstUser = messages.FirstOrDefault(m => m.Role == MessageRole.User);
        if (firstUser is null) return "（无标题）";
        var content = firstUser.Content.Replace("\n", " ").Trim();
        return content.Length <= 50 ? content : content[..50] + "...";
    }

    private static SessionMeta CreateDefaultMeta(string id) => new()
    {
        Id = id,
        CreatedAt = DateTime.MinValue,
        UpdatedAt = DateTime.MinValue,
        MessageCount = 0
    };

    private string GetJsonlPath(string id) => Path.Combine(_storageDir, $"{id}.jsonl");
    private string GetMetaPath(string id) => Path.Combine(_storageDir, $"{id}.meta.json");
}

/// <summary>
/// 消息的 JSONL 序列化 DTO（协议中性，不依赖 OpenAI wire format）。
/// 保留 ToolCalls 和 ToolCallId 以支持完整恢复。
/// </summary>
internal sealed class MessageDto
{
    public string Role { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;
    public List<ToolCallDto>? ToolCalls { get; set; }
    public string? ToolCallId { get; set; }

    public static MessageDto FromMessage(Message msg)
    {
        var dto = new MessageDto
        {
            Role = msg.Role.ToString().ToLowerInvariant(),
            Content = msg.Content,
            ToolCallId = msg.ToolCallId
        };
        if (msg.ToolCalls is { Count: > 0 })
        {
            dto.ToolCalls = msg.ToolCalls.Select(tc => new ToolCallDto
            {
                Id = tc.Id,
                Name = tc.Name,
                Input = tc.Input.GetRawText()  // JsonElement → JSON 字符串
            }).ToList();
        }
        return dto;
    }

    public Message ToMessage()
    {
        var role = Role.ToLowerInvariant() switch
        {
            "system" => MessageRole.System,
            "user" => MessageRole.User,
            "assistant" => MessageRole.Assistant,
            "tool" => MessageRole.Tool,
            _ => MessageRole.User
        };

        Message msg = new(role, Content);

        if (ToolCalls is { Count: > 0 })
        {
            var toolCalls = ToolCalls.Select(tc =>
            {
                using var doc = JsonDocument.Parse(tc.Input ?? "{}");
                return new ToolCall(tc.Id, tc.Name, doc.RootElement.Clone());
            }).ToList();
            msg = msg with { ToolCalls = toolCalls };
        }

        if (ToolCallId is not null)
            msg = msg with { ToolCallId = ToolCallId };

        return msg;
    }
}

internal sealed class ToolCallDto
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? Input { get; set; }
}
```

### 6.11 InstructionLoader（项目指令加载）

```csharp
// Instructions/InstructionLoader.cs
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace ParrotCode;

/// <summary>
/// 指令加载结果。
/// </summary>
public sealed record InstructionResult
{
    /// <summary>合并后的指令文本（注入 system prompt）。</summary>
    public string Content { get; init; } = string.Empty;

    /// <summary>来源文件列表（含全局/项目/本地 + @include 展开）。</summary>
    public IReadOnlyList<string> Sources { get; init; } = Array.Empty<string>();

    /// <summary>是否有任何指令被加载。</summary>
    public bool HasInstructions => !string.IsNullOrWhiteSpace(Content);
}

/// <summary>
/// 项目指令加载器：三级目录扫描 + @include 嵌套（限 3 层）。
/// 加载顺序：
/// 1. ~/.parrocode/instructions.md（全局用户指令）
/// 2. ./PARROTCODE.md（项目根指令）
/// 3. ./.parrotcode/instructions.md（项目本地指令）
/// 每个文件支持 @include path/to/file.md 嵌套引用，限 3 层防无限递归。
/// </summary>
public sealed class InstructionLoader
{
    private readonly string _projectRoot;
    private readonly string _userHome;
    private readonly int _maxIncludeDepth;
    private readonly ILogger? _logger;

    // @include path/to/file.md 或 @include "path with spaces.md"
    private static readonly Regex IncludeRegex = new(
        @"@include\s+(?:""([^""]+)""|(\S+))",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public InstructionLoader(string? projectRoot = null, string? userHome = null, int maxIncludeDepth = 3, ILogger? logger = null)
    {
        _projectRoot = projectRoot ?? Directory.GetCurrentDirectory();
        _userHome = userHome ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        _maxIncludeDepth = maxIncludeDepth;
        _logger = logger;
    }

    /// <summary>
    /// 加载所有指令（三级目录扫描 + @include 展开）。
    /// </summary>
    public InstructionResult Load()
    {
        var sources = new List<string>();
        var sections = new List<string>();

        // 1. 全局用户指令
        var globalPath = Path.Combine(_userHome, ".parrocode", "instructions.md");
        var globalContent = TryReadWithIncludes(globalPath, depth: 0);
        if (globalContent is not null)
        {
            sections.Add("## 全局指令\n" + globalContent.Content);
            sources.AddRange(globalContent.Sources);
        }

        // 2. 项目根指令
        var projectPath = Path.Combine(_projectRoot, "PARROTCODE.md");
        var projectContent = TryReadWithIncludes(projectPath, depth: 0);
        if (projectContent is not null)
        {
            sections.Add("## 项目指令\n" + projectContent.Content);
            sources.AddRange(projectContent.Sources);
        }

        // 3. 项目本地指令
        var localPath = Path.Combine(_projectRoot, ".parrotcode", "instructions.md");
        var localContent = TryReadWithIncludes(localPath, depth: 0);
        if (localContent is not null)
        {
            sections.Add("## 本地指令\n" + localContent.Content);
            sources.AddRange(localContent.Sources);
        }

        return new InstructionResult
        {
            Content = string.Join("\n\n", sections),
            Sources = sources.Distinct().ToList()
        };
    }

    /// <summary>
    /// 读取文件并展开 @include 指令（递归，限 maxIncludeDepth 层）。
    /// </summary>
    private (string Content, List<string> Sources)? TryReadWithIncludes(string filePath, int depth)
    {
        if (!File.Exists(filePath))
            return null;

        if (depth > _maxIncludeDepth)
        {
            _logger?.LogWarning("@include 嵌套超过 {Max} 层，跳过 {File}", _maxIncludeDepth, filePath);
            return null;
        }

        string raw;
        try
        {
            raw = File.ReadAllText(filePath);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning("读取指令文件失败 {File}：{Error}", filePath, ex.Message);
            return null;
        }

        var sources = new List<string> { filePath };
        var content = new StringBuilder(raw);

        // 查找所有 @include 指令
        var matches = IncludeRegex.Matches(raw);
        // 从后往前替换（避免索引偏移）
        for (var i = matches.Count - 1; i >= 0; i--)
        {
            var match = matches[i];
            var includePath = match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Value;

            // 解析路径（相对当前文件所在目录）
            var basePath = Path.GetDirectoryName(filePath) ?? _projectRoot;
            var resolvedPath = Path.IsPathRooted(includePath)
                ? includePath
                : Path.GetFullPath(includePath, basePath);

            var included = TryReadWithIncludes(resolvedPath, depth + 1);
            if (included is not null)
            {
                content.Remove(match.Index, match.Length);
                content.Insert(match.Index, included.Value.Content);
                sources.AddRange(included.Value.Sources);
            }
            else
            {
                // @include 文件不存在——替换为提示
                var warning = $"[指令引用失败：{includePath}]";
                content.Remove(match.Index, match.Length);
                content.Insert(match.Index, warning);
                _logger?.LogWarning("@include 文件不存在：{Path}（引用自 {File}）", resolvedPath, filePath);
            }
        }

        return (content.ToString(), sources);
    }

    /// <summary>生成指令加载概要（/status 用）。</summary>
    public string GetSummary(InstructionResult result)
    {
        if (!result.HasInstructions) return "未加载";
        return $"{result.Sources.Count} 个文件：{string.Join(", ", result.Sources.Select(Path.GetFileName))}";
    }
}
```

### 6.12 TerminalApp 扩展（实现 IUiControl）

```csharp
// Tui/TerminalApp.cs — 扩展：实现 IUiControl + 注入命令系统

internal sealed class TerminalApp : IUiControl, IDisposable
{
    private readonly CommandRegistry _commandRegistry;
    private readonly CommandDispatcher _commandDispatcher;
    private readonly SessionStore _sessionStore;
    private readonly InstructionResult _instructions;
    private readonly string _instructionSummary;
    private readonly string _systemPromptWithInstructions;  // 含项目指令的 system prompt

    public TerminalApp(IBaseProvider provider,
                       ProviderConfig providerConfig,
                       AgentConfig? agentConfig,
                       TuiConfig? tuiConfig,
                       SecurityLevel securityLevel,
                       SecurityGuard securityGuard,
                       ContextCompressor compressor,
                       SessionStore sessionStore,           // 新增
                       InstructionResult instructions,     // 新增
                       ILogger? logger,
                       CancellationToken ct)
    {
        // ... 既有赋值 ...
        _sessionStore = sessionStore;
        _instructions = instructions;

        // 拼接 system prompt：默认 + 项目指令
        var basePrompt = agentConfig?.SystemPrompt ?? DefaultSystemPrompt;
        _systemPromptWithInstructions = instructions.HasInstructions
            ? basePrompt + "\n\n## 项目指令\n" + instructions.Content
            : basePrompt;

        // 构造命令系统
        _commandRegistry = new CommandRegistry(logger);
        // 手动注册需要依赖注入的命令
        _commandRegistry.Register(new HelpCommand(_commandRegistry));
        // 反射自动注册其余无参构造的命令
        _commandRegistry.AutoRegisterFromAssembly();
        _commandDispatcher = new CommandDispatcher(_commandRegistry);

        _instructionSummary = new InstructionLoader().GetSummary(instructions);
    }

    private static string DefaultSystemPrompt =>
        "你是 ParrotCode.Net 的 AI 编程助手。你可以调用工具读写文件、执行命令、搜索代码。" +
        "每次只调用必要的工具，拿到结果后用简洁中文回复用户。";

    /// <summary>
    /// HandleUserInput 改造：用 CommandDispatcher 分发。
    /// </summary>
    private async void HandleUserInput(string line)
    {
        if (string.IsNullOrWhiteSpace(line)) return;

        // Agent 正在运行时忽略新输入
        if (_agentTask is not null && !_agentTask.IsCompleted) return;

        // 命令分发
        var context = BuildCommandContext();
        var dispatchResult = await _commandDispatcher.DispatchAsync(line, context, _ct);
        if (dispatchResult.Handled)
        {
            if (dispatchResult.Output is not null)
                _chatView!.AppendStaticMessage(dispatchResult.Output);
            if (dispatchResult.ExitApp)
                Application.RequestStop(_top!);
            return;
        }

        // 非命令 → 走 AI
        _chatView!.AppendUserMessage(line);
        _history!.AddUser(line);
        _statusBarView!.CurrentRound = 0;
        _statusBarView.EstimatedTokens = _history.EstimatedTokens;
        StartAgentRound();
    }

    private CommandContext BuildCommandContext()
    {
        return new CommandContext(
            History: _history!,
            Compressor: _compressor,
            SecurityGuard: _securityGuard,
            Ui: this,
            SessionStore: _sessionStore,
            Ct: _ct)
        {
            ProviderConfig = _providerConfig,
            TuiConfig = _tuiConfig,
            AgentConfig = _agentConfig,
            InstructionSummary = _instructionSummary,
        };
    }

    private void StartAgentRound()
    {
        // ... 既有装配（不变）...

        var agentLoop = new AgentLoop(_provider,
                                      _registry!,
                                      batchExecutor,
                                      _agentConfig.MaxRounds ?? 10,
                                      _agentConfig.ToolChoice ?? "auto",
                                      _systemPromptWithInstructions,  // 改：用含指令的 prompt
                                      compressor: _compressor,
                                      logger: null);

        _agentTask = agentLoop.RunAsync(_history!, _sink, _ct);
    }

    // ===== IUiControl 实现 =====

    void IUiControl.AppendStaticMessage(string text) => _chatView!.AppendStaticMessage(text);
    void IUiControl.AppendUserMessage(string text) => _chatView!.AppendUserMessage(text);
    void IUiControl.ClearMessages() => _chatView!.ClearMessages();

    void IUiControl.RefreshStatusBar()
    {
        _statusBarView!.Update(_providerConfig, _securityGuard.Level, _tuiConfig, _registry!);
    }

    void IUiControl.UpdateTokenEstimate(int estimatedTokens)
    {
        _statusBarView!.EstimatedTokens = estimatedTokens;
    }

    void IUiControl.UpdateSecurityLevel(SecurityLevel level)
    {
        _securityLevel = level;  // 更新本地字段
        // StatusBarView 的 RefreshText 会读 _securityLevel——需要确保同步
        // 实际通过 RefreshStatusBar 刷新
    }

    void IUiControl.RequestExit() => Application.RequestStop(_top!);
}
```

### 6.13 InputFieldView 扩展（Tab 补全动态化）

```csharp
// Tui/InputFieldView.cs — 改造：Tab 补全命令名从 Registry 动态获取

internal sealed class InputFieldView : TextField
{
    private readonly Channel<string> _submitChannel = Channel.CreateUnbounded<string>();
    private List<string> _commands = new() { "/clear", "/exit", "/quit", "/help", "/status" };
    // ... 既有字段 ...

    /// <summary>设置命令名列表（含 / 前缀），供 Tab 补全。</summary>
    public void SetCommands(IReadOnlyList<string> commandNames)
    {
        _commands = commandNames
            .Select(n => n.StartsWith('/') ? n : "/" + n)
            .OrderBy(n => n)
            .ToList();
    }

    // CompleteCommand 方法不变（数据源 _commands 改为动态）
    private void CompleteCommand(string prefix)
    {
        // 确保 prefix 以 / 开头（补全比较时统一）
        var searchPrefix = prefix.StartsWith('/') ? prefix : "/" + prefix;
        var matches = _commands
            .Where(c => c.StartsWith(searchPrefix, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (matches.Length == 1)
        {
            Text = matches[0];
            CursorPosition = Text.Length;
            SetNeedsDraw();
        }
    }
}
```

> **注**：`TerminalApp.BuildLayout` 后调用 `_inputFieldView.SetCommands(_commandRegistry.GetAllNamesWithAliases())` 初始化补全列表。

### 6.14 AgentLoop 扩展（system prompt 注入）

```csharp
// Agent/AgentLoop.cs — 无代码改动（systemPrompt 参数已存在）
// 仅调用方（TerminalApp.StartAgentRound）传入的 systemPrompt 改为含项目指令的版本

internal sealed class AgentLoop
{
    // ... 既有构造函数（systemPrompt 参数已支持）...

    // BuildMessagesWithSystem 不变——每轮重新拼装 system prompt + 历史
    private IReadOnlyList<Message> BuildMessagesWithSystem(ConversationHistory history)
    {
        var snapshot = history.ToProviderMessages();
        if (string.IsNullOrEmpty(_systemPrompt))
            return snapshot;
        var withSystem = new List<Message>(snapshot.Count + 1)
        {
            new(MessageRole.System, _systemPrompt)
        };
        withSystem.AddRange(snapshot);
        return withSystem;
    }
}
```

> **关键**：`AgentLoop` 无需改动——`systemPrompt` 参数本就支持自定义。改造在 `TerminalApp.StartAgentRound`：传入 `_systemPromptWithInstructions` 而非 `_agentConfig.SystemPrompt`。每轮 `BuildMessagesWithSystem` 重新拼装，项目指令始终在 system prompt 头部，不受压缩影响（压缩只动历史，不动 system prompt）。

### 6.15 Config 扩展

```csharp
// Config/Models.cs — 新增 SessionConfig + InstructionsConfig

/// <summary>
/// 会话持久化配置（迭代 10 新增）。null 时用默认值。
/// </summary>
public sealed record SessionConfig
{
    /// <summary>会话存储目录。默认 ".parrotcode/sessions"（项目根下）。</summary>
    public string? StorageDir { get; init; }

    /// <summary>是否启用会话持久化。默认 true。false 时 /session 命令不可用。</summary>
    public bool? Enable { get; init; }
}

/// <summary>
/// 项目指令配置（迭代 10 新增）。null 时用默认值。
/// </summary>
public sealed record InstructionsConfig
{
    /// <summary>是否启用项目指令加载。默认 true。false 时不扫描任何指令文件。</summary>
    public bool? Enable { get; init; }

    /// <summary>@include 最大嵌套深度。默认 3。</summary>
    public int? MaxIncludeDepth { get; init; }

    /// <summary>自定义项目指令文件路径（覆盖默认的 ./PARROTCODE.md）。</summary>
    public string? ProjectInstructionsPath { get; init; }
}

// AppConfig 扩展
public sealed record AppConfig
{
    // ... 既有字段 ...
    /// <summary>会话持久化配置（迭代 10 新增）。null 时用默认值。</summary>
    public SessionConfig? Session { get; init; }

    /// <summary>项目指令配置（迭代 10 新增）。null 时用默认值。</summary>
    public InstructionsConfig? Instructions { get; init; }
}
```

**示例 YAML**（`example.parrotcode.yaml` 追加）：

```yaml
# 迭代 10 新增：会话持久化配置（全部可选，省略时用默认值）
session:
  enable: true                        # 是否启用会话持久化
  storage_dir: .parrotcode/sessions   # 会话存储目录（相对项目根）

# 迭代 10 新增：项目指令配置（全部可选，省略时用默认值）
instructions:
  enable: true                        # 是否启用项目指令加载
  max_include_depth: 3                # @include 最大嵌套深度
  # project_instructions_path: ./PARROTCODE.md  # 自定义项目指令路径（默认 ./PARROTCODE.md）
```

### 6.16 App 装配扩展

```csharp
// App/App.cs — 扩展：构造 SessionStore / InstructionLoader / CommandRegistry

public async Task RunAsync()
{
    var tuiConfig = _config.Tui ?? new TuiConfig();
    var securityLevel = ParseSecurityLevel(_config.Security?.Level);

    // ... 既有 SecurityGuard + ContextCompressor 构造（不变）...

    var projectRoot = Directory.GetCurrentDirectory();

    // 【迭代 10】构造 SessionStore
    var sessionConfig = _config.Session ?? new SessionConfig();
    var sessionStore = new SessionStore(
        storageDir: sessionConfig.StorageDir ?? ".parrotcode/sessions",
        _logger);

    // 【迭代 10】加载项目指令
    var instructionsConfig = _config.Instructions ?? new InstructionsConfig();
    InstructionResult instructions = new();
    if (instructionsConfig.Enable ?? true)
    {
        var loader = new InstructionLoader(
            projectRoot: projectRoot,
            maxIncludeDepth: instructionsConfig.MaxIncludeDepth ?? 3,
            _logger);
        instructions = loader.Load();
        if (instructions.HasInstructions)
            _logger.LogInformation("已加载项目指令：{Count} 个文件", instructions.Sources.Count);
    }

    using var terminalApp = new TerminalApp(_provider,
                                            _providerConfig,
                                            _config.Agent,
                                            tuiConfig,
                                            securityLevel,
                                            securityGuard,
                                            compressor,
                                            sessionStore,        // 新增
                                            instructions,        // 新增
                                            _logger,
                                            _ct);
    await terminalApp.RunAsync();
}
```

---

## 七、验收标准

### 7.1 编译与测试

| 编号 | 标准 | 验证方式 |
|------|------|---------|
| 10-01 | `dotnet build` 0 错误 0 警告 | 编译 |
| 10-02 | 全量测试全绿（9 + 现有 + 10 新增） | `dotnet test` |
| 10-03 | `CommandRegistryTests` 全绿 | `dotnet test` |
| 10-04 | `CommandParserTests` 全绿 | `dotnet test` |
| 10-05 | `CommandDispatcherTests` 全绿 | `dotnet test` |
| 10-06 | `SessionStoreTests` 全绿 | `dotnet test` |
| 10-07 | `InstructionLoaderTests` 全绿 | `dotnet test` |
| 10-08 | 各内置命令测试全绿（Help/Clear/Compress/Mode/Status/Session/Exit） | `dotnet test` |
| 10-09 | 现有 `AgentLoopTests` / `CompressorTests` 不回归 | `dotnet test` |

### 7.2 CommandRegistry

| 编号 | 标准 | 验证方式 |
|------|------|---------|
| 10-10 | `Register` 注册命令后 `Find(name)` 返回该命令 | 单测 |
| 10-11 | `Register` 注册别名后 `Find(alias)` 返回同一命令实例 | 单测 |
| 10-12 | 重复注册同名命令抛 `InvalidOperationException` | 单测 |
| 10-13 | 别名冲突抛 `InvalidOperationException` | 单测 |
| 10-14 | `AutoRegisterFromAssembly` 自动扫描 `ICommand` 实现类 | 单测 |
| 10-15 | `AutoRegisterFromAssembly` 跳过接口和抽象类 | 单测 |
| 10-16 | `AutoRegisterFromAssembly` 无参构造失败的类跳过不崩溃 | 单测 |
| 10-17 | `GetVisibleCommands` 只返回 `Type == System` 的命令 | 单测 |
| 10-18 | `GetAllNamesWithAliases` 含命令名和别名 | 单测 |
| 10-19 | `Find` 未找到返回 null | 单测 |
| 10-20 | 大小写不敏感查找（`/HELP` 等价 `/help`） | 单测 |

### 7.3 CommandParser

| 编号 | 标准 | 验证方式 |
|------|------|---------|
| 10-25 | 非 `/` 开头返回 null | 单测 |
| 10-26 | 空字符串返回 null | 单测 |
| 10-27 | `/clear` → `("clear", "")` | 单测 |
| 10-28 | `/mode strict` → `("mode", "strict")` | 单测 |
| 10-29 | `/session save my-title` → `("session", "save my-title")` | 单测 |
| 10-30 | `/help` 末尾无空格 → `("help", "")` | 单测 |
| 10-31 | `SplitArgs` 正确分割空格分隔参数 | 单测 |
| 10-32 | `SplitArgs` 支持双引号包裹含空格参数 | 单测 |
| 10-33 | `SplitArgs` 空字符串返回空数组 | 单测 |

### 7.4 CommandDispatcher

| 编号 | 标准 | 验证方式 |
|------|------|---------|
| 10-35 | 非 `/` 前缀返回 `NotHandled` | 单测 |
| 10-36 | 已注册命令正确分发并返回结果 | 单测（用 mock ICommand） |
| 10-37 | 未注册命令返回 `WithOutput("未知命令...")` | 单测 |
| 10-38 | 命令抛异常时返回 `WithOutput("[!] 执行命令失败...")` | 单测 |
| 10-39 | 命令异常不传播到调用方 | 单测 |
| 10-40 | CancellationToken 取消时向上传播 `OperationCanceledException` | 单测 |

### 7.5 内置命令

| 编号 | 标准 | 验证方式 |
|------|------|---------|
| 10-45 | `/help` 输出含所有可见命令的 Usage + Description | 单测 |
| 10-46 | `/help` 不列出 Hidden 命令 | 单测 |
| 10-47 | `/clear` 清空 `History` 和 `Ui` | 单测（mock IUiControl） |
| 10-48 | `/clear` 调用 `Compressor.ResetWarning` | 单测 |
| 10-49 | `/compress` 手动触发 `CheckAndCompressAsync` | 单测 |
| 10-50 | `/compress` 熔断器 open 时先 Reset 再触发 | 单测 |
| 10-51 | `/compress` 成功后输出压缩消息 | 单测 |
| 10-52 | `/compress` 无需压缩时输出"当前无需压缩" | 单测 |
| 10-53 | `/mode` 无参数显示当前等级 | 单测 |
| 10-54 | `/mode strict` 切换 `SecurityGuard.Level` 为 Strict | 单测 |
| 10-55 | `/mode normal` / `/mode permissive` 分别切换 | 单测 |
| 10-56 | `/mode invalid` 无效值回退 Normal（`SecurityLevelParser.Parse` 兜底） | 单测 |
| 10-57 | `/mode` 切换后调用 `Ui.UpdateSecurityLevel` + `Ui.RefreshStatusBar` | 单测 |
| 10-58 | `/status` 输出含 provider/model/security/rounds/tokens | 单测 |
| 10-59 | `/session save` 保存消息到 JSONL 文件 | 单测（临时目录） |
| 10-60 | `/session save` 生成 Meta 文件 | 单测 |
| 10-61 | `/session save` 标题为首条用户消息前 50 字符 | 单测 |
| 10-62 | `/session load <id>` 加载消息到 History | 单测 |
| 10-63 | `/session load` 清空当前历史再加载 | 单测 |
| 10-64 | `/session load` 不存在的 ID 抛 `FileNotFoundException` | 单测 |
| 10-65 | `/session list` 列出所有会话按时间倒序 | 单测 |
| 10-66 | `/session list` 空时返回"无已保存会话" | 单测 |
| 10-67 | `/session current` 返回"未持久化"提示 | 单测 |
| 10-68 | `/exit` 返回 `ExitApp=true` | 单测 |
| 10-69 | `/quit`（别名）等价 `/exit` | 单测 |

### 7.6 SessionStore（JSONL）

| 编号 | 标准 | 验证方式 |
|------|------|---------|
| 10-75 | `SaveAsync` 生成 `{id}.jsonl` 文件 | 单测（临时目录） |
| 10-76 | `SaveAsync` 生成 `{id}.meta.json` 文件 | 单测 |
| 10-77 | JSONL 每行一条消息 JSON，可独立解析 | 单测 |
| 10-78 | `LoadAsync` 正确恢复所有消息 | 单测 |
| 10-79 | `LoadAsync` 恢复的消息含 ToolCalls 和 ToolCallId | 单测 |
| 10-80 | `LoadAsync` 损坏行跳过不抛异常 | 单测（手动构造损坏 JSONL） |
| 10-81 | `LoadAsync` 损坏行记日志 | 单测 |
| 10-82 | `LoadAsync` 未配对 tool_use 截断到最后完整状态 | 单测 |
| 10-83 | `LoadAsync` 配对完整的 tool_use 不被截断 | 单测 |
| 10-84 | `ListAsync` 按 UpdatedAt 倒序 | 单测 |
| 10-85 | `ListAsync` 空目录返回空列表 | 单测 |
| 10-86 | `ListAsync` 损坏 Meta 文件跳过不崩溃 | 单测 |
| 10-87 | Meta 文件含 Id/CreatedAt/UpdatedAt/MessageCount/ProviderName/ModelName/Title | 单测 |
| 10-88 | 存储目录不存在时 `SaveAsync` 自动创建 | 单测 |
| 10-89 | `MessageDto` 正确序列化/反序列化所有 MessageRole | 单测 |
| 10-90 | `MessageDto` 正确序列化/反序列化 ToolCalls（含 Input JSON） | 单测 |

### 7.7 InstructionLoader

| 编号 | 标准 | 验证方式 |
|------|------|---------|
| 10-95 | 无指令文件时返回 `HasInstructions=false` | 单测（临时目录） |
| 10-96 | 项目根有 `PARROTCODE.md` 时加载其内容 | 单测 |
| 10-97 | 全局 `~/.parrocode/instructions.md` 被加载 | 单测 |
| 10-98 | 本地 `.parrotcode/instructions.md` 被加载 | 单测 |
| 10-99 | 三级指令合并后 Content 含全部三段 | 单测 |
| 10-100 | `@include path/to/file.md` 正确展开子文件内容 | 单测 |
| 10-101 | `@include "path with spaces.md"` 支持带空格路径 | 单测 |
| 10-102 | `@include` 嵌套超过 3 层时跳过并记日志 | 单测 |
| 10-103 | `@include` 引用不存在的文件时替换为提示文本 | 单测 |
| 10-104 | `@include` 相对路径基于引用文件所在目录解析 | 单测 |
| 10-105 | `@include` 绝对路径直接使用 | 单测 |
| 10-106 | `Sources` 列表含所有加载的文件路径（含 @include 展开） | 单测 |
| 10-107 | `Enable=false` 时不加载任何指令 | 单测（通过 App 装配验证） |

### 7.8 IUiControl + TerminalApp 集成

| 编号 | 标准 | 验证方式 |
|------|------|---------|
| 10-110 | `TerminalApp` 实现 `IUiControl` 所有方法 | 编译 |
| 10-111 | `HandleUserInput` 用 `CommandDispatcher` 分发 | 代码审查 |
| 10-112 | `/exit` 通过 `IUiControl.RequestExit` 退出 | 手动 |
| 10-113 | `/clear` 清空对话区 + 历史 + 重置压缩器警告 | 手动 |
| 10-114 | `/mode strict` 后状态栏显示 `security=Strict` | 手动 |
| 10-115 | `/status` 输出当前配置概要到对话区 | 手动 |
| 10-116 | `/help` 列出所有命令 | 手动 |
| 10-117 | `/compress` 手动触发压缩，输出压缩结果 | 手动 |
| 10-118 | 未知命令 `/foobar` 输出"未知命令"提示 | 手动 |
| 10-119 | 非命令输入正常走 AI 对话 | 手动 |
| 10-120 | Agent 运行时输入被忽略（命令和对话都忽略） | 手动 |

### 7.9 InputFieldView Tab 补全

| 编号 | 标准 | 验证方式 |
|------|------|---------|
| 10-125 | `SetCommands` 设置命令名列表 | 单测 |
| 10-126 | Tab 补全从动态列表查找 | 单测 |
| 10-127 | 唯一匹配自动填充 | 单测 |
| 10-128 | 多匹配不填充 | 单测 |
| 10-129 | 补全列表含别名（如 `/quit`） | 手动 |

### 7.10 项目指令注入

| 编号 | 标准 | 验证方式 |
|------|------|---------|
| 10-135 | 项目根放 `PARROTCODE.md`，AI 回复体现对约定的遵守 | 手动 |
| 10-136 | 指令内容出现在 system prompt（每轮） | 代码审查 / 日志 |
| 10-137 | 压缩后指令仍生效（system prompt 不受压缩影响） | 手动 |
| 10-138 | `/clear` 后指令仍生效（system prompt 每轮重建） | 手动 |
| 10-139 | `@include` 展开的子文件内容出现在 system prompt | 手动 |

### 7.11 配置解析

| 编号 | 标准 | 验证方式 |
|------|------|---------|
| 10-145 | `session:` 段正确解析为 `SessionConfig` | 单测 |
| 10-146 | `instructions:` 段正确解析为 `InstructionsConfig` | 单测 |
| 10-147 | 无 `session:` 段时用默认值（`.parrotcode/sessions` / enable=true） | 单测 |
| 10-148 | 无 `instructions:` 段时用默认值（enable=true / depth=3） | 单测 |
| 10-149 | `session.enable: false` 时 `/session` 命令返回"未启用" | 单测 |

### 7.12 端到端

| 编号 | 标准 | 验证方式 |
|------|------|---------|
| 10-155 | `/session save` 后退出程序，重启后 `/session list` 能看到该会话 | 手动 |
| 10-156 | `/session load <id>` 恢复历史消息到对话区 | 手动 |
| 10-157 | 恢复的会话能继续对话（AI 记得历史） | 手动 |
| 10-158 | 恢复 30 分钟前的会话时显示时间跨度提醒 | 手动 |
| 10-159 | 恢复的会话含工具调用历史（ToolCalls 完整） | 手动 |
| 10-160 | `PARROTCODE.md` 中写"用中文回复"，AI 遵守 | 手动 |
| 10-161 | `@include` 引用的子文件约定被 AI 遵守 | 手动 |
| 10-162 | `/mode permissive` 后 write_file 不弹 HITL | 手动 |
| 10-163 | `/mode strict` 后 read 项目外文件被拦 | 手动 |
| 10-164 | 现有 9 功能不受影响（截断/摘要/熔断） | 手动回归 |
| 10-165 | 现有 8c 功能不受影响（安全层/HITL） | 手动回归 |
| 10-166 | 现有 7c 功能不受影响（流式渲染/Spinner） | 手动回归 |

---

## 八、测试计划

| 测试文件 | 覆盖范围 | 预估用例数 |
|---------|---------|-----------|
| `CommandRegistryTests.cs` | 注册/查找/别名/冲突/反射扫描/可见性 | ~11 |
| `CommandParserTests.cs` | Parse + SplitArgs 各种输入 | ~9 |
| `CommandDispatcherTests.cs` | 分发/未注册/异常/取消 | ~6 |
| `HelpCommandTests.cs` | 列出命令/Hidden 过滤 | ~3 |
| `ClearCommandTests.cs` | 清空 History/UI/ResetWarning | ~3 |
| `CompressCommandTests.cs` | 手动触发/熔断器 reset/无阈值 | ~4 |
| `ModeCommandTests.cs` | 查看/切换/无效值/刷新状态栏 | ~5 |
| `StatusCommandTests.cs` | 输出格式/各字段 | ~2 |
| `SessionCommandTests.cs` | save/load/list/current/子命令错误 | ~8 |
| `ExitCommandTests.cs` | ExitApp=true | ~2 |
| `SessionStoreTests.cs` | 保存/加载/损坏行/配对修复/列表/Meta | ~16 |
| `InstructionLoaderTests.cs` | 三级扫描/@include/嵌套深度/路径解析 | ~13 |
| `SessionConfigTests.cs` | YAML 解析/默认值 | ~3 |
| `InstructionsConfigTests.cs` | YAML 解析/默认值 | ~3 |
| `InputFieldViewTests.cs`（补充） | SetCommands/动态补全 | ~3 |

**端到端手动测试清单**（对照 10-155 到 10-166）：

1. **会话持久化验证**：
   - 启动程序，对话 3 轮（含工具调用）
   - `/session save`
   - 记录会话 ID，`/exit` 退出
   - 重新启动，`/session list` 看到该会话
   - `/session load <id>`，验证历史消息恢复到对话区
   - 继续对话，验证 AI 记得历史

2. **时间跨度提醒验证**：
   - 保存会话后等待 30 分钟以上（或改系统时间）
   - `/session load <id>`，验证显示"这是 X 小时前的会话"

3. **项目指令验证**：
   - 项目根创建 `PARROTCODE.md`，写"所有回复以'收到'结尾"
   - 启动程序，对话，验证 AI 回复以"收到"结尾
   - `@include` 引用子文件，验证子文件约定生效

4. **命令系统验证**：
   - `/help` 列出所有命令
   - `/mode strict` 后状态栏显示 `security=Strict`
   - `/mode permissive` 后 write_file 不弹 HITL
   - `/compress` 手动触发压缩
   - `/status` 显示配置概要
   - `/foobar`（未知命令）显示提示

5. **崩溃恢复验证**：
   - 手动在 `.parrotcode/sessions/xxx.jsonl` 末尾加一行损坏 JSON
   - `/session load xxx`，验证损坏行跳过、其他消息正常加载

6. **回归验证**：
   - 9 功能：截断/摘要/熔断正常
   - 8c 功能：安全层/HITL 正常
   - 7c 功能：流式输出/Spinner 正常

---

## 九、实施步骤

### Step 1：命令系统骨架（CommandType + ICommand + CommandContext + CommandResult + Registry + Parser + Dispatcher + IUiControl）

- 新建 `Commands/CommandType.cs` / `ICommand.cs` / `CommandContext.cs` / `CommandResult.cs`
- 新建 `Commands/CommandRegistry.cs` / `CommandParser.cs` / `CommandDispatcher.cs`
- 新建 `Tui/IUiControl.cs`
- 新建 `CommandRegistryTests.cs` / `CommandParserTests.cs` / `CommandDispatcherTests.cs`
- 验证：`dotnet build` + `dotnet test` 全绿（命令骨架可独立测试）

### Step 2：内置命令实现（7 个命令）

- 新建 `Commands/Builtin/HelpCommand.cs` / `ClearCommand.cs` / `CompressCommand.cs` / `ModeCommand.cs` / `StatusCommand.cs` / `SessionCommand.cs` / `ExitCommand.cs`
- 新建各命令的测试文件
- 验证：`dotnet build` + `dotnet test` 全绿（用 mock IUiControl / SessionStore）

### Step 3：SessionStore（JSONL 持久化）

- 新建 `Storage/SessionStore.cs` / `SessionMeta.cs` / `SessionSummary.cs`（含 `MessageDto` / `ToolCallDto`）
- 新建 `SessionStoreTests.cs`（用临时目录）
- `Config/Models.cs` 加 `SessionConfig`
- `example.parrotcode.yaml` 加 `session:` 段
- 验证：`dotnet build` + `dotnet test` 全绿

### Step 4：InstructionLoader（项目指令）

- 新建 `Instructions/InstructionLoader.cs` / `InstructionResult.cs`
- 新建 `InstructionLoaderTests.cs`（用临时目录）
- `Config/Models.cs` 加 `InstructionsConfig`
- `example.parrotcode.yaml` 加 `instructions:` 段
- 验证：`dotnet build` + `dotnet test` 全绿

### Step 5：TerminalApp 集成 + App 装配 + 端到端

- `Tui/TerminalApp.cs` 实现 `IUiControl`；`HandleUserInput` 改用 `CommandDispatcher`；注入 `SessionStore` / `InstructionResult`
- `Tui/InputFieldView.cs` 改 Tab 补全数据源为动态
- `App/App.cs` 构造 `SessionStore` / `InstructionLoader` / `CommandRegistry` 传入 `TerminalApp`
- `Agent/AgentLoop.cs` 无改动（systemPrompt 参数已支持）
- `InputFieldViewTests.cs` 补充动态补全测试
- 验证：`dotnet build` 0 警告 + `dotnet test` 全绿
- 端到端手动验收（对照 10-155 到 10-166）
- 标记迭代 10 [已完成]

---

## 十、风险与对策

| 风险 | 概率 | 影响 | 对策 |
|------|------|------|------|
| 反射自动注册扫描到测试程序集的 ICommand | 低 | 低 | `AutoRegisterFromAssembly` 默认扫描 `Assembly.GetExecutingAssembly()`（主程序集），不含测试程序集 |
| `HelpCommand` 需依赖注入无法无参构造 | 中 | 中 | 手动 `Register(new HelpCommand(registry))` 后再 `AutoRegisterFromAssembly`（自动注册跳过已注册的） |
| JSONL 文件并发写入冲突 | 低 | 中 | 本迭代单会话单线程，无并发；`SaveAsync` 用 `FileShare.None` 独占写 |
| `@include` 循环引用导致无限递归 | 中 | 高 | `maxIncludeDepth=3` 限制；超出记日志并跳过 |
| `@include` 路径遍历（`../../../etc/passwd`） | 低 | 中 | 相对路径基于引用文件目录解析，但允许 `..`（项目指令是受信任内容，类似 `.cursorrules`）；安全敏感场景可在 `InstructionLoader` 加路径白名单 |
| 恢复的会话含压缩后的 system 消息（`[结构化摘要]`） | 中 | 低 | `SessionCommand.LoadAsync` 渲染时跳过 `MessageRole.System`（不显示到 UI）；历史正常传递给 AgentLoop |
| 恢复会话后 token 立即超阈值触发自动压缩 | 中 | 低 | `LoadAsync` 后 `Compressor.ResetWarning()`；若仍超 90% 会在下一轮自动摘要——这是预期行为 |
| `/mode` 切换后既有 `SecureBatchToolExecutor` 不感知 | 低 | 低 | `SecurityGuard.Level` 是可变属性，`SecureBatchToolExecutor` 每次执行都读 `guard.Level`（迭代 8b 已预留） |
| 命令系统替换硬编码后 `/exit` `/clear` `/help` 行为变化 | 低 | 中 | `ExitCommand` / `ClearCommand` / `HelpCommand` 保持等价语义；端到端回归验证 |
| `MessageDto` 的 `Input` JSON 反序列化丢失 `JsonElement` 原始 `ValueKind` | 低 | 低 | `ToMessage` 用 `JsonDocument.Parse` 重建 `JsonElement`，`Clone()` 确保生命周期独立 |
| 大历史会话 `SaveAsync` 阻塞 UI | 中 | 中 | `SaveAsync` 是 async，但文件写入仍可能耗时；本迭代接受（消息数通常 < 100）；后续可加进度提示 |

---

## 十一、与后续迭代的关系

### 11.1 迭代 11（MCP 协议客户端）

- MCP 工具调用同样产生 `tool_use` / `tool_result`——JSONL 持久化无需改动（`MessageDto` 协议中性）。
- MCP 工具名含 `{server_name}/{tool_name}` 前缀——`MessageDto` 存原始字符串，不影响。
- `/status` 命令可扩展显示 MCP server 连接状态。

### 11.2 迭代 12（Skill + Hook + 子 Agent）

- **Skill 系统**：新增 `/skill` 命令（`CommandType.System`），自动注册到 Registry。Skill 的 SOP 作为 system prompt 注入——与项目指令注入机制一致。
- **Hook 引擎**：`tool_pre_exec` / `tool_post_exec` Hook 可在命令执行前/后触发。`/clear` `/mode` 等命令可配 Hook（如 `/clear` 前自动 `/session save`）。
- **子 Agent**：Fork 式子 Agent 继承父历史——可 `/session save` 父会话后 Fork。子 Agent 的 JSONL 独立（如需要）。
- **`AllowPermanent` 持久化**：HITL 的永久允许可持久化到 `.parrocode/permissions.json`，跨会话加载。与 SessionStore 同级目录。

### 11.3 进阶练习（本迭代之后）

- **会话自动恢复**：启动时加载上次会话（需持久化"上次会话 ID"到 `.parrocode/last_session.json`）。
- **会话导出**：`/session export <id> markdown` 导出为 Markdown 对话记录。
- **会话搜索**：`/session search <keyword>` 在历史会话中搜索。
- **摘要缓存持久化**：迭代 9 的摘要结果缓存到 `.parrotcode/summaries/`，与 SessionStore 协同。
- **指令热重载**：文件变化时自动重新加载指令（`FileSystemWatcher`）。

---

## 十二、关键设计决策记录

### Q1：为什么命令系统用反射自动注册而非手写 register 列表？

**手写方案**：
```csharp
registry.Register(new HelpCommand(registry));
registry.Register(new ClearCommand());
registry.Register(new CompressCommand());
// ... 每加一个命令改一处
```

**问题**：新增命令需改 `TerminalApp` 构造函数的 register 列表，容易遗漏。

**反射方案**：`AutoRegisterFromAssembly` 扫描所有 `ICommand` 实现类，无参构造的自动实例化注册。新增命令只需新建类文件，自动被扫描。

**取舍**：
- 例外：`HelpCommand` 需要 `CommandRegistry` 依赖注入（列出所有命令），手动注册后再自动扫描（已注册的跳过）。
- 性能：反射扫描只在启动时执行一次，开销可忽略。
- 可测试性：`AutoRegisterFromAssembly` 可单测（用测试程序集验证扫描行为）。

### Q2：为什么 JSONL 每行一条消息而非 JSON 数组？

**JSON 数组方案**：
```json
[
  {"role":"user","content":"你好"},
  {"role":"assistant","content":"..."}
]
```

**问题**：
- 追加写需要重写整个文件（解析数组 → append → 序列化 → 覆盖写）。
- 崩溃时可能损坏整个数组（JSON 语法错误 → 全部消息丢失）。

**JSONL 方案**：每行一条消息 JSON，`FileStream.Append` 追加写。
- 追加写 O(1)，不需要重写。
- 崩溃时最多丢最后一行（损坏行跳过，其他行不受影响）。
- 可流式读取（`ReadLineAsync` 逐行处理，不需要全量加载到内存）。

**取舍**：`SaveAsync` 当前用 `FileMode.Create` 全量写（因为是快照保存）。后续如需增量追加（每轮自动保存），改用 `FileMode.Append` 即可——JSONL 格式天然支持。

### Q3：为什么 SessionStore 用独立的 MessageDto 而非 MessageExtensions.ToOpenAiWire？

**ToOpenAiWire 方案**：复用现有序列化方法。

**问题**：
- `ToOpenAiWire` 输出 OpenAI wire format（`role` / `content` / `tool_calls` / `tool_call_id`），与协议耦合。
- `ToolCall.Input` 序列化为 `arguments` 字符串（OpenAI 风格），反序列化时需特殊处理。
- 未来支持 Anthropic 协议时 wire format 不同，SessionStore 不应依赖具体协议。

**MessageDto 方案**：协议中性的 DTO，`Role` / `Content` / `ToolCalls` / `ToolCallId` 字段名与 `Message` record 一致。`ToolCall.Input` 序列化为 `Input` 字符串（原始 JSON），反序列化时 `JsonDocument.Parse` 重建 `JsonElement`。

**取舍**：多一个 DTO 类，但解耦协议层。SessionStore 只负责持久化中性消息结构，不关心 wire format。

### Q4：为什么 `/compress` 熔断器 open 时先 Reset 再触发？

**不 Reset 方案**：熔断器 open 时 `/compress` 直接返回"已禁用"。

**问题**：用户无法通过手动触发恢复熔断器——即使网络已恢复，仍需重启程序。

**Reset 方案**：`/compress` 是用户主动操作，表明用户想压缩。先 `ResetCircuit()` 清零计数，再 `CheckAndCompressAsync`。成功则熔断器保持关闭；失败则重新计数（可能再次 open）。

**取舍**：给用户"再试一次"的机会。自动触发不 Reset（保护系统不雪崩），手动触发 Reset（信任用户决策）。

### Q5：为什么项目指令注入 system prompt 而非历史？

**注入历史方案**：把指令作为第一条 `MessageRole.System` 消息存入 `ConversationHistory`。

**问题**：
- 压缩时 system 消息会被摘要（迭代 9 的 `StructuredSummarizer` 对所有消息摘要）——指令丢失。
- `/clear` 清空历史后指令丢失。
- 每轮 `BuildMessagesWithSystem` 重复拼装——指令在历史中重复出现。

**注入 system prompt 方案**：指令拼接到 `_systemPrompt`，每轮 `BuildMessagesWithSystem` 重新构造 `system prompt + 历史`。指令始终在头部，不受压缩/清空影响。

**取舍**：`AgentLoop._systemPrompt` 在构造时固定，指令在 `TerminalApp` 构造时加载拼接。如需热重载（文件变化时重新加载），需重建 `AgentLoop`——本迭代不支持，留作进阶练习。

### Q6：为什么 `/session load` 不自动保存当前历史？

**自动保存方案**：`/session load` 前自动 `/session save` 当前历史。

**问题**：
- 用户可能不想保存当前历史（测试/临时对话）。
- 自动保存的会话 ID 不返回给用户——用户不知道去哪找。
- 增加复杂度（需处理自动保存失败）。

**不自动保存方案**：`/session load` 直接清空当前历史再加载。用户需主动 `/session save`。

**取舍**：简化交互。`/session load` 时如果当前历史非空，可提示"当前历史将被覆盖，请先 /session save"——本迭代简化为直接覆盖，后续可加确认（HITL 风格内联提示）。

### Q7：为什么 @include 限制 3 层而非更深？

- 3 层足够覆盖常见场景：主指令 → 通用规范 → 具体条目。
- 更深嵌套增加调试难度（循环引用检测靠深度限制）。
- 可经 `InstructionsConfig.MaxIncludeDepth` 配置。

### Q8：为什么 `HelpCommand` 需要特殊处理（手动注册）？

`HelpCommand` 需要列出所有命令，依赖 `CommandRegistry`。反射自动扫描用无参构造，无法注入 `CommandRegistry`。

**解决方案**：
1. 手动 `Register(new HelpCommand(registry))` 注册。
2. `AutoRegisterFromAssembly` 检测到 `HelpCommand` 已注册（按 Name 判断），跳过。

**替代方案**：`HelpCommand` 不依赖 `CommandRegistry`，改为 `CommandContext` 暴露 `Registry` 属性。但 `CommandContext` 已经有 7 个字段，再加一个臃肿。选择手动注册。

---

**文档结束**。状态：[设计完成，待实现]
