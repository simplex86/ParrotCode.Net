# 迭代 11：MCP 协议客户端（Stdio + HTTP SSE）— 总览

> **状态**：[设计完成，待实现]
> **前置迭代**：10a [已完成]（命令系统）、10b [已完成]（JSONL 持久化）、10c [已完成]（项目指令）
> **后续迭代**：12（Skill 系统 + Hook 引擎 + 子 Agent）
> **对应 `plan.md` 第三章「迭代 11」**，本文档为总览，保留用于追溯整体设计与跨子迭代决策。
>
> **本文档为总览**，实施拆分为三个子迭代（各自含独立验收标准）：
> - [iter-11a-design.md](iter-11a-design.md)：JSON-RPC 协议层 + Stdio 传输（协议编解码 + id 匹配 + 子进程传输 + MockTransport）
> - [iter-11b-design.md](iter-11b-design.md)：MCP 客户端 + 工具适配器（initialize→tools/list→tools/call 生命周期 + IBaseTool 适配）
> - [iter-11c-design.md](iter-11c-design.md)：HTTP SSE 传输 + 连接管理器 + 端到端装配（SSE 解析 + 并行连接 + App/TerminalApp 集成）
>
> **拆分理由**：MCP 是本项目概念最密集的迭代，涉及五个独立关注点（协议层、Stdio 传输、HTTP SSE 传输、客户端生命周期、工具适配）。拆分后每个子迭代复杂度可控，风险前低后高：11a 纯协议/IO 无外部依赖，11b 用 mock 不依赖真实 server，11c 才接真实 server 端到端打通。
>
> **子迭代依赖顺序**：11a → 11b（依赖 11a 的 JsonRpc + ITransport + MockTransport）→ 11c（依赖 11b 的 McpClient + McpToolAdapter）。

---

## 一、概述

MCP（Model Context Protocol）是 Anthropic 提出的开放协议，用于 AI 应用与外部工具服务器之间的标准化通信。本迭代交付 MCP 客户端——让 Agent 能调用外部 MCP server 提供的工具，与内置工具统一接入 AgentLoop。

### 核心交付物

1. **JSON-RPC 2.0 协议层**：请求/响应/通知编解码，id 匹配（`TaskCompletionSource<JsonElement>` Future 模式）
2. **Stdio 传输**：`System.Diagnostics.Process` 重定向 stdin/stdout，stderr 单独收集日志
3. **HTTP SSE 传输**：`HttpClient` POST 发请求 + SSE 流接收响应
4. **MCP 客户端**：完整生命周期——`initialize` → `initialized` → `tools/list` → `tools/call`
5. **MCP 工具适配器**：`McpToolAdapter : IBaseTool`，把 MCP 工具注册到 `ToolRegistry`，Agent 透明调用
6. **连接管理器**：`McpConnectionManager` 并行连接所有配置的 MCP server，单个失败不阻塞其他
7. **配置**：`McpConfig` + YAML 配置节 `mcp:` + MCP server 配置

### MCP 协议流程

```
Client                                    Server
  │                                         │
  │──── initialize ────────────────────────→│  (握手：声明能力)
  │←─── initialize response ───────────────│
  │──── initialized (notification) ────────→│  (确认就绪)
  │                                         │
  │──── tools/list ────────────────────────→│  (发现工具)
  │←─── tools/list response ───────────────│
  │                                         │
  │──── tools/call ────────────────────────→│  (调用工具)
  │←─── tools/call response ───────────────│
  │                                         │
  │──── shutdown (notification) ───────────→│  (关闭)
  │     [Process exit / HTTP disconnect]    │
```

---

## 二、范围

### 2.1 本迭代包含（In Scope）

