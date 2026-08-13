# 迭代 15：Hook 引擎（生命周期事件钩子 + 条件匹配 + 4 种动作 + 拦截能力）

> **状态**：[设计完成，待实现]
> **前置迭代**：14a [已完成]（角色系统）、14b [已完成]（SubAgentRunner + sub_agent 工具）
> **后续迭代**：无（前 15 个迭代收官；可选扩展见 plan.md 第三节）
> **参考实现**：MewCode Python 版 `mewcode/hooks/`（models / conditions / templates / actions / loader / engine）
> **拆分说明**：本迭代拆分为两个正交的子迭代，各自有独立设计文档：
> - **[15a：Hook 核心引擎](./iter-15a-design.md)**——加载层 + 纯逻辑层（Models / Conditions / Templates / Loader / Engine / Actions 的 shell+prompt_inject+http）
> - **[15b：Hook 集成接入](./iter-15b-design.md)**——执行层 + 装配（Actions 的 sub_agent + SecureBatchToolExecutor + AgentLoop + App + TerminalApp + Config）

---

## 子迭代概览

| | 15a：Hook 核心引擎 | 15b：Hook 集成接入 |
|---|---|---|
| **设计文档** | [iter-15a-design.md](./iter-15a-design.md) | [iter-15b-design.md](./iter-15b-design.md) |
| **层次** | 加载层 + 纯逻辑层（Loader / Models / Conditions / Templates / Engine / Actions 部分） | 执行层 + 集成层（Actions sub_agent / SecureBatchToolExecutor / AgentLoop / 装配） |
| **改动文件** | 6 个新文件（`Hooks/` 全套）+ Actions 只实现 shell/prompt_inject/http | Actions 追加 sub_agent + 修改 `Config/Models.cs` / `SecureBatchToolExecutor.cs` / `AgentLoop.cs` / `App.cs` / `TerminalApp.cs` / `example.parrotcode.yaml` |
| **风险** | 低（纯新增文件，零既有文件改动） | 中（修改 AgentLoop 核心循环 + SecureBatchToolExecutor 安全链路） |
| **独立验收** | 73 项单测（纯逻辑，无运行时入口） | 52 项功能验收 + 7 项端到端验收 |
| **零改动** | 全部既有文件（`AgentLoop` / `SecureBatchToolExecutor` / `AgentEvent` / `Tools/` / `SubAgent/` / `Skills/` / `Conversation/` / `Config/` / `App/` / `Tui/`） | `AgentEvent.cs` / `BatchToolExecutor.cs` / `SecurityGuard.cs` / `Tools/` / `Conversation/` / `SubAgent/` / `Skills/` / `Commands/` / `Mcp/` |
| **端到端** | 无（15a 无运行时入口，仅单测验证） | `tool_pre_exec` + shell / `tool_pre_exec` + prompt_inject 拦截 / `session_end` + sub_agent 总结 等 7 项 |

两者改动文件不重叠（15a 全部是 `Hooks/` 新增文件，15b 修改既有文件 + Actions 追加 sub_agent 动作）。15b 技术上依赖 15a 的 `HookEngine` / `ActionExecutor` / `HookRule` 等类型。

---

## 15a 设计摘要

详见 [iter-15a-design.md](./iter-15a-design.md)。核心要点：

- **6 个新文件**全套交付：`Models.cs`（12 事件 + 4 算子 + 4 动作类型 + Rule/Condition/Action/Control）+ `Conditions.cs`（ConditionEvaluator）+ `Templates.cs`（TemplateEngine）+ `Loader.cs`（HookLoader 两级 YAML）+ `Engine.cs`（HookEngine）+ `Actions.cs`（ActionExecutor）
- **Actions 实现 3 种动作**：shell（跨平台命令执行）/ prompt_inject（模板渲染）/ http（webhook 调用）——**不含 sub_agent**（依赖 SubAgentRunner，留 15b）
- **零既有文件改动**：15a 不碰任何既有代码，可独立 build + test 通过
- **纯逻辑可单测**：ConditionEvaluator / TemplateEngine / HookLoader / HookEngine / ActionExecutor 全部纯逻辑，无运行时入口
- **73 项单测**覆盖核心逻辑

