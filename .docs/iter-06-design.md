# 迭代 6：ReAct Agent 循环（事件流 + Function Calling 闭环）— 详细设计

> 状态：[设计完成，待实现]
> 对应 `plan.md` 第三章「迭代 6」，本文档在其基础上补充实现级细节与可执行的验收清单。
> 前置：迭代 5 已交付工具系统骨架（`IBaseTool` / `ToolBase` / `ToolResult` / `ToolCategory` / `ToolParameter` / `ToolRegistry` / `ToolExecutor` + 三个文件工具 + `ClosedLoopDemo`）；迭代 4 已交付 `ConversationHistory`（含 `AddUser` / `AddAssistant` / `AddTool` / `ToProviderMessages`）/ `TokenEstimator` / `MessageExtensions`；迭代 3 已交付 `IBaseProvider.ChatAsync` + `ChatStreamAsync`（返回 `IAsyncEnumerable<string>`）/ `OpenAIProvider`（SSE 流式）/ `MockProvider` / `Message` + `MessageRole` + `ToolCall(Id, Name, JsonElement Input)` 类型（`Message.ToolCalls` 字段已在迭代 2a 预留）。本迭代把工具系统、Provider、History 串成 ReAct 闭环：LLM 决定调用工具 → 解析 `tool_calls` → 分批执行 → 结果回灌 → LLM 继续推理，直到 LLM 不再调工具或达到最大轮次。

## 一、概述

迭代 5 让"工具调用 → 执行 → 结果"的闭环在**无 LLM** 环境下跑通——`ToolCall` 由测试手工构造。迭代 6 把 LLM 接回环里：让 LLM 自己决定调哪个工具、传什么参数，宿主执行后把结果回灌，LLM 基于结果继续推理，形成 ReAct（Reason + Act）循环。

1. **`IBaseProvider` 演进**：迭代 3 的 `ChatStreamAsync` 只返回 `IAsyncEnumerable<string>`（纯文本 token），无法承载 `tool_calls`。本迭代新增重载 `ChatStreamAsync(messages, tools, toolChoice, ct)` 返回 `IAsyncEnumerable<ChatChunk>`，其中 `ChatChunk` 是文本增量与工具调用增量的 union。**保留旧重载**——迭代 4 的 App 与既有测试不回归。
2. **`ChatChunk`**：LLM 流式响应的协议中性单元。`TextDelta(string)` 承载文本片段（与旧 `string` token 等价），`ToolCallDelta(int Index, string? Id, string? Name, string? ArgumentsFragment)` 承载工具调用的分片（OpenAI 的 `tool_calls` 按 index 累积 `arguments` 字符串），`Done` 标记流终止。Provider 层把 OpenAI 的 wire format 翻译成 `ChatChunk`，AgentLoop 只消费 `ChatChunk`，不感知协议细节。
3. **`AgentEvent`**：AgentLoop 产出的事件单元，12 种类型覆盖一轮 ReAct 的完整生命周期（`RoundStart` / `TextDelta` / `AssistantMessage` / `ToolCallStart` / `ToolResult` / `ToolBlocked` / `RoundEnd` / `AgentDone` / `MaxRoundsReached` / `Error` / `Cancelled` / `Warning`）。事件流解耦"生产"（AgentLoop）与"展示"（本迭代是控制台打印，迭代 7 是 TUI 渲染）。
4. **`IAgentEventSink`**：事件消费者抽象。本迭代实现为 `ChannelEventSink`（基于 `System.Threading.Channels.Channel<AgentEvent>` 的有界/无界通道）。AgentLoop 写入 sink，App 从 sink 读取并打印。迭代 7 TUI 替换 sink 实现即可，AgentLoop 不动。
5. **`AgentLoop`**：ReAct 核心循环。`for round in 1..maxRounds`：调 Provider 流式 → 累积 `ChatChunk`（文本 + tool_calls）→ assistant 消息入历史 → 若无 tool_calls 则 `AgentDone` 退出 → 否则分批执行 → tool 结果入历史 → 进入下一轮。最大轮次默认 10，防止无限循环。
6. **`BatchToolExecutor`**：基于迭代 5 `ToolExecutor` 的分批执行器。把同批 `tool_calls` 按 `ToolCategory` 分组：`Read` 组用 `Task.WhenAll` 并发（幂等、无副作用），`Write` 组顺序 `await`（有副作用、避免竞态）。单批最大并行度可配（默认 5），防止 OOM。
7. **LLM 响应解析**：OpenAI 流式响应中 `delta.tool_calls` 是按 `index` 累积的分片——`id` 与 `function.name` 通常在首片到达，`function.arguments` 是 JSON 字符串分片逐片到达，需按 index 拼接完整后 `JsonDocument.Parse` 得到 `JsonElement Input`。本迭代在 `OpenAIProvider` 内实现累积算法，单元测试覆盖分片边界。
8. **三个补充工具**：`RunCommandTool`（执行 shell 命令）/ `GlobTool`（文件名模式匹配）/ `GrepTool`（内容正则搜索）。`RunCommandTool` 声明 `Category=Write`（有副作用），`GlobTool` / `GrepTool` 声明 `Category=Read`（幂等、可并发）。至此内置工具达 6 个，覆盖 plan.md 的"5 个就够"目标。
9. **MockProvider 脚本化扩展**：迭代 3 的 `MockProvider` 只回显最后一条 user，无法测试 AgentLoop。本迭代给它加脚本队列——`EnqueueScript(params ChatChunk[])` 注入预设响应序列，让无 LLM 环境下也能跑通"LLM 返回 tool_call → 执行 → LLM 返回总结"的完整 ReAct 闭环。默认行为（无脚本时）保持迭代 3 的回显语义，既有测试不回归。
10. **App 接入**：App 主循环从"直接调 Provider 流式打印"改为"调 AgentLoop + 消费事件流打印"。`/clear` 行为保持。用户输入 → AgentLoop 跑多轮 ReAct → 事件流打印 → 等下一轮用户输入。

本迭代**刻意保持**：
- **不接 TUI**：事件流消费者是控制台打印（`Console.Write` + Spectre 着色），迭代 7 才替换为 Spectre 全屏 TUI + 流式渲染 + HITL 确认对话框。
- **不做安全层**：`RunCommandTool` 接受任意命令、文件工具接受任意路径。黑名单 / 沙箱 / 三档权限 / HITL 确认全部在迭代 8。本迭代 `ToolBlocked` 事件类型**定义但不在主路径产生**（预留迭代 7/8 接入）。
- **不做上下文截断**：工具结果（如 `read_file` 读 10 万行日志）直接全量入历史。截断（50K 字符阈值）在迭代 9 Truncator。
- **不做会话持久化**：历史仍为内存版，程序退出即丢失。JSONL 存档在迭代 10。
- **不做 system prompt 管理 / PromptBuilder**：本迭代 App 注入一段最小 system prompt（说明可用工具），完整 PromptBuilder 在后续迭代。
- **不做 sub_agent / Skill / Hook / MCP**：分别在第 12 / 12 / 12 / 11 迭代。
- **不做 Anthropic Provider**：`ToAnthropicSchema` 在迭代 5 已预留接口，Anthropic Provider 实际接入时再补 wire format。本迭代只验证 OpenAI 兼容协议（含 DeepSeek）。

> **拆分考量**：迭代 6 是否拆为 6a（Provider 演进 + ChatChunk + 响应解析）+ 6b（AgentLoop + 事件流 + 分批执行）？
> - 不拆理由：Provider 演进脱离 AgentLoop 是空壳——`ChatChunk` 的设计直接由 AgentLoop 的消费需求驱动（需要区分文本与工具调用增量），响应解析的正确性只能通过 AgentLoop 端到端验证。两者一起设计与验收，能形成"让 AI 读 README 并总结"这一可验证交付物。
> - **结论**：本迭代不拆分，作为整体设计。

## 二、学习目标

1. **ReAct 范式**：理解 Agent 的核心循环是 Reason（LLM 推理产出文本 + 工具调用）+ Act（宿主执行工具）+ Observe（结果回灌给 LLM）的迭代。LLM 不是"一次性回答"，而是"思考一步、做一步、看结果、再思考"——这是 Agent 与 Chatbot 的本质区别。
2. **Function Calling 协议**：理解 OpenAI 的 `tools` 字段（工具 schema 数组）+ `tool_choice`（auto/none/required/指定）+ 响应中的 `tool_calls`（`id` + `function.name` + `function.arguments` JSON 字符串）。LLM 生成的是 JSON 字符串参数，宿主解析后执行——这是 LLM 与宿主的"函数调用契约"。
3. **流式 tool_calls 累积**：OpenAI 流式响应中 `tool_calls` 按 `index` 分片到达——`id` / `name` 在首片，`arguments` 是 JSON 字符串的片段逐片累积。理解"流式解析 = 按 index 拼接 arguments 字符串 + 最终 JSON.Parse"，与非流式（一次性拿到完整 `tool_calls`）的差异。
4. **事件流架构**：AgentLoop 作为事件生产者，UI/打印作为消费者，通过 `Channel<AgentEvent>` 解耦。理解"生产者不关心谁消费、消费者不关心谁生产"的解耦收益——迭代 7 TUI 替换消费者即可，AgentLoop 不动。
5. **读写工具分批执行**：`Read` 工具幂等无副作用可并发（`Task.WhenAll` 提速），`Write` 工具有副作用需串行（顺序 `await` 避免竞态）。理解 `ToolCategory` 枚举在迭代 5 的设计动机在本迭代落地为执行策略。
6. **最大轮次防护**：LLM 可能陷入"调工具 → 拿结果 → 再调工具"的死循环（如反复 `read_file` 同一文件）。`maxRounds` 默认 10 是兜底，让 Agent 在失控时能优雅停止而非无限消耗 token。
7. **错误回灌与自我修正**：工具失败（如 `edit_file` 多次匹配）的 `ToolResult.Error` 作为 tool 消息回灌给 LLM，它看到错误后调整策略（提供更精确的 `old_text`）。这是 ReAct"自我修正"的物质基础——迭代 5 `ToolExecutor` 把异常转 `ToolResult.Fail` 的设计在此闭环。
8. **Provider 协议无关抽象**：`ChatChunk` 是协议中性的响应单元，`OpenAIProvider` 把 OpenAI wire format 翻译成 `ChatChunk`，未来 `AnthropicProvider` 把 Anthropic wire format 也翻译成 `ChatChunk`。AgentLoop 只消费 `ChatChunk`，不感知协议差异——体会迭代 2a"协议无关抽象"的累积收益。
9. **取消的全链路贯穿**：`CancellationToken` 从用户 Ctrl+C 一路传到 Provider HTTP 请求与工具执行。理解"取消是协作式"——`cancellationToken.ThrowIfCancellationRequested()` 在循环点检查，`HttpClient` / `FileStream` 异步 API 接受 ct 响应取消。
10. **Channel 的背压与解耦**：`Channel.CreateUnbounded<AgentEvent>` 无界通道避免背压（AgentLoop 不会因消费者慢而阻塞），代价是消费者跟不上时事件积压在内存。本迭代工具执行快、打印快，无界足够；后续如需背压可改 `Bounded`。

## 三、范围

### 3.1 本迭代包含（In Scope）

| 项 | 说明 |
| --- | --- |
| `Agent/ChatChunk.cs` | LLM 流式响应单元：`abstract record ChatChunk` + `TextDelta` / `ToolCallDelta` / `Done` 派生 |
| `Agent/AgentEvent.cs` | 12 种事件类型：`abstract record AgentEvent` + 派生 |
| `Agent/IAgentEventSink.cs` | 事件消费者接口：`WriteAsync(AgentEvent)` + `Complete()` |
| `Agent/ChannelEventSink.cs` | 基于 `Channel<AgentEvent>` 的实现：无界通道 + `Reader` 暴露给消费者 |
| `Agent/AgentLoop.cs` | ReAct 核心循环：`RunAsync(userInput, sink, ct)` 多轮推理 + 分批执行 |
| `Agent/BatchToolExecutor.cs` | 分批执行器：Read 并发 / Write 串行，委托迭代 5 `ToolExecutor` 单次执行 |
| `Providers/IBaseProvider.cs` | 扩展：新增 `ChatStreamAsync(messages, tools, toolChoice, ct)` 重载返回 `IAsyncEnumerable<ChatChunk>`；旧重载保留 |
| `Providers/OpenAIProvider.cs` | 扩展：`BuildRequestBody` 加 `tools` / `tool_choice` 字段；流式解析加 `delta.tool_calls` 累积算法 |
| `Providers/MockProvider.cs` | 扩展：`EnqueueScript(ChatChunk[])` 脚本队列；默认回显行为不变 |
| `Providers/MessageTypes.cs` | 扩展：`Message` 加 `ToolCallId` 字段（tool 角色消息关联 tool_call_id）；`ToolCall` 不变 |
| `Conversation/History.cs` | 扩展：`AddAssistant(content, toolCalls)` 重载 + `AddTool(content, toolCallId)` 重载；旧重载保留 |
| `Conversation/MessageExtensions.cs` | 扩展：`ToOpenAiWire()` 把 `Message` 序列化为 OpenAI wire format（含 `tool_calls` / `tool_call_id`） |
| `Tools/RunCommandTool.cs` | 执行 shell 命令：参数 `command` + `args` + `cwd` + `timeout`，Category=Write |
| `Tools/GlobTool.cs` | 文件名模式匹配：参数 `pattern` + `path`，Category=Read |
| `Tools/GrepTool.cs` | 内容正则搜索：参数 `pattern` + `path` + `include`，Category=Read |
| `App/App.cs` | 改：主循环调 AgentLoop + 消费事件流打印；装配 ToolRegistry（6 个工具）+ BatchToolExecutor |
| `Config/Models.cs` | 扩展：`AppConfig` 加 `Agent` 节（`MaxRounds` / `ToolChoice` / `MaxParallelism`） |
| `example.parrotcode.yaml` | 加 `agent:` 配置节示例 |
| 单元测试 | `AgentLoopTests` / `ChatChunkAccumulatorTests` / `BatchToolExecutorTests` / `ChannelEventSinkTests` / `RunCommandToolTests` / `GlobToolTests` / `GrepToolTests` + `OpenAIProviderTests`（补充 tool_calls 解析）+ `MockProviderTests`（补充脚本）+ `MessageExtensionsTests`（补充 wire format）+ `AgentLoopIntegrationTests`（集成） |

### 3.2 本迭代不包含（Out of Scope）

- TUI 全屏渲染 / 流式渲染 / HITL 确认对话框 → 迭代 7
- 安全层（黑名单 / 沙箱 / 三档权限 / HITL）→ 迭代 8（`ToolBlocked` 事件类型预留，本迭代主路径不产生）
- 工具结果超长截断（50K 字符阈值）→ 迭代 9 Truncator
- 上下文压缩 / 摘要 / 熔断 → 迭代 9
- 会话持久化（JSONL）→ 迭代 10
- 完整斜杠命令系统 → 迭代 10（`/clear` 仍是硬编码）
- system prompt 完整管理 / PromptBuilder → 后续迭代（本迭代注入最小 system prompt）
- `AnthropicProvider` 实际接入 → 后续迭代（`ToAnthropicSchema` 接口已预留）
- `sub_agent` 工具 / Skill / Hook → 迭代 12
- MCP 工具适配 → 迭代 11
- 工具调用计费 / 限流 / 配额 → 后续迭代
- `tool_choice` 强制指定工具的细粒度 UI → 后续迭代（配置可设，UI 在迭代 7+）

### 3.3 与迭代 7 的边界

事件流在迭代 6 与迭代 7 都涉及，边界如下：

| 本迭代（迭代 6） | 迭代 7 |
| --- | --- |
| 事件消费者是 `Console.Write` + Spectre 着色（行模式打印） | 替换为 Spectre 全屏 TUI + `Live` 流式渲染 |
| `ToolBlocked` 事件类型**定义但不在主路径产生** | HITL 在 WRITE 工具执行前 emit `ToolBlocked` 并 `await Task<HitlDecision>` |
| 无状态栏 | 状态栏显示 Provider / Model / 安全等级 / 上下文占比 / 当前轮次 |
| 无交互式确认 | WRITE 工具弹 A/S/P/D 确认框 |
| 事件流通道无界 | 可能改有界（TUI 渲染速度限制需背压） |

> 本迭代的 `IAgentEventSink` 抽象是迭代 7 TUI 接入的基础设施。设计时确保接口稳定（`WriteAsync` + `Complete`），迭代 7 只替换 sink 实现类。

### 3.4 与迭代 8 的边界

| 本迭代（迭代 6） | 迭代 8 |
| --- | --- |
| `RunCommandTool` 接受任意命令 | `Blacklist` 拦截 `rm -rf /` 等危险命令 |
| 文件工具接受任意路径 | `PathSandbox` 拒绝绝对路径 / `..` 遍历 / 项目目录边界 |
| 无 HITL | WRITE 工具执行前 emit `ToolBlocked` + `await` 用户决策 |
| `ToolExecutor` 直接执行 | `SecurityGuard` 作为 `ToolExecutor` 前置过滤器 |
| `ToolBlocked` 事件类型预留但不产生 | 主路径产生 `ToolBlocked`，LLM 收到拒绝原因调整策略 |

> 本迭代的 `BatchToolExecutor` 在工具执行前留一个 hook 点（虚方法 `OnBeforeExecuteAsync`），迭代 8 的 `SecurityGuard` 在此接入，工具系统自身无需感知安全层。