| 项 | 子迭代 | 说明 |
| --- | --- | --- |
| `Mcp/Protocol/JsonRpc.cs` | 11a | JSON-RPC 2.0 编解码 + id 匹配 |
| `Mcp/Protocol/McpMethods.cs` | 11a | MCP 方法名常量 + 请求/响应 record |
| `Mcp/Transport/ITransport.cs` | 11a | 传输层抽象接口 |
| `Mcp/Transport/StdioTransport.cs` | 11a | Stdio 子进程传输 |
| `Mcp/McpClient.cs` | 11b | MCP 客户端（initialize → tools/list → tools/call） |
| `Mcp/McpToolAdapter.cs` | 11b | MCP 工具 → IBaseTool 适配 |
| `Mcp/Transport/HttpSseTransport.cs` | 11c | HTTP SSE 传输 |
| `Mcp/McpConnectionManager.cs` | 11c | 连接管理器（并行连接 + 生命周期） |
| `Mcp/McpConfig.cs` | 11c | 配置 record |
| `Config/Models.cs` 扩展 | 11c | `McpConfig` + `McpServerConfig` + `AppConfig.Mcp` |
| `example.parrotcode.yaml` 扩展 | 11c | `mcp:` 配置节示例 |
| `App/App.cs` 扩展 | 11c | 构造 `McpConnectionManager`，连接 MCP server |
| `Tui/TerminalApp.cs` 扩展 | 11c | 注册 MCP 工具到 ToolRegistry |
| `Commands/Builtin/StatusCommand.cs` 扩展 | 11c | 显示 MCP server 连接状态 |
| `Commands/CommandContext.cs` 扩展 | 11c | 新增 `McpSummary` |
| 单元测试 | 11a/11b/11c | `McpProtocolTests` / `McpTransportTests` / `McpClientTests` / `McpConnectionManagerTests` |

### 2.2 本迭代不包含（Out of Scope）

- MCP Resources 支持（`resources/list` / `resources/read`）——可选扩展（无明确迭代计划）
- MCP Prompts 支持（`prompts/list` / `prompts/get`）——可选扩展（无明确迭代计划）
- MCP Sampling 支持（`sampling/createMessage`）——可选扩展（需反向 LLM 调用）
- MCP server 动态发现（如 DNS-SD / 注册中心）——可选扩展
- MCP server 运行时热重载（增删 server 需重启）——可选扩展
- Stdio 传输的 MCP server 进程自动重启——可选扩展
- HTTP SSE 传输的 OAuth 认证——本迭代仅支持无认证 / Bearer Token
- `tools/list_changed` 通知处理——可选扩展

---

## 三、架构总览

```
┌─────────────────────────────────────────────────────────┐
│  应用层                                                  │
│  App.cs ──→ McpConnectionManager ──→ TerminalApp         │  11c
├─────────────────────────────────────────────────────────┤
│  连接管理层                                              │
│  McpConnectionManager (并行连接 + 生命周期)               │  11c
├─────────────────────────────────────────────────────────┤
│  工具适配层                                              │
│  McpToolAdapter : IBaseTool (工具名前缀 + Category 判定)  │  11b
├─────────────────────────────────────────────────────────┤
│  MCP 客户端层                                            │
│  McpClient (initialize → tools/list → tools/call)        │  11b
├─────────────────────────────────────────────────────────┤
│  传输层                                                  │
│  ITransport ┬─ StdioTransport (子进程 stdin/stdout)      │  11a
│             └─ HttpSseTransport (POST + SSE 流)          │  11c
├─────────────────────────────────────────────────────────┤
│  协议层                                                  │
│  JsonRpc (编解码 + id 匹配) + McpMethods (常量/record)   │  11a
└─────────────────────────────────────────────────────────┘
```

### 数据流

```
用户输入 → TerminalApp.HandleUserInput
             │
             └─ 对话 → AgentLoop → SecureBatchToolExecutor
                        │
                        ├─ 内置工具（ReadFile/WriteFile/...）
                        └─ McpToolAdapter（MCP 工具）
                                  │
                                  ▼
                           McpClient.CallToolAsync
                                  │
                                  ▼
                           JsonRpc.CreateRequest
                                  │
                                  ▼
                           ITransport.SendAsync
                           ├─ StdioTransport → 子进程 stdin
                           └─ HttpSseTransport → HTTP POST
                                  │
                                  ▼
                           ITransport.ReceiveAsync ← 接收循环
                           ├─ StdioTransport ← 子进程 stdout
                           └─ HttpSseTransport ← SSE 流
                                  │
                                  ▼
                           JsonRpc.HandleMessage → 匹配 id → TCS.SetResult
                                  │
                                  ▼
                           McpClient → McpToolCallResult → ToolResult
```

---

## 四、关键设计决策（跨子迭代）

### Q1：为什么工具名用 `{serverName}/{toolName}` 前缀？

