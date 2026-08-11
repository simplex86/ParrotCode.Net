# ParrotCode.Net — 迭代开发计划

> 基于 .NET 8 从零构建一个仿 Claude Code 的终端 AI 编程助手，通过分阶段迭代逐步学习 Agent 相关知识与工程实现。
>
> 参考项目：`D:\github\mewcode`（Python 版 MewCode，11 阶段）。
> 本计划在其基础上做两处调整：
> 1. **技术栈映射**：Python asyncio/httpx/Prompt Toolkit/YAML → .NET `async/await` + `HttpClient` + `Spectre.Console` + `YamlDotNet`。
> 2. **学习曲线细分**：考虑到完全没有 Agent 开发经验，把"核心循环 + 工具系统"进一步拆细，让每个迭代都能跑起来、看得见效果。

## 一、技术栈选型（.NET 8 对应表）

| 能力 | MewCode (Python) | ParrotCode.Net (.NET 8) | 说明 |
| --- | --- | --- | --- |
| 异步并发 | `asyncio` + `httpx` | `async/await` + `HttpClient` + `System.Threading.Channels` | Channel 用于事件流 |
| SSE 流式解析 | `httpx` 流 + 手写解析 | `HttpClient.SendAsync` + `ReadAsStreamAsync` + 逐行解析 | 不引入第三方 SDK |
| TUI | Prompt Toolkit | `Spectre.Console` | 成熟、跨平台、支持流式渲染 |
| 配置 | `pyyaml` + `pydantic` | `YamlDotNet` + `record` + DataAnnotations | 校验手写 |
| JSON | 内置 `json` | `System.Text.Json` + `JsonSerializer` | 源生成器可选 |
| JSONL 会话 | 手写追加 | `FileStream` Append + 逐行 `JsonDocument` | 同 MewCode 思路 |
| MCP 子进程 | `asyncio.subprocess` | `System.Diagnostics.Process` + 重定向管道 | |
| MCP HTTP/SSE | `httpx` SSE | `HttpClient` + 手写 SSE | |
| 依赖注入 | `main.py` 41 组件手写注入 | `Microsoft.Extensions.DependencyInjection` | 渐进引入 |
| 日志 | `print` | `Microsoft.Extensions.Logging` | 控制台 + 文件双 sink |
| 单元测试 | 暂无 | `xUnit` + `FluentAssertions` | 每个 Agent 模块配测试 |

## 二、总体路线图（15 个迭代）

```
迭代 1  项目脚手架 + 最小可跑（Hello Agent）
迭代 2  配置系统 + Provider 抽象层
迭代 3  第一个 LLM Provider（OpenAI 兼容）+ 流式输出
迭代 4  对话历史 + 多轮上下文
迭代 5  工具系统骨架 + read_file / write_file / edit_file
迭代 6  ReAct Agent 循环（事件流 + 工具调用闭环）
迭代 7  TUI 接入（Spectre.Console + 流式渲染 + HITL）
迭代 8  安全纵深防御（黑名单 + 沙箱 + 三档权限）
迭代 9  上下文管理（工具结果截断 + 结构化摘要 + 压缩协调）
迭代 10 斜杠命令 + 会话持久化（JSONL）+ 项目指令
迭代 11 MCP 协议客户端（Stdio + HTTP SSE）
迭代 12 Skill 系统
迭代 13 Skill 目录化与三层加载
迭代 14 子 Agent
迭代 15 Hook 引擎
```

> 说明：MewCode 的 Team 编排、Git Worktree 隔离、自动笔记三个子系统作为 **可选扩展** 放在计划末尾，不强制实现，等前 15 个迭代跑通后再决定是否引入。

每个迭代遵循统一结构：
- **学习目标**：本阶段掌握的 Agent 概念 / .NET 技巧。
- **交付物**：能运行的代码 + 一段演示。
- **影响文件**：新增/修改的目录与文件。
- **关键设计点**：需要决策或注意的地方。
- **验收标准**：跑通即过。
- **进阶练习（可选）**：留给下一轮自己加的扩展。

---

## 迭代 1：项目脚手架 + 最小可跑

### 学习目标
- 理解"Agent = LLM + 工具 + 循环"的最小骨架。
- 熟悉 .NET 8 控制台项目结构、`async Task Main`、`HttpClient` 基础调用。

### 交付物
一个能跑的控制台程序：用户输入一句话，程序直接调用某个 LLM API（先用 OpenAI 兼容协议的 mock 或真实 key），把回复打印出来。**不**做流式、**不**做工具、**不**做多轮。