### 3.5 与迭代 5 的边界

| 迭代 5 | 本迭代（迭代 6） |
| --- | --- |
| `ToolExecutor` 单次执行 | `BatchToolExecutor` 委托 `ToolExecutor` 做单次执行，外加分批调度 |
| `ToolCategory` 枚举存在但不消费 | `BatchToolExecutor` 按 Category 分组：Read 并发 / Write 串行 |
| `ToolCall` 手工构造（ClosedLoopDemo） | `ToolCall` 由 `OpenAIProvider` 解析 LLM 响应产生 |
| `ConversationHistory.AddTool` 定义但不使用 | 启用：`AddTool(content, toolCallId)` 把 `ToolResult.Content` 入历史 |
| `Message.ToolCalls` 字段预留 | 启用：assistant 消息携带 `ToolCalls` |
| 三个文件工具 | 补齐 `RunCommandTool` / `GlobTool` / `GrepTool`（共 6 个） |

## 四、架构设计

### 4.1 模块结构（迭代 6 增量）

```
ParrotCode.Net/
├── Program.cs                 # 改：装配 ToolRegistry + BatchToolExecutor + AgentLoop 注入 App
├── App/
│   └── App.cs                 # 改：主循环调 AgentLoop + 消费事件流打印
├── Config/
│   └── Models.cs              # 改：AppConfig 加 Agent 节
├── Conversation/
│   ├── History.cs             # 改：AddAssistant(content, toolCalls) + AddTool(content, toolCallId) 重载
│   ├── MessageExtensions.cs   # 改：ToOpenAiWire() 序列化含 tool_calls 的消息
│   └── TokenEstimator.cs      # 不变
├── Providers/
│   ├── IBaseProvider.cs       # 改：新增 ChatStreamAsync(messages, tools, toolChoice, ct) 重载
│   ├── MessageTypes.cs        # 改：Message 加 ToolCallId 字段
│   ├── MockProvider.cs        # 改：EnqueueScript 脚本队列
│   ├── OpenAIProvider.cs      # 改：BuildRequestBody 加 tools/tool_choice；流式解析 tool_calls 累积
│   ├── ProviderException.cs   # 不变
│   └── ProviderFactory.cs     # 不变
├── Tools/                     # 迭代 5 既有 + 3 个新工具
│   ├── IBaseTool.cs           # 不变
│   ├── ToolBase.cs            # 不变
│   ├── ToolResult.cs          # 不变
│   ├── ToolCategory.cs        # 不变
│   ├── ToolParameter.cs       # 不变
│   ├── ToolRegistry.cs        # 不变
│   ├── ToolExecutor.cs        # 不变
│   ├── ReadFileTool.cs        # 不变
│   ├── WriteFileTool.cs       # 不变
│   ├── EditFileTool.cs        # 不变
│   ├── ClosedLoopDemo.cs      # 不变（迭代 5 验收物保留）
│   ├── RunCommandTool.cs      # 新增：执行 shell 命令
│   ├── GlobTool.cs            # 新增：文件名模式匹配
│   └── GrepTool.cs            # 新增：内容正则搜索
└── Agent/                     # 新增目录
    ├── ChatChunk.cs           # 新增：LLM 流式响应单元（union）
    ├── AgentEvent.cs          # 新增：12 种事件类型
    ├── IAgentEventSink.cs     # 新增：事件消费者接口
    ├── ChannelEventSink.cs    # 新增：Channel 实现
    ├── AgentLoop.cs           # 新增：ReAct 核心循环
    └── BatchToolExecutor.cs   # 新增：分批执行器
```

> 命名空间约定沿用迭代 1-5：所有源文件统一 `namespace ParrotCode`，文件夹仅作物理组织。

### 4.2 调用流程（ReAct 循环）

```
┌──────────────────────────────────────────────────────────────────────┐
│  App 主循环                                                          │
│  while (!ct.IsCancellationRequested):                                │
│      line = Console.ReadLine()                                       │
│      history.AddUser(line)                                           │
│      var sink = new ChannelEventSink()                               │
│      var loopTask = agentLoop.RunAsync(history, sink, ct)            │
│      await foreach (var evt in sink.Reader.ReadAllAsync(ct)):        │
│          RenderEvent(evt)  # Console.Write / Spectre 着色             │
│      await loopTask                                                  │
└──────────────────────────────────────────────────────────────────────┘
                                    │
                                    ▼
┌──────────────────────────────────────────────────────────────────────┐
│  AgentLoop.RunAsync(history, sink, ct)                               │
│  for round in 1..maxRounds:                                          │
│      sink.WriteAsync(RoundStart(round))                              │
│      messages = history.ToProviderMessages() + system prompt         │
│      tools = registry.ToOpenAiSchemas()                              │
│      ┌─ await foreach (chunk in provider.ChatStreamAsync(...)) ─┐    │
│      │  switch chunk:                                            │    │
│      │    TextDelta(t) → textBuf.Append(t); sink.Write(TextDelta)│   │
│      │    ToolCallDelta → 累积到 toolCallAccumulator[index]      │    │
│      │    Done → break                                          │    │
│      └──────────────────────────────────────────────────────────┘    │
│      var toolCalls = toolCallAccumulator.Build()                     │
│      history.AddAssistant(textBuf.ToString(), toolCalls)             │
│      if toolCalls.Empty:                                             │
│          sink.WriteAsync(AgentDone(textBuf.ToString()))              │
│          return                                                      │
│      sink.WriteAsync(ToolCallStart(...))  # 每个工具调用              │
│      var results = await batchExecutor.ExecuteAsync(toolCalls, ct)   │
│      foreach (call, result) in zip(toolCalls, results):              │
│          history.AddTool(result.Content, call.Id)                    │
│          sink.WriteAsync(ToolResult(call, result))                   │
│      sink.WriteAsync(RoundEnd(round))                                │
│  sink.WriteAsync(MaxRoundsReached(maxRounds))                        │
└──────────────────────────────────────────────────────────────────────┘
                                    │
                                    ▼
┌──────────────────────────────────────────────────────────────────────┐
│  BatchToolExecutor.ExecuteAsync(toolCalls, ct)                       │
│  readCalls  = toolCalls.Where(c => registry.Get(c.Name).Category=Read)│
│  writeCalls = toolCalls.Where(c => registry.Get(c.Name).Category=Write)│
│  # Read 组并发（Task.WhenAll，限流到 maxParallelism）                 │
│  readResults = await Task.WhenAll(readCalls.Chunk(maxParallel)       │
│      .Select(batch => batch.Select(c => executor.ExecuteAsync(c,ct))│
│                            .Cast<Task<ToolResult>>().ToArray())      │
│      .Select(async tasks => await Task.WhenAll(tasks)))              │
│  # Write 组串行                                                       │
│  writeResults = new List<ToolResult>()                               │
│  foreach (c in writeCalls):                                          │
│      writeResults.Add(await executor.ExecuteAsync(c, ct))            │
│  return MergeInOriginalOrder(readResults, writeResults, toolCalls)   │
└──────────────────────────────────────────────────────────────────────┘
```

### 4.3 关键类型设计

#### 4.3.1 `ChatChunk`（LLM 流式响应单元）

```csharp
namespace ParrotCode;

/// <summary>
/// LLM 流式响应的协议中性单元。Provider 层把 OpenAI / Anthropic wire format
/// 翻译成 ChatChunk，AgentLoop 只消费 ChatChunk，不感知协议细节。
/// 迭代 3 的 IAsyncEnumerable<string> 只能承载文本，无法承载 tool_calls——
/// ChatChunk 是其演进：用 union 区分文本增量与工具调用增量。
/// </summary>
public abstract record ChatChunk
{
    /// <summary>
    /// 文本增量（与迭代 3 的 string token 等价）。
    /// LLM 产出的回复文本按片段到达，消费方拼接得到完整回复。
    /// </summary>
    public sealed record TextDelta(string Text) : ChatChunk;

    /// <summary>
    /// 工具调用增量。OpenAI 流式中 tool_calls 按 index 分片到达：
    /// - 首片通常含 Id + Name（arguments 可能空或开始片段）
    /// - 后续片只含 ArgumentsFragment（arguments JSON 字符串的片段）
    /// - 同 index 的多片需累积：Id/Name 取首个非空，Arguments 拼接所有片段
    /// AgentLoop 按 Index 累积，流结束后 Build 成完整 ToolCall。
    /// </summary>
    public sealed record ToolCallDelta(
        int Index,
        string? Id,
        string? Name,
        string? ArgumentsFragment) : ChatChunk;

    /// <summary>
    /// 流终止标记（OpenAI 的 data: [DONE]）。
    /// 收到此 chunk 后 AgentLoop 停止本轮流式消费，进入 tool_calls 构建阶段。
    /// </summary>
    public sealed record Done : ChatChunk;
}
```

> **设计要点**：
> - **`abstract record ChatChunk` + 派生**：C# 的 discriminated union 模式。用 `record` 保证不可变 + 值相等，用 `abstract` 强制只能构造派生类型。`switch` 模式匹配按类型分发，编译器检查穷尽性。
> - **`TextDelta` 承载文本**：与迭代 3 的 `string` token 语义一致，便于 AgentLoop 的文本累积逻辑复用迭代 4 的 `StringBuilder` 模式。
> - **`ToolCallDelta` 按 Index 累积**：OpenAI 流式 `tool_calls` 是数组，每个元素的 `arguments` 是 JSON 字符串分片。同 `index` 的多片需拼接——这是流式 Function Calling 的核心复杂度。`Id` / `Name` 通常在首片到达，后续片为 null，累积器取首个非空值。
> - **`Done` 显式标记**：虽然 `IAsyncEnumerable` 自然结束也代表流终止，但 OpenAI 的 `[DONE]` 标记是协议级信号，显式映射成 `Done` chunk 让 AgentLoop 的状态机更清晰（收 Done 即进入构建阶段）。
> - **协议中性**：`ChatChunk` 不含任何 OpenAI 特定字段（如 `finish_reason`）。Provider 层把 `finish_reason=tool_calls` 翻译成"有 tool_calls 待执行"，把 `finish_reason=stop` 翻译成"无 tool_calls，AgentDone"——这些语义在 AgentLoop 通过"toolCalls 是否为空"判断，不需要 `finish_reason` 透传。

#### 4.3.2 `AgentEvent`（事件流单元，12 种）

```csharp
namespace ParrotCode;

/// <summary>
/// AgentLoop 产出的事件单元。事件流解耦生产（AgentLoop）与展示（控制台/TUI）。
/// 12 种类型覆盖一轮 ReAct 的完整生命周期 + 控制信号。
/// 本迭代消费者是控制台打印；迭代 7 替换为 Spectre TUI 渲染器。
/// </summary>
public abstract record AgentEvent
{
    /// <summary>新一轮 ReAct 开始。Round 是 1-based 轮次号。</summary>
    public sealed record RoundStart(int Round) : AgentEvent;

    /// <summary>文本增量（与 ChatChunk.TextDelta 对应，转发给消费者实时展示）。</summary>
    public sealed record TextDelta(string Text) : AgentEvent;

    /// <summary>本轮 LLM 完整回复（流式结束后产出，便于消费者做"整段刷新"渲染）。</summary>
    public sealed record AssistantMessage(string Content) : AgentEvent;

    /// <summary>工具调用开始（LLM 决定调工具）。Call 含 Id/Name/Input。</summary>
    public sealed record ToolCallStart(ToolCall Call) : AgentEvent;

    /// <summary>工具执行结果。Call + Result 配对，消费者可展示"调用 X → 成功/失败"。</summary>
    public sealed record ToolResult(ToolCall Call, global::ParrotCode.ToolResult Result) : AgentEvent;

    /// <summary>
    /// 工具被拦截（迭代 8 安全层 / 迭代 7 HITL 拒绝）。
    /// 本迭代主路径不产生——BatchToolExecutor 不做安全检查。
    /// 预留事件类型，迭代 7/8 接入时填充。
    /// </summary>
    public sealed record ToolBlocked(ToolCall Call, string Reason) : AgentEvent;

    /// <summary>本轮 ReAct 结束。Round 与 RoundStart 对应。</summary>
    public sealed record RoundEnd(int Round) : AgentEvent;

    /// <summary>Agent 完成（LLM 不再调工具，输出最终回复）。FinalText 为最终文本（可能为空）。</summary>
    public sealed record AgentDone(string? FinalText) : AgentEvent;

    /// <summary>达到最大轮次，Agent 被强制停止。Rounds 为实际执行的轮次数。</summary>
    public sealed record MaxRoundsReached(int Rounds) : AgentEvent;

    /// <summary>非致命警告（如某工具超时但 Agent 继续）。Message 为人类可读原因。</summary>
    public sealed record Warning(string Message) : AgentEvent;

    /// <summary>致命错误（如 Provider 401 / 网络断开）。Agent 终止。</summary>
    public sealed record Error(string Message, Exception? Exception) : AgentEvent;

    /// <summary>用户取消（Ctrl+C）。Agent 优雅停止。</summary>
    public sealed record Cancelled : AgentEvent;
}
```

> **设计要点**：
> - **`ToolResult` 命名冲突**：事件类型 `ToolResult` 与迭代 5 `ParrotCode.ToolResult` 重名。用全限定 `global::ParrotCode.ToolResult` 区分，或把事件类型改名为 `ToolResultEvent`。本设计选后者更清晰——实际实现时事件类用 `ToolResultEvent` / `ToolCallStartEvent` 等带 `Event` 后缀避免歧义（本节示例为可读性省略后缀，实现时补上）。
> - **`ToolBlocked` 预留**：迭代 8 的 `SecurityGuard` 拒绝工具时，`BatchToolExecutor` emit `ToolBlocked`，拒绝原因作为 `ToolResult.Fail` 回灌给 LLM。本迭代定义事件类型但不产生，让迭代 7/8 接入时事件分类已稳定。
> - **`AssistantMessage` vs `TextDelta`**：`TextDelta` 是逐字流式（实时展示），`AssistantMessage` 是本轮完整回复（流式结束后产出）。两者都发——消费者可选其一渲染（迭代 6 控制台用 `TextDelta` 逐字打印，迭代 7 TUI 可能用 `AssistantMessage` 整段刷新）。
> - **`Error` vs `Warning`**：`Error` 终止 Agent（Provider 401 / 5xx），`Warning` 不终止（某工具超时但 Agent 继续）。分类让消费者决定是否打断渲染。
> - **`Cancelled` 显式**：用户 Ctrl+C 时 emit `Cancelled` 让消费者做收尾（如换行、打印"已取消"），而非依赖 `OperationCanceledException` 传播。

#### 4.3.3 `IAgentEventSink` + `ChannelEventSink`

```csharp
using System.Threading.Channels;

namespace ParrotCode;

/// <summary>
/// 事件消费者抽象。AgentLoop 写入 sink，App/TUI 从 sink 读取。
/// 本迭代只有 ChannelEventSink 一个实现；迭代 7 可加 TuiEventSink 直接渲染（不走 Channel）。
/// </summary>
public interface IAgentEventSink
{
    /// <summary>写入事件。不阻塞——Channel 写入通常立即返回（无界通道）。</summary>
    ValueTask WriteAsync(AgentEvent evt, CancellationToken cancellationToken);

    /// <summary>标记事件流结束。AgentLoop 退出前调用，让消费者的 ReadAllAsync 自然结束。</summary>
    void Complete();
}

/// <summary>
/// 基于 System.Threading.Channels 的无界通道实现。
/// AgentLoop 写入 Writer，消费者通过 Reader.ReadAllAsync 读取。
/// 无界：AgentLoop 不会因消费者慢而阻塞；代价是消费者跟不上时事件积压内存。
/// 本迭代工具执行快、打印快，无界足够；后续如需背压改 Channel.CreateBounded。
/// </summary>
public sealed class ChannelEventSink : IAgentEventSink
{
    private readonly Channel<AgentEvent> _channel =
        Channel.CreateUnbounded<AgentEvent>(new UnboundedChannelOptions
        {
            SingleReader = true,   // 只有 App 一个消费者
            SingleWriter = true    // 只有 AgentLoop 一个生产者
        });

    /// <summary>消费者读取端。App 用 await foreach (evt in sink.Reader.ReadAllAsync(ct))。</summary>
    public ChannelReader<AgentEvent> Reader => _channel.Reader;

    public ValueTask WriteAsync(AgentEvent evt, CancellationToken cancellationToken) =>
        _channel.Writer.WriteAsync(evt, cancellationToken);

    public void Complete() => _channel.Writer.Complete();
}
```

> **设计要点**：
> - **`Channel.CreateUnbounded` + `SingleReader/SingleWriter`**：本迭代生产者（AgentLoop）与消费者（App）各一，开启单读单写优化减少同步开销。如未来需多消费者（如 TUI + 日志 sink），去掉 `SingleReader`。
> - **`ValueTask` 而非 `Task`**：无界通道写入通常同步完成（不阻塞），`ValueTask` 避免同步路径的 `Task` 分配。消费者 `ReadAllAsync` 仍是 `IAsyncEnumerable<AgentEvent>`。
> - **`Complete()` 显式结束**：AgentLoop 退出前调 `Complete()`，让 `Reader.ReadAllAsync` 的 `await foreach` 自然结束——消费者不需要额外信号判断 Agent 是否完成。
> - **不暴露 `Writer`**：只暴露 `Reader` 给消费者，`Writer` 通过 `WriteAsync` 受控访问。避免消费者误写入。

