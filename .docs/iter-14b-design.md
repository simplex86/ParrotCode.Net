# 迭代 14b：SubAgentRunner + sub_agent 工具 + 装配

> **状态**：[设计完成，待实现]
> **前置迭代**：14a [已完成]（角色系统 + 三层工具过滤）
> **后续迭代**：15（Hook 引擎——sub_agent 动作依赖本迭代的 SubAgentRunner）
> **总览文档**：[iter-14-design.md](./iter-14-design.md)
> **关联文档**：[iter-14a-design.md](./iter-14a-design.md)

---

## 一、子迭代目标

### 1.1 核心目标

交付子 Agent 的执行层和集成层——基于 14a 的角色系统，实现 `SubAgentRunner`（复用 `AgentLoop` 跑嵌套循环）+ `sub_agent` 工具 + 装配。

1. **两种运行模式**：
   - **定义式（Definitional）**：空白对话 + 角色 SOP 作为 system prompt。子 Agent 不继承父上下文，从零开始执行任务。
   - **Fork 式（Fork）**：继承父对话历史副本 + 注入强硬指令。子 Agent 能看到父 Agent 的完整上下文，但被约束为"不创建子 worker、不与用户对话、直接干活、结构化报告 ≤ 500 字"。

2. **`SubAgentRunner`**：创建嵌套 `AgentLoop` 实例（独立 history / registry / sink / system prompt），复用同一 Provider / SecurityGuard。子 Agent 用 `NullHitlGate` 自主运行（安全层仍生效）。

3. **`CollectingEventSink`**：收集子 Agent 事件不渲染到 TUI，提取最终报告。

4. **`sub_agent` 工具**：LLM 自主调用，参数 `task`（必填）+ `role`（可选）+ `mode`（可选）。同步阻塞——子 Agent 完成后报告作为 `ToolResult` 返回主对话。

5. **`BackgroundTaskManager`**：后台任务基础设施预留（本迭代不暴露 `background` 参数）。

6. **`AgentLoop` 零改动**：子 Agent 是 `AgentLoop` 的嵌套实例，不感知"被嵌套调用"。

### 1.2 本迭代要验证的关键问题

| 问题 | 验证方式 | 失败对策 |
|------|---------|---------|
| 子 Agent 是否复用 `AgentLoop` 跑嵌套循环 | 代码审查 + 单测 | `SubAgentRunner` 新建 `AgentLoop` 实例，传入独立 history / registry / sink |
| 定义式模式子 Agent 是否不继承父历史 | 单测：fork history ≠ parent history | `Definitional` 模式新建空 `ConversationHistory` |
| Fork 模式子 Agent 是否继承父历史 | 单测：fork history 内容 = parent history 快照 | `ConversationHistory.ReplaceMessages(parent.ToProviderMessages())` |
| Fork 模式是否不修改父历史 | 单测：子 Agent 运行后父历史消息数不变 | fork 是副本（`ReplaceMessages` 拷贝），子 Agent 写 fork 不写 parent |
| MaxRounds 时报告是否非空 | 单测：子 Agent maxRounds=2，LLM 每轮都调工具 | `CollectingEventSink` 缓存 `AssistantMessageEvent` 文本作为 fallback |
| `sub_agent` 工具是否被全局禁止嵌套 | 单测：子 Agent 的工具列表不含 `sub_agent` | `ToolFilter.Build` 始终排除 `sub_agent`（14a 已实现） |
| 子 Agent 是否不触发 HITL（自主运行） | 单测：子 Agent 的 BatchToolExecutor 用 NullHitlGate | `SubAgentRunner` 构造 `SecureBatchToolExecutor(hitlGate: new NullHitlGate())` |
| 子 Agent 报告是否作为 ToolResult 返回主对话 | 端到端 | `SubAgentTool.ExecuteAsync` 返回 `ToolResult.Ok(report)` |
| 子 Agent 是否复用父 SecurityGuard | 代码审查 | `SubAgentRunner` 构造 `SecureBatchToolExecutor(guard: _securityGuard)` |
| `sub_agent.enable: false` 时工具不注册 | 单测 | `TerminalApp.RunAsync` 条件注册 |

### 1.3 非目标（14b 明确不做）

- ❌ 不做真正的异步后台模式（`background=true`）——`BackgroundTaskManager` 作为基础设施预留，但 `sub_agent` 工具 schema 不暴露 `background` 参数
- ❌ 不做子 Agent 的进度实时展示——子 Agent 事件被 `CollectingEventSink` 吞掉
- ❌ 不做子 Agent 的 HITL——子 Agent 用 `NullHitlGate` 自主运行（安全层仍生效）
- ❌ 不做子 Agent 的上下文压缩——子 Agent 历史短（maxRounds=5），不需要 `ContextCompressor`
- ❌ 不做子 Agent 的 Skill 系统——子 Agent 不注册 `skill_loader`（Fork 模式）或由角色 tools_deny 决定（Definitional 模式）
- ❌ 不做 Git Worktree 隔离——进阶练习
- ❌ 不做 `/subagent` 斜杠命令——`sub_agent` 是 LLM 自主调用的工具

### 1.4 与既有系统的衔接策略

- **复用 `AgentLoop`**：子 Agent 是 `AgentLoop` 的嵌套实例——独立 history / registry / system prompt / sink，同一 Provider / SecurityGuard。`AgentLoop` 零改动
- **复用 `ToolRegistry` / `ToolExecutor` / `SecureBatchToolExecutor`**：子 Agent 用 14a 的 `ToolFilter.Build` 从父 ToolRegistry 构建过滤副本
- **复用 `SecurityGuard`**：子 Agent 的 `SecureBatchToolExecutor` 注入父 `SecurityGuard`（同安全等级、同沙箱）
- **复用 `IBaseProvider`**：子 Agent 用同一 Provider 实例（同一 LLM 连接 / 同一 API key）
- **复用 14a 的 `RoleRegistry` / `ToolFilter`**：14b 直接消费
- **`AgentEvent` 零扩展**：子 Agent 的事件被 `CollectingEventSink` 吞掉，不产生新事件类型
- **条件注入**：`sub_agent.enable: false` 时 `App.cs` 不构造 `RoleRegistry`，`TerminalApp` 不注册 `sub_agent` 工具

---

## 二、文件改动清单

### 2.1 新增文件（4 个）

```
SubAgent/
├── Models.cs                  # SubAgentRequest / SubAgentResult / SubAgentMode（追加到 14a 的 Models.cs）
├── Runner.cs                   # SubAgentRunner + CollectingEventSink（internal）
├── Manager.cs                  # BackgroundTaskManager + BackgroundTask（基础设施，预留异步扩展）
└── SubAgentTool.cs             # sub_agent 工具（继承 ToolBase）
```

> **注**：`Models.cs` 在 14a 已创建（角色部分），14b 追加 SubAgent 运行时类型。`SubAgentMode` 枚举在 14a 已定义（供 `ToolFilter` 使用）。

### 2.2 修改文件（5 个）

| 文件 | 改动 |
|------|------|
| `Config/Models.cs` | 新增 `SubAgentConfig` + `AppConfig.SubAgent` 字段 |
| `App/App.cs` | 构造 `RoleLoader` → `RoleRegistry`，传入 `TerminalApp`；条件注入（`enable: false` 时传 null） |
| `Tui/TerminalApp.cs` | 构造函数加 `RoleRegistry?` + `SubAgentConfig?` 参数；`RunAsync` 中构造 `SubAgentRunner` + 注册 `SubAgentTool` |
| `Security/SecurityGuard.cs` | `SystemTools` 白名单加 `"sub_agent"`（1 行，防御性豁免） |
| `example.parrotcode.yaml` | 新增 `sub_agent:` 配置节示例 |