### 影响文件
```
ParrotCode.Net/
├── Program.cs                 # 入口：读输入 → 调 LLM → 打印
├── ParrotCode.Net.csproj      # 引入 Spectre.Console（仅用于着色输出）
└── appsettings.json           # 占位配置（后续被 YAML 替代）
```

### 关键设计点
- 入口用 `async Task Main`，不要 `void Main`。
- LLM 调用先用一个 `MockProvider` 返回固定文本，跑通管线；真实 Provider 在迭代 3 接入。
- 引入 `Microsoft.Extensions.Logging.Console`，把日志打 stderr，输出打 stdout，便于后续 TUI 接管。

### 验收标准
- `dotnet run` 启动，输入"你好"，输出"你好（mock）"或真实 LLM 回复。
- Ctrl+C 能干净退出。

### 进阶练习
- 用 `CancellationToken` 实现"输入期间按 Esc 取消"。

---

## 迭代 2：配置系统 + Provider 抽象层

### 学习目标
- 配置三级发现（环境变量 > 项目目录 > 用户目录）。
- 面向接口设计 Provider 抽象，体会"协议无关"的好处。

### 交付物
- `config/` 模块：YAML 加载 + `AppConfig` / `ProviderConfig` 数据模型。
- `providers/IBaseProvider` 抽象接口 + `ToolCall` / `Message` 类型 + 工厂方法。
- `parrotcode.yaml` 示例配置（参照 MewCode 的 `example.mewcode.yaml`）。

### 影响文件
```
ParrotCode.Net/
├── Config/
│   ├── Models.cs              # AppConfig / ProviderConfig (record)
│   ├── Loader.cs              # 三级发现 + YamlDotNet 解析 + 校验
│   └── ConfigException.cs
├── Providers/
│   ├── IBaseProvider.cs       # 抽象接口
│   ├── ToolCall.cs            # record ToolCall(string Id, string Name, JsonElement Input)
│   ├── MessageTypes.cs        # Message 类型（用 JsonNode 或 record）
│   └── ProviderFactory.cs     # 按 protocol 字段路由
├── example.parrotcode.yaml
└── Program.cs                 # 接入配置加载
```

### 关键设计点
- **配置发现顺序**：`PARROTCODE_CONFIG` 环境变量 → `./.parrotcode.yaml` → `~/.parrocode/config.yaml`，与 MewCode 一致。
- `ProviderConfig` 字段：`Name / Protocol / Model / BaseUrl / ApiKey`。
- `IBaseProvider` 接口先只声明 `Task<string> ChatAsync(...)` 非流式版本，流式在迭代 3 加。
- 工厂方法用 `switch` 表达式按 `Protocol` 路由，未支持的协议抛 `ArgumentException`。

### 验收标准
- 故意把 YAML 写错，能看到带行号的错误信息。
- 切换 `active_provider` 字段，程序能选到不同的 Provider（即使它们都还没实现）。

### 进阶练习
- 用 `Microsoft.Extensions.Configuration` 把 YAML 接入 `IConfiguration`，对比手写加载的差异。

---

## 迭代 3：第一个 LLM Provider（OpenAI 兼容）+ 流式输出

### 学习目标
- **SSE 流式解析**：这是 Agent 实时反馈的基石。
- HttpClient 流式读取 + `IAsyncEnumerable<T>` 模式。

### 交付物
- `OpenAIProvider`：实现 `IAsyncEnumerable<string>` 流式返回 token。
- `AnthropicProvider`（可选）：作为对比实现，理解协议差异。
- Program 改为流式打印（逐字刷新到控制台）。

### 影响文件
```
Providers/
├── OpenAIProvider.cs          # /v1/chat/completions + stream=true
├── AnthropicProvider.cs       # /v1/messages + stream=true（可选）
└── IBaseProvider.cs           # 扩展 ChatStreamAsync 返回 IAsyncEnumerable<string|ToolCall>
```

### 关键设计点
- **SSE 解析**：`response.Content.ReadAsStreamAsync()` → 逐行读 → 前缀 `data: ` → JSON 解析 → 取 `choices[0].delta.content`。
- `IAsyncEnumerable<string>` + `CancellationToken` 是 .NET 流式 Agent 的标准姿势。
- HTTP 错误（401/429）转成有意义的异常类型，不要裸抛 `HttpRequestException`。
- ApiKey 从配置读，**不**落日志。