#### 4.3.4 `IBaseProvider` 扩展

```csharp
namespace ParrotCode;

/// <summary>
/// 协议无关的 Provider 抽象。替代迭代 1 的临时 IChatProvider。
/// 迭代 6：新增带 tools 的流式重载，返回 IAsyncEnumerable<ChatChunk>。
/// 旧重载（ChatAsync + ChatStreamAsync 返回 string）保留，迭代 3/4 既有代码与测试不回归。
/// </summary>
public interface IBaseProvider
{
    /// <summary>非流式聊天（迭代 3 保留）。用于不需要工具调用与实时反馈的场景。</summary>
    Task<string> ChatAsync(IReadOnlyList<Message> messages, CancellationToken cancellationToken);

    /// <summary>
    /// 纯文本流式（迭代 3 保留）。返回 IAsyncEnumerable<string>。
    /// 不传 tools，LLM 不会产出 tool_calls。
    /// 迭代 4 的 App 仍用此重载（本迭代 App 改用新重载，但旧重载保留供其他场景）。
    /// </summary>
    IAsyncEnumerable<string> ChatStreamAsync(
        IReadOnlyList<Message> messages,
        CancellationToken cancellationToken);

    /// <summary>
    /// 带 tools 的流式（迭代 6 新增）。返回 IAsyncEnumerable<ChatChunk>。
    /// AgentLoop 用此重载：tools 来自 ToolRegistry.ToOpenAiSchemas()，
    /// toolChoice 控制 LLM 是否强制调用工具（auto/none/required）。
    /// Provider 把协议 wire format 翻译成 ChatChunk，AgentLoop 不感知协议细节。
    /// </summary>
    IAsyncEnumerable<ChatChunk> ChatStreamAsync(
        IReadOnlyList<Message> messages,
        JsonElement? tools,
        string toolChoice,
        CancellationToken cancellationToken);
}
```

> **设计要点**：
> - **新增重载而非改签名**：保留旧 `ChatStreamAsync(IReadOnlyList<Message>, CancellationToken)` 让迭代 4 App 与既有测试不回归。新重载通过 `tools` + `toolChoice` 参数区分。旧重载等价于 `tools=null, toolChoice="none"`。
> - **`tools` 为 `JsonElement?`**：直接来自 `ToolRegistry.ToOpenAiSchemas()`，已是 OpenAI wire format 的 JSON 数组。Provider 透传到请求体，不重新序列化。`null` 表示不带工具（等价于旧行为）。
> - **`toolChoice` 为 string**：`"auto"`（默认，LLM 自决）/ `"none"`（禁止调工具）/ `"required"`（必须调工具）。OpenAI 还支持指定工具 `{"type":"function","function":{"name":"..."}}`，本迭代用 string 简化，指定工具场景用 JSON 字符串透传（Provider 不解析，直接放进请求体）。
> - **`AnthropicProvider` 实现此接口时**：把 `tools`（OpenAI schema）转成 Anthropic 的 `input_schema` 格式，`toolChoice` 映射到 Anthropic 的 `tool_choice`。本迭代不实现 AnthropicProvider，但接口设计确保未来可加。

#### 4.3.5 `OpenAIProvider` 扩展（tool_calls 流式累积解析）

```csharp
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;

namespace ParrotCode;

public sealed class OpenAIProvider : IBaseProvider
{
    // —— 既有代码不变（构造 / ChatAsync / 旧 ChatStreamAsync / SendAsync / FormatError 等） ——

    // —— 新增：带 tools 的流式 ——

    public async IAsyncEnumerable<ChatChunk> ChatStreamAsync(
        IReadOnlyList<Message> messages,
        JsonElement? tools,
        string toolChoice,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var body = BuildRequestBody(messages, stream: true, tools, toolChoice);
        using var response = await SendAsync(body, cancellationToken);

        using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(stream);

        // tool_calls 累积器：按 index 拼接 arguments 字符串
        var accumulator = new ToolCallAccumulator();

        while (!cancellationToken.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(cancellationToken);
            if (line is null) break;
            if (string.IsNullOrEmpty(line)) continue;
            if (!line.StartsWith("data: ")) continue;

            var data = line["data: ".Length..];
            if (data == "[DONE]")
            {
                yield return new ChatChunk.Done();
                break;
            }

            using var doc = JsonDocument.Parse(data);
            var choices = doc.RootElement.GetProperty("choices");
            if (choices.GetArrayLength() == 0) continue;

            var delta = choices[0].GetProperty("delta");

            // 1. 文本增量
            if (delta.TryGetProperty("content", out var contentEl))
            {
                var text = contentEl.GetString();
                if (!string.IsNullOrEmpty(text))
                    yield return new ChatChunk.TextDelta(text);
            }

            // 2. 工具调用增量（按 index 累积）
            if (delta.TryGetProperty("tool_calls", out var tcEl))
            {
                foreach (var tc in tcEl.EnumerateArray())
                {
                    var index = tc.GetProperty("index").GetInt32();
                    var id = tc.TryGetProperty("id", out var idEl) ? idEl.GetString() : null;
                    var fn = tc.TryGetProperty("function", out var fnEl) ? fnEl : (JsonElement?)null;
                    var name = fn?.TryGetProperty("name", out var nameEl) == true
                        ? nameEl.GetString() : null;
                    var args = fn?.TryGetProperty("arguments", out var argsEl) == true
                        ? argsEl.GetString() : null;

                    accumulator.Accumulate(index, id, name, args);
                }
            }
            // reasoning_content（DeepSeek-reasoner）被自然忽略
        }

        // 流结束后不再 yield tool_calls——累积器由 AgentLoop 取走 Build
        // （AgentLoop 通过 provider 调用前后无法传递 accumulator，故用下述方案：
        //   累积的 tool_calls 在流结束前的最后一批 chunk 里 yield 出去）
        // 见下方"累积器输出策略"说明
    }

    // —— BuildRequestBody 扩展：加 tools / tool_choice ——

    private string BuildRequestBody(
        IReadOnlyList<Message> messages,
        bool stream,
        JsonElement? tools = null,
        string? toolChoice = null)
    {
        // 消息序列化用 MessageExtensions.ToOpenAiWire()（含 tool_calls / tool_call_id）
        var msgArray = messages.Select(m => m.ToOpenAiWire());

        // 用 JsonNode 拼装以便注入 tools（JsonElement 不能直接放进匿名对象）
        var root = new System.Text.Json.Nodes.JsonObject
        {
            ["model"] = _config.Model,
            ["messages"] = System.Text.Json.Nodes.JsonNode.Parse(
                JsonSerializer.Serialize(msgArray)),
            ["stream"] = stream
        };

        if (tools is { ValueKind: JsonValueKind.Array })
        {
            root["tools"] = System.Text.Json.Nodes.JsonNode.Parse(
                tools.Value.GetRawText());
            root["tool_choice"] = toolChoice ?? "auto";
        }

        return root.ToJsonString();
    }
}
```

> **累积器输出策略说明**：
>
> 上述示例为可读性简化。实际实现的难点是：`ToolCallAccumulator` 在 Provider 内部累积，但 `ChatChunk` 流是 `IAsyncEnumerable`——Provider 无法在流结束后"附加"一个包含完整 tool_calls 的 chunk（`yield return` 已结束）。
>
> **方案 A（推荐）**：`ToolCallAccumulator` 在 Provider 内累积，每当某 `index` 的 `arguments` 拼接完成（靠 `finish_reason` 或下一个 index 出现判断）时，`yield return new ChatChunk.ToolCallDelta(index, id, name, argumentsFull)` 一次性 yield 完整结果。AgentLoop 收到带完整 arguments 的 `ToolCallDelta` 即构造 `ToolCall`。
>
> **方案 B**：Provider 把 `ToolCallAccumulator` 作为 `out` 参数返回（`IAsyncEnumerable` 不支持 out）。需把方法签名改为返回 `(IAsyncEnumerable<ChatChunk> Stream, ToolCallAccumulator Accumulator)`，AgentLoop 在流结束后取 accumulator.Build()。
>
> **方案 C**：累积器放 AgentLoop 内。Provider 把每个分片 `ToolCallDelta(index, id, name, argsFragment)` 原样 yield，AgentLoop 内部累积。Provider 不持有累积器。
>
> **决策：方案 C**。Provider 只做协议翻译（OpenAI delta → ChatChunk），累积逻辑放 AgentLoop 的 `ToolCallAccumulator`。理由：
> 1. Provider 无状态——流式方法不返回额外对象，签名干净。
> 2. 累积逻辑协议无关（任何按 index 分片的协议都适用），放 AgentLoop 复用性高。
> 3. AgentLoop 已有累积文本的 `StringBuilder`，再加 tool_calls 累积器一致。
>
> 方案 C 下 Provider 实现更简洁：每个 `delta.tool_calls[i]` 直接 yield 成 `ChatChunk.ToolCallDelta(i, id, name, argsFragment)`，AgentLoop 的累积器消费。

#### 4.3.6 `ToolCallAccumulator`（AgentLoop 内部累积器）

```csharp
using System.Text.Json;

namespace ParrotCode;

/// <summary>
/// 工具调用累积器：按 index 拼接 OpenAI 流式 tool_calls 分片。
/// 协议无关——任何按 index 分片的 tool_calls（OpenAI/兼容协议）都适用。
/// AgentLoop 内部使用，Provider 只 yield ChatChunk.ToolCallDelta 分片。
/// </summary>
internal sealed class ToolCallAccumulator
{
    private readonly Dictionary<int, AccEntry> _entries = new();

    private sealed class AccEntry
    {
        public string? Id;
        public string? Name;
        public readonly StringBuilder Arguments = new();
    }

    /// <summary>
    /// 累积一个分片。Id/Name 取首个非空值，Arguments 拼接所有片段。
    /// </summary>
    public void Accumulate(int index, string? id, string? name, string? argsFragment)
    {
        if (!_entries.TryGetValue(index, out var entry))
        {
            entry = new AccEntry();
            _entries[index] = entry;
        }
        if (id is not null) entry.Id = id;
        if (name is not null) entry.Name = name;
        if (argsFragment is not null) entry.Arguments.Append(argsFragment);
    }

    /// <summary>
    /// 构建完整 ToolCall 列表（按 index 升序）。
    /// Arguments 字符串解析为 JsonElement；空或非法 JSON 用空对象兜底。
    /// </summary>
    public IReadOnlyList<ToolCall> Build()
    {
        var result = new List<ToolCall>(_entries.Count);
        foreach (var kv in _entries.OrderBy(x => x.Key))
        {
            var entry = kv.Value;
            var argsStr = entry.Arguments.ToString();
            JsonElement input;
            if (string.IsNullOrWhiteSpace(argsStr))
            {
                input = JsonDocument.Parse("{}").RootElement.Clone();
            }
            else
            {
                try
                {
                    input = JsonDocument.Parse(argsStr).RootElement.Clone();
                }
                catch (JsonException)
                {
                    // LLM 生成的 arguments 非法 JSON——用空对象兜底，
                    // 工具执行时 GetRequiredString 会返回"缺少必需参数"错误，回灌给 LLM 自我修正
                    input = JsonDocument.Parse("""{"_parse_error":"arguments 非法 JSON"}""").RootElement.Clone();
                }
            }
            result.Add(new ToolCall(
                Id: entry.Id ?? $"call_{kv.Key}",
                Name: entry.Name ?? string.Empty,
                Input: input));
        }
        return result;
    }

    public bool IsEmpty => _entries.Count == 0;
}
```

> **设计要点**：
> - **`JsonElement.Clone()`**：`JsonDocument.Parse` 返回的 `JsonElement` 在 `JsonDocument` Dispose 后失效。`Clone()` 脱离 `JsonDocument` 生命周期，安全传递给工具。这是迭代 5 `ClosedLoopDemo` 风险表记录的 `JsonElement` 生命周期问题的标准解法。
> - **`OrderBy(x => x.Key)`**：按 index 升序输出，保证工具调用顺序与 LLM 产出一致（OpenAI 的 `tool_calls` 数组按 index 有序）。
> - **非法 JSON 兜底**：LLM 可能生成不完整的 arguments JSON（如截断）。不抛异常——构造一个带 `_parse_error` 的对象传给工具，工具 `GetRequiredString` 找不到期望参数返回"缺少必需参数"错误，回灌给 LLM 自我修正。这是 ReAct"错误回灌"的体现。
> - **`Id` 兜底 `call_{index}`**：OpenAI 通常返回 `call_xxx` 形式的 Id，但若 LLM 不返回（如某些兼容服务），用 index 生成确定性 Id。`tool_call_id` 关联 assistant 的 tool_call 与 tool 消息，必须唯一。

#### 4.3.7 `Message` + `ConversationHistory` 扩展

```csharp
// Providers/MessageTypes.cs 扩展
using System.Text.Json;

namespace ParrotCode;

public sealed record Message(MessageRole Role, string Content)
{
    /// <summary>assistant 消息携带的工具调用（仅 Role=Assistant 时可能非空）。</summary>
    public IReadOnlyList<ToolCall>? ToolCalls { get; init; }

    /// <summary>
    /// tool 角色消息关联的 tool_call_id（OpenAI 要求 tool 消息必须带 tool_call_id
    /// 关联到触发它的 assistant tool_call）。
    /// 迭代 6 启用。
    /// </summary>
    public string? ToolCallId { get; init; }
}
```

```csharp
// Conversation/History.cs 扩展（新增重载，旧方法保留）
public sealed class ConversationHistory
{
    // —— 既有 AddUser(string) / AddAssistant(string) / AddTool(string) / Clear / Count / EstimatedTokens 保留 ——

    /// <summary>
    /// 追加携带工具调用的 assistant 消息（ReAct 循环中 LLM 决定调工具时用）。
    /// Content 可能为空（LLM 只调工具不输出文本），ToolCalls 不能为空。
    /// </summary>
    public void AddAssistant(string content, IReadOnlyList<ToolCall> toolCalls)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(toolCalls);
        if (toolCalls.Count == 0)
            throw new ArgumentException("toolCalls 不能为空（无工具调用请用 AddAssistant(content))", nameof(toolCalls));
        _messages.Add(new Message(MessageRole.Assistant, content) { ToolCalls = toolCalls });
    }

    /// <summary>
    /// 追加 tool 消息（工具执行结果）并关联 tool_call_id。
    /// tool_call_id 必须与触发它的 assistant 消息的 ToolCalls[i].Id 一致——
    /// OpenAI 要求 tool 消息按 tool_call_id 关联，否则报错。
    /// </summary>
    public void AddTool(string content, string toolCallId)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(toolCallId);
        _messages.Add(new Message(MessageRole.Tool, content) { ToolCallId = toolCallId });
    }
}
```

> **设计要点**：
> - **`AddAssistant(content, toolCalls)` 重载**：ReAct 循环中 LLM 回复含 tool_calls 时用此重载。Content 可能为空（LLM 只调工具不说话），ToolCalls 必须非空（无工具调用用旧 `AddAssistant(content)`）。
> - **`AddTool(content, toolCallId)` 重载**：OpenAI 协议要求 tool 消息带 `tool_call_id` 关联到 assistant 的 tool_call。旧 `AddTool(content)` 保留供无关联场景（如 Anthropic 的 tool_result block 用 name 关联，迭代 6 不涉及）。
> - **`ToolCallId` 为 `string?`**：仅 tool 角色消息非空。assistant / user / system 消息为 null。序列化时 null 字段省略（见 `ToOpenAiWire`）。

#### 4.3.8 `MessageExtensions.ToOpenAiWire()`（含 tool_calls 的消息序列化）

```csharp
// Conversation/MessageExtensions.cs 扩展
using System.Text.Json;

namespace ParrotCode;

public static class MessageExtensions
{
    // —— 既有 ToOpenAiRoleString / EstimateTokens 保留 ——

    /// <summary>
    /// 把 Message 序列化为 OpenAI wire format 的匿名对象（供 JsonSerializer 序列化）。
    /// - assistant + ToolCalls: {"role":"assistant","content":...,"tool_calls":[{id,type,function:{name,arguments}}]}
    /// - tool + ToolCallId: {"role":"tool","content":...,"tool_call_id":...}
    /// - 其他: {"role":...,"content":...}
    /// </summary>
    public static object ToOpenAiWire(this Message message)
    {
        if (message.Role == MessageRole.Assistant && message.ToolCalls is { Count: > 0 })
        {
            return new
            {
                role = "assistant",
                content = string.IsNullOrEmpty(message.Content) ? null : message.Content,
                tool_calls = message.ToolCalls.Select(tc => new
                {
                    id = tc.Id,
                    type = "function",
                    function = new
                    {
                        name = tc.Name,
                        arguments = tc.Input.GetRawText()  // JsonElement → JSON 字符串
                    }
                }).ToArray()
            };
        }

        if (message.Role == MessageRole.Tool && message.ToolCallId is not null)
        {
            return new
            {
                role = "tool",
                content = message.Content,
                tool_call_id = message.ToolCallId
            };
        }

        return new
        {
            role = message.Role.ToOpenAiRoleString(),
            content = message.Content
        };
    }
}
```