### 2.3 不变文件

- `Agent/AgentLoop.cs`——**零改动**（新建实例复用，不感知嵌套）
- `Agent/BatchToolExecutor.cs` / `Security/SecureBatchToolExecutor.cs`——**零改动**（新建实例复用）
- `Agent/AgentEvent.cs`——**零改动**（`CollectingEventSink` 消费现有事件类型，不新增）
- `Agent/ChannelEventSink.cs`——**零改动**（`CollectingEventSink` 是独立新类）
- `Conversation/History.cs`——**零改动**（`ReplaceMessages` 已有，用于 Fork 副本）
- `Tools/ToolBase.cs` / `ToolRegistry.cs` / `ToolExecutor.cs`——**零改动**（复用）
- `Commands/CommandContext.cs`——**零改动**（`sub_agent` 是工具不是命令）
- `Skills/`——**零改动**（子 Agent 不加载 Skill，角色系统在 14a 已独立完成）
- `Tui/IUiControl.cs` / `HitlPrompt.cs` / `IHitlGate.cs`——**零改动**（子 Agent 用 `NullHitlGate`）
- `SubAgent/Filter.cs` / `Roles/`——**零改动**（14a 已完成）

---

## 三、详细设计

### 3.1 数据模型（`SubAgent/Models.cs` 追加）

```csharp
namespace ParrotCode;

/// <summary>
/// 子 Agent 运行模式。
/// 注：此枚举在 14a 已定义（供 ToolFilter 使用），此处仅展示完整定义。
/// </summary>
public enum SubAgentMode
{
    /// <summary>
    /// 定义式：空白对话 + 角色 SOP 作为 system prompt。
    /// 不继承父上下文，从零开始执行任务。适合独立子任务。
    /// </summary>
    Definitional,

    /// <summary>
    /// Fork 式：继承父对话历史 + 注入强硬指令。
    /// 子 Agent 能看到父上下文，但被约束为不创建子 worker、不对话、直接干活、结构化报告。
    /// </summary>
    Fork
}

/// <summary>
/// 子 Agent 请求（sub_agent 工具的参数载体）。
/// </summary>
public sealed record SubAgentRequest
{
    /// <summary>任务描述（作为子 Agent 的 user 消息）。必填。</summary>
    public required string Task { get; init; }

    /// <summary>角色名（definitional 模式用，默认 general）。角色定义 system prompt + 工具过滤。</summary>
    public string Role { get; init; } = "general";

    /// <summary>运行模式。默认 Definitional。</summary>
    public SubAgentMode Mode { get; init; } = SubAgentMode.Definitional;
}

/// <summary>
/// 子 Agent 运行结果。
/// </summary>
public sealed record SubAgentResult
{
    public bool Success { get; init; }

    /// <summary>子 Agent 的最终报告（AgentDoneEvent.FinalText 或 MaxRounds 时的 LastAssistantText，可能截断）。</summary>
    public string? Report { get; init; }

    /// <summary>失败原因。</summary>
    public string? Error { get; init; }

    /// <summary>子 Agent 实际执行的轮次数。</summary>
    public int RoundsUsed { get; init; }
}
```

### 3.2 CollectingEventSink（`SubAgent/Runner.cs` 内部类）