### 验收标准
- 输入"写一首关于秋天的诗"，能看到逐字流式输出。
- 断网或 key 错误时，能看到友好错误而不是堆栈。

### 进阶练习
- 加上 DeepSeek 的 `reasoning_content` 字段解析，体会"reasoning vs output"分离。

---

## 迭代 4：对话历史 + 多轮上下文

### 学习目标
- 对话状态管理：消息列表的追加、角色（user/assistant/tool）维护。
- Token 估算（用字符数近似），为迭代 9 的压缩打基础。

### 交付物
- `Conversation/History.cs`：`ConversationHistory` 类，维护 `List<Message>`，提供 `AddUser / AddAssistant / AddTool / ToProviderMessages` 方法。
- Program 改为循环读取用户输入，每次带上完整历史发给 LLM。

### 影响文件
```
Conversation/
├── History.cs                 # ConversationHistory（不含 system prompt）
├── MessageExtensions.cs       # 消息序列化为 provider 格式
└── TokenEstimator.cs          # 粗略 token 估算（字符数/3）
```

### 关键设计点
- **不**在 History 里放 system prompt，system prompt 由 PromptBuilder 在调用前拼装（参考 MewCode 设计）。
- 历史持久化放到迭代 10，本迭代只做内存版。
- Tool 消息格式（OpenAI 的 `role: tool` vs Anthropic 的 `tool_result` block）在 Provider 层转换，History 只存中性结构。

### 验收标准
- 连续问 3 轮，第 3 轮 AI 能记得前两轮内容。
- `/clear` 命令（即便还没命令系统）能清空历史重新开始。

### 进阶练习
- 加一个"上下文窗口占比"提示，超过 70% 给警告。

---

## 迭代 5：工具系统骨架 + 三个文件工具

### 学习目标
- **工具即函数**：把"读文件"包装成 LLM 可调用的结构化接口。
- JSON Schema 描述参数 → LLM 生成参数 → 执行 → 结果回灌。
- 工具分类（READ 并发 / WRITE 串行）的设计动机。

### 交付物
- `Tools/` 模块：`IBaseTool` 接口、`ToolRegistry`、`ToolExecutor`。
- 三个内置工具：`ReadFileTool` / `WriteFileTool` / `EditFileTool`。
- 单元测试：每个工具至少 3 个用例（正常 / 边界 / 错误）。

### 影响文件
```
Tools/
├── IBaseTool.cs               # Name / Description / Category / Parameters / ExecuteAsync
├── ToolResult.cs              # record ToolResult(bool Success, string Content, string Error)
├── ToolCategory.cs            # enum { Read, Write }
├── ToolParameter.cs           # record
├── ToolRegistry.cs            # 按名查找 + 转 OpenAI/Anthropic schema
├── ToolExecutor.cs            # 超时 + 错误捕获
├── ReadFileTool.cs
├── WriteFileTool.cs
└── EditFileTool.cs            # 精确匹配替换（str.Count 判断唯一性）
Tests/Tools/
├── ReadFileToolTests.cs
├── WriteFileToolTests.cs
└── EditFileToolTests.cs
```

### 关键设计点
- `EditFileTool` 严格按 MewCode 的语义：原文必须**唯一匹配**，0 次或多次匹配都报错并附上下文。这是后续 Agent 自我修正能力的关键。
- 工具 Schema 转换方法 `ToOpenAiSchema()` / `ToAnthropicSchema()` 放在工具基类上，避免 Provider 层耦合具体工具。
- `ToolExecutor` 用 `Task.WhenAny(task, Task.Delay(timeout))` 实现超时。

### 验收标准
- 单测全绿。
- 手写一段代码模拟"LLM 返回 tool_call → 执行 → 拿到结果"的闭环（暂不接 LLM）。

### 进阶练习
- 加 `RunCommandTool`（执行 shell 命令），但本迭代**不**接入安全层，先跑通。

---

## 迭代 6：ReAct Agent 循环（事件流 + 工具调用闭环）

### 学习目标
- **ReAct 范式**：Reason + Act 的核心循环。
- **事件流架构**：AgentLoop 产出事件 → 消费者（本迭代是控制台打印）消费，解耦生产与展示。
- 读写工具的分批执行（读并发、写串行）。