> **设计要点**：
> - **`content` 为 null 当 assistant 只调工具**：OpenAI 协议允许 assistant 消息 `content` 为 null（纯工具调用）。但部分兼容服务（如 DeepSeek 早期版本）要求 content 非空——本迭代传 null，若兼容性问题再调整为空字符串。测试覆盖 null 与非 null 两种。
> - **`arguments` 是 JSON 字符串**：OpenAI 要求 `function.arguments` 是 JSON 字符串（不是对象）。`tc.Input.GetRawText()` 把 `JsonElement` 序列化回字符串。例如 `{"path":"foo.txt"}` 的 JsonElement 的 `GetRawText()` 返回 `{"path":"foo.txt"}` 字符串。
> - **`type = "function"`**：OpenAI 工具调用类型目前只有 `function`（预留 `code_interpreter` 等）。硬编码。
> - **`tool_call_id` 关联**：tool 消息必须带 `tool_call_id` 关联到 assistant 的 `tool_calls[i].id`。OpenAI 校验此关联，缺失报 400。

#### 4.3.9 `MockProvider` 脚本化扩展

```csharp
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading.Channels;

namespace ParrotCode;

/// <summary>
/// 固定回显 Provider，用于在接入真实 LLM 前跑通管线。
/// 迭代 6 扩展：支持脚本队列，注入预设 ChatChunk 序列模拟 LLM 的 tool_call 响应，
/// 让无 LLM 环境下也能测试 AgentLoop 的 ReAct 闭环。
/// 默认行为（无脚本时）保持迭代 3 的回显语义，既有测试不回归。
/// </summary>
public sealed class MockProvider : IBaseProvider
{
    private readonly ConcurrentQueue<IReadOnlyList<ChatChunk>> _scripts = new();

    /// <summary>
    /// 注入一段脚本（对应 AgentLoop 的一轮 LLM 调用）。
    /// AgentLoop 每轮调 ChatStreamAsync 时出队一段脚本并按序产出。
    /// 脚本耗尽后回退到默认回显行为。
    /// </summary>
    public void EnqueueScript(params ChatChunk[] chunks) =>
        _scripts.Enqueue(chunks);

    // —— 旧 ChatAsync / ChatStreamAsync(string) 保留，行为不变 ——

    public Task<string> ChatAsync(IReadOnlyList<Message> messages, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var lastUser = messages.LastOrDefault(m => m.Role == MessageRole.User);
        var content = lastUser?.Content ?? string.Empty;
        return Task.FromResult($"{content}（mock）");
    }

    public async IAsyncEnumerable<string> ChatStreamAsync(
        IReadOnlyList<Message> messages,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var lastUser = messages.LastOrDefault(m => m.Role == MessageRole.User);
        var content = lastUser?.Content ?? string.Empty;
        yield return $"{content}（mock）";
        await Task.CompletedTask;
    }

    // —— 新增：带 tools 的流式 ——

    public async IAsyncEnumerable<ChatChunk> ChatStreamAsync(
        IReadOnlyList<Message> messages,
        JsonElement? tools,
        string toolChoice,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (_scripts.TryDequeue(out var script))
        {
            foreach (var chunk in script)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return chunk;
            }
            yield return new ChatChunk.Done();
        }
        else
        {
            // 无脚本：回退到回显（与旧 ChatStreamAsync 一致，但包装成 ChatChunk）
            var lastUser = messages.LastOrDefault(m => m.Role == MessageRole.User);
            var content = lastUser?.Content ?? string.Empty;
            yield return new ChatChunk.TextDelta($"{content}（mock）");
            yield return new ChatChunk.Done();
        }
        await Task.CompletedTask;
    }
}
```

> **设计要点**：
> - **`ConcurrentQueue` 脚本队列**：线程安全，支持多次 `EnqueueScript` 注入多轮脚本（对应 AgentLoop 多轮 ReAct）。每轮 `ChatStreamAsync` 出队一段。
> - **脚本耗尽回退**：无脚本时新重载也回退到回显行为（包装成 `ChatChunk.TextDelta` + `Done`），让 AgentLoop 在无脚本时也能跑（产出"输入（mock）"后无 tool_calls，立即 AgentDone）。
> - **测试场景**：`EnqueueScript(new ChatChunk.ToolCallDelta(0, "call_1", "read_file", """{"path":"README.md"}"""), new ChatChunk.Done())` 模拟 LLM 调 read_file；`EnqueueScript(new ChatChunk.TextDelta("README 内容是..."), new ChatChunk.Done())` 模拟 LLM 拿到结果后输出总结。两段脚本即可测试完整 ReAct 闭环。
> - **`Done` 显式追加**：脚本末尾若未含 `Done`，MockProvider 自动追加——避免测试构造脚本时漏写 `Done` 导致 AgentLoop 死等。

#### 4.3.10 `BatchToolExecutor`（分批执行器）

```csharp
using Microsoft.Extensions.Logging;

namespace ParrotCode;

/// <summary>
/// 分批工具执行器：按 ToolCategory 分组调度。
/// Read 组（幂等、无副作用）用 Task.WhenAll 并发，限流到 maxParallelism 防止 OOM；
/// Write 组（有副作用）顺序 await 避免竞态。
/// 委托迭代 5 ToolExecutor 做单次执行（超时 + 异常捕获）。
/// 本迭代不接安全层——迭代 8 SecurityGuard 作为 OnBeforeExecuteAsync hook 接入。
/// </summary>
public sealed class BatchToolExecutor
{
    private readonly ToolExecutor _executor;
    private readonly ToolRegistry _registry;
    private readonly int _maxParallelism;
    private readonly ILogger? _logger;

    public BatchToolExecutor(
        ToolExecutor executor,
        ToolRegistry registry,
        int maxParallelism = 5,
        ILogger? logger = null)
    {
        _executor = executor ?? throw new ArgumentNullException(nameof(executor));
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        if (maxParallelism < 1) throw new ArgumentOutOfRangeException(nameof(maxParallelism));
        _maxParallelism = maxParallelism;
        _logger = logger;
    }

    /// <summary>
    /// 分批执行工具调用列表，返回与输入同序的结果列表。
    /// 流程：按 Category 分组 → Read 并发（分批限流）→ Write 串行 → 按原序合并。
    /// 任何工具失败不中断同批其他工具——失败原因作为 ToolResult.Fail 回灌给 LLM 自我修正。
    /// </summary>
    public async Task<IReadOnlyList<ToolResult>> ExecuteAsync(
        IReadOnlyList<ToolCall> calls,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(calls);
        if (calls.Count == 0) return Array.Empty<ToolResult>();

        cancellationToken.ThrowIfCancellationRequested();

        // 按 Category 分组，保留原始索引以便最后按原序合并
        var readIndices = new List<int>();
        var writeIndices = new List<int>();
        for (var i = 0; i < calls.Count; i++)
        {
            var tool = _registry.Get(calls[i].Name);
            if (tool is null)
            {
                // 未注册工具——归到 Write 组串行处理（ToolExecutor 会返回 Fail）
                writeIndices.Add(i);
            }
            else if (tool.Category == ToolCategory.Read)
            {
                readIndices.Add(i);
            }
            else
            {
                writeIndices.Add(i);
            }
        }

        var results = new ToolResult[calls.Count];

        // Read 组并发（分批限流）
        foreach (var batch in readIndices.Chunk(_maxParallelism))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var tasks = batch.Select(i => _executor.ExecuteAsync(calls[i], cancellationToken)).ToArray();
            var batchResults = await Task.WhenAll(tasks);
            for (var j = 0; j < batch.Length; j++)
            {
                results[batch[j]] = batchResults[j];
            }
        }

        // Write 组串行
        foreach (var i in writeIndices)
        {
            cancellationToken.ThrowIfCancellationRequested();
            results[i] = await _executor.ExecuteAsync(calls[i], cancellationToken);
        }

        return results;
    }
}
```

> **设计要点**：
> - **按 Category 分组**：`Read` 并发提速（如同时读 3 个文件），`Write` 串行避免竞态（如连续 edit 同一文件）。这是迭代 5 `ToolCategory` 枚举的设计动机落地。
> - **`Chunk(_maxParallelism)` 限流**：`readIndices.Chunk(5)` 把 10 个 Read 调用分成 2 批 × 5，每批 `Task.WhenAll` 并发。防止一次并发 50 个 `read_file` 导致文件句柄耗尽或 OOM。默认 5 是经验值，可配。
> - **结果按原序合并**：用 `results[originalIndex] = batchResult` 保证返回顺序与输入 `calls` 一致——LLM 期望工具结果与它发起的 tool_calls 顺序对应。
> - **未注册工具归 Write 组**：找不到工具时归串行组，`ToolExecutor` 返回 `ToolResult.Fail("未注册工具")`。不归并发组是因为"未注册"是异常情况，串行便于日志追踪。
> - **不中断同批**：某工具失败不中断同批其他工具。失败原因作为 `ToolResult.Fail` 回灌给 LLM——这是 ReAct 自我修正的物质基础。
> - **`OnBeforeExecuteAsync` hook 预留**：实际实现时加 `protected virtual Task<ToolResult?> OnBeforeExecuteAsync(ToolCall call, CancellationToken ct)` 返回非 null 表示拦截（如安全层拒绝）。本迭代默认实现返回 null（不拦截），迭代 8 `SecurityGuard` 覆写此 hook。

#### 4.3.11 `AgentLoop`（ReAct 核心循环）

```csharp
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace ParrotCode;

/// <summary>
/// ReAct Agent 核心循环：Reason（LLM 推理）+ Act（工具执行）+ Observe（结果回灌）迭代。
/// 每轮：调 Provider 流式 → 累积文本 + tool_calls → assistant 入历史 →
///       若无 tool_calls 则 AgentDone → 否则分批执行 → tool 结果入历史 → 下一轮。
/// 最大轮次默认 10，防止无限循环。CancellationToken 全链路贯穿。
/// 事件流通过 IAgentEventSink 产出，解耦生产与展示。
/// </summary>
internal sealed class AgentLoop
{
    private readonly IBaseProvider _provider;
    private readonly ToolRegistry _registry;
    private readonly BatchToolExecutor _batchExecutor;
    private readonly int _maxRounds;
    private readonly string _toolChoice;
    private readonly string _systemPrompt;
    private readonly ILogger? _logger;

    public AgentLoop(
        IBaseProvider provider,
        ToolRegistry registry,
        BatchToolExecutor batchExecutor,
        int maxRounds = 10,
        string toolChoice = "auto",
        string? systemPrompt = null,
        ILogger? logger = null)
    {
        _provider = provider ?? throw new ArgumentNullException(nameof(provider));
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _batchExecutor = batchExecutor ?? throw new ArgumentNullException(nameof(batchExecutor));
        if (maxRounds < 1) throw new ArgumentOutOfRangeException(nameof(maxRounds));
        _maxRounds = maxRounds;
        _toolChoice = toolChoice;
        _systemPrompt = systemPrompt ?? DefaultSystemPrompt;
        _logger = logger;
    }

    private static string DefaultSystemPrompt =>
        "你是 ParrotCode.Net 的 AI 编程助手。你可以调用工具读写文件、执行命令、搜索代码。" +
        "每次只调用必要的工具，拿到结果后用简洁中文回复用户。";

    /// <summary>
    /// 运行 ReAct 循环。用户输入已由调用方 AddUser 到 history。
    /// 事件流写入 sink，结束时调 sink.Complete()。
    /// 异常不逃逸——Provider/工具错误转为 Error 事件，取消转为 Cancelled 事件。
    /// </summary>
    public async Task RunAsync(ConversationHistory history, IAgentEventSink sink, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(history);
        ArgumentNullException.ThrowIfNull(sink);

        try
        {
            await RunCoreAsync(history, sink, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await sink.WriteAsync(new AgentEvent.Cancelled(), cancellationToken);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "AgentLoop 致命错误");
            await sink.WriteAsync(new AgentEvent.Error(ex.Message, ex), CancellationToken.None);
        }
        finally
        {
            sink.Complete();
        }
    }

    private async Task RunCoreAsync(
        ConversationHistory history,
        IAgentEventSink sink,
        CancellationToken cancellationToken)
    {
        var tools = _registry.GetAll().Count > 0 ? _registry.ToOpenAiSchemas() : (JsonElement?)null;

        for (var round = 1; round <= _maxRounds; round++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await sink.WriteAsync(new AgentEvent.RoundStart(round), cancellationToken);

            // 构造消息：system prompt + 历史快照
            var messages = BuildMessagesWithSystem(history);

            // 流式调用 LLM
            var textBuf = new StringBuilder();
            var tcAcc = new ToolCallAccumulator();

            await foreach (var chunk in _provider.ChatStreamAsync(messages, tools, _toolChoice, cancellationToken))
            {
                switch (chunk)
                {
                    case ChatChunk.TextDelta(var text):
                        textBuf.Append(text);
                        await sink.WriteAsync(new AgentEvent.TextDelta(text), cancellationToken);
                        break;
                    case ChatChunk.ToolCallDelta(var idx, var id, var name, var args):
                        tcAcc.Accumulate(idx, id, name, args);
                        break;
                    case ChatChunk.Done:
                        break;
                }
            }

            var assistantText = textBuf.ToString();
            var toolCalls = tcAcc.Build();

            // assistant 消息入历史
            if (toolCalls.Count > 0)
            {
                history.AddAssistant(assistantText, toolCalls);
            }
            else
            {
                history.AddAssistant(assistantText);
            }

            if (!string.IsNullOrEmpty(assistantText))
            {
                await sink.WriteAsync(new AgentEvent.AssistantMessage(assistantText), cancellationToken);
            }

            // 无工具调用 → Agent 完成
            if (toolCalls.Count == 0)
            {
                await sink.WriteAsync(new AgentEvent.AgentDone(assistantText), cancellationToken);
                _logger?.LogInformation("Agent 完成，共 {Rounds} 轮", round);
                return;
            }

            // 有工具调用 → 通知开始 + 分批执行
            foreach (var call in toolCalls)
            {
                await sink.WriteAsync(new AgentEvent.ToolCallStart(call), cancellationToken);
            }

            var results = await _batchExecutor.ExecuteAsync(toolCalls, cancellationToken);

            for (var i = 0; i < toolCalls.Count; i++)
            {
                var call = toolCalls[i];
                var result = results[i];
                history.AddTool(result.Success ? result.Content : $"错误：{result.Error}", call.Id);
                await sink.WriteAsync(new AgentEvent.ToolResult(call, result), cancellationToken);
            }

            await sink.WriteAsync(new AgentEvent.RoundEnd(round), cancellationToken);
        }

        // 达到最大轮次
        await sink.WriteAsync(new AgentEvent.MaxRoundsReached(_maxRounds), cancellationToken);
        _logger?.LogWarning("Agent 达到最大轮次 {Rounds}，强制停止", _maxRounds);
    }

    /// <summary>
    /// 构造带 system prompt 的消息列表。
    /// system prompt 放头部，history 快照跟后。
    /// 每轮重新构造——history 在工具结果入历史后变化。
    /// </summary>
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

> **设计要点**：
> - **`for round in 1..maxRounds`**：ReAct 主循环。每轮完整执行"调 LLM → 累积 → 入历史 → 判断 → 执行工具 → 结果入历史"。无 tool_calls 即 `AgentDone` 退出，达到 maxRounds 即 `MaxRoundsReached` 退出。
> - **`ToolCallAccumulator` 在 AgentLoop 内**：按方案 C，Provider 只 yield 分片，累积器在 AgentLoop。`switch` 模式匹配按 `ChatChunk` 类型分发：`TextDelta` 累积文本 + 转发事件，`ToolCallDelta` 累积到 `tcAcc`，`Done` 忽略（循环自然结束）。
> - **assistant 消息入历史**：有 tool_calls 用 `AddAssistant(content, toolCalls)`，无 tool_calls 用 `AddAssistant(content)`。Content 可能为空（LLM 只调工具不说话）。
> - **tool 消息入历史**：成功用 `result.Content`，失败用 `错误：{result.Error}`。失败原因回灌给 LLM 自我修正——这是 ReAct 的核心。
> - **事件流顺序**：`RoundStart` → `TextDelta`(多个) → `AssistantMessage` → `ToolCallStart`(多个) → `ToolResult`(多个) → `RoundEnd`。消费者按顺序渲染。
> - **`finally sink.Complete()`**：无论正常结束、取消、异常，都调 `Complete()` 让消费者的 `ReadAllAsync` 自然结束。这是"生产者负责关闭通道"的契约。
> - **异常不逃逸**：`RunAsync` 捕获所有异常转 `Error` 事件。`OperationCanceledException` + `ct.IsCancellationRequested` 转 `Cancelled` 事件。其他异常转 `Error(message, ex)`。调用方（App）不需要 try/catch AgentLoop。
> - **`BuildMessagesWithSystem` 每轮重建**：history 在工具结果入历史后变化，每轮取新快照。system prompt 放头部。无 PromptBuilder 时硬编码默认 prompt。

#### 4.3.12 三个补充工具

**`RunCommandTool`（执行 shell 命令）**

```csharp
using System.Diagnostics;
using System.Text.Json;

namespace ParrotCode;

/// <summary>
/// 执行 shell 命令工具。Category=Write（有副作用）。
/// 参数：command（命令名）+ args（参数）+ cwd（工作目录，可选）+ timeout（秒，默认 30）。
/// 用 Process.Start + 重定向 stdout/stderr。
/// 本迭代不接安全层——黑名单拦截在迭代 8。
/// </summary>
public sealed class RunCommandTool : ToolBase
{
    private const int DefaultTimeoutSeconds = 30;

