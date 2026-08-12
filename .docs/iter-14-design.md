# 迭代 14：子 Agent（SubAgent Runner + 角色 + 三层工具过滤 + sub_agent 工具）

> **状态**：[设计完成，待实现]
> **前置迭代**：12 [已完成]（Skill 系统）、13a [已完成]（Skill 目录化）、13b [已完成]（/skill 管理命令）
> **后续迭代**：15（Hook 引擎——`sub_agent` 动作依赖本迭代的 `SubAgentRunner`）
> **拆分说明**：本迭代拆分为两个正交的子迭代，各自有独立设计文档：
> - **[14a：角色系统与三层工具过滤](./iter-14a-design.md)**——加载层（角色加载 + 工具过滤）
> - **[14b：SubAgentRunner + sub_agent 工具 + 装配](./iter-14b-design.md)**——执行层（嵌套 AgentLoop + 工具集成）

---

## 子迭代概览

| | 14a：角色系统与三层工具过滤 | 14b：SubAgentRunner + sub_agent 工具 + 装配 |
|---|---|---|
| **设计文档** | [iter-14a-design.md](./iter-14a-design.md) | [iter-14b-design.md](./iter-14b-design.md) |
| **层次** | 加载层（Loader / Models / Filter） | 执行层 + 集成层（Runner / Tool / 装配） |
| **改动文件** | `SubAgent/Models.cs`（角色部分）+ `SubAgent/Filter.cs` + `SubAgent/Roles/`（含 3 个 Builtin） | `SubAgent/Models.cs`（追加）+ `Runner.cs` + `SubAgentTool.cs` + `Manager.cs` + `Config/Models.cs` + `App.cs` + `TerminalApp.cs` + `SecurityGuard.cs` + `example.parrotcode.yaml` |
| **风险** | 低（纯加载逻辑，与 SkillLoader 同构） | 高（AgentLoop 嵌套调用，最易出错的部分） |
| **独立验收** | 23 项功能验收（RoleLoader 三级扫描 + ToolFilter 三层过滤） | 28 项功能验收（含 MaxRounds 兜底 + Fork 副本隔离 + 并发安全） |
| **零改动** | `AgentLoop` / `ToolRegistry` / `ToolBase` / `Skills/` / `AgentEvent` | `AgentLoop` / `AgentEvent` / `ConversationHistory` / `BatchToolExecutor` / `SecureBatchToolExecutor` / `Skills/` / `SubAgent/Filter.cs` / `SubAgent/Roles/` |
| **端到端** | 无（14a 无运行时入口，仅单测验证） | `sub_agent(task="...", role="explorer")` + `sub_agent(task="...", mode="fork")` 跑通 |

两者改动文件不重叠（14a 改 `SubAgent/Models.cs` 角色部分 + `Filter.cs` + `Roles/`，14b 追加 `Models.cs` 运行时类型 + 新增 `Runner.cs`/`SubAgentTool.cs`/`Manager.cs` + 修改 Config/装配）。14b 技术上依赖 14a 的 `RoleRegistry` / `ToolFilter` / `SubAgentMode` 枚举。

---

## 14a 设计摘要

详见 [iter-14a-design.md](./iter-14a-design.md)。核心要点：

- **角色文件格式**：与 Skill 同构的 YAML frontmatter + Markdown 正文（`name` / `description` / `tools_allow` / `tools_deny`）
- **`RoleLoader` 三级扫描**：内置 → 全局（`~/.parrotcode/roles/`）→ 项目（`./.parrotcode/roles/`），后者覆盖前者同名，与 `SkillLoader` 同构
- **`RoleRegistry`**：角色注册表，按名查找，无激活状态（角色是定义，不是运行时状态）
- **`ToolFilter` 三层过滤**：
  - 第 1 层（全局）：始终排除 `sub_agent`——禁止子 Agent 嵌套
  - 第 2 层（角色）：`tools_allow`（白名单交集）/ `tools_deny`（黑名单并集）
  - 第 3 层（模式）：Fork 模式额外排除 `skill_loader`
- **3 个内置角色**：`explorer`（只读探索：read_file/glob/grep/run_command）/ `planner`（只读规划：read_file/glob/grep）/ `general`（通用：继承父工具集，仅排除 sub_agent/skill_loader）
- **`SubAgentMode` 枚举**：14a 定义（供 ToolFilter 使用），14b 复用
- **零改动**：`AgentLoop` / `ToolRegistry` / `ToolBase` / `Skills/` / `AgentEvent`
- **23 项功能验收标准**