---

## 15b 设计摘要

详见 [iter-15b-design.md](./iter-15b-design.md)。核心要点：

- **Actions 追加 sub_agent 动作**：通过 `SetSubAgentRunner` setter 注入 SubAgentRunner（解决时序问题——SubAgentRunner 在 `_history` 创建后才构造）
- **SecureBatchToolExecutor 改动**：构造函数加 `HookEngine?` 参数，`OnBeforeExecuteAsync` 安全检查后追加 `tool_pre_exec` 触发。安全层先于 Hook（安全是硬约束，Hook 是用户定制）
- **AgentLoop 最小改动**：构造函数加 `HookEngine?` 参数，`RunCoreAsync` 生命周期节点追加 `if (_hookEngine is not null) await FireAsync(...)` 调用。null 时行为等价改动前
- **TerminalApp 改动**：传参 + session_start/end 触发 + SetSubAgentRunner 注入
- **App 装配**：构造 HookLoader → HookEngine + 条件注入（`enable: false` 时传 null）+ system_startup/shutdown
- **保守默认**：`hooks.enable` 默认 false（Hook 能执行 shell/http，安全敏感，需显式开启）
- **52 项功能验收 + 7 项端到端验收**（含环境配置 + 操作步骤）

---

## 整体运行时序

### 完整生命周期触发时序

```
程序启动：
  App.RunAsync
    ├─ 构造 HookEngine（如果 enable=true 且有规则）
    ├─ FireAsync(SystemStartup)               ← system_startup
    └─ TerminalApp.RunAsync
         ├─ 注册工具（含 sub_agent）
         ├─ ActionExecutor.SetSubAgentRunner  ← 注入 SubAgentRunner（sub_agent 动作用）
         └─ Application.Run（事件循环）
              └─ 用户输入 → StartAgentRound
                   ├─ FireAsync(SessionStart) + ResetOnce   ← session_start
                   ├─ AgentLoop.RunAsync
                   │    ├─ Round 1:
                   │    │    ├─ FireAsync(RoundStart)       ← round_start
                   │    │    ├─ FireAsync(MessagePreSend)   ← message_pre_send
                   │    │    ├─ LLM 流式调用
                   │    │    ├─ FireAsync(MessagePostReceive) ← message_post_receive
                   │    │    ├─ 工具调用 → SecureBatchToolExecutor:
                   │    │    │    ├─ SecurityGuard.CheckAsync（安全层）
                   │    │    │    ├─ FireAsync(ToolPreExec)   ← tool_pre_exec（拦截）
                   │    │    │    │    └─ 返回拒绝原因？→ ToolResult.Fail → LLM 调整
                   │    │    │    ├─ HITL（Write 工具）
                   │    │    │    └─ 执行工具
                   │    │    ├─ FireAsync(ToolPostExec)      ← tool_post_exec（每个工具一次）
                   │    │    ├─ FireAsync(RoundEnd)          ← round_end
                   │    │    └─ （如有压缩）FireAsync(SystemCompress) ← system_compress
                   │    ├─ Round 2-N: 同上
                   │    └─ AgentDone / MaxRounds / Error
                   │         └─ （Error 时）FireAsync(SystemError)    ← system_error
                   └─ FireAsync(SessionEnd)                 ← session_end
    └─ FireAsync(SystemShutdown)              ← system_shutdown
```

### tool_pre_exec 拦截时序

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

### async 动作 fire-and-forget 时序

```
ToolPostExec 事件触发：
  HookEngine.FireAsync(ToolPostExec, {tool_name:"read_file", ...})
    └─ rule "audit-tool-call":
         control.async = true
         action: http POST to webhook
         → _ = FireActionsAsync(...)   ← fire-and-forget，不 await
    └─ FireAsync 立即返回 null
  AgentLoop 不等待 webhook 响应，继续下一轮
```