### 交付物
- `Agent/Loop.cs`：`AgentLoop` 类，实现 ReAct 循环。
- `Agent/Events.cs`：12 种事件类型（参照 MewCode）。
- Program 接入：用户输入 → AgentLoop 跑 → 事件打控制台。
- 补齐工具：`RunCommandTool` / `GlobTool` / `GrepTool`（5 个就够，sub_agent 留到迭代 12）。

### 影响文件
```
Agent/
├── Events.cs                  # UserMessage/Thinking/TextDelta/ToolCall/ToolResult/...
├── Loop.cs                    # AgentLoop：调 LLM → 解析 tool_call → 执行 → 回填 → 继续
└── IAgentEventSink.cs         # 事件消费者接口（Channel 或 IAsyncEnumerable）
Tools/
├── RunCommandTool.cs
├── GlobTool.cs
└── GrepTool.cs
```

### 关键设计点
- **事件流传输**：用 `System.Threading.Channels.Channel<AgentEvent>`，AgentLoop 写入，TUI 读取。这是迭代 7 TUI 接入的基础。
- **循环结构**：
  ```
  for round in 1..max_rounds:
      emit RoundStartEvent
      stream = provider.ChatStreamAsync(history, tools)
      foreach chunk in stream:
          if chunk is ToolCall: 收集
          else: emit TextDeltaEvent
      if no tool_call: emit AgentDoneEvent; break
      分批执行：Read 工具 Task.WhenAll，Write 工具顺序执行
      emit ToolResultEvent + 回填 history
  ```
- **最大轮次**：默认 10，防止无限循环。
- **取消**：`CancellationToken` 贯穿，支持中途 Esc 取消。

### 验收标准
- 让 AI "读取 README.md 并总结"，能看到：AI 调 read_file → 工具执行 → AI 拿到内容 → 输出总结。
- 让 AI "在 D:\tmp 下创建一个 hello.txt 写入你好"，能看到 write_file 被调用。
- 连续 11 轮还有工具调用时，Agent 能正常停止并提示达到最大轮次。

### 进阶练习
- 实现 `PlanOnly` 模式：只允许 READ 工具，WRITE 工具被拦截并 emit `ToolBlockedEvent`。

---

## 迭代 7：TUI 接入（Spectre.Console + 流式渲染 + HITL）

### 学习目标
- 全屏终端 UI 的状态管理。
- **人在回路（HITL）**：危险操作前问用户，这是 Agent 安全的核心。

### 交付物
- `Tui/App.cs`：基于 Spectre.Console 的主界面，流式渲染 AI 输出、工具调用状态、状态栏。
- `Tui/Render.cs`：消息样式（用户/AI/thinking/error 不同颜色）。
- HITL：WRITE 工具执行前弹确认，A/S/P/D 四键（Allow once / Session / Permanent / Deny）。

### 影响文件
```
Tui/
├── App.cs                     # 主应用：输入框 + 滚动输出 + 状态栏
├── Render.cs                  # 各事件类型的渲染
├── HitlPrompt.cs              # 人在回路确认对话框
└── IUiControl.cs              # UI 抽象接口（命令系统要用）
```

### 关键设计点
- **流式渲染**：Spectre.Console 的 `Live` 显示器或手动刷新。Token 到达即追加，避免整段重绘。
- **事件消费**：从迭代 6 的 Channel 读取事件，按类型分发到渲染器。
- **HITL 实现**：AgentLoop 在执行 WRITE 工具前 emit `HitlRequestEvent` 并 `await` 一个 `Task<HitlDecision>`；TUI 收到事件后弹框，用户按键完成这个 Task。
- **状态栏**：显示当前 Provider / Model / 安全等级 / 上下文占比。

### 验收标准
- 流式输出不闪烁、不丢字。
- write_file 执行前会弹确认，选 Deny 后 AI 收到拒绝原因并调整行为。
- Tab 能补全 `/` 开头的命令（即便命令系统在迭代 10 才完善）。

### 进阶练习
- 加 thinking 内容的灰色折叠渲染。

---

## 迭代 8：安全纵深防御

### 学习目标
- **纵深防御**：多层独立检查，单层失效不致命。
- 黑名单 / 沙箱 / 权限模式三层的设计权衡。

### 交付物
- `Security/` 模块：黑名单、路径沙箱、三档权限模式、`SecurityGuard` 管线。