---

## 14b 设计摘要

详见 [iter-14b-design.md](./iter-14b-design.md)。核心要点：

- **两种运行模式**：
  - **定义式（Definitional）**：空白对话 + 角色 SOP 作为 system prompt，不继承父上下文
  - **Fork 式（Fork）**：继承父对话历史副本 + 注入强硬指令（不创建子 worker、不对话、直接干活、结构化报告 ≤ 500 字）
- **`SubAgentRunner`**：创建嵌套 `AgentLoop` 实例（独立 history / registry / sink / system prompt），复用同一 Provider / SecurityGuard。子 Agent 用 `NullHitlGate` 自主运行（安全层仍生效）
- **`CollectingEventSink`**：收集子 Agent 事件不渲染到 TUI，提取最终报告（见下方关键技术决策）
- **`sub_agent` 工具**：LLM 自主调用，参数 `task`（必填）+ `role`（可选，默认 general）+ `mode`（可选，默认 definitional）。`Category=Read`，同步阻塞——子 Agent 完成后报告作为 `ToolResult` 返回主对话
- **`BackgroundTaskManager`**：后台任务基础设施预留（本迭代不暴露 `background` 参数）
- **`AgentLoop` 零改动**：子 Agent 是 `AgentLoop` 的嵌套实例，不感知"被嵌套调用"
- **`SecurityGuard.cs` 仅加 1 行**：`SystemTools` 白名单加 `"sub_agent"`（防御性豁免）
- **28 项功能验收标准**（含 MaxRounds 兜底 + Fork 副本隔离 + 并发安全）

---

## 关键技术决策（源码审查结论）

编码前对 [AgentLoop.cs](../ParrotCode.Net/Agent/AgentLoop.cs)、[History.cs](../ParrotCode.Net/Conversation/History.cs)、[ToolRegistry.cs](../ParrotCode.Net/Tools/ToolRegistry.cs)、[BatchToolExecutor.cs](../ParrotCode.Net/Agent/BatchToolExecutor.cs)、[AgentEvent.cs](../ParrotCode.Net/Agent/AgentEvent.cs)、[MessageTypes.cs](../ParrotCode.Net/Providers/MessageTypes.cs) 做了源码审查，确认以下技术方案可行：

### ✅ 已验证可行