---

## 关键技术决策

### ✅ 拆分边界决策

15a/15b 的拆分边界是 **sub_agent 动作的依赖链**：

- shell / prompt_inject / http 三种动作**不依赖** SubAgentRunner → 15a
- sub_agent 动作**依赖** SubAgentRunner（迭代 14b 的类型，在 TerminalApp.RunAsync 中才构造）→ 15b

这使 15a 的 ActionExecutor 是完整可用的（3 种动作全覆盖），15b 只追加第 4 种动作 + 集成接入。

### ✅ 拦截集成复用迭代 8 预留机制

[BatchToolExecutor.cs:147](../ParrotCode.Net/Agent/BatchToolExecutor.cs#L147) 的 `virtual OnBeforeExecuteAsync` 返回 `ToolResult?`（null=放行，Fail=拦截）——这是迭代 8 为 SecurityGuard 预留的 hook 点。`SecureBatchToolExecutor` 已覆写此方法接入安全层。15b 在 `OnBeforeExecuteAsync` 中**安全检查之后**追加 Hook `tool_pre_exec` 触发——安全层先于用户 Hook（安全是硬约束，Hook 是用户定制）。

### ✅ AgentLoop 最小改动策略

所有 fire 调用前 `if (_hookEngine is not null)` 保护——HookEngine 为 null 时行为等价改动前。既有测试不传 HookEngine，不受影响。改动全部是 `await _hookEngine.FireAsync(...)` 形式的追加，不修改既有控制流。

### ✅ 保守默认（enable: false）

`hooks.enable` 默认 **false**（与 Skill/SubAgent 默认 true 不同）——Hook 能执行任意 shell 命令和 HTTP 请求，是安全敏感特性，需用户显式开启。用户在 `.parrotcode.yaml` 中写 `hooks.enable: true` 后才加载规则文件。

---

## 整体验收路径

1. **15a 验收**（先做，低风险）：73 项单测——Models / Conditions / Templates / Loader / Engine / Actions（shell/prompt_inject/http）全覆盖。零既有文件改动，独立 build + test 通过
2. **15b 验收**（后做，中风险）：52 项功能验收 + 7 项端到端验收——
   - SecureBatchToolExecutor 的 tool_pre_exec 拦截（安全层先于 Hook）
   - AgentLoop 生命周期 fire 调用（round/message/tool_post/system_error/system_compress）
   - TerminalApp session_start/end + SetSubAgentRunner 注入
   - sub_agent 动作（依赖 SubAgentRunner）
   - `hooks.enable: false` 旁路
   - 端到端：7 项（含环境配置 + 操作步骤，详见 [iter-15b-design.md](./iter-15b-design.md) 第五节）

详见各子迭代设计文档的验收标准章节。

---

## 风险总览

| 风险 | 子迭代 | 对策 |
|------|--------|------|
| Hook 动作执行慢阻塞 AgentLoop | 15a/15b | 用户应给慢动作配 `async: true`（fire-and-forget）。拦截事件禁止 async（Loader 校验） |
| Hook 动作执行 shell 命令有安全风险 | 15b | Hook 默认 `enable: false`。shell 动作不经过 SecurityGuard（Hook 是用户主动配置的自动化，信任级别不同于 LLM 调用的工具） |
| `sub_agent` 动作时序问题 | 15b | `ActionExecutor.SetSubAgentRunner` setter 注入。注入前 sub_agent 动作记警告并跳过 |
| AgentLoop 改动破坏既有测试 | 15b | 所有 fire 调用前 `if (_hookEngine is not null)` 保护。既有测试不传 HookEngine |
| SecureBatchToolExecutor 改动破坏安全层 | 15b | Hook 触发在安全检查**之后**。安全层拦截时不触发 Hook（提前 return） |
| Hook 规则文件含敏感信息（webhook URL） | 15a | `.parrotcode/hooks.yaml` 被 `.gitignore` 忽略 |
| `system_error` Hook 在 CancellationToken 已取消时触发 | 15b | `FireAsync` 传 `CancellationToken.None`（错误路径），保证 Hook 能执行 |

---

## 与后续迭代的衔接

- **进阶练习（http 审计）**：配置 `tool_post_exec` + `http` 动作，把每次工具调用 POST 到 webhook 做调用审计
- **进阶练习（消息修改）**：`message_pre_send` 扩展为拦截事件，允许 Hook 修改发给 LLM 的消息列表（需 AgentLoop 更大改动）
- **进阶练习（Hook 热重载）**：`/hook reload` 命令运行时重新加载 hooks.yaml
- **进阶练习（Hook TUI 展示）**：Hook 动作结果转发到 TUI（类似子 Agent 进度展示）
- **可选扩展（自动笔记）**：`session_end` + `sub_agent` 动作实现跨会话记忆（MewCode 的 `notes/` 模块）

---

## 交付检查清单

### 15a 交付检查

- [ ] `Hooks/Models.cs` 新增（HookEvent 12 枚举 + HookOperator 4 + HookMatchMode + HookActionType 4 + ConditionRule / HookCondition / HookAction / HookControl / HookRule + InterceptEvents + HookConfigException）
- [ ] `Hooks/Conditions.cs` 新增 ConditionEvaluator（exact/not/regex/glob + ALL/ANY + dot-path）
- [ ] `Hooks/Templates.cs` 新增 TemplateEngine（{{var}} dot-path 替换）
- [ ] `Hooks/Actions.cs` 新增 ActionExecutor（shell/prompt_inject/http + 错误隔离 + SetSubAgentRunner 预留空方法）
- [ ] `Hooks/Loader.cs` 新增 HookLoader（两级 YAML + 集中校验）
- [ ] `Hooks/Engine.cs` 新增 HookEngine（FireAsync + once 跟踪 + ResetOnce）
- [ ] 全部既有文件 git diff 为空
- [ ] 单测：Models / Conditions / Templates / Loader / Engine / Actions 全覆盖（73 项）
- [ ] `dotnet build` 0 error 0 warning
- [ ] 全部既有测试通过

### 15b 交付检查

- [ ] `Hooks/Actions.cs` 追加 sub_agent 动作实现（依赖 SubAgentRunner）
- [ ] `Config/Models.cs` 新增 `HooksConfig` + `AppConfig.Hooks`
- [ ] `Security/SecureBatchToolExecutor.cs` 构造函数加 `HookEngine?` + OnBeforeExecuteAsync 追加 tool_pre_exec
- [ ] `Agent/AgentLoop.cs` 构造函数加 `HookEngine?` + RunCoreAsync 追加 fire 调用
- [ ] `App/App.cs` 构造 HookLoader → HookEngine + 条件注入 + system_startup/shutdown
- [ ] `Tui/TerminalApp.cs` 构造函数加 `HookEngine?` + StartAgentRound 传参 + session_start/end + SetSubAgentRunner
- [ ] `example.parrotcode.yaml` 新增 `hooks:` 配置节
- [ ] `.parrotcode/hooks.yaml.example` 新增规则示例文件
- [ ] `AgentEvent.cs` / `BatchToolExecutor.cs` / `SecurityGuard.cs` git diff 为空
- [ ] `Tools/` / `Conversation/` / `SubAgent/` / `Skills/` / `Commands/` / `Mcp/` git diff 为空
- [ ] 单测：SecureBatchToolExecutorHook / AgentLoopHook / HooksConfig / Actions sub_agent 全覆盖
- [ ] `dotnet build` 0 error 0 warning
- [ ] 全部既有测试通过（含 15a 的测试）
- [ ] 端到端：tool_pre_exec + shell（git stash before write_file）
- [ ] 端到端：tool_pre_exec + prompt_inject（拦截写系统目录）
- [ ] 端到端：tool_post_exec + http（webhook 审计）
- [ ] 端到端：session_end + sub_agent（会话总结）
- [ ] 端到端：enable: false 旁路