### 影响文件
```
Security/
├── Blacklist.cs               # 硬编码危险命令（rm -rf /、curl|sh、fork bomb...）
├── PathSandbox.cs             # 拒绝绝对路径、.. 遍历、项目目录边界
├── SecurityLevel.cs           # enum { Strict, Normal, Permissive }
├── SecurityPolicy.cs          # 规则评估 + 会话/项目/全局优先级
├── Guard.cs                   # SecurityGuard 管线：黑名单 → 沙箱 → 策略 → HITL
└── Models.cs                  # SecurityDecision / HitlDecision
```

### 关键设计点
- **三档模式默认行为**：
  - Strict：只允许白名单路径的读写。
  - Normal：读放行、写询问（HITL）。
  - Permissive：仅黑名单拦截。
- **黑名单始终生效**，不依赖档位（防止 Permissive 模式下 `rm -rf /`）。
- **失败信息回灌**：拒绝原因作为 `ToolResult.Error` 返回 LLM，让它调整策略。
- 规则优先级：会话级 > 项目级 > 全局级。

### 验收标准
- 在 Normal 模式下，`read_file` 不弹确认；`write_file` 弹确认。
- 黑名单命令（如 `rm -rf /tmp`）即使在 Permissive 模式也被拦。
- 拒绝后 AI 能说"我无法执行这个命令，换个方式"。

### 进阶练习
- 把黑名单做成可配置的 YAML 规则文件。

---

## 迭代 9：上下文管理（截断 + 摘要 + 压缩）

### 学习目标
- **上下文窗口管理**：长对话如何不爆 token。
- 两层策略：层 1 工具结果截断（局部）、层 2 结构化摘要（全局）。
- **熔断器模式**：摘要连续失败就停，避免雪崩。

### 交付物
- `Conversation/Truncator.cs`：层 1 工具结果截断。
- `Conversation/Summarizer.cs`：层 2 结构化摘要 + 熔断。
- `Conversation/Compressor.cs`：两层协调器。

### 影响文件
```
Conversation/
├── Truncator.cs               # 单条 >50K 写盘留 2K 预览；单轮 >200K 截断最大
├── Summarizer.cs              # 9 段摘要 Prompt + draft 草稿 + CircuitBreaker
├── Compressor.cs              # 两层统一入口
└── CircuitBreaker.cs          # 通用熔断器
```

### 关键设计点
- **层 1 阈值**：单条工具结果 > 50K 字符 → 写到 `.parrocode/truncated/` 文件，留 2K 预览 + 文件路径。
- **层 2 阈值**：token > 70% 窗口警告，> 90% 触发摘要。
- **摘要 Prompt**：禁止工具调用（首尾强调）+ 先 draft 再正式，避免摘要过程中又触发工具调用导致死循环。
- **边界消息**：摘要后插入一条提示"以下是被压缩的历史，文件内容请重新读取不要脑补"。
- **熔断**：摘要连续失败 2 次停止自动触发，转人工。

### 验收标准
- 让 AI 读一个 10 万行的日志文件，工具结果被截断且 AI 知道去哪看全文。
- 人为构造超长对话，触发摘要后历史变短、AI 仍能继续工作。
- 故意让摘要 API 报错 2 次，第 3 次不再自动触发。

### 进阶练习
- 把摘要结果缓存到 `.parrocode/summaries/`，下次启动直接加载。

---

## 迭代 10：斜杠命令 + 会话持久化 + 项目指令

### 学习目标
- **斜杠命令系统**：用户与 Agent 的元交互入口。
- **JSONL 会话持久化**：O(1) 追加写、崩溃恢复、损坏行跳过。
- **项目指令加载**：让 Agent 知道项目约定（类似 `.cursorrules` / `CLAUDE.md`）。

### 交付物
- `Commands/` 模块：注册中心 + 解析器 + 分发器 + 内置命令。
- `Storage/SessionStore.cs`：JSONL 持久化。
- `Instructions/Loader.cs`：项目指令 + `@include` 嵌套。

### 影响文件
```
Commands/
├── CommandType.cs             # enum { System, Skill, Hidden }
├── IUiControl.cs              # UI 抽象（迭代 7 已建，这里完善）
├── Registry.cs                # 注册 + 别名冲突检测
├── Parser.cs                  # /name args 解析
├── Dispatcher.cs              # / 前缀走命令，否则走 AI
└── Builtin/
    ├── HelpCommand.cs
    ├── ClearCommand.cs
    ├── CompressCommand.cs
    ├── ModeCommand.cs
    ├── StatusCommand.cs
    └── SessionCommand.cs      # /session load <id> / save / list
Storage/
└── SessionStore.cs            # JSONL + meta.json + 损坏行跳过 + 迁移
Instructions/
└── Loader.cs                  # PARROTCODE.md + ~/.parrocode/instructions.md + @include
```