| 技术假设 | 源码证据 |
|---------|---------|
| `AgentLoop` 构造支持 `systemPrompt` / `compressor` / `logger` 参数 | [AgentLoop.cs:25-43](../ParrotCode.Net/Agent/AgentLoop.cs#L25-L43) 签名完全匹配 |
| `AgentLoop` 无静态状态，支持嵌套实例化 | 全部实例字段，无 static |
| `ToolRegistry` 支持从父 registry 复制工具 | [ToolRegistry.cs:40](../ParrotCode.Net/Tools/ToolRegistry.cs#L40) `GetAll()` 返回快照，`Register` 到新 registry OK |
| Fork 副本隔离安全 | [MessageTypes.cs:33-44](../ParrotCode.Net/Providers/MessageTypes.cs#L33-L44) `Message` 是 `sealed record` + `init` 属性，不可变。浅拷贝共享引用但无法修改 |
| 多个 sub_agent 并发安全 | [BatchToolExecutor.cs:102-111](../ParrotCode.Net/Agent/BatchToolExecutor.cs#L102-L111) Read 组并发，主 AgentLoop await 期间不写 history；`ToProviderMessages()` 只读+ToArray 并发安全 |

### ❌ 设计修正（已写入 14b 文档）

**Bug：MaxRounds 时 `CollectingEventSink.FinalText` 为 null**

[AgentLoop.cs:198-200](../ParrotCode.Net/Agent/AgentLoop.cs#L198-L200) 显示 MaxRounds 路径**只发 `MaxRoundsReachedEvent`，不发 `AgentDoneEvent`**。原设计假设"达到最大轮次也有 FinalText"是错误的。

**修正方案**：`CollectingEventSink` 额外监听 `AssistantMessageEvent`，缓存 `LastAssistantText`。`SubAgentRunner` 提取报告用三级兜底：
```csharp
var report = sink.FinalText ?? sink.LastAssistantText ?? string.Empty;
```

详见 [iter-14b-design.md](./iter-14b-design.md) 第 3.2 节（CollectingEventSink）和第 4.3 节（MaxRounds 兜底时序）。

---

## 整体运行时序

### Definitional 模式

```
主 Agent → sub_agent(task="探索项目结构", role="explorer", mode="definitional")
  └─ SubAgentTool.ExecuteAsync
       └─ SubAgentRunner.RunAsync
            ├─ RoleRegistry.Get("explorer") → RoleDefinition
            ├─ ToolFilter.Build(parentRegistry, explorer, Definitional) → filteredRegistry
            ├─ history = new ConversationHistory() + AddUser(task)  ← 空白对话
            ├─ systemPrompt = explorer.Body + 子 Agent 约束
            ├─ SecureBatchToolExecutor(NullHitlGate, 父 SecurityGuard)
            ├─ CollectingEventSink
            └─ AgentLoop.RunAsync（嵌套实例）
                 ├─ Round 1-N: LLM 调工具 → AssistantMessageEvent（缓存）→ 结果入子 history
                 └─ Round N+1: LLM 不调工具 → AgentDoneEvent(FinalText=报告)
            └─ report = FinalText ?? LastAssistantText → 截断到 2000 字符
       └─ ToolResult.Ok("[子 Agent 报告 | ...]\n\n{报告}")
  └─ 主 AgentLoop 把 ToolResult 入主 history
```

### Fork 模式

```
主 Agent → sub_agent(task="总结当前对话要点", mode="fork")
  └─ SubAgentTool.ExecuteAsync
       └─ SubAgentRunner.RunAsync
            ├─ RoleRegistry.Get("general")（默认）
            ├─ ToolFilter.Build(parentRegistry, general, Fork) → filteredRegistry（排除 sub_agent + skill_loader）
            ├─ history = new ConversationHistory()
            │  history.ReplaceMessages(parentHistory.ToProviderMessages())  ← Fork 副本（浅拷贝但 Message 不可变）
            │  history.AddUser(task)
            ├─ systemPrompt = Fork 指令 + 子 Agent 约束（不含角色 SOP）
            └─ AgentLoop.RunAsync（嵌套）→ 报告
       └─ ToolResult.Ok("[子 Agent 报告 | 角色=general | 模式=fork | ...]\n\n{报告}")
```

### MaxRounds 兜底

```
子 Agent 达到 MaxRounds（LLM 每轮都调工具，不结束）：
  AgentLoop.RunAsync（子）
    ├─ Round 1-N: AssistantMessageEvent(文本N) → RoundEndEvent
    └─ MaxRoundsReachedEvent(N)  ← 不发 AgentDoneEvent！
                                  ↑ FinalText 仍为 null
                                    但 LastAssistantText = 文本N（最后一轮）

  SubAgentRunner 提取报告：
    report = sink.FinalText ?? sink.LastAssistantText ?? string.Empty
           = null ?? "文本N" ?? ...
           = "文本N"  ← MaxRounds 兜底成功
```

---

## 整体验收路径

1. **14a 验收**（先做，低风险）：23 项功能验收——RoleLoader 三级扫描 + ToolFilter 三层过滤 + 单测全覆盖
2. **14b 验收**（后做，高风险）：28 项功能验收——
   - SubAgentRunner 两种模式（Definitional / Fork）
   - CollectingEventSink 报告提取（含 MaxRounds 兜底）
   - Fork 副本隔离（父历史不被修改）
   - 子 Agent 不触发 HITL（NullHitlGate）
   - 子 Agent 安全层生效（复用父 SecurityGuard）
   - `sub_agent.enable: false` 旁路
   - 端到端：`sub_agent(task="探索项目结构", role="explorer")` + `sub_agent(task="总结对话", mode="fork")` 跑通

详见各子迭代设计文档的验收标准章节。

---

## 风险总览

| 风险 | 子迭代 | 对策 |
|------|--------|------|
| `SubAgentMode` 枚举在 14b 才用，14a 的 `ToolFilter` 需要它 | 14a | 14a 编码时先在 `Models.cs` 定义 `SubAgentMode` 枚举（无依赖简单枚举） |
| 子 Agent 递归调用 sub_agent 导致无限循环 | 14a | `ToolFilter.Build` 始终排除 `sub_agent`——子 Agent 的 ToolRegistry 中无此工具 |
| MaxRounds 时报告为空 | 14b | **已修正**：`CollectingEventSink` 监听 `AssistantMessageEvent` 缓存 `LastAssistantText`，三级兜底提取报告 |
| Fork 模式子 Agent 修改父历史 | 14b | 源码审查验证：`Message` 是 `sealed record` + `init` 属性（不可变），子 Agent 只追加到自己的 list |
| 子 Agent 卡死（LLM 持续调工具不结束） | 14b | `MaxRounds` 默认 5，`AgentLoop` 既有 `MaxRoundsReachedEvent` 机制兜底，`ToolExecutor` 有 30s 超时 |
| `AgentLoop` 是 `internal sealed` 类 | 14b | SubAgentRunner 在同程序集 `ParrotCode` 命名空间可访问。测试项目需 `InternalsVisibleTo` |
| 子 Agent 的 `run_command` 执行危险命令 | 14b | 复用父 `SecurityGuard`——黑名单始终生效，路径沙箱按档位生效 |

---

## 与后续迭代的衔接

- **迭代 15（Hook 引擎）**：Hook 引擎的 `sub_agent` 动作依赖本迭代的 `SubAgentRunner`——Hook 规则可触发"起子 Agent 执行自动化任务"。`tool_pre_exec` Hook 可针对 `sub_agent` 工具做额外检查。`BackgroundTaskManager` 可被 Hook 引擎用于异步执行 `sub_agent` 动作。
- **进阶练习（Git Worktree）**：`SubAgentRunner` 可扩展 `worktreePath` 参数，让子 Agent 在独立 Git Worktree 中操作（参考 MewCode 的 `worktree/` 模块）。
- **进阶练习（后台模式）**：`sub_agent` 工具 schema 加 `background` 参数，配合 `BackgroundTaskManager` 实现异步后台模式 + `/tasks` 命令查看状态。
- **进阶练习（子 Agent 进度展示）**：`CollectingEventSink` 可扩展为 `ProgressEventSink`，把子 Agent 中间事件转发到主 TUI。

---

## 交付检查清单

### 14a 交付检查

- [ ] `SubAgent/Models.cs` 新增 `RoleDefinition` / `RoleMeta` / `RoleSource` + `SubAgentMode` 枚举
- [ ] `SubAgent/Roles/RoleLoader.cs` 新增 `RoleLoader` + `RoleRegistry`
- [ ] `SubAgent/Filter.cs` 新增 `ToolFilter`（三层过滤）
- [ ] `SubAgent/Roles/Builtin/explorer.md` 新增
- [ ] `SubAgent/Roles/Builtin/planner.md` 新增
- [ ] `SubAgent/Roles/Builtin/general.md` 新增
- [ ] `AgentLoop.cs` / `ToolRegistry.cs` / `Skills/` git diff 为空
- [ ] 单测：RoleLoader / RoleRegistry / ToolFilter 全覆盖
- [ ] `dotnet build` 0 error 0 warning
- [ ] 全部既有测试通过

### 14b 交付检查

- [ ] `SubAgent/Models.cs` 追加 `SubAgentRequest` / `SubAgentResult`
- [ ] `SubAgent/Runner.cs` 新增 `SubAgentRunner` + `CollectingEventSink`（含 `LastAssistantText` 兜底）
- [ ] `SubAgent/SubAgentTool.cs` 新增 `sub_agent` 工具
- [ ] `SubAgent/Manager.cs` 新增 `BackgroundTaskManager` + `BackgroundTask`
- [ ] `Config/Models.cs` 新增 `SubAgentConfig` + `AppConfig.SubAgent`
- [ ] `App/App.cs` 条件装配 `RoleLoader` → `RoleRegistry`
- [ ] `Tui/TerminalApp.cs` 构造函数加 `RoleRegistry?` + `SubAgentConfig?`；`RunAsync` 构造 `SubAgentRunner` + 注册 `SubAgentTool`
- [ ] `Security/SecurityGuard.cs` `SystemTools` 加 `"sub_agent"`（1 行）
- [ ] `example.parrotcode.yaml` 加 `sub_agent:` 配置节
- [ ] `AgentLoop.cs` / `AgentEvent.cs` / `ConversationHistory.cs` / `BatchToolExecutor.cs` / `SecureBatchToolExecutor.cs` git diff 为空
- [ ] `Skills/` / `SubAgent/Filter.cs` / `SubAgent/Roles/` git diff 为空（14a 已完成）
- [ ] 单测：SubAgentRunner / SubAgentTool / CollectingEventSink / BackgroundTaskManager / Config 全覆盖
- [ ] `dotnet build` 0 error 0 warning
- [ ] 全部既有测试通过
- [ ] 端到端：`sub_agent(task="探索项目结构", role="explorer")` 跑通
- [ ] 端到端：`sub_agent(task="总结对话", mode="fork")` 跑通
- [ ] 端到端：MaxRounds 兜底（子 Agent 调满 5 轮，报告非空）