    public override string Name => "run_command";
    public override string Description =>
        "执行 shell 命令并返回 stdout/stderr。用于编译、测试、git 等操作。" +
        "命令在 shell 中执行（Windows 用 cmd /c，Unix 用 sh -c）。" +
        "默认超时 30 秒，可通过 timeout 参数调整（最大 300 秒）。";
    public override ToolCategory Category => ToolCategory.Write;

    public override IReadOnlyList<ToolParameter> Parameters { get; } = new[]
    {
        new ToolParameter("command", "string", "要执行的命令（如 git status / dotnet build）", Required: true),
        new ToolParameter("args", "string", "命令参数（已拼接好的字符串，如 'status --short'）", Required: false),
        new ToolParameter("cwd", "string", "工作目录（默认当前目录）", Required: false),
        new ToolParameter("timeout", "integer", "超时秒数（默认 30，最大 300）", Required: false)
    };

    public override async Task<ToolResult> ExecuteAsync(JsonElement input, CancellationToken cancellationToken)
    {
        var (command, err1) = GetRequiredString(input, "command", out _);
        // 注：实际实现用迭代 5 的 GetRequiredString 签名（out error）
        // 此处示意，签名以 ToolBase 实际为准
        if (err1 is not null) return ToolResult.Fail(err1);
        if (string.IsNullOrWhiteSpace(command))
            return ToolResult.Fail("参数 command 不能为空");

        var args = GetOptionalString(input, "args", out _, "");
        var cwd = GetOptionalString(input, "cwd", out _, "");
        var timeoutSec = GetOptionalInt(input, "timeout", out _, DefaultTimeoutSeconds);
        timeoutSec = Math.Clamp(timeoutSec, 1, 300);

        var psi = new ProcessStartInfo
        {
            FileName = OperatingSystem.IsWindows() ? "cmd.exe" : "/bin/sh",
            Arguments = OperatingSystem.IsWindows() ? $"/c {command} {args}" : $"-c \"{command} {args}\"",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        if (!string.IsNullOrEmpty(cwd)) psi.WorkingDirectory = cwd;

        try
        {
            using var proc = Process.Start(psi);
            if (proc is null) return ToolResult.Fail("无法启动进程");

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(timeoutSec));

            var stdoutTask = proc.StandardOutput.ReadToEndAsync(cts.Token);
            var stderrTask = proc.StandardError.ReadToEndAsync(cts.Token);
            var exited = await WaitForExitAsync(proc, cts.Token);

            if (!exited)
            {
                try { proc.Kill(entireProcessTree: true); } catch { /* 忽略 */ }
                return ToolResult.Fail($"命令执行超时（{timeoutSec}s）");
            }

            var stdout = await stdoutTask;
            var stderr = await stderrTask;
            var combined = string.IsNullOrEmpty(stderr)
                ? stdout
                : $"{stdout}\n[stderr]\n{stderr}";
            return ToolResult.Ok($"[exit {proc.ExitCode}]\n{combined}");
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            return ToolResult.Fail($"命令执行失败：{ex.Message}");
        }
    }

    private static async Task<bool> WaitForExitAsync(Process proc, CancellationToken ct)
    {
        var tcs = new TaskCompletionSource<bool>();
        await using var _ = ct.Register(() => tcs.TrySetResult(false));
        proc.Exited += (_, _) => tcs.TrySetResult(true);
        if (proc.HasExited) return true;
        return await tcs.Task;
    }

    // 辅助：GetOptionalInt（ToolBase 可补充，或本工具内私有）
    private static int GetOptionalInt(JsonElement input, string name, out string? error, int defaultValue)
    {
        if (!input.TryGetProperty(name, out var el)) { error = null; return defaultValue; }
        if (el.ValueKind == JsonValueKind.Number && el.TryGetInt32(out var v)) { error = null; return v; }
        error = $"参数 {name} 类型错误：期望 integer"; return 0;
    }
}
```

> **设计要点**：
> - **`Category=Write`**：执行命令有副作用（写文件、改系统状态），归串行组避免竞态。
> - **Windows `cmd /c` vs Unix `/bin/sh -c`**：跨平台 shell 包装。`OperatingSystem.IsWindows()` 运行时判断。
> - **超时 `CancelAfter` + `Kill(entireProcessTree)`**：超时不杀进程会泄漏，`Kill(true)` 杀整个进程树（避免子进程泄漏）。`WaitForExitAsync` 用 `TaskCompletionSource` + `proc.Exited` 事件，避免阻塞。
> - **stdout + stderr 合并**：返回 `[exit N]\n{stdout}\n[stderr]\n{stderr}`。exit code 让 LLM 判断成功失败（非 0 通常有错）。stderr 单独标注让 LLM 区分。
> - **不接黑名单**：本迭代 `rm -rf /` 也能执行。迭代 8 `Blacklist` 在 `BatchToolExecutor.OnBeforeExecuteAsync` 拦截。
> - **`timeout` 上限 300**：防止 LLM 传 `timeout=999999` 导致 Agent 卡死。

**`GlobTool`（文件名模式匹配）**

```csharp
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ParrotCode;

/// <summary>
/// 文件名模式匹配工具。Category=Read（幂等、可并发）。
/// 参数：pattern（glob 模式，如 *.cs）+ path（搜索根目录，默认当前目录）。
/// 递归搜索匹配文件，返回路径列表。
/// </summary>
public sealed class GlobTool : ToolBase
{
    public override string Name => "glob";
    public override string Description =>
        "按 glob 模式递归查找文件。pattern 支持 * / ** / ?（如 **/*.cs 匹配所有 .cs 文件）。" +
        "返回匹配文件的相对路径列表（按路径排序，最多返回 200 条）。";
    public override ToolCategory Category => ToolCategory.Read;

    public override IReadOnlyList<ToolParameter> Parameters { get; } = new[]
    {
        new ToolParameter("pattern", "string", "glob 模式（如 **/*.cs / *.md）", Required: true),
        new ToolParameter("path", "string", "搜索根目录（默认当前目录）", Required: false)
    };

    private const int MaxResults = 200;

    public override async Task<ToolResult> ExecuteAsync(JsonElement input, CancellationToken cancellationToken)
    {
        var pattern = GetRequiredString(input, "pattern", out var err1);
        if (err1 is not null) return ToolResult.Fail(err1);
        var path = GetOptionalString(input, "path", out var err2, ".");

        if (string.IsNullOrWhiteSpace(pattern))
            return ToolResult.Fail("参数 pattern 不能为空");
        if (!Directory.Exists(path))
            return ToolResult.Fail($"目录不存在：{path}");

        try
        {
            var regex = GlobToRegex(pattern);
            var matches = new List<string>();
            await foreach (var file in EnumerateFilesAsync(path, cancellationToken))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var relative = Path.GetRelativePath(path, file).Replace('\\', '/');
                if (regex.IsMatch(relative))
                {
                    matches.Add(relative);
                    if (matches.Count >= MaxResults) break;
                }
            }
            matches.Sort(StringComparer.Ordinal);
            return ToolResult.Ok(matches.Count == 0
                ? "未找到匹配文件"
                : $"找到 {matches.Count} 个文件：\n{string.Join('\n', matches)}");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return ToolResult.Fail($"查找失败：{ex.Message}");
        }
    }

    private static async IAsyncEnumerable<string> EnumerateFilesAsync(
        string path, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        // 简化：用 Task.Run 包装同步 EnumerateFiles
        // 生产实现可用 Channel 异步产出
        foreach (var f in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
        {
            ct.ThrowIfCancellationRequested();
            yield return f;
        }
        await Task.CompletedTask;
    }

    /// <summary>
    /// 把 glob 模式转成正则。支持 * / ** / ?。
    /// ** 匹配任意层级目录，* 匹配除路径分隔符外任意字符，? 匹配单字符。
    /// </summary>
    private static Regex GlobToRegex(string pattern)
    {
        var sb = new StringBuilder("^");
        var i = 0;
        while (i < pattern.Length)
        {
            if (pattern[i] == '*' && i + 1 < pattern.Length && pattern[i + 1] == '*')
            {
                sb.Append(".*"); i += 2;
                if (i < pattern.Length && pattern[i] == '/') i++; // 吃掉 **/
            }
            else if (pattern[i] == '*') { sb.Append("[^/]*"); i++; }
            else if (pattern[i] == '?') { sb.Append("[^/]"); i++; }
            else if ("+()|^$.{}\\".IndexOf(pattern[i]) >= 0) { sb.Append('\\').Append(pattern[i]); i++; }
            else { sb.Append(pattern[i]); i++; }
        }
        sb.Append('$');
        return new Regex(sb.ToString(), RegexOptions.Compiled);
    }
}
```

**`GrepTool`（内容正则搜索）**

```csharp
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ParrotCode;

/// <summary>
/// 内容正则搜索工具。Category=Read（幂等、可并发）。
/// 参数：pattern（正则）+ path（搜索目录，默认当前）+ include（文件名 glob 过滤，如 *.cs）。
/// 逐文件逐行搜索，返回匹配的 文件:行号:行内容 列表（最多 100 条）。
/// </summary>
public sealed class GrepTool : ToolBase
{
    public const int MaxMatches = 100;

    public override string Name => "grep";
    public override string Description =>
        "在文件内容中搜索正则匹配。返回 文件:行号:行内容 列表。" +
        "默认搜当前目录所有文件，可用 include 过滤文件类型（如 *.cs）。" +
        "最多返回 100 条匹配，超出截断并提示。";
    public override ToolCategory Category => ToolCategory.Read;

    public override IReadOnlyList<ToolParameter> Parameters { get; } = new[]
    {
        new ToolParameter("pattern", "string", "正则表达式", Required: true),
        new ToolParameter("path", "string", "搜索根目录（默认当前目录）", Required: false),
        new ToolParameter("include", "string", "文件名 glob 过滤（如 *.cs，默认所有文件）", Required: false)
    };