### 关键设计点
- **JSONL 设计**：每行一条消息的 JSON，文件末尾追加。Meta 文件存会话概要（ID、时间、消息数）。
- **崩溃恢复**：读取时逐行解析，损坏行跳过并记日志；未配对的 `tool_use`（缺 `tool_result`）截断到上一个完整状态。
- **时间跨度提醒**：恢复会话时如果距今 > 30 分钟，提示用户"这是 X 小时前的会话"。
- **指令 `@include`**：支持 `@include path/to/file.md` 嵌套，限制 3 层防无限递归。
- 命令注册用反射或源生成器自动扫描 `ICommand` 实现类，避免手写 register 列表。

### 验收标准
- `/help` 列出所有命令；`/clear` 清空历史；`/mode strict` 切换安全等级。
- 退出程序后 `/session load <id>` 能恢复上次对话。
- 项目根目录放一个 `PARROTCODE.md`，AI 能在回复中体现对约定的遵守。

### 进阶练习
- 加 `/session list` 按时间倒序列出最近 10 个会话。

---

## 迭代 11：MCP 协议客户端

### 学习目标
- **MCP（Model Context Protocol）**：Agent 连接外部工具服务器的标准协议。
- JSON-RPC 2.0 + 异步 Future 匹配。
- 两种传输：Stdio 子进程 + HTTP SSE。

### 交付物
- `Mcp/` 模块：协议编解码、两种传输、客户端、适配器、连接池、管理器。

### 影响文件
```
Mcp/
├── Protocol.cs                # JSON-RPC 2.0 请求/响应/通知
├── Transport/
│   ├── ITransport.cs
│   ├── StdioTransport.cs      # Process + 重定向 stdin/stdout
│   └── HttpTransport.cs       # HttpClient + SSE
├── Client.cs                  # initialize → initialized → tools/list → tools/call
├── Adapter.cs                 # MCP 工具 → IBaseTool 适配
├── Pool.cs                    # 并行 connect_all
├── Manager.cs                 # 生命周期管理
└── Config.cs                  # .parrocode-mcp.yaml
```

### 关键设计点
- **JSON-RPC id 匹配**：每个请求带自增 id，响应按 id 匹配到 `TaskCompletionSource<JsonElement>`。
- **Stdio 传输**：`Process.Start` 重定向 stdin/stdout，注意 stderr 单独收集日志，不要污染 JSON-RPC 通道。
- **HTTP SSE**：POST `/mcp` 发请求，SSE 流接收响应；注意 SSE 的 `event:` / `data:` 分隔。
- **命名前缀**：MCP 工具名变成 `{server_name}/{tool_name}`，防多 server 冲突。
- **并行连接**：`Task.WhenAll` 连接所有 server，单个失败不阻塞其他。
- **资源/提示词延迟发现**：Tools 启动即注册，Resources/Prompts 按需 list。

### 验收标准
- 配置一个 Stdio MCP server（如 filesystem server），能列出并调用其工具。
- MCP 工具在 AI 的工具列表里可见，AI 能自主调用。
- MCP server 进程在程序退出时被干净关闭。

### 进阶练习
- 接一个 HTTP MCP server，对比 Stdio 的延迟差异。

---

## 迭代 12：Skill 系统

### 学习目标
- 可编程 SOP（标准作业流程），让 Agent 按固定步骤做事。

### 交付物
- `Skills/` 模块：YAML frontmatter + MD 正文、三级存储、两阶段加载。

### 影响文件
```
Skills/
├── Models.cs                  # SkillMeta record
├── Loader.cs                  # 三级目录扫描 + YAML 解析
├── Registry.cs                # 两阶段加载（Phase1 注入名+描述，Phase2 激活 SOP）
├── SkillTool.cs               # skill_loader 工具（系统豁免）
├── Executor.cs                # 隔离执行
└── Builtin/
    ├── commit.md              # Conventional Commits SOP
    ├── review.md              # 代码审查 SOP
    └── test.md                # 测试生成 SOP
```