> **⚠️ 设计修正（源码审查发现）**：
> 原设计假设 MaxRounds 时 `FinalText` 有值，但 [AgentLoop.cs:198-200](file:///d:/cs/ParrotCode.Net/ParrotCode.Net/Agent/AgentLoop.cs#L198-L200) 显示 MaxRounds 路径**只发 `MaxRoundsReachedEvent`，不发 `AgentDoneEvent`**。因此 `CollectingEventSink` 必须额外监听 `AssistantMessageEvent`，缓存最后一轮 assistant 文本作为 fallback。

子 Agent 的事件消费者——收集事件但不渲染到 TUI。只关心最终报告和轮次计数。

```csharp
/// <summary>
/// 收集型事件 Sink：收集子 Agent 事件，不渲染到 TUI。
/// 提取最终报告 + 轮次计数。
///
/// 报告来源（优先级）：
/// 1. AgentDoneEvent.FinalText（正常完成——LLM 不再调工具）
/// 2. LastAssistantText（MaxRounds 兜底——缓存每轮 AssistantMessageEvent 的文本）
/// 3. ErrorEvent 转错误报告
///
/// 子 Agent 的中间事件（TextDelta / ToolCall / ToolResult 等）被丢弃——
/// 主 Agent 只看最终报告（作为 sub_agent 工具的 ToolResult）。
/// </summary>
internal sealed class CollectingEventSink : IAgentEventSink
{
    /// <summary>
    /// 正常完成时的最终文本（AgentDoneEvent 设置）。
    /// MaxRounds 路径不会设置此字段。
    /// </summary>
    public string? FinalText { get; private set; }

    /// <summary>
    /// 最后一轮 assistant 文本（AssistantMessageEvent 缓存）。
    /// 作为 MaxRounds 时的 fallback 报告——AgentLoop 在 MaxRounds 路径不发 AgentDoneEvent，
    /// 但每轮都会发 AssistantMessageEvent（含本轮 LLM 的完整回复）。
    /// </summary>
    public string? LastAssistantText { get; private set; }

    public int RoundsCompleted { get; private set; }

    public ValueTask WriteAsync(AgentEvent evt, CancellationToken cancellationToken)
    {
        switch (evt)
        {
            case AgentEvent.AgentDoneEvent(var text):
                // 正常完成——最终文本优先
                FinalText = text;
                break;
            case AgentEvent.AssistantMessageEvent(var content):
                // 缓存每轮 assistant 文本——MaxRounds 时作为 fallback 报告
                LastAssistantText = content;
                break;
            case AgentEvent.RoundEndEvent:
                RoundsCompleted++;
                break;
            case AgentEvent.ErrorEvent(var msg, _):
                FinalText = $"[子 Agent 错误] {msg}";
                break;
            // MaxRoundsReachedEvent / CancelledEvent 不改变 FinalText
            // MaxRounds 时由 SubAgentRunner 用 LastAssistantText 兜底
        }
        return ValueTask.CompletedTask;
    }

    public void Complete() { /* 无资源释放 */ }
}
```

**关键设计点**：
- `AgentDoneEvent`（正常完成）优先——`FinalText` 是 LLM 的最终回复
- `AssistantMessageEvent` 每轮都缓存——MaxRounds 时取最后一轮作为报告
- `ErrorEvent` 覆盖 `FinalText`——错误转为错误报告
- `SubAgentRunner` 提取报告：`sink.FinalText ?? sink.LastAssistantText ?? string.Empty`

### 3.3 SubAgentRunner（`SubAgent/Runner.cs`）

核心编排器——创建嵌套 `AgentLoop` 实例，运行子 Agent，收集报告。

```csharp
using System.Text;
using Microsoft.Extensions.Logging;

namespace ParrotCode;

/// <summary>
/// 子 Agent 运行器：创建嵌套 AgentLoop 实例执行子任务。
/// 复用父 Provider / SecurityGuard，新建独立 history / registry / sink / system prompt。
/// 子 Agent 用 NullHitlGate（自主运行，不问用户）；安全层仍生效（黑名单 + 沙箱）。
/// </summary>
public sealed class SubAgentRunner
{
    private readonly IBaseProvider _provider;
    private readonly ToolRegistry _parentRegistry;
    private readonly SecurityGuard _securityGuard;
    private readonly RoleRegistry _roleRegistry;
    private readonly SubAgentConfig _config;
    private readonly ILogger? _logger;

    public SubAgentRunner(IBaseProvider provider,
                          ToolRegistry parentRegistry,
                          SecurityGuard securityGuard,
                          RoleRegistry roleRegistry,
                          SubAgentConfig config,
                          ILogger? logger = null)
    {
        _provider = provider ?? throw new ArgumentNullException(nameof(provider));
        _parentRegistry = parentRegistry ?? throw new ArgumentNullException(nameof(parentRegistry));
        _securityGuard = securityGuard ?? throw new ArgumentNullException(nameof(securityGuard));
        _roleRegistry = roleRegistry ?? throw new ArgumentNullException(nameof(roleRegistry));
        _config = config ?? new SubAgentConfig();
        _logger = logger;
    }

    /// <summary>
    /// 同步运行子 Agent，返回报告。
    /// 阻塞调用方直到子 Agent 完成（AgentDone 或 MaxRounds）。
    /// </summary>
    public async Task<SubAgentResult> RunAsync(
        SubAgentRequest request,
        ConversationHistory? parentHistory,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        // 1. 获取角色定义
        var role = _roleRegistry.Get(request.Role);
        if (role is null)
            return new SubAgentResult { Success = false, Error = $"未找到角色：{request.Role}" };

        // 2. 构建过滤后的工具注册表（14a 的 ToolFilter）
        var filteredRegistry = ToolFilter.Build(_parentRegistry, role, request.Mode);

        // 3. 构建子 Agent 的 history
        var history = BuildHistory(request, parentHistory);

        // 4. 构建 system prompt
        var systemPrompt = BuildSystemPrompt(request, role);

        // 5. 构建子 Agent 的 BatchToolExecutor（NullHitlGate，无交互）
        var executor = new ToolExecutor(
            filteredRegistry,
            TimeSpan.FromSeconds(30),
            _logger);
        var batchExecutor = new SecureBatchToolExecutor(
            executor,
            filteredRegistry,
            _securityGuard,
            maxParallelism: 5,
            hitlGate: new NullHitlGate(),  // 子 Agent 不问用户
            logger: _logger);

        // 6. 构建收集型 EventSink
        var sink = new CollectingEventSink();

        // 7. 构建并运行 AgentLoop（嵌套实例，零改动 AgentLoop 类）
        var maxRounds = _config.MaxRounds ?? 5;
        var loop = new AgentLoop(
            _provider,
            filteredRegistry,
            batchExecutor,
            maxRounds: maxRounds,
            toolChoice: "auto",
            systemPrompt: systemPrompt,
            compressor: null,  // 子 Agent 不做压缩
            logger: null);  // 不给 logger，避免 stderr 交错（与主 AgentLoop 一致的硬约束）

        _logger?.LogInformation("启动子 Agent：role={Role}, mode={Mode}, maxRounds={Max}",
            request.Role, request.Mode, maxRounds);

        await loop.RunAsync(history, sink, cancellationToken);

        // 8. 提取报告（FinalText 优先，MaxRounds 时用 LastAssistantText 兜底）
        var report = sink.FinalText ?? sink.LastAssistantText ?? string.Empty;
        var maxChars = _config.ReportMaxChars ?? 2000;
        if (report.Length > maxChars)
        {
            report = report[..maxChars] + "\n\n...（报告过长，已截断）";
            _logger?.LogWarning("子 Agent 报告截断：{Orig} → {Max}", report.Length, maxChars);
        }

        _logger?.LogInformation("子 Agent 完成：role={Role}, rounds={Rounds}, report={Len} 字符",
            request.Role, sink.RoundsCompleted, report.Length);

        return new SubAgentResult
        {
            Success = true,
            Report = report,
            RoundsUsed = sink.RoundsCompleted
        };
    }

    /// <summary>
    /// 构建子 Agent 的对话历史。
    /// Definitional：空 history + task 作为首条 user 消息。
    /// Fork：复制父 history + task 作为追加 user 消息。
    ///
    /// Fork 副本隔离安全性（源码审查验证）：
    /// - ToProviderMessages() 返回 _messages.ToArray()——数组是新数组
    /// - ReplaceMessages(messages) 清空 + AddRange——浅拷贝（共享 Message 引用）
    /// - Message 是 sealed record + init 属性——不可变，子 Agent 无法修改已有消息
    /// - 子 Agent 只能通过 AddUser/AddAssistant 在自己的 _messages 末尾追加，不影响 parent
    /// </summary>
    private ConversationHistory BuildHistory(
        SubAgentRequest request, ConversationHistory? parentHistory)
    {
        var history = new ConversationHistory();

        if (request.Mode == SubAgentMode.Fork && parentHistory is not null)
        {
            // Fork：复制父历史（副本，不修改父）
            history.ReplaceMessages(parentHistory.ToProviderMessages());
        }

        // 追加任务作为 user 消息
        history.AddUser(request.Task);
        return history;
    }

    /// <summary>
    /// 构建子 Agent 的 system prompt。
    /// Definitional：角色 SOP 正文 + 子 Agent 约束。
    /// Fork：Fork 指令 + 子 Agent 约束（不含角色 SOP，角色仅用于工具过滤）。
    /// </summary>
    private static string BuildSystemPrompt(SubAgentRequest request, RoleDefinition role)
    {
        var sb = new StringBuilder();

        if (request.Mode == SubAgentMode.Definitional)
        {
            // Definitional：角色 SOP 是 system prompt 主体
            sb.AppendLine(role.Body);
        }
        else
        {
            // Fork：角色 SOP 不注入（角色仅用于工具过滤），用 Fork 指令
            sb.AppendLine("你是一个 Fork 子 Agent，继承了父 Agent 的对话上下文。");
            sb.AppendLine("请在父上下文基础上完成分配的子任务。");
        }

        // 子 Agent 通用约束（两种模式都追加）
        sb.AppendLine();
        sb.AppendLine("## 子 Agent 严格约束");
        sb.AppendLine("1. 不要调用 sub_agent 工具（禁止创建子 worker，防止无限递归）");
        sb.AppendLine("2. 不要与用户对话（你是子任务执行者，不是对话者）");
        sb.AppendLine("3. 直接完成分配的任务，不要询问澄清");
        sb.AppendLine("4. 完成后输出结构化报告，不超过 500 字");
        sb.AppendLine("5. 报告应包含：执行摘要、关键发现/产出、结论");

        return sb.ToString();
    }
}
```

**关键设计点**：

1. **`AgentLoop` 零改动**：`SubAgentRunner` 新建 `AgentLoop` 实例，传入独立 history / registry / sink / system prompt。`AgentLoop` 不感知"被嵌套调用"——它就是跑一个普通的 ReAct 循环。源码审查确认 `AgentLoop` 构造函数签名（[AgentLoop.cs:25-43](file:///d:/cs/ParrotCode.Net/ParrotCode.Net/Agent/AgentLoop.cs#L25-L43)）支持 `systemPrompt` / `compressor` / `logger` 参数。

2. **NullHitlGate**：子 Agent 用 `NullHitlGate`——自主运行，不问用户。安全层（`SecurityGuard`）仍生效：黑名单命令被拦、路径沙箱生效。

3. **Fork 不修改父历史**（源码审查验证）：`history.ReplaceMessages(parentHistory.ToProviderMessages())` 创建父历史的浅拷贝。[History.cs:99-102](file:///d:/cs/ParrotCode.Net/ParrotCode.Net/Conversation/History.cs#L99-L102) `ToProviderMessages` 返回 `_messages.ToArray()`（新数组），[MessageTypes.cs:33-44](file:///d:/cs/ParrotCode.Net/ParrotCode.Net/Providers/MessageTypes.cs#L33-L44) `Message` 是 `sealed record` + `init` 属性（不可变）。子 Agent 的所有写操作（`AddUser` / `AddAssistant` / `AddTool`）都在副本 list 末尾追加，不影响父历史的 list。

4. **报告提取优先级**：`sink.FinalText ?? sink.LastAssistantText ?? string.Empty`——正常完成用 `FinalText`，MaxRounds 兜底用 `LastAssistantText`，都没有则空字符串。

5. **报告截断**：`ReportMaxChars` 默认 2000 字符。超长截断 + 提示。

6. **`compressor: null`**：子 Agent 不做上下文压缩——maxRounds=5，历史短，不会触发压缩阈值。

7. **`logger: null`**：子 Agent 的 `AgentLoop` 不传 logger，避免 stderr 日志在 TUI 模式下交错（与主 AgentLoop 一致的硬约束）。`SubAgentRunner` 自身仍用 `_logger` 记录启动/完成日志。

### 3.4 SubAgentTool（`SubAgent/SubAgentTool.cs`）

`sub_agent` 工具——LLM 自主调用委派子任务。

```csharp
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace ParrotCode;

/// <summary>
/// sub_agent 工具（迭代 14b）：LLM 调用此工具委派子任务给子 Agent。
/// 子 Agent 独立运行（独立 history / 工具子集 / system prompt），完成后报告作为 ToolResult 返回。
/// Category=Read（工具本身幂等——子 Agent 的副作用由其内部工具控制，sub_agent 只编排）。
/// SecurityGuard 天然豁免：非 run_command（不触发黑名单），参数为 task/role/mode（不匹配 path/cwd）。
/// </summary>
public sealed class SubAgentTool : ToolBase
{
    /// <inheritdoc/>
    public override string Name => "sub_agent";

    /// <inheritdoc/>
    public override string Description =>
        "委派子任务给子 Agent 执行。子 Agent 拥有独立的对话上下文和工具子集，" +
        "完成后返回结构化报告（≤500字）。适合探索、规划、分析等可独立完成的子任务。" +
        "mode='definitional'（默认）：空白对话+角色SOP；mode='fork'：继承当前对话上下文。";

    /// <inheritdoc/>
    public override ToolCategory Category => ToolCategory.Read;

    /// <inheritdoc/>
    public override IReadOnlyList<ToolParameter> Parameters { get; } = new[]
    {
        new ToolParameter("task", "string",
            "子任务描述（清晰、自包含的任务指令）", Required: true),
        new ToolParameter("role", "string",
            "角色名（explorer=只读探索 / planner=只读规划 / general=通用，默认 general）",
            Required: false),
        new ToolParameter("mode", "string",
            "运行模式（definitional=空白对话+角色SOP / fork=继承当前上下文，默认 definitional）",
            Required: false)
    };

    private readonly SubAgentRunner _runner;
    private readonly ConversationHistory _parentHistory;

    public SubAgentTool(SubAgentRunner runner, ConversationHistory parentHistory)
    {
        _runner = runner ?? throw new ArgumentNullException(nameof(runner));
        _parentHistory = parentHistory ?? throw new ArgumentNullException(nameof(parentHistory));
    }

    /// <inheritdoc/>
    public override async Task<ToolResult> ExecuteAsync(JsonElement input, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var task = GetRequiredString(input, "task", out var err1);
        if (err1 is not null) return ToolResult.Fail(err1);

        var role = GetOptionalString(input, "role", out var err2, "general");
        if (err2 is not null) return ToolResult.Fail(err2);

        var modeStr = GetOptionalString(input, "mode", out var err3, "definitional");
        if (err3 is not null) return ToolResult.Fail(err3);

        if (string.IsNullOrWhiteSpace(task))
            return ToolResult.Fail("参数 task 不能为空");

        // 解析 mode（大小写不敏感）
        if (!Enum.TryParse<SubAgentMode>(modeStr, ignoreCase: true, out var mode))
            return ToolResult.Fail($"参数 mode 无效：{modeStr}（可选值：definitional / fork）");

        var request = new SubAgentRequest
        {
            Task = task,
            Role = role,
            Mode = mode
        };

        // Fork 模式传父历史，Definitional 模式传 null（不继承）
        var parentHistory = mode == SubAgentMode.Fork ? _parentHistory : null;

        var result = await _runner.RunAsync(request, parentHistory, cancellationToken);

        if (!result.Success)
            return ToolResult.Fail(result.Error ?? "子 Agent 执行失败");

        // 报告作为 ToolResult 返回，AgentLoop 会把它入 history（后续轮主 Agent 可见）
        var report = result.Report ?? string.Empty;
        var reportWithMeta = $"[子 Agent 报告 | 角色={role} | 模式={mode} | 轮次={result.RoundsUsed}]\n\n{report}";
        return ToolResult.Ok(reportWithMeta);
    }
}
```

**关键设计点**：

1. **Category=Read**：`sub_agent` 工具本身是幂等的编排操作——它不直接产生副作用（子 Agent 的副作用由其内部工具控制）。设为 Read 让它能与其他 Read 工具并发执行（如果主 Agent 同时发起多个 sub_agent 调用）。

2. **`_parentHistory` 引用的线程安全**（源码审查验证）：[BatchToolExecutor.cs:102-111](file:///d:/cs/ParrotCode.Net/ParrotCode.Net/Agent/BatchToolExecutor.cs#L102-L111) Read 组用 `Task.WhenAll` 并发。如果主 Agent 同时调 2 个 sub_agent（都是 Read 组），两个子 Agent 并发运行。但主 AgentLoop 在 `await _batchExecutor.ExecuteAsync(...)` 期间不写 history（[AgentLoop.cs:152](file:///d:/cs/ParrotCode.Net/ParrotCode.Net/Agent/AgentLoop.cs#L152)），所以两个子 Agent 并发读 `_parentHistory.ToProviderMessages()` 安全（只读 + ToArray 创建新数组）。两个子 Agent 用各自的 `history` 实例，互不影响。

3. **报告含元信息**：返回的报告前加 `[子 Agent 报告 | 角色=... | 模式=... | 轮次=...]` 头。

4. **SecurityGuard 豁免**：`sub_agent` 参数是 `task` / `role` / `mode`（字符串），不匹配 `path` / `cwd`，不触发沙箱。非 `run_command`，不触发黑名单。仍加入 `SystemTools` 白名单作为防御性编程（见 3.7）。

### 3.5 BackgroundTaskManager（`SubAgent/Manager.cs`）

后台任务基础设施——本迭代作为预留，`sub_agent` 工具 schema 不暴露 `background` 参数。

```csharp
using System.Collections.Concurrent;

namespace ParrotCode;

/// <summary>
/// 后台任务状态。
/// </summary>
public enum BackgroundTaskStatus
{
    Running,
    Completed,
    Failed
}

/// <summary>
/// 后台任务条目。
/// </summary>
internal sealed class BackgroundTask
{
    public string TaskId { get; }
    public SubAgentRequest Request { get; }
    public BackgroundTaskStatus Status { get; private set; } = BackgroundTaskStatus.Running;
    public SubAgentResult? Result { get; private set; }
    public string? Error { get; private set; }
    public DateTime StartedAt { get; } = DateTime.UtcNow;
    public DateTime? CompletedAt { get; private set; }

    public BackgroundTask(string taskId, SubAgentRequest request)
    {
        TaskId = taskId;
        Request = request;
    }

    public void Complete(SubAgentResult result)
    {
        Status = BackgroundTaskStatus.Completed;
        Result = result;
        CompletedAt = DateTime.UtcNow;
    }

    public void Fail(string error)
    {
        Status = BackgroundTaskStatus.Failed;
        Error = error;
        CompletedAt = DateTime.UtcNow;
    }
}

/// <summary>
/// 后台任务管理器：管理异步子 Agent 任务。
/// 本迭代作为基础设施预留——sub_agent 工具仅支持同步模式。
/// 后台模式（background=true）的接线留进阶练习：
///   1. sub_agent 工具调 StartTask 返回 taskId
///   2. 主 Agent 下一轮调 sub_agent 时，GetCompletedReports 的报告注入 ToolResult
///   3. 或新增 /tasks 命令查看后台任务状态
/// </summary>
public sealed class BackgroundTaskManager
{
    private readonly SubAgentRunner _runner;
    private readonly ConcurrentDictionary<string, BackgroundTask> _tasks = new();
    private readonly int _maxConcurrent;
    private readonly ILogger? _logger;

    public BackgroundTaskManager(SubAgentRunner runner,
                                  int maxConcurrent = 3,
                                  ILogger? logger = null)
    {
        _runner = runner ?? throw new ArgumentNullException(nameof(runner));
        _maxConcurrent = maxConcurrent;
        _logger = logger;
    }

    /// <summary>
    /// 启动后台子 Agent 任务。立即返回 taskId，不阻塞。
    /// </summary>
    public string StartTask(SubAgentRequest request,
                             ConversationHistory? parentHistory,
                             CancellationToken cancellationToken)
    {
        // 并发数检查
        var running = _tasks.Values.Count(t => t.Status == BackgroundTaskStatus.Running);
        if (running >= _maxConcurrent)
            throw new InvalidOperationException($"已达到最大并发后台任务数 {_maxConcurrent}");

        var taskId = Guid.NewGuid().ToString("N")[..8];
        var task = new BackgroundTask(taskId, request);
        _tasks[taskId] = task;

        _ = Task.Run(async () =>
        {
            try
            {
                var result = await _runner.RunAsync(request, parentHistory, cancellationToken);
                task.Complete(result);
            }
            catch (Exception ex)
            {
                task.Fail(ex.Message);
                _logger?.LogWarning(ex, "后台子 Agent 任务 {Id} 失败", taskId);
            }
        }, cancellationToken);

        _logger?.LogInformation("启动后台子 Agent 任务 {Id}：role={Role}", taskId, request.Role);
        return taskId;
    }

    /// <summary>
    /// 获取所有已完成任务的报告（含成功和失败）。
    /// 调用方负责把报告注入主对话 history。
    /// </summary>
    public IReadOnlyList<(string TaskId, SubAgentResult Result)> GetCompletedReports()
    {
        return _tasks.Values
            .Where(t => t.Status == BackgroundTaskStatus.Completed && t.Result is not null)
            .Select(t => (t.TaskId, t.Result!))
            .ToList();
    }

    /// <summary>
    /// 查询任务状态。
    /// </summary>
    public (BackgroundTaskStatus Status, SubAgentResult? Result, string? Error) GetStatus(string taskId)
    {
        if (!_tasks.TryGetValue(taskId, out var task))
            throw new ArgumentException($"未找到任务：{taskId}", nameof(taskId));
        return (task.Status, task.Result, task.Error);
    }
}
```

### 3.6 SubAgentConfig（`Config/Models.cs` 新增）

```csharp
/// <summary>
/// 子 Agent 系统配置（迭代 14 新增）。所有字段可选，缺省用默认值。
/// </summary>
public sealed record SubAgentConfig
{
    /// <summary>是否启用子 Agent 系统。默认 true。false 时 sub_agent 工具不注册。</summary>
    public bool? Enable { get; init; }

    /// <summary>子 Agent 最大轮次。默认 5（比主 Agent 的 10 少，防止失控）。</summary>
    public int? MaxRounds { get; init; }

    /// <summary>报告最大字符数。默认 2000（超长截断 + 提示）。</summary>
    public int? ReportMaxChars { get; init; }

    /// <summary>最大并发后台任务数。默认 3（BackgroundTaskManager 用）。</summary>
    public int? MaxConcurrentBackground { get; init; }
}
```

`AppConfig` 新增字段：

```csharp
/// <summary>子 Agent 系统配置（迭代 14 新增）。null 时用默认值。</summary>
public SubAgentConfig? SubAgent { get; init; }
```

`example.parrotcode.yaml` 新增：

```yaml
# 迭代 14 新增：子 Agent 系统（sub_agent 工具）
sub_agent:
  enable: true                     # 是否启用子 Agent（false 时 sub_agent 工具不注册）
  max_rounds: 5                    # 子 Agent 最大轮次（比主 Agent 少，防止失控）
  report_max_chars: 2000           # 报告最大字符数（超长截断）
  max_concurrent_background: 3     # 最大并发后台任务数（后台模式预留）
```

### 3.7 SecurityGuard 改动（`Security/SecurityGuard.cs`）

`SystemTools` 白名单加 `"sub_agent"`（1 行，防御性豁免）：

```csharp
private static readonly HashSet<string> SystemTools = new(StringComparer.Ordinal)
{
    "skill_loader",
    "sub_agent"  // 迭代 14 新增：sub_agent 是编排工具，参数为 task/role/mode，不匹配 path/cwd
};
```

> **设计说明**：`sub_agent` 的参数是 `task` / `role` / `mode`（字符串），不匹配 `path` / `cwd`，不触发沙箱。非 `run_command`，不触发黑名单。加入 `SystemTools` 是防御性编程——避免未来参数变更（如加 `cwd` 参数让子 Agent 在指定目录工作）破坏豁免。子 Agent **内部**的工具调用正常走安全层（`SecureBatchToolExecutor` 注入了 `SecurityGuard`）。

### 3.8 App.cs 装配

```csharp
// 【迭代 14】构造子 Agent 角色系统
var subAgentConfig = _config.SubAgent ?? new SubAgentConfig();
RoleRegistry? roleRegistry = null;
if (subAgentConfig.Enable ?? true)
{
    var roleLoader = new RoleLoader(projectRoot: projectRoot, logger: _logger);
    var roles = roleLoader.Load();
    roleRegistry = new RoleRegistry(roles);
    if (roles.Count > 0)
        _logger.LogInformation("已加载 {Count} 个子 Agent 角色", roles.Count);
}

using var terminalApp = new TerminalApp(_provider,
                                        _providerConfig,
                                        _config.Agent,
                                        tuiConfig,
                                        securityLevel,
                                        securityGuard,
                                        compressor,
                                        sessionStore,
                                        instructions,         // 10c
                                        mcpManager,           // 11c
                                        skillRegistry,        // 迭代 12
                                        skillExecutor,        // 迭代 12
                                        roleRegistry,         // 迭代 14 新增
                                        subAgentConfig,       // 迭代 14 新增
                                        _logger,
                                        _ct);
```

### 3.9 TerminalApp 改动

构造函数新增 `RoleRegistry?` + `SubAgentConfig?` 参数。`RunAsync` 中构造 `SubAgentRunner` + 注册 `SubAgentTool`：

```csharp
// TerminalApp 构造函数新增字段
private readonly RoleRegistry? _roleRegistry;
private readonly SubAgentConfig _subAgentConfig;

// RunAsync 中，注册工具阶段（在 skill_loader 注册之后）：
if (_roleRegistry is not null && (_subAgentConfig.Enable ?? true))
{
    var runner = new SubAgentRunner(_provider,
                                    _registry,
                                    _securityGuard,
                                    _roleRegistry,
                                    _subAgentConfig,
                                    logger: null);  // TUI 模式 logger 传 null
    _registry.Register(new SubAgentTool(runner, _history!));
}
```

> **设计说明**：`SubAgentRunner` 在 `RunAsync` 中构造（而非构造函数），因为它需要 `_registry`（ToolRegistry）和 `_history`（ConversationHistory），两者都在 `RunAsync` 中创建。这与 `SkillTool` 的注册时机一致。

---

## 四、子 Agent 运行时序

### 4.1 Definitional 模式时序

```
主 Agent 对话：
  用户输入 → AgentLoop.RunAsync（主）
    └─ LLM 决定调用 sub_agent(task="探索项目结构", role="explorer", mode="definitional")
         └─ SubAgentTool.ExecuteAsync
              └─ SubAgentRunner.RunAsync
                   ├─ 1. 获取角色：RoleRegistry.Get("explorer") → RoleDefinition
                   ├─ 2. 工具过滤：ToolFilter.Build(parentRegistry, explorer, Definitional)
                   │    → filteredRegistry（含 read_file/glob/grep/run_command，排除 write_file/sub_agent/skill_loader）
                   ├─ 3. 构建 history：空 ConversationHistory + AddUser("探索项目结构")
                   ├─ 4. 构建 system prompt：explorer.Body + 子 Agent 约束
                   ├─ 5. 构建 BatchToolExecutor：SecureBatchToolExecutor(NullHitlGate)
                   ├─ 6. CollectingEventSink
                   └─ 7. AgentLoop.RunAsync（子，嵌套）
                        ├─ Round 1: LLM 调 glob → AssistantMessageEvent(文本1) → 结果入子 history
                        ├─ Round 2: LLM 调 read_file → AssistantMessageEvent(文本2) → 结果入子 history
                        ├─ Round 3: LLM 调 grep → AssistantMessageEvent(文本3) → 结果入子 history
                        └─ Round 4: LLM 不调工具 → AgentDoneEvent(FinalText=报告)
                                                          ↑ CollectingEventSink.FinalText 被设置
                   └─ 8. 提取报告：sink.FinalText ?? sink.LastAssistantText → 截断到 2000 字符
              └─ ToolResult.Ok("[子 Agent 报告 | 角色=explorer | ...]\n\n{报告}")
         └─ AgentLoop（主）把 ToolResult 入主 history
    └─ 后续轮主 Agent 看到报告，继续工作
```

### 4.2 Fork 模式时序

```
主 Agent 对话：
  用户输入 → AgentLoop.RunAsync（主）
    └─ LLM 决定调用 sub_agent(task="基于当前对话，总结我们讨论的架构决策", mode="fork")
         └─ SubAgentTool.ExecuteAsync
              └─ SubAgentRunner.RunAsync
                   ├─ 1. 获取角色：RoleRegistry.Get("general")（默认）
                   ├─ 2. 工具过滤：ToolFilter.Build(parentRegistry, general, Fork)
                   │    → filteredRegistry（排除 sub_agent + skill_loader）
                   ├─ 3. 构建 history：
                   │    history = new ConversationHistory()
                   │    history.ReplaceMessages(parentHistory.ToProviderMessages())  ← Fork 副本（浅拷贝但 Message 不可变）
                   │    history.AddUser("基于当前对话，总结我们讨论的架构决策")
                   ├─ 4. 构建 system prompt：Fork 指令 + 子 Agent 约束（不含角色 SOP）
                   ├─ 5-7. 同 Definitional（AgentLoop 嵌套运行）
                   └─ 8. 提取报告
              └─ ToolResult.Ok("[子 Agent 报告 | 角色=general | 模式=fork | ...]\n\n{报告}")
         └─ AgentLoop（主）把 ToolResult 入主 history
```

### 4.3 MaxRounds 兜底时序（设计修正点）

```
子 Agent 达到 MaxRounds（LLM 每轮都调工具，不结束）：
  AgentLoop.RunAsync（子）
    ├─ Round 1: LLM 调工具 → AssistantMessageEvent(文本1) → RoundEndEvent
    ├─ Round 2: LLM 调工具 → AssistantMessageEvent(文本2) → RoundEndEvent
    ├─ ...（达到 maxRounds=5）
    └─ MaxRoundsReachedEvent(5)  ← 不发 AgentDoneEvent！
                                    ↑ CollectingEventSink.FinalText 仍为 null
                                      但 LastAssistantText = 文本5（最后一轮）

  SubAgentRunner 提取报告：
    report = sink.FinalText ?? sink.LastAssistantText ?? string.Empty
           = null ?? "文本5" ?? ...
           = "文本5"  ← MaxRounds 兜底成功
```

### 4.4 关键不变量

- `AgentLoop.cs` 全程零改动——子 Agent 是新建实例，不修改类
- `AgentEvent.cs` 零改动——`CollectingEventSink` 消费现有事件类型
- `ConversationHistory.cs` 零改动——`ReplaceMessages` 已有（迭代 9 压缩用）
- `SecurityGuard.cs` 仅加 1 行（`SystemTools` 加 `"sub_agent"`）
- 子 Agent 的 `AgentLoop` 用 `compressor: null` + `logger: null`——不触发压缩、不产生 stderr 日志
- 子 Agent 的 `BatchToolExecutor` 用 `NullHitlGate`——不问用户，安全层仍生效

---

## 五、验收标准

### 5.1 功能验收

| # | 验收项 | 验证方法 | 通过标准 |
|---|--------|---------|---------|
| 1 | `sub_agent` 工具注册 | 端到端：启动 TUI，检查工具列表 | `sub_agent` 在 `ToolRegistry` 中可见，schema 含 task/role/mode 参数 |
| 2 | Definitional 模式基本运行 | 端到端：让主 Agent 调 `sub_agent(task="列出项目顶层文件", role="explorer")` | 子 Agent 运行完毕，报告作为 ToolResult 入主 history，主 Agent 后续轮可见报告 |
| 3 | Fork 模式基本运行 | 端到端：让主 Agent 调 `sub_agent(task="总结当前对话要点", mode="fork")` | 子 Agent 继承父历史，报告中体现对父上下文的理解 |
| 4 | Definitional 不继承父历史 | 单测：`SubAgentRunner.RunAsync(Definitional)` 的 history 不含父消息 | 子 Agent history 只有 task 这一条 user 消息 |
| 5 | Fork 继承父历史 | 单测：`SubAgentRunner.RunAsync(Fork)` 的 history 含父消息快照 | 子 Agent history 含父 history 全部消息 + task |
| 6 | Fork 不修改父历史 | 单测：Fork 模式子 Agent 运行后父 history 消息数不变 | 父 history 的 `Count` 运行前后一致 |
| 7 | MaxRounds 时报告非空 | 单测：maxRounds=2，LLM 每轮都调工具（不发 AgentDoneEvent） | 报告 = 最后一轮 assistant 文本（`LastAssistantText` 兜底） |
| 8 | 正常完成时报告优先 FinalText | 单测：LLM 不调工具正常结束 | 报告 = `AgentDoneEvent.FinalText` |
| 9 | 子 Agent 不触发 HITL | 单测：子 Agent 的 BatchToolExecutor 用 NullHitlGate | 子 Agent 执行 write_file（general 角色）时不弹 HITL |
| 10 | 子 Agent 安全层生效 | 单测：子 Agent 在 Strict 模式尝试写项目外路径 | 被 PathSandbox 拦截，错误回灌子 Agent LLM |
| 11 | 子 Agent 达到 MaxRounds 正常停止 | 单测：maxRounds=2，子 Agent 调 3 轮工具 | 第 2 轮后停止，报告为最后一轮 assistant 文本 |
| 12 | 报告截断 | 单测：子 Agent 报告 > 2000 字符 | 截断到 2000 + "（报告过长，已截断）" |
| 13 | 报告含元信息头 | 单测：`SubAgentTool.ExecuteAsync` 返回的 Content | 含 `[子 Agent 报告 \| 角色=... \| 模式=... \| 轮次=...]` |
| 14 | 角色不存在返回错误 | 单测：`sub_agent(role="nonexistent")` | `ToolResult.Fail("未找到角色：nonexistent")` |
| 15 | 缺 task 参数返回错误 | 单测：`sub_agent()` 无 task | `ToolResult.Fail("缺少必需参数：task")` |
| 16 | 无效 mode 返回错误 | 单测：`sub_agent(task="...", mode="invalid")` | `ToolResult.Fail("参数 mode 无效：invalid...")` |
| 17 | mode 大小写不敏感 | 单测：`sub_agent(task="...", mode="FORK")` | 正常执行 Fork 模式 |
| 18 | role 默认 general | 单测：`sub_agent(task="...")` 不传 role | 使用 general 角色 |
| 19 | mode 默认 definitional | 单测：`sub_agent(task="...")` 不传 mode | 使用 Definitional 模式 |
| 20 | `sub_agent.enable: false` 旁路 | 端到端：配置 false | `sub_agent` 工具未注册，主 Agent 看不到此工具 |
| 21 | `sub_agent` 豁免 SecurityGuard | 单测：`SecurityGuard.CheckAsync("sub_agent")` 放行 | 不被拦截（SystemTools 白名单） |
| 22 | 多个子 Agent 并发 | 单测：主 Agent 同时调 2 个 sub_agent（Read 组并发） | 两个子 Agent 并发运行，各自返回报告 |
| 23 | 子 Agent 报告入主 history | 端到端：sub_agent 调用后检查主 history | 主 history 含 sub_agent 的 ToolResult（报告内容） |
| 24 | ErrorEvent 转错误报告 | 单测：子 Agent LLM 调用失败（API 401） | 报告为 `[子 Agent 错误] ...` |
| 25 | CollectingEventSink 圆满完成 | 单测：AgentDoneEvent 设置 FinalText | FinalText = AgentDoneEvent 的 text |
| 26 | CollectingEventSink MaxRounds 兜底 | 单测：MaxRoundsReachedEvent + AssistantMessageEvent | FinalText 为 null，LastAssistantText 为最后一轮文本 |
| 27 | CancellationToken 传递 | 单测：取消令牌触发 | 子 Agent 优雅停止（CancelledEvent） |
| 28 | schema 生成 | 单测：`ToOpenAiSchema` | 含 task/role/mode 参数定义 |

### 5.2 工程验收

- `dotnet build` 0 error 0 warning
- 全部既有测试通过（含迭代 12/13a/13b/14a 的测试）
- 新增单测覆盖 SubAgentRunner / SubAgentTool / CollectingEventSink / BackgroundTaskManager / SubAgentConfig（见 14b.六）
- `AgentLoop.cs` git diff 为空（零改动验证）
- `AgentEvent.cs` / `ConversationHistory.cs` / `BatchToolExecutor.cs` / `SecureBatchToolExecutor.cs` git diff 为空
- `SecurityGuard.cs` 仅加 1 行（`"sub_agent"` 加入 `SystemTools`）

### 5.3 代码质量

- `AgentLoop.cs` 零改动
- `AgentEvent.cs` 零改动
- `ConversationHistory.cs` 零改动
- `BatchToolExecutor.cs` / `SecureBatchToolExecutor.cs` 零改动
- `ToolBase.cs` / `ToolRegistry.cs` / `ToolExecutor.cs` 零改动
- `Skills/` 全部零改动
- `SubAgent/Filter.cs` / `SubAgent/Roles/` 零改动（14a 已完成）
- `nullable` 引用类型开启，无 warning
- `async` 全链路，无 `.Result` / `.Wait()`
- `CancellationToken` 贯穿（`SubAgentRunner.RunAsync` + `SubAgentTool.ExecuteAsync`）

---

## 六、测试清单

### 6.1 SubAgentRunnerTests

- Definitional 模式：history 不含父消息，system prompt 含角色 SOP
- Fork 模式：history 含父消息快照，system prompt 含 Fork 指令
- Fork 模式不修改父 history（运行前后父 history.Count 不变）
- 角色不存在返回失败
- 报告截断（mock 子 Agent 产出 > 2000 字符报告）
- MaxRounds 限制（mock 子 Agent 调 N 轮工具，验证 LastAssistantText 兜底）
- 正常完成（AgentDoneEvent）优先用 FinalText
- NullHitlGate 注入（mock BatchToolExecutor 验证）
- 子 Agent 错误事件转为报告（ErrorEvent → FinalText 含错误信息）
- CancellationToken 传递

### 6.2 SubAgentToolTests

- 合法参数返回 `ToolResult.Ok(report)`
- 缺 task 参数返回 `ToolResult.Fail`
- 无效 mode 返回 `ToolResult.Fail`
- mode 大小写不敏感（"FORK" / "fork" / "Fork" 均可）
- role 默认 "general"
- mode 默认 "definitional"
- 报告含元信息头
- schema 生成（`ToOpenAiSchema`）含 task/role/mode
- `Category == Read`
- Fork 模式传父历史，Definitional 模式传 null

### 6.3 CollectingEventSinkTests

- `AgentDoneEvent` 设置 `FinalText`
- `AssistantMessageEvent` 设置 `LastAssistantText`（每轮覆盖）
- `RoundEndEvent` 递增 `RoundsCompleted`
- `ErrorEvent` 设置 `FinalText` 含错误信息
- `MaxRoundsReachedEvent` 不改变 `FinalText`（由前序 AssistantMessageEvent 提供 LastAssistantText 兜底）
- `CancelledEvent` 不改变 `FinalText` / `LastAssistantText`
- `Complete()` 无副作用
- 多轮 AssistantMessageEvent：`LastAssistantText` 为最后一轮文本

### 6.4 BackgroundTaskManagerTests

- `StartTask` 返回 taskId，任务在后台运行
- `GetCompletedReports` 返回已完成任务
- `GetStatus` 返回任务状态（Running/Completed/Failed）
- 并发数限制（超过 MaxConcurrent 抛异常）
- 任务失败状态正确记录

### 6.5 SubAgentConfigTests

- 默认值：`Enable ?? true` / `MaxRounds ?? 5` / `ReportMaxChars ?? 2000` / `MaxConcurrentBackground ?? 3`
- YAML 加载：`sub_agent.enable: false` 正确反序列化
- null 配置用默认值

### 6.6 端到端测试

- 主 Agent 调 `sub_agent(task="探索项目结构", role="explorer")` → 子 Agent 用 glob/read_file 探索 → 报告入主 history
- 主 Agent 调 `sub_agent(task="总结对话", mode="fork")` → 子 Agent 继承父历史 → 报告体现父上下文
- `sub_agent.enable: false` 时主 Agent 看不到 sub_agent 工具
- explorer 角色子 Agent 尝试 write_file → 工具不在 filteredRegistry 中 → 子 Agent 收到"未注册工具"错误并调整
- 子 Agent 达到 MaxRounds → 报告为最后一轮 assistant 文本（非空）

---

## 七、风险与对策

| 风险 | 对策 |
|------|------|
| 子 Agent 递归调用 sub_agent 导致无限循环 | `ToolFilter.Build` 始终排除 `sub_agent`（14a 已实现）——子 Agent 的 ToolRegistry 中无此工具，LLM 无法调用 |
| 子 Agent 产出过长报告污染主对话 | `ReportMaxChars` 默认 2000 字符截断 + 提示。报告作为 `ToolResult.Content` 入主 history，迭代 9 的截断/压缩对它一视同仁 |
| Fork 模式子 Agent 修改父历史 | 源码审查验证：`ReplaceMessages(ToProviderMessages())` 浅拷贝但 `Message` 是 `sealed record` + `init` 属性（不可变）。子 Agent 只追加到自己的 list，不影响 parent |
| MaxRounds 时报告为空 | **已修正**：`CollectingEventSink` 监听 `AssistantMessageEvent` 缓存 `LastAssistantText`，`SubAgentRunner` 用 `FinalText ?? LastAssistantText ?? string.Empty` 兜底 |
| 子 Agent 在 Strict 模式下被沙箱拦截无法工作 | 设计预期行为：Strict 模式要求最小权限。子 Agent 沙箱拦截错误回灌 LLM 自我修正。explorer/planner 角色只读，一般不触发写拦截 |
| 子 Agent 卡死（LLM 持续调工具不结束） | `MaxRounds` 默认 5，`AgentLoop` 既有 `MaxRoundsReachedEvent` 机制兜底。`ToolExecutor` 有 30s 超时 |
| 子 Agent 的 `run_command` 执行危险命令 | 复用父 `SecurityGuard`——黑名单始终生效，路径沙箱按档位生效。子 Agent 用 `NullHitlGate` 不问用户，但安全层是独立于 HITL 的防线 |
| 主 Agent 同时发起多个 sub_agent 导致资源耗尽 | `sub_agent` 是 `Category.Read`，受 `BatchToolExecutor` 的 `maxParallelism`（默认 5）限流。每个子 Agent 是独立的 `AgentLoop` 实例，不共享状态 |
| 子 Agent 的 LLM 调用失败（API 401/429） | `AgentLoop` 既有 `ErrorEvent` 机制——`CollectingEventSink` 捕获并转为报告 `[子 Agent 错误] ...` |
| Fork 模式父历史过长导致子 Agent 上下文溢出 | 子 Agent 不做压缩（`compressor: null`），但 maxRounds=5 限制了后续增长。极端情况下子 Agent LLM 返回上下文溢出错误，`CollectingEventSink` 捕获为报告 |
| `SubAgentTool` 持有父 `ConversationHistory` 引用有线程安全风险 | 源码审查验证：主 AgentLoop 在 `await ExecuteAsync` 期间不写 history；`ToProviderMessages()` 只读 + ToArray 并发安全 |
| `AgentLoop` 是 `internal sealed` 类 | SubAgentRunner 在同程序集 `ParrotCode` 命名空间，可访问。测试项目需 `InternalsVisibleTo`（检查是否已有） |

---

## 八、与后续迭代的衔接

- **迭代 15（Hook 引擎）**：Hook 引擎的 `sub_agent` 动作依赖本迭代的 `SubAgentRunner`——Hook 规则可触发"起子 Agent 执行自动化任务"。`tool_pre_exec` Hook 可针对 `sub_agent` 工具做额外检查。`BackgroundTaskManager` 可被 Hook 引擎用于异步执行 `sub_agent` 动作。
- **进阶练习（Git Worktree）**：`SubAgentRunner` 可扩展 `worktreePath` 参数，让子 Agent 在独立 Git Worktree 中操作（参考 MewCode 的 `worktree/` 模块）。需在 `SubAgentRequest` 加 `WorktreePath` 字段，`SubAgentRunner` 构造子 Agent 的 `RunCommandTool` 时传入 `cwd=worktreePath`。
- **进阶练习（后台模式）**：`sub_agent` 工具 schema 加 `background` 参数（bool）。`background=true` 时调 `BackgroundTaskManager.StartTask` 返回 taskId，主 Agent 可通过 `/tasks` 命令或下次 `sub_agent` 调用时获取已完成报告。
- **进阶练习（子 Agent 进度展示）**：`CollectingEventSink` 可扩展为 `ProgressEventSink`，把子 Agent 的 `ToolCallStartEvent` / `TextDeltaEvent` 转发到主 Agent 的 TUI（用缩进或不同颜色区分子 Agent 输出）。

---

## 九、交付检查清单

- [ ] `SubAgent/Models.cs` 追加 `SubAgentRequest` / `SubAgentResult`（`SubAgentMode` 在 14a 已定义）
- [ ] `SubAgent/Runner.cs` 新增 `SubAgentRunner` + `CollectingEventSink`（internal，含 `LastAssistantText` 兜底）
- [ ] `SubAgent/SubAgentTool.cs` 新增 `sub_agent` 工具
- [ ] `SubAgent/Manager.cs` 新增 `BackgroundTaskManager` + `BackgroundTask`（基础设施）
- [ ] `Config/Models.cs` 新增 `SubAgentConfig` + `AppConfig.SubAgent`
- [ ] `App/App.cs` 条件装配 `RoleLoader` → `RoleRegistry`
- [ ] `Tui/TerminalApp.cs` 构造函数加 `RoleRegistry?` + `SubAgentConfig?`；`RunAsync` 构造 `SubAgentRunner` + 注册 `SubAgentTool`
- [ ] `Security/SecurityGuard.cs` `SystemTools` 加 `"sub_agent"`（1 行）
- [ ] `example.parrotcode.yaml` 加 `sub_agent:` 配置节
- [ ] `AgentLoop.cs` git diff 为空（零改动验证）
- [ ] `AgentEvent.cs` / `ConversationHistory.cs` / `BatchToolExecutor.cs` / `SecureBatchToolExecutor.cs` git diff 为空
- [ ] `Skills/` / `SubAgent/Filter.cs` / `SubAgent/Roles/` git diff 为空（14a 已完成）
- [ ] 单测：SubAgentRunner / SubAgentTool / CollectingEventSink / BackgroundTaskManager / Config 全覆盖
- [ ] `dotnet build` 0 error 0 warning
- [ ] 全部既有测试通过
- [ ] 端到端：`sub_agent(task="探索项目结构", role="explorer")` 跑通
- [ ] 端到端：`sub_agent(task="总结对话", mode="fork")` 跑通
- [ ] 端到端：MaxRounds 兜底（子 Agent 调满 5 轮，报告非空）

---

## 十、与 14a 的衔接

本迭代（14b）依赖 14a 的交付物：
- `RoleRegistry`：SubAgentRunner 按名查找角色定义
- `ToolFilter.Build`：SubAgentRunner 调用构建子 Agent 的过滤 ToolRegistry
- `SubAgentMode` 枚举：SubAgentRequest/Result 复用（14a 已定义）
- `RoleDefinition` / `RoleMeta`：SubAgentRunner 构建 system prompt 用

14a 的 SubAgentMode 枚举在本迭代被 SubAgentRequest/Result 复用，无需重复定义。

详见 [iter-14a-design.md](./iter-14a-design.md)。