    public override async Task<ToolResult> ExecuteAsync(JsonElement input, CancellationToken cancellationToken)
    {
        var pattern = GetRequiredString(input, "pattern", out var err1);
        if (err1 is not null) return ToolResult.Fail(err1);
        var path = GetOptionalString(input, "path", out var err2, ".");
        var include = GetOptionalString(input, "include", out var err3, "");

        if (string.IsNullOrWhiteSpace(pattern))
            return ToolResult.Fail("参数 pattern 不能为空");
        if (!Directory.Exists(path))
            return ToolResult.Fail($"目录不存在：{path}");

        Regex regex;
        try { regex = new Regex(pattern, RegexOptions.Compiled); }
        catch (ArgumentException ex) { return ToolResult.Fail($"正则非法：{ex.Message}"); }

        var includeRegex = string.IsNullOrEmpty(include)
            ? null
            : new Regex(GlobToRegex(include), RegexOptions.Compiled);

        try
        {
            var matches = new List<string>();
            foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var relative = Path.GetRelativePath(path, file).Replace('\\', '/');
                if (includeRegex is not null && !includeRegex.IsMatch(relative)) continue;

                string[] lines;
                try { lines = await File.ReadAllLinesAsync(file, cancellationToken); }
                catch (IOException) { continue; } // 跳过无法读的文件

                for (var i = 0; i < lines.Length; i++)
                {
                    if (regex.IsMatch(lines[i]))
                    {
                        matches.Add($"{relative}:{i + 1}:{Truncate(lines[i], 120)}");
                        if (matches.Count >= MaxMatches)
                        {
                            matches.Add($"...（已达 {MaxMatches} 条上限，可能还有更多匹配）");
                            return ToolResult.Ok(string.Join('\n', matches));
                        }
                    }
                }
            }
            return ToolResult.Ok(matches.Count == 0
                ? "未找到匹配"
                : $"找到 {matches.Count} 条匹配：\n{string.Join('\n', matches)}");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return ToolResult.Fail($"搜索失败：{ex.Message}");
        }
    }

    private static string Truncate(string s, int max) =>
        s.Length <= max ? s : s[..(max - 3)] + "...";

    // GlobToRegex 复用 GlobTool 的实现（实际可提取到 ToolBase 或共享工具类）
    private static Regex GlobToRegex(string pattern)
    {
        // 同 GlobTool.GlobToRegex，此处省略——实际实现提取到 Shared/GlobPattern.cs
        throw new NotImplementedException("提取到共享工具类");
    }
}
```

> **三个工具的设计共性**：
> - 遵循迭代 5 的 `ToolBase` 模式（Name/Description/Category/Parameters/ExecuteAsync）。
> - `RunCommandTool=Write`（有副作用），`GlobTool`/`GrepTool=Read`（幂等、可并发）。
> - 结果上限（200/100 条）防止 OOM 与 token 爆窗——迭代 9 Truncator 在历史层再截断。
> - 跨平台路径用 `Path.GetRelativePath` + `Replace('\\', '/')` 统一为 Unix 风格。
> - `GlobToRegex` 共享——实际实现提取到 `Tools/GlobPattern.cs` 静态工具类，避免重复。

### 4.4 LLM 响应解析详解（OpenAI tool_calls delta 累积）

OpenAI 流式响应中 `tool_calls` 的到达模式是迭代 6 的核心复杂度，单独详述：

#### 4.4.1 非流式 vs 流式 tool_calls

**非流式**（`stream=false`）响应一次性返回完整 `tool_calls`：
```json
{
  "choices": [{
    "message": {
      "role": "assistant",
      "content": null,
      "tool_calls": [{
        "id": "call_abc123",
        "type": "function",
        "function": {
          "name": "read_file",
          "arguments": "{\"path\":\"README.md\"}"
        }
      }]
    },
    "finish_reason": "tool_calls"
  }]
}
```

**流式**（`stream=true`）的 `tool_calls` 分片到达，按 `index` 累积：
```
data: {"choices":[{"delta":{"tool_calls":[{"index":0,"id":"call_abc123","type":"function","function":{"name":"read_file","arguments":""}}]}}]}
data: {"choices":[{"delta":{"tool_calls":[{"index":0,"function":{"arguments":"{\"pa"}}]}}]}
data: {"choices":[{"delta":{"tool_calls":[{"index":0,"function":{"arguments":"th\":\"REA"}}]}}]}
data: {"choices":[{"delta":{"tool_calls":[{"index":0,"function":{"arguments":"DME.md\"}"}}]}}]}
data: {"choices":[{"delta":{"tool_calls":[{"index":1,"id":"call_def456","type":"function","function":{"name":"glob","arguments":""}}]}}]}
data: {"choices":[{"delta":{"tool_calls":[{"index":1,"function":{"arguments":"{\"pattern\":\"**/*.cs\"}"}}]}}]}
data: {"choices":[{"finish_reason":"tool_calls"}]}
data: [DONE]
```

#### 4.4.2 累积算法

`ToolCallAccumulator.Accumulate(index, id, name, argsFragment)`：
1. 按 `index` 找到或创建 `AccEntry`。
2. `id` / `name` 取首个非空值（后续片通常为 null）。
3. `argsFragment` 拼接到 `Arguments` StringBuilder。

`Build()` 在流结束后调用：
1. 按 `index` 升序遍历 `AccEntry`。
2. `Arguments.ToString()` 得到完整 JSON 字符串。
3. `JsonDocument.Parse(argsStr).RootElement.Clone()` 得到 `JsonElement Input`。
4. 构造 `ToolCall(Id, Name, Input)`。

#### 4.4.3 边界情况

| 情况 | 处理 |
| --- | --- |
| `arguments` 为空字符串 | `Build` 时 `{}` 兜底，工具返回"缺少必需参数" |
| `arguments` 非法 JSON（截断/LLM 生成错） | `{\"_parse_error\":\"arguments 非法 JSON\"}` 兜底，工具返回错误，LLM 自我修正 |
| `id` 缺失（某些兼容服务） | `call_{index}` 兜底，保证 `tool_call_id` 关联可用 |
| `name` 缺失 | 空字符串，`ToolExecutor` 找不到工具返回"未注册工具" |
| 多个 `tool_calls` 交错到达 | 按 `index` 分别累积，`Build` 按 `index` 升序输出 |
| `finish_reason=tool_calls` 但无 `tool_calls` delta | `Build` 返回空列表，AgentLoop 判断"无 tool_calls"误判为 AgentDone——罕见，记录 Warning |
| `finish_reason=stop` 且无 `tool_calls` | 正常 AgentDone 路径 |

#### 4.4.4 单元测试覆盖

`ChatChunkAccumulatorTests` 覆盖：
- 单工具单分片
- 单工具多分片（arguments 拆 3 片）
- 多工具交错分片（index 0/1/0/1）
- 空 arguments
- 非法 JSON arguments
- 缺失 id / name
- `Build` 后 accumulator 状态重置（或不可重用）

### 4.5 Function Calling 闭环

完整的 Function Calling 闭环（以"读 README 并总结"为例）：

```
用户: 读 README.md 并总结

第 1 轮 ReAct:
  history.AddUser("读 README.md 并总结")
  messages = [System, User("读 README.md 并总结")]
  LLM 流式响应:
    ToolCallDelta(index=0, id="call_1", name="read_file", args="")
    ToolCallDelta(index=0, args="{\"path\":\"")
    ToolCallDelta(index=0, args="README.md\"}")
    Done
  accumulator.Build() → [ToolCall("call_1", "read_file", {"path":"README.md"})]
  history.AddAssistant("", [ToolCall(...)])  # content 空
  分批执行:
    read_file 是 Read 类别 → 并发组（仅 1 个）
    ReadFileTool.Execute({"path":"README.md"}) → ToolResult.Ok("# ParrotCode.Net\n...")
  history.AddTool("# ParrotCode.Net\n...", "call_1")
  进入第 2 轮

第 2 轮 ReAct:
  messages = [System, User, Assistant(tool_calls=[...]), Tool("# ParrotCode.Net\n...")]
  LLM 流式响应:
    TextDelta("README 主要内容是：")
    TextDelta("\n- ParrotCode.Net 是一个...")
    TextDelta("\n- 采用 12 个迭代...")
    Done
  accumulator.Build() → []  # 无 tool_calls
  history.AddAssistant("README 主要内容是：...")
  AgentDone → 退出循环
```

闭环要点：
1. **LLM 决定调工具**：LLM 看到 `tools` schema，判断"读 README"需要 `read_file`，生成 tool_call。
2. **宿主执行**：AgentLoop 解析 tool_calls，调 `BatchToolExecutor` 执行 `read_file`。
3. **结果回灌**：`ToolResult.Content` 通过 `history.AddTool` 入历史，下一轮 LLM 能看到。
4. **LLM 基于结果推理**：第 2 轮 LLM 看到 tool 结果，输出总结文本，不再调工具。
5. **AgentDone**：无 tool_calls 即完成，事件流结束。

### 4.6 事件流传输（Channel）

```
AgentLoop (生产者)                    App (消费者)
     │                                    │
     │ sink.WriteAsync(RoundStart)        │
     │──────────────────────────────────▶│ RenderEvent(RoundStart)
     │ sink.WriteAsync(TextDelta "P")     │
     │──────────────────────────────────▶│ Console.Write("P")
     │ sink.WriteAsync(TextDelta "a")     │
     │──────────────────────────────────▶│ Console.Write("a")
     │ ...                                │
     │ sink.WriteAsync(ToolCallStart)     │
     │──────────────────────────────────▶│ RenderEvent("→ read_file")
     │ sink.WriteAsync(ToolResult)        │
     │──────────────────────────────────▶│ RenderEvent("✓ 成功")
     │ sink.WriteAsync(AgentDone)         │
     │──────────────────────────────────▶│ RenderEvent("完成")
     │ sink.Complete()                    │
     │──────────────────────────────────▶│ await foreach 结束
     ▼                                    ▼
```

- **无界通道**：AgentLoop 写入不阻塞（除非消费者取消）。代价是消费者慢时事件积压内存——本迭代工具执行快、打印快，积压可控。
- **`SingleReader/SingleWriter`**：开启优化减少同步开销。
- **`Complete()` 关闭**：AgentLoop 退出前调 `Complete()`，消费者的 `ReadAllAsync` 自然结束——不需要额外信号。
- **取消传播**：消费者 `await foreach` 用 `ct`，Ctrl+C 时 `ReadAllAsync` 抛 `OperationCanceledException` 退出。AgentLoop 的 `RunAsync` 也收到 ct，转 `Cancelled` 事件后 `Complete()`。

### 4.7 工具名命名规范（沿用迭代 5）

新增三个工具名遵循迭代 5 的 snake_case 规范：
- `run_command`（动词_名词）
- `glob`（单动词，搜索语义清晰）
- `grep`（单动词，Unix 惯例）

参数名同 snake_case：`command` / `args` / `cwd` / `timeout` / `pattern` / `path` / `include`。

## 五、依赖变更

**无新增 NuGet 依赖。**

- `System.Threading.Channels` 在 .NET 8 是 BCL 内置（`System.Threading.Channels` 命名空间，无需单独引用）。
- `Process` / `ProcessStartInfo` 来自 `System.Diagnostics`（BCL 内置）。
- `Regex` 来自 `System.Text.RegularExpressions`（BCL 内置）。
- `JsonNode` / `JsonObject` 来自 `System.Text.Json.Nodes`（BCL 内置，.NET 8 内置）。
- `Spectre.Console` / `Microsoft.Extensions.Logging` 已在迭代 1/2b 引入。

`ParrotCode.Net.csproj` / `ParrotCode.Net-xUnit.csproj`：**不变**。

> 与迭代 3/4/5 一致，零新依赖，纯代码实现。

## 六、配置文件

### 6.1 `Config/Models.cs` 扩展

```csharp
namespace ParrotCode;

public sealed record AppConfig
{
    public string? ActiveProvider { get; init; }
    public IList<ProviderConfig> Providers { get; init; } = Array.Empty<ProviderConfig>();

    /// <summary>Agent 配置（迭代 6 新增）。null 时用默认值。</summary>
    public AgentConfig? Agent { get; init; }
}

/// <summary>
/// Agent 循环配置。所有字段可选，缺省用默认值。
/// </summary>
public sealed record AgentConfig
{
    /// <summary>最大 ReAct 轮次，默认 10。防止无限循环。</summary>
    public int? MaxRounds { get; init; }

    /// <summary>tool_choice：auto（默认）/ none / required。</summary>
    public string? ToolChoice { get; init; }

    /// <summary>Read 工具最大并发度，默认 5。</summary>
    public int? MaxParallelism { get; init; }

    /// <summary>工具执行超时秒数，默认 30（透传给 ToolExecutor）。</summary>
    public int? ToolTimeoutSeconds { get; init; }

    /// <summary>system prompt，null 用默认。</summary>
    public string? SystemPrompt { get; init; }
}
```

### 6.2 `example.parrotcode.yaml` 加示例

```yaml
active_provider: deepseek
providers:
  - name: mock
    protocol: mock
    model: mock
    base_url: ""
    api_key: ""
  - name: deepseek
    protocol: openai
    model: deepseek-chat
    base_url: https://api.deepseek.com/v1
    api_key: ${DEEPSEEK_API_KEY}

# 迭代 6 新增
agent:
  max_rounds: 10
  tool_choice: auto
  max_parallelism: 5
  tool_timeout_seconds: 30
  # system_prompt: "自定义 system prompt"
```

### 6.3 默认值与覆盖

| 字段 | 默认值 | 覆盖来源 |
| --- | --- | --- |
| `MaxRounds` | 10 | `agent.max_rounds` |
| `ToolChoice` | `"auto"` | `agent.tool_choice` |
| `MaxParallelism` | 5 | `agent.max_parallelism` |
| `ToolTimeoutSeconds` | 30 | `agent.tool_timeout_seconds` |
| `SystemPrompt` | 内置默认 | `agent.system_prompt` |

> ConfigLoader 解析时若 `agent` 节缺失，`AppConfig.Agent` 为 null，App 用硬编码默认值构造 `AgentLoop`。

## 七、迁移说明（迭代 5 → 迭代 6）

| 迭代 5 | 迭代 6 | 处理 |
| --- | --- | --- |
| `IBaseProvider` 只有 `ChatAsync` + `ChatStreamAsync(string)` | 新增 `ChatStreamAsync(messages, tools, toolChoice, ct)` 重载返回 `IAsyncEnumerable<ChatChunk>` | 接口扩展（保留旧重载） |
| `OpenAIProvider.ChatStreamAsync` 只解析 `delta.content` | 加 `delta.tool_calls` 累积解析 | 扩展（旧路径保留） |
| `MockProvider` 只回显 | 加 `EnqueueScript` 脚本队列 + 新重载 | 扩展（默认行为不变） |
| `Message` 有 `ToolCalls` 字段（预留） | 启用 + 加 `ToolCallId` 字段 | 启用既有 + 新增字段 |
| `ConversationHistory.AddAssistant(string)` / `AddTool(string)` | 加 `AddAssistant(content, toolCalls)` / `AddTool(content, toolCallId)` 重载 | 新增重载（旧保留） |
| `MessageExtensions.ToOpenAiRoleString` | 加 `ToOpenAiWire()` 含 tool_calls 序列化 | 新增方法 |
| `ToolExecutor` 单次执行 | `BatchToolExecutor` 委托之 + 分批调度 | 新增类（ToolExecutor 不变） |
| `ClosedLoopDemo` 不接 LLM | 保留（迭代 5 验收物） | 不变 |
| `App` 直接调 Provider 流式打印 | App 调 AgentLoop + 消费事件流 | 改写主循环 |
| `Program` 装配 Provider + App | 加装配 ToolRegistry + BatchToolExecutor + AgentLoop | 扩展装配 |
| 无 `Agent/` 目录 | 新增 `Agent/` 目录 + 6 个文件 | 新模块 |
| 3 个文件工具 | 补齐 3 个工具（共 6 个） | 新增 |

迁移后回归不变式：
- `active_provider: mock` 且无脚本时，`dotnet run` 输入"你好" → AgentLoop 第 1 轮 LLM 回 `你好（mock）` 无 tool_calls → AgentDone → 打印"你好（mock）"。行为与迭代 4 一致（多了一层 AgentLoop 包装，但用户可见输出相同）。
- `/clear` 行为保持。
- 迭代 1-5 既有测试全绿（旧 `ChatStreamAsync(string)` 重载保留，`MockProvider` 默认行为不变）。

> **回归保护**：迭代 6 保留所有旧接口与方法签名，新增重载与新类。`OpenAIProvider` 旧 `ChatStreamAsync(string)` 路径不变，既有 `OpenAIProviderTests` 全绿。`MockProvider` 默认行为（无脚本）不变，既有 `MockProviderTests` 全绿。

## 八、单元测试

### 8.1 `ChatChunkAccumulatorTests`（新增）

| 用例 | 操作 | 期望 |
| --- | --- | --- |
| `Accumulate_SingleChunkSingleTool_BuildsCorrectToolCall` | Accumulate(0, "call_1", "read_file", """{"path":"a.txt"}""") → Build | [ToolCall("call_1","read_file",{"path":"a.txt"})] |
| `Accumulate_MultiChunkArguments_ConcatenatesCorrectly` | Accumulate(0,id,name,"") → Accumulate(0,null,null,"{\"pa") → Accumulate(0,null,null,"th\":\"a\"}") → Build | arguments == {"path":"a"} |
| `Accumulate_MultipleToolsInterleaved_BuildsInIndexOrder` | Accumulate(0,...) → Accumulate(1,...) → Accumulate(0,...) → Build | 2 个 ToolCall，按 index 升序 |
| `Accumulate_IdNullInLaterChunks_KeepsFirstId` | Accumulate(0,"call_1",name,"") → Accumulate(0,null,null,args) | Id == "call_1" |
| `Accumulate_AllIdMissing_GeneratesCallIndexId` | Accumulate(0,null,name,args) → Build | Id == "call_0" |
| `Accumulate_EmptyArguments_UsesEmptyObject` | Accumulate(0,id,name,"") → Build | Input == {} |
| `Accumulate_InvalidJsonArguments_UsesParseErrorObject` | Accumulate(0,id,name,"{invalid") → Build | Input 含 `_parse_error` 字段 |
| `Build_PreservesIndexOrder` | Accumulate(2,...) → Accumulate(0,...) → Accumulate(1,...) → Build | 顺序 [0,1,2] |
| `IsEmpty_InitiallyTrue` | 新建 accumulator | IsEmpty == true |
| `IsEmpty_AfterAccumulate_False` | Accumulate(0,...) | IsEmpty == false |

### 8.2 `OpenAIProviderTests`（补充 tool_calls 解析）

| 用例 | 操作 | 期望 |
| --- | --- | --- |
| `ChatStreamAsync_WithTools_ParsesToolCall` | mock SSE 返回单工具单分片 tool_calls | yield ToolCallDelta + Done |
| `ChatStreamAsync_ToolCallArgumentsSplit_ParsesCorrectly` | mock SSE 把 arguments 拆 3 片 | yield 3 个 ToolCallDelta，累积后 arguments 完整 |
| `ChatStreamAsync_MultipleToolCalls_ParsesAll` | mock SSE 返回 2 个工具 | yield 多个 ToolCallDelta，index 0/1 |
| `ChatStreamAsync_TextAndToolCallMixed_BothYielded` | mock SSE 先 content 后 tool_calls | yield TextDelta + ToolCallDelta |
| `ChatStreamAsync_DoneMarker_YieldsDoneChunk` | mock SSE 含 `[DONE]` | yield Done |
| `ChatStreamAsync_NoTools_YieldsOnlyText` | tools=null + mock SSE 只 content | 只 yield TextDelta，无 ToolCallDelta |
| `BuildRequestBody_WithTools_ContainsToolsField` | tools 非 null | 请求体含 `tools` + `tool_choice` 字段 |
| `BuildRequestBody_WithoutTools_OmitsToolsField` | tools=null | 请求体无 `tools` 字段 |
| `BuildRequestBody_ToolChoiceRequired_SetsRequired` | toolChoice="required" | 请求体 `tool_choice == "required"` |
| `BuildRequestBody_AssistantWithToolCalls_SerializedCorrectly` | messages 含 assistant+ToolCalls | 请求体消息含 `tool_calls` 数组 |
| `BuildRequestBody_ToolMessage_HasToolCallId` | messages 含 tool 消息 | 请求体消息含 `tool_call_id` |

### 8.3 `MockProviderTests`（补充脚本）

| 用例 | 操作 | 期望 |
| --- | --- | --- |
| `EnqueueScript_OneScript_DequeuedOnFirstCall` | EnqueueScript(ToolCallDelta + Done) → ChatStreamAsync | yield 脚本内容 |
| `EnqueueScript_MultipleScripts_DequeuedInOrder` | Enqueue 2 段 → 调 2 次 ChatStreamAsync | 第 1 次出队第 1 段，第 2 次出队第 2 段 |
| `ChatStreamAsync_NoScript_FallsBackToEcho` | 无脚本调新重载 | yield TextDelta("{lastUser}（mock）") + Done |
| `EnqueueScript_ScriptWithoutDone_AutoAppended` | EnqueueScript(TextDelta only) | ChatStreamAsync 末尾自动 yield Done |
| `ChatStreamAsync_WithTools_IgnoresToolsParam` | 传 tools 非 null | MockProvider 不解析 tools，按脚本产出 |

### 8.4 `MessageExtensionsTests`（补充 wire format）

| 用例 | 操作 | 期望 |
| --- | --- | --- |
| `ToOpenAiWire_UserMessage_HasRoleAndContent` | Message(User, "hi") | {role:"user", content:"hi"} |
| `ToOpenAiWire_AssistantWithToolCalls_HasToolCallsArray` | Message(Assistant, "") {ToolCalls=[...]} | 含 tool_calls 数组，每元素含 id/type/function |
| `ToOpenAiWire_AssistantWithToolCalls_FunctionArgumentsIsString` | 同上 | function.arguments 是 JSON 字符串（非对象） |
| `ToOpenAiWire_AssistantWithoutToolCalls_NoToolCallsField` | Message(Assistant, "hi") | 无 tool_calls 字段 |
| `ToOpenAiWire_ToolMessage_HasToolCallId` | Message(Tool, "result"){ToolCallId="call_1"} | 含 tool_call_id 字段 |
| `ToOpenAiWire_AssistantEmptyContent_ContentIsNull` | Message(Assistant, ""){ToolCalls=[...]} | content 为 null |

### 8.5 `ChannelEventSinkTests`（新增）

| 用例 | 操作 | 期望 |
| --- | --- | --- |
| `WriteAsync_ThenReadAllAsync_ReceivesEvent` | WriteAsync(RoundStart) → Complete → ReadAllAsync | 收到 RoundStart |
| `Complete_StopsReadAllAsync` | Complete → ReadAllAsync | 枚举自然结束 |
| `WriteAsync_MultipleEvents_ReceivedInOrder` | WriteAsync(A) → WriteAsync(B) → Complete | 收到 [A, B] |
| `WriteAsync_AfterComplete_Throws` | Complete → WriteAsync | 抛 ChannelClosedException |
| `ReadAllAsync_Cancellation_ThrowsOperationCanceled` | ReadAllAsync(已取消 ct) | 抛 OperationCanceledException |

### 8.6 `BatchToolExecutorTests`（新增）

| 用例 | 操作 | 期望 |
| --- | --- | --- |
| `ExecuteAsync_ReadTools_RunInParallel` | 3 个 ReadFileTool 调用 + 计时 | 总耗时 ≈ 单次（并发）而非 3 倍 |
| `ExecuteAsync_WriteTools_RunSequentially` | 3 个 WriteFileTool 调用 + 计时 | 总耗时 ≈ 3 倍单次（串行） |
| `ExecuteAsync_MixedCategories_ReadFirstThenWrite` | 2 Read + 2 Write | Read 并发执行，Write 串行，结果按原序返回 |
| `ExecuteAsync_EmptyCalls_ReturnsEmpty` | 空列表 | 返回空列表 |
| `ExecuteAsync_UnknownTool_ReturnsFailInResult` | 含未注册工具 | 对应位置 ToolResult.Fail("未注册工具") |
| `ExecuteAsync_ToolFails_OthersContinue` | 1 个失败 + 2 个成功 | 失败位置 Fail，成功位置 Ok，不中断 |
| `ExecuteAsync_ResultOrderMatchesInputOrder` | 5 个混合调用 | 返回结果顺序与输入一致 |
| `ExecuteAsync_MaxParallelismLimitsConcurrency` | 10 个 Read + maxParallel=2 | 同时运行不超过 2（用计数器验证） |
| `ExecuteAsync_Cancellation_Throws` | ct 已取消 | 抛 OperationCanceledException |
| `ExecuteAsync_ReadToolFails_DoesNotBlockOthers` | Read 组 1 失败 | 同批其他 Read 仍执行 |

### 8.7 `RunCommandToolTests`（新增）

| 用例 | 操作 | 期望 |
| --- | --- | --- |
| `RunCommand_Echo_Success` | run_command `echo hello` | Success=true, Content 含 "hello" |
| `RunCommand_NonZeroExit_IncludesExitCode` | run_command 不存在的命令 | Content 含 "[exit N]" N≠0 |
| `RunCommand_CwdRespected` | cwd=临时目录 + `pwd`/`cd` | Content 含临时目录路径 |
| `RunCommand_Timeout_ReturnsError` | timeout=1 + `sleep 5` | Fail("超时") |
| `RunCommand_StderrIncluded` | 命令输出 stderr | Content 含 "[stderr]" 段 |
| `RunCommand_MissingCommand_Fails` | 缺 command 参数 | Fail("缺少必需参数") |
| `RunCommand_CategoryIsWrite` | 检查 Category | Write |
| `RunCommand_TimeoutCappedAt300` | timeout=99999 | 实际超时 300s（用反射或日志验证） |

### 8.8 `GlobToolTests`（新增）

| 用例 | 操作 | 期望 |
| --- | --- | --- |
| `Glob_MatchesFilesByPattern` | 临时目录建 a.cs + b.txt → glob *.cs | 返回 a.cs |
| `Glob_RecursiveStarStar` | 建 sub/c.cs → glob **/*.cs | 返回 a.cs + sub/c.cs |
| `Glob_NoMatch_ReturnsEmptyMessage` | glob *.xyz | "未找到匹配文件" |
| `Glob_PathNotExist_Fails` | path=不存在的目录 | Fail("目录不存在") |
| `Glob_MaxResultsTruncated` | 建 250 个文件 → glob * | 返回 200 条 + 截断提示 |
| `Glob_MissingPattern_Fails` | 缺 pattern | Fail("缺少必需参数") |
| `Glob_CategoryIsRead` | 检查 Category | Read |

### 8.9 `GrepToolTests`（新增）

| 用例 | 操作 | 期望 |
| --- | --- | --- |
| `Grep_FindsPatternInFiles` | 临时文件含 "hello" → grep "hello" | 返回 文件:行号:行 |
| `Grep_IncludeFilter` | a.cs + b.txt 都含 pattern → include=*.cs | 只返回 a.cs 的匹配 |
| `Grep_RegexPattern` | 文件含 "foo123" → grep "foo\d+" | 匹配 |
| `Grep_InvalidRegex_Fails` | pattern="[invalid" | Fail("正则非法") |
| `Grep_NoMatch_ReturnsEmptyMessage` | grep "xyz不存在" | "未找到匹配" |
| `Grep_MaxMatchesTruncated` | 200+ 行匹配 | 返回 100 条 + 截断提示 |
| `Grep_PathNotExist_Fails` | path 不存在 | Fail("目录不存在") |
| `Grep_CategoryIsRead` | 检查 Category | Read |

### 8.10 `AgentLoopTests`（新增，用 MockProvider 脚本）

| 用例 | 操作 | 期望 |
| --- | --- | --- |
| `RunAsync_NoToolCalls_AgentDoneAfterRound1` | MockProvider 脚本只返回 TextDelta | 1 轮后 AgentDone |
| `RunAsync_OneToolCallThenDone_TwoRounds` | 脚本 1 返回 tool_call + 脚本 2 返回文本 | 2 轮后 AgentDone |
| `RunAsync_ToolFailure_ErrorFedBackToLlm` | 脚本 1 调不存在的工具 → 脚本 2 LLM 看到错误后回复文本 | 2 轮后 AgentDone，第 2 轮 LLM 看到"未注册工具"错误 |
| `RunAsync_MaxRoundsReached_EmitsMaxRoundsEvent` | 脚本总是返回 tool_call + maxRounds=2 | 2 轮后 MaxRoundsReached |
| `RunAsync_EventsInCorrectOrder` | 收集所有事件 | RoundStart → TextDelta → AssistantMessage → ToolCallStart → ToolResult → RoundEnd → ... → AgentDone |
| `RunAsync_CancellationToken_EmitsCancelled` | 启动后立即取消 | Cancelled 事件 + sink.Complete |
| `RunAsync_ProviderError_EmitsError` | MockProvider 抛异常 | Error 事件 + sink.Complete |
| `RunAsync_HistoryUpdatedCorrectly` | 1 轮 tool_call + 1 轮文本 | history 含 User, Assistant(tool_calls), Tool, Assistant(text) |
| `RunAsync_EmptyAssistantContent_StillAddsToHistory` | LLM 只调工具不输出文本 | assistant 消息 content="" 且 ToolCalls 非空 |
| `RunAsync_MultipleToolCallsInOneRound_AllExecuted` | 脚本返回 2 个 tool_call | 2 个 ToolResult 事件 |
| `RunAsync_SystemPromptIncludedInMessages` | 检查传给 Provider 的 messages | 首条是 System |
| `RunAsync_SinkCompleted_AfterAgentDone` | AgentDone 后 | sink.Complete 被调用 |

### 8.11 `AgentLoopIntegrationTests`（集成，端到端）

| 用例 | 操作 | 期望 |
| --- | --- | --- |
| `EndToEnd_ReadFileAndSummarize` | MockProvider 脚本调 read_file → 真实 ReadFileTool 读临时文件 → 脚本总结 | AgentDone，总结含文件内容 |
| `EndToEnd_WriteFileAndConfirm` | 脚本调 write_file → 真实 WriteFileTool 写临时文件 → 脚本确认 | 文件被创建，AgentDone |
| `EndToEnd_GlobAndGrep` | 脚本调 glob → 真实 GlobTool → 脚本调 grep → 真实 GrepTool → 总结 | 4 轮，AgentDone |
| `EndToEnd_ToolErrorSelfCorrection` | 脚本调 edit_file 多次匹配（失败）→ 脚本 LLM 看到错误后改用 write_file | 2 轮，最终成功 |
| `EndToEnd_MaxRoundsStops` | 脚本无限循环调 read_file + maxRounds=3 | 3 轮后 MaxRoundsReached |
| `EndToEnd_EventsRenderedToConsole` | 真实 App + Console 输出 | 控制台看到 RoundStart/TextDelta/ToolCallStart/ToolResult/AgentDone 渲染 |

### 8.12 回归

- `dotnet test` 全绿（含迭代 1-5 既有 + 迭代 6 新增 11 个测试文件）。
- `dotnet run`（mock 无脚本）行为与迭代 4 一致——输入"你好"输出"你好（mock）"。
- `OpenAIProviderTests` / `MockProviderTests` 既有用例全绿（旧重载保留）。
- `/clear` 行为保持。

## 九、验收标准

> 全部满足方可标记 `[已完成]`。每条尽量可被命令或肉眼验证。

### 9.1 构建与运行

- [ ] `ParrotCode.Net.csproj` 无改动（零新依赖，`System.Threading.Channels` 是 BCL 内置）。
- [ ] `dotnet build` 无 error、无 warning。
- [ ] `dotnet test` 全绿（含迭代 1-5 既有 + 迭代 6 新增 11 个测试文件）。
- [ ] `dotnet run`（`active_provider: mock`）能启动，启动横幅正常。

### 9.2 ChatChunk 与响应解析

- [ ] `Agent/ChatChunk.cs` 定义 `abstract record ChatChunk` + `TextDelta` / `ToolCallDelta` / `Done` 派生。
- [ ] `ToolCallAccumulator` 按 index 累积 arguments，`Build` 返回完整 `ToolCall` 列表。
- [ ] 空 arguments 兜底为 `{}`。
- [ ] 非法 JSON arguments 兜底为含 `_parse_error` 的对象。
- [ ] 缺失 id 兜底为 `call_{index}`。
- [ ] `Build` 按 index 升序输出。
- [ ] `ChatChunkAccumulatorTests` 10 个用例全绿。

### 9.3 IBaseProvider 扩展

- [ ] `IBaseProvider` 新增 `ChatStreamAsync(messages, tools, toolChoice, ct)` 重载返回 `IAsyncEnumerable<ChatChunk>`。
- [ ] 旧 `ChatAsync` + `ChatStreamAsync(string)` 重载保留不变。
- [ ] `OpenAIProvider` 实现新重载：解析 `delta.content` + `delta.tool_calls` + `[DONE]`。
- [ ] `OpenAIProvider.BuildRequestBody` 支持 `tools` / `tool_choice` 字段。
- [ ] `OpenAIProvider.BuildRequestBody` 序列化 assistant+ToolCalls 与 tool+ToolCallId 消息正确。
- [ ] `OpenAIProviderTests` 补充 tool_calls 解析用例全绿。
- [ ] 旧 `OpenAIProviderTests` 既有用例全绿（无回归）。

### 9.4 MockProvider 脚本化

- [ ] `MockProvider` 新增 `EnqueueScript(params ChatChunk[])` 方法。
- [ ] `MockProvider` 实现新 `ChatStreamAsync` 重载：有脚本出队产出，无脚本回退回显。
- [ ] 脚本末尾无 `Done` 时自动追加。
- [ ] 默认行为（无脚本）与迭代 3 回显一致。
- [ ] `MockProviderTests` 补充脚本用例全绿。
- [ ] 旧 `MockProviderTests` 既有用例全绿（无回归）。

### 9.5 Message / ConversationHistory 扩展

- [ ] `Message` 加 `ToolCallId` 字段（`string?`，init）。
- [ ] `ConversationHistory.AddAssistant(string content, IReadOnlyList<ToolCall> toolCalls)` 重载。
- [ ] `ConversationHistory.AddTool(string content, string toolCallId)` 重载。
- [ ] 旧 `AddAssistant(string)` / `AddTool(string)` 保留。
- [ ] `AddAssistant(content, toolCalls)` 在 toolCalls 空时抛 `ArgumentException`。
- [ ] `MessageExtensions.ToOpenAiWire()` 正确序列化 assistant+ToolCalls / tool+ToolCallId / 普通消息。
- [ ] `MessageExtensionsTests` 补充 wire format 用例全绿。

### 9.6 事件流（AgentEvent + IAgentEventSink + ChannelEventSink）

- [ ] `Agent/AgentEvent.cs` 定义 12 种事件类型（`RoundStart` / `TextDelta` / `AssistantMessage` / `ToolCallStart` / `ToolResult` / `ToolBlocked` / `RoundEnd` / `AgentDone` / `MaxRoundsReached` / `Warning` / `Error` / `Cancelled`）。
- [ ] `IAgentEventSink` 接口含 `WriteAsync` + `Complete`。
- [ ] `ChannelEventSink` 基于 `Channel<AgentEvent>`，暴露 `Reader`。
- [ ] `ChannelEventSink` 用 `SingleReader/SingleWriter` 优化。
- [ ] `WriteAsync` 后 `Complete` 后 `ReadAllAsync` 自然结束。
- [ ] `ChannelEventSinkTests` 5 个用例全绿。

### 9.7 BatchToolExecutor

- [ ] `Agent/BatchToolExecutor.cs` 定义 `BatchToolExecutor` 类。
- [ ] 按 `ToolCategory` 分组：Read 并发 / Write 串行。
- [ ] Read 组用 `Task.WhenAll` + `Chunk(maxParallelism)` 限流。
- [ ] Write 组顺序 `await`。
- [ ] 结果按输入顺序返回。
- [ ] 工具失败不中断同批其他工具。
- [ ] 未注册工具返回 `ToolResult.Fail`。
- [ ] 外部取消透传 `OperationCanceledException`。
- [ ] `BatchToolExecutorTests` 10 个用例全绿。

### 9.8 AgentLoop

- [ ] `Agent/AgentLoop.cs` 定义 `AgentLoop` 类。
- [ ] `RunAsync(history, sink, ct)` 实现 ReAct 循环。
- [ ] 最大轮次默认 10，可配。
- [ ] 无 tool_calls 时 emit `AgentDone` 退出。
- [ ] 达到 maxRounds 时 emit `MaxRoundsReached` 退出。
- [ ] 事件流顺序正确：RoundStart → TextDelta(多) → AssistantMessage → ToolCallStart(多) → ToolResult(多) → RoundEnd → ...。
- [ ] 工具结果（含失败原因）通过 `history.AddTool` 回灌给 LLM。
- [ ] 异常不逃逸：Provider 错误转 `Error` 事件，取消转 `Cancelled` 事件。
- [ ] `finally sink.Complete()` 确保通道关闭。
- [ ] system prompt 注入消息列表头部。
- [ ] `AgentLoopTests` 12 个用例全绿。
- [ ] `AgentLoopIntegrationTests` 6 个用例全绿。

### 9.9 三个补充工具

#### RunCommandTool

- [ ] `Tools/RunCommandTool.cs` 定义 `RunCommandTool : ToolBase`。
- [ ] `Name == "run_command"`，`Category == Write`。
- [ ] 参数 `command`（必需）+ `args` / `cwd` / `timeout`（可选）。
- [ ] 执行命令返回 `[exit N]\n{stdout}\n[stderr]\n{stderr}`。
- [ ] 超时返回 `ToolResult.Fail("超时")` 并杀进程树。
- [ ] 跨平台（Windows `cmd /c` / Unix `/bin/sh -c`）。
- [ ] `timeout` 上限 300 秒。
- [ ] `RunCommandToolTests` 8 个用例全绿。

#### GlobTool

- [ ] `Tools/GlobTool.cs` 定义 `GlobTool : ToolBase`。
- [ ] `Name == "glob"`，`Category == Read`。
- [ ] 参数 `pattern`（必需）+ `path`（可选）。
- [ ] 支持 `*` / `**` / `?` glob 模式。
- [ ] 递归搜索，返回相对路径列表（最多 200 条）。
- [ ] `GlobToolTests` 7 个用例全绿。

#### GrepTool

- [ ] `Tools/GrepTool.cs` 定义 `GrepTool : ToolBase`。
- [ ] `Name == "grep"`，`Category == Read`。
- [ ] 参数 `pattern`（必需）+ `path` / `include`（可选）。
- [ ] 正则搜索，返回 `文件:行号:行` 列表（最多 100 条）。
- [ ] `include` 用 glob 过滤文件名。
- [ ] 非法正则返回 `ToolResult.Fail("正则非法")`。
- [ ] `GrepToolTests` 8 个用例全绿。

### 9.10 端到端 ReAct 闭环（核心验收）

- [ ] **mock 模式 + 脚本**：MockProvider 注入"调 read_file → 总结"脚本，AgentLoop 跑 2 轮，事件流打印完整流程。
- [ ] **DeepSeek 真实模式**（需 `DEEPSEEK_API_KEY`）：
  - 让 AI "读取 README.md 并总结"：看到 AI 调 `read_file` → 工具执行 → AI 输出总结。
  - 让 AI "在 D:\tmp 下创建 hello.txt 写入你好"：看到 `write_file` 被调用，文件被创建。
  - 让 AI "找出项目里所有 .cs 文件"：看到 `glob` 被调用，返回文件列表。
  - 让 AI "搜索代码里哪里用了 ConfigException"：看到 `grep` 被调用，返回匹配行。
- [ ] 连续 11 轮还有工具调用时，Agent 在第 10 轮后停止并 emit `MaxRoundsReached`。
- [ ] 工具失败时（如 `edit_file` 多次匹配），AI 看到错误后调整策略（自我修正）。
- [ ] Ctrl+C 能优雅取消，emit `Cancelled`，程序不崩溃。

### 9.11 App 接入

- [ ] `App/App.cs` 主循环改为调 `AgentLoop.RunAsync` + 消费 `sink.Reader`。
- [ ] 事件流渲染到控制台（`RoundStart` 灰色 / `TextDelta` 绿色逐字 / `ToolCallStart` 青色 / `ToolResult` 成功绿失败红 / `AgentDone` 灰色）。
- [ ] `/clear` 行为保持（清空历史）。
- [ ] `exit` / `quit` / EOF 退出行为保持。
- [ ] Program 装配 ToolRegistry（6 个工具）+ BatchToolExecutor + AgentLoop 注入 App。

### 9.12 配置

- [ ] `Config/Models.cs` 加 `AgentConfig`（MaxRounds / ToolChoice / MaxParallelism / ToolTimeoutSeconds / SystemPrompt）。
- [ ] `AppConfig.Agent` 字段。
- [ ] `example.parrotcode.yaml` 加 `agent:` 节示例。
- [ ] ConfigLoader 解析 `agent` 节（缺失时 null，App 用默认值）。
- [ ] 配置项可被覆盖（如 `max_rounds: 5` 生效）。

### 9.13 异常与边界

- [ ] Provider 401 错误 → emit `Error` 事件，Agent 终止，主循环继续（下一轮用户输入正常）。
- [ ] Provider 429 错误 → emit `Error` 事件，主循环继续。
- [ ] Provider 5xx 错误 → emit `Error` 事件，主循环继续。
- [ ] 网络断开 → emit `Error("无法连接到 ...")` 事件，主循环继续。
- [ ] 工具执行超时 → `ToolResult.Fail("超时")` 回灌 LLM，Agent 继续。
- [ ] 工具抛异常 → `ToolResult.Fail(ex.Message)` 回灌 LLM，Agent 继续。
- [ ] LLM 生成非法 arguments JSON → 兜底对象传工具，工具返回"缺少必需参数"，Agent 继续。
- [ ] LLM 调未注册工具 → `ToolResult.Fail("未注册工具")` 回灌，Agent 继续。
- [ ] Ctrl+C → emit `Cancelled`，Agent 优雅停止。

### 9.14 敏感信息

- [ ] 事件流与日志不出现 ApiKey 明文。
- [ ] `ToolResult.Content` / `Error` 不包含 ApiKey（即使读到的文件含 key，也是文件内容而非日志泄露）。
- [ ] `run_command` 的日志只记命令名不记参数中的敏感信息（如 `git` 不记 token）。
- [ ] Provider 请求体日志（若有）不出现 ApiKey。

### 9.15 跨平台

- [ ] Windows 上 `dotnet test` 全绿。
- [ ] macOS / Linux 上 `dotnet test` 全绿。
- [ ] `RunCommandTool` 在三平台用各自 shell（`cmd /c` / `/bin/sh -c`）。
- [ ] 文件路径在事件流中统一 Unix 风格（`/` 分隔）。
- [ ] `GlobTool` / `GrepTool` 路径处理跨平台一致。

### 9.16 迁移与回归

- [ ] `IBaseProvider` 旧重载（`ChatAsync` + `ChatStreamAsync(string)`）**不变**。
- [ ] `OpenAIProvider` 旧 `ChatStreamAsync(string)` 路径**不变**。
- [ ] `MockProvider` 默认行为（无脚本回显）**不变**。
- [ ] `Message` 加字段但不破坏既有构造（`ToolCalls` / `ToolCallId` 是 init 可选）。
- [ ] `ConversationHistory` 旧 `AddAssistant(string)` / `AddTool(string)` 保留。
- [ ] 迭代 5 的 `ToolExecutor` / `ToolRegistry` / 三个文件工具 / `ClosedLoopDemo` **不变**。
- [ ] 迭代 1-5 的所有测试**全绿**（无回归）。
- [ ] `dotnet run`（mock 无脚本）输入"你好" → "你好（mock）"（与迭代 4 一致）。

## 十、进阶练习（可选，不计入验收）

1. **`PlanOnly` 模式**：`tool_choice="none"` + 只允许 Read 工具，Write 工具被拦截并 emit `ToolBlocked`。让 AI 只做"分析"不做"修改"。

2. **工具调用计数与限流**：在 `BatchToolExecutor` 加 `MaxCallsPerSession`（如 100 次），超过 emit `Error` 终止。防止 Agent 死循环消耗 token。

3. **`tool_choice` 强制指定工具**：支持 `tool_choice={"type":"function","function":{"name":"read_file"}}` 强制调特定工具。UI 在迭代 7 加。

4. **流式 tool_calls 实时展示**：在 `ToolCallDelta` 累积阶段就 emit "部分 tool_call" 事件（如"LLM 正在构造 read_file 调用..."），让用户看到 LLM 的"思考过程"。本迭代等 `Done` 才 emit `ToolCallStart`。

5. **并行工具执行的进度展示**：Read 组并发执行时，逐个完成就 emit `ToolResult`（而非等全部完成）。本迭代是等 `Task.WhenAll` 全部完成才统一 emit。

6. **`SubAgentTool`**：起子 Agent 处理子任务。本迭代不实现（plan.md 列入迭代 12），但可提前设计 `sub_agent(task, role)` 工具签名。

7. **事件流持久化**：把 `AgentEvent` 流序列化到 JSONL 文件，用于回放调试。本迭代不实现（迭代 10 会话持久化覆盖）。

8. **`run_command` 的 stdout 流式返回**：本迭代 `run_command` 等命令结束后一次性返回 stdout。改为用 `Channel<string>` 流式产出 stdout 行，让长命令（如 `dotnet build`）实时反馈。

9. **工具调用结果缓存**：`read_file` 同一路径在一轮 ReAct 内多次调用时缓存结果（避免重复 IO）。失效策略：`write_file` / `edit_file` 后清缓存。

10. **`HITL` 预览**：在 `BatchToolExecutor.OnBeforeExecuteAsync` hook 加一个简单的 `Console.ReadLine()` 确认（"执行 write_file? (y/n)"），为迭代 7/8 HITL 预热。

## 十一、风险与注意事项

| 风险 | 缓解 |
| --- | --- |
| LLM 流式 tool_calls 分片解析错误（arguments 截断） | `ToolCallAccumulator` 对非法 JSON 兜底为 `_parse_error` 对象，工具返回错误回灌 LLM 自我修正。单测 §8.1 覆盖 |
| LLM 陷入无限工具调用循环 | `maxRounds` 默认 10 兜底，emit `MaxRoundsReached` 停止。配置可调 |
| Read 工具并发导致文件句柄耗尽 | `maxParallelism` 默认 5 限流。`BatchToolExecutor` 用 `Chunk` 分批 |
| Write 工具并发导致文件竞态 | Write 组严格串行（顺序 await），不并发 |
| `run_command` 执行危险命令（`rm -rf /`） | 本迭代不接安全层，文档明确风险。迭代 8 `Blacklist` 在 `OnBeforeExecuteAsync` 拦截 |
| `Channel` 无界导致内存爆 | 本迭代工具执行快、打印快，积压可控。若需背压改 `Channel.CreateBounded` |
| 事件流消费者慢导致 AgentLoop 阻塞 | `WriteAsync` 在无界通道不阻塞。若改有界需处理 `WaitToWriteAsync` |
| Provider 401/429/5xx 错误处理 | `RunAsync` 捕获转 `Error` 事件，主循环继续。不逃逸 |
| Ctrl+C 取消时工具仍在执行 | `CancellationToken` 传到工具，文件 IO 响应取消；`run_command` 用 `Kill(entireProcessTree)` 杀进程 |
| LLM 生成空 arguments | 兜底 `{}`，工具返回"缺少必需参数" |
| LLM 调未注册工具 | `ToolExecutor` 返回 `ToolResult.Fail("未注册工具")`，回灌 LLM |
| `JsonElement` 生命周期（JsonDocument Dispose 后失效） | `ToolCallAccumulator.Build` 用 `Clone()` 脱离 JsonDocument 生命周期 |
| assistant 消息 content 为 null 导致兼容服务报错 | `ToOpenAiWire` 对空 content 传 null。若 DeepSeek 等不兼容，调整为空字符串。测试覆盖 |
| `tool_call_id` 关联错误导致 OpenAI 400 | `history.AddTool(content, call.Id)` 严格用 ToolCall.Id 关联。单测验证 |
| `run_command` 超时后进程泄漏 | `Kill(entireProcessTree: true)` 杀整个进程树。日志记录 |
| `GlobTool` / `GrepTool` 搜索整个磁盘 | 默认 `path="."` 限制当前目录。LLM 传绝对路径时迭代 8 沙箱拦截 |
| 工具结果超长（如读 10 万行文件）爆 token | 本迭代不截断（迭代 9 Truncator）。`GlobTool`/`GrepTool` 内置 200/100 上限部分缓解 |
| 多轮 ReAct 后历史爆 token | 本迭代不压缩（迭代 9）。日志显示 token 数供观察。`maxRounds` 间接限制 |
| MockProvider 脚本耗尽后回退回显导致测试误判 | 测试明确注入足够脚本。回退回显是兜底而非主路径 |
| `ChannelEventSink` 单读单写限制被破坏 | 文档明确只有一个消费者（App）+ 一个生产者（AgentLoop）。若多消费者去掉 `SingleReader` |
| `OpenAIProvider.BuildRequestBody` 用 JsonNode 拼装与旧匿名对象不一致 | 旧 `ChatStreamAsync(string)` 路径保留匿名对象实现，新重载用 JsonNode。测试分别覆盖 |
| 事件类型 `ToolResult` 与 `ParrotCode.ToolResult` 命名冲突 | 事件类用 `ToolResultEvent` 后缀，或全限定 `global::ParrotCode.ToolResult`。实现时统一 |
| `GlobToRegex` 在 GlobTool / GrepTool 重复 | 提取到 `Tools/GlobPattern.cs` 静态工具类共享 |

## 十二、交付清单

### 12.1 新增源文件

- [ ] `ParrotCode.Net/Agent/ChatChunk.cs`（新增：LLM 流式响应单元 union）
- [ ] `ParrotCode.Net/Agent/AgentEvent.cs`（新增：12 种事件类型）
- [ ] `ParrotCode.Net/Agent/IAgentEventSink.cs`（新增：事件消费者接口）
- [ ] `ParrotCode.Net/Agent/ChannelEventSink.cs`（新增：Channel 实现）
- [ ] `ParrotCode.Net/Agent/AgentLoop.cs`（新增：ReAct 核心循环）
- [ ] `ParrotCode.Net/Agent/BatchToolExecutor.cs`（新增：分批执行器）
- [ ] `ParrotCode.Net/Tools/RunCommandTool.cs`（新增：执行 shell 命令）
- [ ] `ParrotCode.Net/Tools/GlobTool.cs`（新增：文件名模式匹配）
- [ ] `ParrotCode.Net/Tools/GrepTool.cs`（新增：内容正则搜索）
- [ ] `ParrotCode.Net/Tools/GlobPattern.cs`（新增：glob 转正则共享工具类）

### 12.2 修改源文件

- [ ] `ParrotCode.Net/Providers/IBaseProvider.cs`（新增带 tools 的 ChatStreamAsync 重载）
- [ ] `ParrotCode.Net/Providers/OpenAIProvider.cs`（BuildRequestBody 加 tools/tool_choice；流式解析 tool_calls）
- [ ] `ParrotCode.Net/Providers/MockProvider.cs`（EnqueueScript 脚本队列 + 新重载）
- [ ] `ParrotCode.Net/Providers/MessageTypes.cs`（Message 加 ToolCallId 字段）
- [ ] `ParrotCode.Net/Conversation/History.cs`（AddAssistant/AddTool 重载）
- [ ] `ParrotCode.Net/Conversation/MessageExtensions.cs`（ToOpenAiWire 方法）
- [ ] `ParrotCode.Net/App/App.cs`（主循环改调 AgentLoop + 消费事件流）
- [ ] `ParrotCode.Net/Program.cs`（装配 ToolRegistry + BatchToolExecutor + AgentLoop）
- [ ] `ParrotCode.Net/Config/Models.cs`（AppConfig.Agent + AgentConfig）
- [ ] `ParrotCode.Net/example.parrotcode.yaml`（agent 节示例）

### 12.3 新增测试文件

- [ ] `ParrotCode.Net-xUnit/ChatChunkAccumulatorTests.cs`
- [ ] `ParrotCode.Net-xUnit/ChannelEventSinkTests.cs`
- [ ] `ParrotCode.Net-xUnit/BatchToolExecutorTests.cs`
- [ ] `ParrotCode.Net-xUnit/RunCommandToolTests.cs`
- [ ] `ParrotCode.Net-xUnit/GlobToolTests.cs`
- [ ] `ParrotCode.Net-xUnit/GrepToolTests.cs`
- [ ] `ParrotCode.Net-xUnit/AgentLoopTests.cs`
- [ ] `ParrotCode.Net-xUnit/AgentLoopIntegrationTests.cs`

### 12.4 补充测试文件

- [ ] `ParrotCode.Net-xUnit/OpenAIProviderTests.cs`（补充 tool_calls 解析用例）
- [ ] `ParrotCode.Net-xUnit/MockProviderTests.cs`（补充脚本用例）
- [ ] `ParrotCode.Net-xUnit/MessageExtensionsTests.cs`（补充 wire format 用例）

### 12.5 演示与验收

- [ ] 演示：mock 模式 + 脚本跑通 ReAct 闭环（read_file → 总结）
- [ ] 演示：DeepSeek 真实模式，让 AI 读 README.md 并总结（验证 Function Calling 端到端）
- [ ] 演示：DeepSeek 真实模式，让 AI 用 glob 找出所有 .cs 文件再用 grep 搜索关键字（验证多工具多轮 ReAct）
- [ ] 演示：DeepSeek 真实模式，让 AI 用 write_file 创建文件（验证 Write 工具串行执行）
- [ ] 演示：连续 11 轮调工具时第 10 轮后 emit `MaxRoundsReached`（验证最大轮次防护）
- [ ] 演示：Ctrl+C 中断长任务，emit `Cancelled` 程序不崩溃（验证取消传播）
- [ ] 演示：工具失败（如 edit_file 多次匹配）后 AI 自我修正（验证错误回灌）

## 十三、实现顺序建议

为降低集成风险，建议按以下顺序分步实现（每步可单独编译验证）：

1. **类型层（无逻辑）**：`ChatChunk` / `AgentEvent` / `IAgentEventSink` / `ChannelEventSink` + `Message.ToolCallId` 字段 + `MessageExtensions.ToOpenAiWire()`。先建立类型契约，后续填充逻辑。
2. **累积器**：`ToolCallAccumulator` + `ChatChunkAccumulatorTests`。独立测试流式 tool_calls 累积算法，不依赖 Provider。
3. **Provider 扩展**：`IBaseProvider` 新重载 + `OpenAIProvider` 流式 tool_calls 解析 + `MockProvider.EnqueueScript` + `OpenAIProviderTests` / `MockProviderTests` 补充。
4. **History 扩展**：`ConversationHistory.AddAssistant(content, toolCalls)` + `AddTool(content, toolCallId)` 重载 + `MessageExtensionsTests` 补充。
5. **三个补充工具**：`RunCommandTool` / `GlobTool` / `GrepTool` + `GlobPattern` 共享类 + 各自单测。独立可测，不依赖 AgentLoop。
6. **BatchToolExecutor**：`BatchToolExecutor` + `BatchToolExecutorTests`。验证分批调度逻辑，不依赖 AgentLoop。
7. **AgentLoop**：`AgentLoop` + `AgentLoopTests`（用 MockProvider 脚本）+ `AgentLoopIntegrationTests`（端到端）。核心闭环集成。
8. **App 接入**：改 `App.cs` 主循环 + 改 `Program.cs` 装配 + `Config/Models.cs` 扩展 + `example.parrotcode.yaml`。
9. **端到端验收**：`dotnet test` 全绿 + mock 模式跑通 + DeepSeek 真实模式跑通。

> 每步完成后 `dotnet build` 应无 error。步骤 1-6 完成后既有功能不回归（旧 App 仍可用旧 `ChatStreamAsync(string)` 跑）。步骤 7-8 切换 App 到 AgentLoop 后，旧路径保留但主路径改为新闭环。

---

## 附录 A：事件流渲染示例（控制台）

App 消费事件流的控制台渲染示例（非验收项，仅示意）：

```
> 读 README.md 并总结
你：读 README.md 并总结
AI：[Round 1] → read_file({"path":"README.md"})
   ✓ 成功（325 字符）
[Round 2] README 主要内容是：
- ParrotCode.Net 是一个 .NET 控制台 AI 编程助手
- 采用 12 个迭代逐步构建
- 迭代 6 实现 ReAct Agent 循环...
[完成]
```

渲染规则：
- `RoundStart(N)` → 灰色 `[Round N]`
- `TextDelta(t)` → 绿色逐字 `Console.Write(t)`
- `ToolCallStart(call)` → 青色 `→ {call.Name}({call.Input})`
- `ToolResult(call, result)` → 成功绿色 `✓ 成功（{len} 字符）`，失败红色 `✗ 失败：{result.Error}`
- `AgentDone(text)` → 灰色 `[完成]`
- `MaxRoundsReached(n)` → 黄色 `[已达最大轮次 {n}]`
- `Error(msg)` → 红色 `[错误] {msg}`
- `Cancelled` → 灰色 `[已取消]`

## 附录 B：OpenAI 流式 SSE 完整示例

一段含文本 + 双工具调用的完整 SSE 流（用于 `OpenAIProviderTests` 构造 mock SSE）：

```
data: {"choices":[{"delta":{"role":"assistant","content":""}}]}

data: {"choices":[{"delta":{"content":"我来帮你"}}]}

data: {"choices":[{"delta":{"content":"读取这两个文件"}}]}

data: {"choices":[{"delta":{"tool_calls":[{"index":0,"id":"call_abc","type":"function","function":{"name":"read_file","arguments":""}}]}}]}

data: {"choices":[{"delta":{"tool_calls":[{"index":0,"function":{"arguments":"{\"path\":"}}]}}]}

data: {"choices":[{"delta":{"tool_calls":[{"index":0,"function":{"arguments":"\"a.txt\"}"}}]}}]}

data: {"choices":[{"delta":{"tool_calls":[{"index":1,"id":"call_def","type":"function","function":{"name":"read_file","arguments":""}}]}}]}

data: {"choices":[{"delta":{"tool_calls":[{"index":1,"function":{"arguments":"{\"path\":\"b.txt\"}"}}]}}]}

data: {"choices":[{"finish_reason":"tool_calls"}]}

data: [DONE]
```

累积后 `Build()` 应返回：
- `ToolCall("call_abc", "read_file", {"path":"a.txt"})`
- `ToolCall("call_def", "read_file", {"path":"b.txt"})`

文本缓冲为 `"我来帮你读取这两个文件"`。

---

> 本文档到此结束。`plan.md` 的迭代 6 条目可标记为「设计完成，待实现」。实现完成后将本文件头部状态改为 `[已完成]`。