### 关键设计点
- **Skill 两阶段加载**：Phase 1 把名字+描述注入 system prompt（让 LLM 知道有这玩意）→ LLM 调 `skill_loader(name)` → Phase 2 把完整 SOP 注入后续每轮。避免一次性把所有 Skill 正文塞进 prompt。
- **Skill 工具白名单**：Skill 声明 `tools_allow` / `tools_deny`，多个 Skill 同时激活取交集。`skill_loader` 本身始终豁免。

### 验收标准
- `/commit` 触发 commit Skill，AI 按 Conventional Commits 流程工作。

### 进阶练习
- 为 code review 或测试生成编写自定义 Skill，验证两阶段加载是否把 SOP 正确注入每轮对话。

---

## 迭代 13：Skill 目录化与三层加载 + /skill 管理命令

> 拆分为两个正交子迭代：13a（目录化与三层加载，加载层）+ 13b（/skill 管理命令，交互层）。详见 `.docs/iter-13-design.md`。

### 学习目标
- Skill 从单文件升级为目录结构，支持 `scripts/` / `references/` / `assets/` 三层按需加载（13a）。
- 斜杠命令的子命令解析与 Skill 生命周期管理（13b）。

### 交付物
- **13a**：SkillLoader 目录扫描改造 + 三层加载机制（Phase 3 按需）+ 向后兼容单文件。
- **13b**：`/skill` 命令（list / info / activate / deactivate）。

### 影响文件
```
Skills/                         # 13a
├── Loader.cs                   # 扫描 <name>/SKILL.md 目录而非单文件
├── Models.cs                   # SkillDefinition 加 SkillDir + Resources + SkillResource
├── Registry.cs                 # BuildSop 追加资源清单段
├── Executor.cs                 # 13b 新增 GetAll()
└── Builtin/
    ├── commit/SKILL.md         # 升级为目录
    ├── review/SKILL.md
    └── test/SKILL.md
Commands/Builtin/               # 13b
└── SkillCommand.cs             # /skill list|info|activate|deactivate
```

### 关键设计点
- **Skill 目录结构**：`<name>/SKILL.md`（必须）+ `scripts/`（可选）+ `references/`（可选）+ `assets/`（可选）。
- **三层加载**：Phase 1 名字+描述（已有）→ Phase 2 SKILL.md 正文 + 资源清单（已有+追加）→ **Phase 3 子资源按需**（LLM 按清单用 `read_file` / `run_command` 访问）。
- **零新增工具**：references 用 `read_file`，scripts 用 `run_command`，assets 用 `read_file` / `write_file`——全部复用现有工具。
- **scripts 安全**：复用 SecurityGuard 黑名单 + 路径沙箱，不额外加沙箱（信任级别 ≤ 用户主动 `run_command`）。
- **向后兼容**：单文件 Skill 退化为"只有 SKILL.md 的目录"，旧 Skill 无需改动。
- **/skill activate**：复用 `/commit` 的注入模式（SOP 入 history + 触发 Agent round），是 `/commit` 的泛化版。
- **13a/13b 正交**：13a 改 `Skills/`，13b 改 `Commands/`，改动文件不重叠，可独立验收。

### 验收标准
- Skill 目录结构正确解析（`SKILL.md` + `scripts/` + `references/` + `assets/`）。
- LLM 能通过 `read_file` 读取 references。
- LLM 能通过 `run_command` 执行 scripts。
- LLM 能访问 assets。
- Phase 3 按需加载（子资源不进 Phase 1/2，只在 LLM 调用时按需读取）。
- 单文件旧 Skill 仍可用（向后兼容）。
- `/skill list` 列出所有 Skill（name + description + 来源 + 激活状态 + 资源数）。
- `/skill info <name>` 显示 Skill 详情（含资源清单 + SOP 预览）。
- `/skill activate <name>` 激活 Skill 并触发 Agent round。
- `/skill deactivate <name>` 停用 Skill。

### 进阶练习
- 写一个带 scripts 的 Skill（如 xlsx-cleaner），验证脚本执行与结果回传。
- 为 `/skill` 添加 `skills` 别名或 Tab 补全子命令。

---

## 迭代 14：子 Agent

### 学习目标
- Fork 父上下文或定义式空白对话，并行处理子任务。

### 交付物
- `SubAgent/` 模块：Fork / 定义两种模式 + 后台任务管理。

### 影响文件
```
SubAgent/
├── Models.cs
├── Runner.cs                  # SubAgentRunner
├── Filter.cs                  # 三层工具过滤（全局/角色/后台白名单）
├── Manager.cs                 # BackgroundTaskManager
├── SubAgentTool.cs            # sub_agent 工具
└── Roles/
    ├── RoleLoader.cs
    └── Builtin/
        ├── explorer.md
        ├── planner.md
        └── general.md
```