- **防冲突**：不同 server 可能有同名工具（如两个 filesystem server 都有 `read_file`）
- **可追溯**：从工具名即可知道来自哪个 server，便于调试和日志
- **LLM 友好**：斜杠分隔符清晰表达层级关系，LLM 理解无障碍
- **与内置工具不冲突**：内置工具用 snake_case（如 `read_file`），MCP 用 `{server}/{tool}`（如 `filesystem/read_file`）

### Q2：为什么 MCP 工具无注解时默认 Write？

- **安全优先**：MCP 工具的副作用不确定，默认 Write 让安全层和 HITL 覆盖
- **与内置工具一致**：内置工具的 Category 由开发者明确声明；MCP 工具缺少信息时保守估计
- **可覆盖**：MCP server 可通过 `annotations.readOnlyHint=true` 声明只读

### Q3：为什么不在 AgentLoop 层面感知 MCP？

- **透明性**：AgentLoop 只通过 `ToolRegistry` 和 `BatchToolExecutor` 与工具交互，不感知来源
- **可测试**：MCP 工具和内置工具走完全相同的执行路径，安全层和 HITL 一视同仁
- **扩展性**：未来增加 Skill 工具、SubAgent 工具时，同样只需注册到 ToolRegistry

### Q4：为什么 App.cs 而非 TerminalApp 管理 MCP 连接？

- **生命周期对齐**：MCP 连接在 App 入口启动，在 App 退出时关闭，与 TerminalApp 的 UI 生命周期解耦
- **TerminalApp 可测性**：TerminalApp 接收已连接的 `McpConnectionManager`，不需要自己启动连接
- **资源清理**：`using var terminalApp` 确保 UI 先清理，然后 `await mcpManager.CloseAllAsync()` 确保网络/进程后清理

### Q5：为什么 initialize 超时 30 秒、tools/call 超时 60 秒？

- **initialize**：MCP server 启动通常很快（< 5 秒），但 npm install 首次可能慢，30 秒平衡体验与等待
- **tools/call**：工具执行时间不确定（可能涉及网络/IO），60 秒给足余量
- **可配置**：超时值可通过 `McpServerConfig` 未来扩展（本迭代硬编码）

---

## 五、与后续迭代的关系

### 5.1 迭代 12（Skill + Hook + 子 Agent）

- **Skill 系统**：Skill 声明 `tools_allow` / `tools_deny`——MCP 工具名含 server 前缀，过滤规则需匹配 `{server}/{tool}` 格式
- **Hook 引擎**：`tool_pre_exec` / `tool_post_exec` Hook 对 MCP 工具同样生效（通过 `BatchToolExecutor.OnBeforeExecuteAsync`）
- **子 Agent**：Fork 式子 Agent 继承父 ToolRegistry（含 MCP 工具）——需考虑 MCP 客户端的并发安全性

### 5.2 可选扩展（无明确迭代计划）

前 12 迭代跑通后再考虑，不属于任何具体迭代：

| 内容 | 复杂度 | 价值评估 |
|------|--------|---------|
| MCP Resources（resources/list/read） | 中 | 价值有限，工具已覆盖大部分场景 |
| MCP Prompts（prompts/list/get） | 中 | 与项目指令系统重叠 |
| MCP Sampling（server 反向调 LLM） | 高 | 需双向能力协商，架构改动大 |
| MCP server 热重载 | 中 | 配置变化时自动重连 |
| MCP server 进程守护（崩溃重启） | 中 | 提升健壮性 |
| tools/list_changed 通知 | 低 | 工具列表动态更新 |
| OAuth 认证 | 中 | 企业场景需要 |
| 超时可配置 | 低 | 小改进 |

---

## 六、子迭代验收概览

| 子迭代 | 验收编号范围 | 核心验收关卡 |
|--------|-------------|-------------|
| 11a | 11a-01 ~ 11a-30 | MockTransport 跑通 id 匹配；StdioTransport 能启子进程收发 |
| 11b | 11b-01 ~ 11b-40 | mock server 模拟 initialize→tools/list→tools/call 全流程；工具适配器注册到 ToolRegistry |
| 11c | 11c-01 ~ 11c-30 | 配置真实 MCP server，AI 能调用其工具；程序退出时子进程干净关闭 |

详见各子迭代文档。

---

**文档结束**。状态：[设计完成，待实现]