### 关键设计点
- **子 Agent 两种模式**：
  - 定义式：空白对话 + 角色 SOP（explorer/planner/general）。
  - Fork 式：继承父历史 + 注入强硬指令（不创建子 worker、不对话、直接干活、结构化报告 ≤ 500 字）。
- **三层工具过滤**：全局禁止 sub_agent 嵌套 → 角色 allow/deny → 后台任务只读白名单。

### 验收标准
- 让主 Agent 用 `sub_agent(task="探索一下这个项目的目录结构", role="explorer")`，后台子 Agent 完成后把报告注入主对话。

### 进阶练习
- 把子 Agent 接到 Git Worktree（参考 MewCode 的 `worktree/` 模块），让子 Agent 在独立工作目录操作。

---

## 迭代 15：Hook 引擎

### 学习目标
- 生命周期事件钩子，实现"工具执行前自动跑脚本"等自动化。

### 交付物
- `Hooks/` 模块：12 种事件 + 条件匹配 + 4 种动作。

### 影响文件
```
Hooks/
├── Models.cs                  # Rule / Condition / Action
├── Conditions.cs              # exact/not/regex/glob + ALL/ANY
├── Templates.cs               # {{var}} 替换
├── Actions.cs                 # shell / prompt_inject / http / sub_agent
├── Loader.cs                  # YAML 加载 + 集中校验
└── Engine.cs                  # HookEngine
```

### 关键设计点
- **Hook 12 种事件**：会话/轮次/消息/工具/系统五类。`tool_pre_exec` 可返回拒绝原因 → LLM 收到调整（拦截能力）。
- **Hook 4 种动作**：`shell`（跑命令）/ `prompt_inject`（注入提示）/ `http`（调 webhook）/ `sub_agent`（起子 Agent，依赖迭代 14 的 `SubAgentRunner`）。
- **错误隔离**：Hook 失败只记日志，不中断 Agent 主循环。

### 验收标准
- 配置一个 `tool_pre_exec` Hook，在 `write_file` 前自动跑 `git stash`。

### 进阶练习
- 配置一个 `http` 动作的 Hook，把每次工具调用事件 POST 到自己的 webhook 做调用审计。

---

## 三、可选扩展（前 15 迭代跑通后再考虑）

| 模块 | 对应 MewCode | 价值 | 复杂度 |
| --- | --- | --- | --- |
| Git Worktree 隔离 | `worktree/` | 子 Agent 操作不污染主仓库 | 中 |
| Agent Team 编排 | `teams/` | 多 Agent 协作完成大任务 | 高 |
| 自动笔记 | `notes/` | 跨会话记忆 | 中 |
| Extended Thinking 渲染 | `providers/anthropic.py` | Claude 思考过程可视化 | 低 |
| 多行输入 / Syntax Highlight | TUI 增强 | 体验提升 | 中 |

## 四、跨迭代约定

### 工程规范
- **目标框架**：`net8.0`，跨平台（Windows / macOS / Linux）。
- ** nullable 引用类型**：开启，避免 `NullReferenceException`。
- **异步全链路**：从 `Main` 到工具执行全程 `async`，禁止 `.Result` / `.Wait()`。
- **CancellationToken 贯穿**：所有异步方法都接受 `CancellationToken`。
- **日志与输出分离**：日志走 `ILogger`（stderr + 文件），用户可见输出走 stdout / TUI。
- **配置与代码分离**：API key 等敏感信息只在 YAML / 环境变量，不进代码、不进日志。

### 测试策略
- 每个 Agent 模块（Tools / Security / Conversation / Mcp / Skills / Hooks / SubAgent）配单元测试。
- Provider 层用 `HttpMessageHandler` mock，不打真实 API。
- 每个迭代结束跑一次 `dotnet test` 全绿才算完成。

### Git 约定
- 每个迭代一个分支：`iter-01-scaffold` / `iter-02-config` / ...
- 迭代完成合并到 `main`，打 tag `v0.1` / `v0.2` / ...
- Commit message 遵循 Conventional Commits（顺便也是 commit Skill 的测试场景）。

### 文档约定
- 每个迭代开始时在本文件对应章节标记 `[进行中]`，完成标记 `[已完成]`。
- 重大设计决策记录在 `docs/decisions/` 下（ADR 风格）。
