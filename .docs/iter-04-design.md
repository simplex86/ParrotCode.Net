# 迭代 4：对话历史 + 多轮上下文 — 详细设计

> 状态：[已完成]
> 对应 `plan.md` 第三章「迭代 4」，本文档在其基础上补充实现级细节与可执行的验收清单。
> 前置：迭代 3 已交付 `IBaseProvider`（含 `ChatAsync` + `ChatStreamAsync`）/ `OpenAIProvider`（SSE 流式）/ `MockProvider`（流式）/ `ProviderException` 异常层次 / `Message` + `MessageRole` + `ToolCall` 类型。本迭代在其上引入对话历史管理，让 AI 能跨轮次记住上下文。

## 一、概述

迭代 3 交付了流式 LLM 调用，但 App 主循环每轮只构造**单元素** `Message` 列表发给 LLM——AI 无法记得前一轮说了什么。本迭代引入 `ConversationHistory` 管理多轮消息列表，让每次调用都带上完整上下文：

1. **ConversationHistory**：维护 `List<Message>`，提供 `AddUser` / `AddAssistant` / `AddTool` / `ToProviderMessages` / `Clear` 方法。**不含** system prompt（由后续迭代的 PromptBuilder 在调用前拼装）。
2. **TokenEstimator**：粗略 token 估算（字符数 / 3），为迭代 9 的上下文压缩打基础。本迭代仅做估算与日志展示，不触发任何自动压缩。
3. **MessageExtensions**：消息相关的扩展方法——`MessageRole` 到 OpenAI 角色字符串的映射 + token 估算的便捷扩展。将 OpenAIProvider 中内联的角色映射提取为可复用方法，为后续 AnthropicProvider 的角色映射建立模式。
4. **App 多轮集成**：主循环改为维护 `ConversationHistory` 实例，每轮将完整历史发给 Provider；流式输出时用 `StringBuilder` 收集完整回复，结束后追加到历史。
5. **`/clear` 命令**：最小实现——主循环中字符串匹配 `/clear` 即清空历史。完整的斜杠命令系统在迭代 10。

本迭代**刻意保持**：
- **不做**历史持久化（JSONL 存档 / 恢复）→ 迭代 10。本迭代为内存版，程序退出历史即丢失。
- **不做** system prompt 管理 / PromptBuilder → 后续迭代。History 只管 user / assistant / tool 消息。
- **不做**工具调用产生与执行 → 迭代 5/6。`AddTool` 方法定义但本迭代不使用；`Message.ToolCalls` 字段仍只定义不填充。
- **不做**上下文压缩 / 截断 / 摘要 → 迭代 9。TokenEstimator 仅估算并记录日志，不触发任何自动操作。
- **不做**完整斜杠命令系统（注册中心 / 解析器 / 分发器）→ 迭代 10。`/clear` 是主循环里的硬编码 `if` 判断。
- **不做**上下文窗口占比 UI 提示 → 进阶练习。核心交付仅估算 token 数并写日志。

> **拆分考量**：迭代 4 是否拆为 4a（History + TokenEstimator 类型层）+ 4b（App 多轮集成 + /clear）？
> - 不拆理由：History 脱离 App 集成是空壳——多轮上下文的核心价值体现在"连续 3 轮 AI 记得前文"这一端到端行为上。TokenEstimator + History + App 集成三者紧密耦合，拆开无法独立验收。
> - **结论**：本迭代不拆分，作为整体设计。

## 二、学习目标

1. **对话状态管理**：理解 Agent 的"记忆"本质是一个有序消息列表，每轮追加 user + assistant 消息，下次调用整体发送。体会"无状态 API + 有状态客户端"的 Agent 架构模式。
2. **角色维护**：user / assistant / tool 三种角色的语义——user 是用户输入，assistant 是 AI 回复，tool 是工具执行结果。消息顺序必须交替合理（user → assistant → user → assistant ...），否则 LLM 可能困惑。
3. **Token 估算**：理解"字符数 / 3"为何是跨语言（中英混合）的合理粗略近似——英文约 4 char/token，中文约 1-2 char/token，取 3 是折中。为迭代 9 的精确 token 计数与压缩触发打基础。
4. **流式输出与历史收集的协调**：`await foreach` 消费 token 流的同时用 `StringBuilder` 收集完整回复，流式结束后才追加到历史。理解"流式输出（给用户看）"与"完整回复（给历史存）"的分离。
5. **中性存储 + Provider 转换**：History 存协议中性的 `Message`，Provider 层负责转换为 wire format（OpenAI 的 `{role, content}` vs Anthropic 的 content block）。体会迭代 2a "协议无关抽象"的实际收益。
6. **异常时的历史一致性**：Provider 调用失败时，user 消息已在历史中但 assistant 消息未追加——理解这种"半截历史"的语义正确性（用户确实问了，AI 确实没答上）。

## 三、范围

### 3.1 本迭代包含（In Scope）

| 项 | 说明 |
| --- | --- |
| ConversationHistory | `Conversation/History.cs`：管理 `List<Message>`，提供 `AddUser` / `AddAssistant` / `AddTool` / `ToProviderMessages` / `Clear` / `Count` / `EstimatedTokens` |
| TokenEstimator | `Conversation/TokenEstimator.cs`：静态方法 `Estimate(string)` / `Estimate(Message)` / `Estimate(IReadOnlyList<Message>)`，公式为字符数 / 3（向上取整） |
| MessageExtensions | `Conversation/MessageExtensions.cs`：`ToOpenAiRoleString(this MessageRole)` 角色映射 + `EstimateTokens` 便捷扩展 |
| OpenAIProvider 重构 | `BuildRequestBody` 中的角色映射改为调用 `MessageExtensions.ToOpenAiRoleString()`，消除内联 switch |
| App 多轮集成 | 主循环维护 `ConversationHistory`；每轮 `AddUser` → 发送完整历史 → 流式收集 → `AddAssistant` |
| /clear 命令 | 主循环中 `if (line is "/clear")` 硬编码判断，清空历史并打印确认 |
| 日志增强 | 每轮结束后记录历史消息数与估算 token 数到 stderr 日志 |
| 单元测试 | `ConversationHistoryTests`（新增）+ `TokenEstimatorTests`（新增）+ `MessageExtensionsTests`（新增）+ `MockProviderTests`（补充多轮用例） |

### 3.2 本迭代不包含（Out of Scope）

- 历史持久化（JSONL 存档 / 崩溃恢复 / 会话加载）→ 迭代 10
- system prompt 管理 / PromptBuilder → 后续迭代
- 工具调用产生与执行（`AddTool` 定义但不使用）→ 迭代 5/6
- 上下文压缩 / 截断 / 摘要 / 熔断 → 迭代 9
- 完整斜杠命令系统（注册中心 / 解析器 / 分发器 / `/help` / `/mode` 等）→ 迭代 10
- 上下文窗口占比 UI 提示 / 超限警告 → 进阶练习
- 精确 token 计数（tiktoken / 分词器）→ 后续迭代（本迭代用字符数近似）
- `AnthropicProvider` 的角色映射 / 消息格式转换 → 后续迭代
- `Message` 类型新增 `ToolCallId` 字段 → 迭代 5/6（tool 消息需要关联 tool_call_id）

### 3.3 与迭代 9 的边界

TokenEstimator 在本迭代与迭代 9 都涉及，边界如下：

| 本迭代（迭代 4） | 迭代 9 |
| --- | --- |
| 粗略估算（字符数 / 3） | 可选升级为精确计数（分词器） |
| 仅估算 + 日志记录 | 根据占比触发截断 / 摘要 / 压缩 |
| 不关心上下文窗口大小 | 需要知道窗口大小（来自配置或模型映射） |
| `EstimatedTokens` 属性供查询 | `Compressor` 调用 `EstimatedTokens` 判断是否触发压缩 |

> 本迭代的 TokenEstimator 是迭代 9 压缩协调器的基础设施。设计时确保 `Estimate(IReadOnlyList<Message>)` 接口稳定，迭代 9 可直接复用或替换内部实现。

## 四、架构设计

### 4.1 模块结构（迭代 4 增量）

```
ParrotCode.Net/
├── Program.cs                 # 不变（装配不涉及 History，由 App 内部管理）
├── App/
│   └── App.cs                 # 改：维护 ConversationHistory + /clear + 流式收集回复
├── Config/
│   └── Models.cs              # 不变
├── Providers/
│   ├── IBaseProvider.cs       # 不变
│   ├── MessageTypes.cs        # 不变（Message / MessageRole / ToolCall 来自 2a）
│   ├── MockProvider.cs        # 不变
│   ├── OpenAIProvider.cs      # 改：BuildRequestBody 角色映射改用 MessageExtensions
│   ├── ProviderException.cs   # 不变
│   └── ProviderFactory.cs     # 不变
└── Conversation/              # 新增目录
    ├── History.cs             # 新增：ConversationHistory
    ├── TokenEstimator.cs      # 新增：粗略 token 估算
    └── MessageExtensions.cs   # 新增：角色映射 + token 估算扩展方法
```

> 命名空间约定沿用迭代 1/2/3：所有源文件统一 `namespace ParrotCode`，文件夹仅作物理组织。

### 4.2 调用流程（多轮）

```
┌──────────┐
│   App    │  var history = new ConversationHistory();
│ (主循环) │
└────┬─────┘
     │
     │  ┌──────────── 第一轮 ────────────┐
     ▼  │                                 │
┌──────────┐                              │
│ 读用户输入│  "我叫张三"                   │
└────┬─────┘                              │
     │ history.AddUser("我叫张三")         │
     ▼                                    │
┌────────────────┐  history.ToProviderMessages()  ┌──────────────┐
│ ConversationHistory │ ──── [User("我叫张三")] ──▶ │  Provider    │
└────────────────┘                              │ (流式调用)    │
     ▲                                    └──────┬───────┘
     │ SB.Append(token)  ◀──── "你好张三" ────────┘
     │ history.AddAssistant("你好张三")            │
     │                                    │
     │  ┌──────────── 第二轮 ────────────┐
     ▼  │                                 │
┌──────────┐                              │
│ 读用户输入│  "我叫什么？"                 │
└────┬─────┘                              │
     │ history.AddUser("我叫什么？")       │
     ▼                                    │
┌────────────────┐  history.ToProviderMessages()  ┌──────────────┐
│ ConversationHistory │ ── [User("我叫张三"),    ─▶│  Provider    │
└────────────────┘       Assistant("你好张三"),   │ (流式调用)    │
                         User("我叫什么？")]      └──────┬───────┘
     ▲                                           │
     │  ◀──── "你叫张三" ─────────────────────────┘
     │ history.AddAssistant("你叫张三")
     │
     ▼
  （第三轮 AI 仍记得前两轮内容）
```

### 4.3 关键类型设计

#### 4.3.1 `ConversationHistory`（`Conversation/History.cs`）

```csharp
namespace ParrotCode;

/// <summary>
/// 对话历史管理：维护有序消息列表，支持多轮上下文。
/// 不含 system prompt（由后续迭代的 PromptBuilder 在调用前拼装）。
/// 本迭代为内存版，持久化在迭代 10。
/// </summary>
public sealed class ConversationHistory
{
    private readonly List<Message> _messages = new();

    /// <summary>当前历史消息数（不含 system prompt）。</summary>
    public int Count => _messages.Count;

    /// <summary>估算的全部历史 token 数（字符数 / 3 近似）。</summary>
    public int EstimatedTokens => TokenEstimator.Estimate(_messages);

    /// <summary>追加 user 消息。</summary>
    public void AddUser(string content)
    {
        ArgumentNullException.ThrowIfNull(content);
        _messages.Add(new Message(MessageRole.User, content));
    }

    /// <summary>追加 assistant 消息（AI 的完整回复）。</summary>
    public void AddAssistant(string content)
    {
        ArgumentNullException.ThrowIfNull(content);
        _messages.Add(new Message(MessageRole.Assistant, content));
    }

    /// <summary>
    /// 追加 tool 消息（工具执行结果）。
    /// 本迭代定义但不使用；迭代 5/6 接入工具后启用。
    /// </summary>
    public void AddTool(string content)
    {
        ArgumentNullException.ThrowIfNull(content);
        _messages.Add(new Message(MessageRole.Tool, content));
    }

    /// <summary>
    /// 返回当前历史的快照，供 Provider 调用使用。
    /// 返回数组快照而非 live view——避免异步 Provider 调用期间历史被修改导致不一致。
    /// </summary>
    public IReadOnlyList<Message> ToProviderMessages()
    {
        return _messages.ToArray();
    }

    /// <summary>清空全部历史，重新开始对话。</summary>
    public void Clear()
    {
        _messages.Clear();
    }
}
```

> **设计要点说明**：
>
> - **不含 system prompt**：参考 MewCode 设计，system prompt 不进 History，由 PromptBuilder 在调用前拼装到消息列表头部。本迭代无 PromptBuilder，App 也不注入 system prompt——保持与迭代 3 行为一致（无 system prompt），仅多了多轮历史。
> - **`ToProviderMessages()` 返回快照**：`_messages.ToArray()` 返回数组拷贝。原因：Provider 调用是异步的，如果返回 live view（`AsReadOnly()`），调用期间历史被修改会导致 Provider 看到不一致的状态。快照保证 Provider 拿到的是调用时刻的固定视图。数组元素是 `record` 引用（不可变），数组本身是新分配的，修改不影响 History 内部状态。
> - **`AddTool(string content)` 简化签名**：本迭代无工具调用，仅定义方法形状。迭代 5/6 接入工具后，可能新增 `AddTool(string content, string toolCallId)` 重载或在 `Message` 上加 `ToolCallId` 字段——届时评估。当前 `Message(Tool, content)` 足够。
> - **`AddAssistant(string content)` 不含 ToolCalls**：本迭代 AI 回复纯文本。迭代 6 接入 ReAct 循环后，assistant 消息可能携带 `ToolCalls`，届时新增 `AddAssistant(string content, IReadOnlyList<ToolCall>? toolCalls)` 重载。
> - **`EstimatedTokens` 每次计算**：O(n) 遍历所有消息的 Content.Length。典型对话 < 100 条消息，性能可忽略。不做缓存——缓存需在 Add/Clear 时失效，增加复杂度且收益小。
> - **不实现 `IEnumerable<Message>`**：API 最小化。测试通过 `ToProviderMessages()` + `Count` 检查内容，无需枚举。
> - **`AddUser` / `AddAssistant` / `AddTool` 显式 null 检查**：`ArgumentNullException.ThrowIfNull(content)` 在方法入口守卫。C# record 的非 nullable 参数仅是编译期分析（warning），不在运行时自动抛异常——因此 Add 方法显式调用 `ThrowIfNull` 确保 `null` 传入时立即抛 `ArgumentNullException`，避免 null Content 进入历史列表。

#### 4.3.2 `TokenEstimator`（`Conversation/TokenEstimator.cs`）

```csharp
namespace ParrotCode;

/// <summary>
/// 粗略 token 估算器：字符数 / 3（向上取整）。
/// 英文约 4 char/token，中文约 1-2 char/token，取 3 是跨语言折中近似。
/// 仅供上下文占比估算与日志展示，不用于计费或精确截断（迭代 9 可能升级为分词器）。
/// </summary>
public static class TokenEstimator
{
    private const double CharsPerToken = 3.0;

    /// <summary>估算纯文本的 token 数。空字符串或 null 返回 0。</summary>
    public static int Estimate(string? text)
    {
        if (string.IsNullOrEmpty(text)) return 0;
        // 向上取整：1-3 字符算 1 token，4-6 字符算 2 tokens...
        // 比向下取整更保守（高估），利于上下文窗口管理。
        return (int)Math.Ceiling(text.Length / CharsPerToken);
    }

    /// <summary>估算单条消息的 token 数（仅 Content，不含 role 开销）。</summary>
    public static int Estimate(Message message)
    {
        return Estimate(message.Content);
    }

    /// <summary>估算消息列表的总 token 数（仅 Content 之和，不含 role / 格式开销）。</summary>
    public static int Estimate(IReadOnlyList<Message> messages)
    {
        var total = 0;
        foreach (var msg in messages)
        {
            total += Estimate(msg);
        }
        return total;
    }
}
```

> **设计要点说明**：
>
> - **向上取整**：`(int)Math.Ceiling(length / 3.0)` 让 1-3 字符算 1 token。比向下取整（`length / 3` 整数除法会让 1-2 字符算 0 token）更合理——单字符也占至少 1 token。等价的整数写法为 `(length + 2) / 3`，用 `Math.Ceiling` 更直观。
> - **仅 Content 不含 role 开销**：OpenAI 实际计费中每条消息有约 4 token 的 role/content 包装开销。本迭代不计入——"粗略估算"的定位是判断上下文占比量级（10% vs 70% vs 90%），差几 token 无关紧要。迭代 9 如需精确可补上。
> - **`static` 类**：纯函数无状态，`static` 最自然。不做成实例类或注入接口——本迭代不需要 mock token 估算（测试直接验证公式）。
> - **`IReadOnlyList<Message>` 而非 `IEnumerable<Message>`**：与 `IBaseProvider.ChatAsync` / `ChatStreamAsync` 的入参类型一致。`IReadOnlyList` 有 `Count` 且允许索引访问，比 `IEnumerable` 语义更明确。

#### 4.3.3 `MessageExtensions`（`Conversation/MessageExtensions.cs`）

```csharp
namespace ParrotCode;

/// <summary>
/// 消息相关扩展方法：Provider 角色映射 + token 估算便捷方法。
/// 将 OpenAIProvider 中内联的角色映射提取为可复用方法，为后续 AnthropicProvider 建立模式。
/// </summary>
public static class MessageExtensions
{
    /// <summary>
    /// 将 MessageRole 映射为 OpenAI 协议的角色字符串。
    /// OpenAI / DeepSeek 等 OpenAI 兼容服务通用。
    /// </summary>
    public static string ToOpenAiRoleString(this MessageRole role) => role switch
    {
        MessageRole.System => "system",
        MessageRole.User => "user",
        MessageRole.Assistant => "assistant",
        MessageRole.Tool => "tool",
        _ => "user"  // 未知角色兜底为 user，不抛异常（容错优先）
    };

    /// <summary>估算单条消息的 token 数（便捷扩展，委托 TokenEstimator）。</summary>
    public static int EstimateTokens(this Message message) => TokenEstimator.Estimate(message);

    /// <summary>估算消息列表的总 token 数（便捷扩展，委托 TokenEstimator）。</summary>
    public static int EstimateTokens(this IReadOnlyList<Message> messages) => TokenEstimator.Estimate(messages);
}
```

> **设计要点说明**：
>
> - **`ToOpenAiRoleString` 提取动机**：迭代 3 的 `OpenAIProvider.BuildRequestBody` 内联了这个 switch。提取到扩展方法后：
>   1. OpenAIProvider 的 `BuildRequestBody` 更简洁（一行 `m.Role.ToOpenAiRoleString()`）。
>   2. 未来 `AnthropicProvider` 可在同文件加 `ToAnthropicRoleString`，角色映射集中管理。
>   3. 可独立单元测试角色映射，不依赖 HTTP mock。
> - **未知角色兜底 `user`**：与迭代 3 OpenAIProvider 的内联 switch 行为一致（`_ => "user"`）。不抛异常——容错优先，让对话继续而非崩溃。
> - **`EstimateTokens` 扩展方法**：纯便捷语法糖，让调用方写 `message.EstimateTokens()` 而非 `TokenEstimator.Estimate(message)`。委托实现，不引入额外逻辑。
> - **`public` 而非 `internal`**：角色映射与 token 估算可能被其他模块（如迭代 9 的 Compressor、迭代 7 的 TUI 状态栏）使用。`public` 避免后续迭代改可见性。

#### 4.3.4 `OpenAIProvider` 重构（`Providers/OpenAIProvider.cs`）

`BuildRequestBody` 中的角色映射从内联 switch 改为调用扩展方法：

```csharp
// —— 迭代 3 原实现 ——
private string BuildRequestBody(IReadOnlyList<Message> messages, bool stream)
{
    var msgArray = messages.Select(m => new
    {
        role = m.Role switch
        {
            MessageRole.System => "system",
            MessageRole.User => "user",
            MessageRole.Assistant => "assistant",
            MessageRole.Tool => "tool",
            _ => "user"
        },
        content = m.Content
    });
    // ...
}

// —— 迭代 4 重构后 ——
private string BuildRequestBody(IReadOnlyList<Message> messages, bool stream)
{
    var msgArray = messages.Select(m => new
    {
        role = m.Role.ToOpenAiRoleString(),
        content = m.Content
    });
    // ...
}
```

> - 纯重构，行为不变。角色映射逻辑搬到 `MessageExtensions.ToOpenAiRoleString`，`BuildRequestBody` 更简洁。
> - 迭代 3 的 `OpenAIProviderTests` 全部回归通过（角色映射行为不变，只是调用路径变了）。

#### 4.3.5 `App` 多轮集成（`App/App.cs`）

```csharp
using System.Text;
using Microsoft.Extensions.Logging;
using Spectre.Console;

namespace ParrotCode;

internal sealed class App(IBaseProvider provider, ProviderConfig providerConfig,
    ILogger logger, CancellationToken ct)
{
    public async Task RunAsync()
    {
        var history = new ConversationHistory();

        AnsiConsole.MarkupLine(
            $"[grey]ParrotCode.Net[/] {(providerConfig.Protocol == "mock"
                ? "[green]mock 模式[/]"
                : "[green]stream 模式[/]")} | " +
            $"provider=[cyan]{Markup.Escape(providerConfig.Name)}[/] " +
            $"model=[cyan]{Markup.Escape(providerConfig.Model)}[/] " +
            $"protocol=[cyan]{Markup.Escape(providerConfig.Protocol)}[/]");

        while (!ct.IsCancellationRequested)
        {
            AnsiConsole.Markup("[bold blue]> [/]");
            var line = Console.ReadLine();
            if (line is null) break;  // EOF

            if (line is "exit" or "quit") break;

            // /clear：清空对话历史（最小实现，完整命令系统在迭代 10）
            if (line is "/clear")
            {
                history.Clear();
                AnsiConsole.MarkupLine("[grey]已清空对话历史。[/]");
                continue;
            }

            if (string.IsNullOrWhiteSpace(line)) continue;

            AnsiConsole.MarkupLine($"[grey]你：[/]{Markup.Escape(line)}");
            logger.LogInformation("调用 provider（流式），输入长度 {Len}", line.Length);

            // 先追加 user 消息到历史，再发送完整历史给 Provider
            history.AddUser(line);
            var messages = history.ToProviderMessages();

            try
            {
                AnsiConsole.Markup("[green]AI：[/]");
                var replyBuilder = new StringBuilder();
                await foreach (var token in provider.ChatStreamAsync(messages, ct))
                {
                    Console.Write(token);
                    replyBuilder.Append(token);
                }
                Console.WriteLine();

                // 流式正常结束后，追加完整 assistant 回复到历史
                history.AddAssistant(replyBuilder.ToString());
                logger.LogInformation("本轮结束，历史 {Count} 条消息，约 {Tokens} tokens",
                    history.Count, history.EstimatedTokens);
            }
            catch (OperationCanceledException)
            {
                AnsiConsole.MarkupLine("\n[grey]已取消。[/]");
                break;
            }
            catch (ProviderAuthException ex)
            {
                AnsiConsole.MarkupLine($"[red]认证失败：[/]{Markup.Escape(ex.Message)}");
                AnsiConsole.MarkupLine("[grey]请检查 api_key 配置。[/]");
                // user 消息已在历史中，但无 assistant 回复——语义正确（问了但没答上）
            }
            catch (ProviderRateLimitException ex)
            {
                AnsiConsole.MarkupLine($"[yellow]请求过快：[/]{Markup.Escape(ex.Message)}");
                AnsiConsole.MarkupLine("[grey]请稍后重试。[/]");
            }
            catch (ProviderException ex)
            {
                AnsiConsole.MarkupLine($"[red]Provider 错误：[/]{Markup.Escape(ex.Message)}");
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "provider 调用失败");
                AnsiConsole.MarkupLine($"[red]错误：[/]{Markup.Escape(ex.Message)}");
            }
        }

        logger.LogInformation("程序退出");
    }
}
```

> **多轮集成说明**：
>
> - **`history` 生命周期**：在 `RunAsync` 内创建，随 App 生命周期存在。Program 不感知 History——它是 App 的内部状态，不暴露到装配层。
> - **user 消息先入历史再调用**：`history.AddUser(line)` 在 Provider 调用前执行。即使调用失败，user 消息仍在历史中——这反映真实情况（用户确实问了）。失败时不追加 assistant 回复，历史中留下"有问无答"的记录，语义正确。
> - **`StringBuilder` 收集回复**：`await foreach` 消费 token 流时同步追加到 `StringBuilder`。流式正常结束后 `history.AddAssistant(replyBuilder.ToString())` 追加完整回复。**只在 foreach 正常结束后追加**——如果中途抛异常，`AddAssistant` 不执行，历史中无残缺回复。
> - **`ToProviderMessages()` 快照**：在 `AddUser` 之后、Provider 调用之前取快照。Provider 拿到的是调用时刻的固定消息列表，不受后续 `AddAssistant` 影响。
> - **`/clear` 实现**：`if (line is "/clear")` 硬编码字符串匹配。大小写敏感、无参数解析——`/clear` 精确匹配才生效，`/CLEAR` 或 `/clear all` 不匹配（后者会作为普通输入发给 LLM）。完整命令系统在迭代 10 替换此硬编码。
> - **日志增强**：每轮正常结束后记录 `历史 {Count} 条消息，约 {Tokens} tokens`。用于调试多轮上下文是否正确累积，以及观察 token 增长趋势。写 stderr（Logger 默认），不污染 stdout。
> - **异常时历史处理**：Provider 调用失败时，`AddAssistant` 不执行。下一轮用户输入时，历史中上一条是 user（无配对 assistant）+ 新 user。OpenAI API 接受连续 user 消息，模型能理解这是"上一轮没答上，用户继续问"。

### 4.4 `/clear` 命令处理

本迭代的 `/clear` 是最小实现，不引入命令系统基础设施：

| 方面 | 本迭代（最小实现） | 迭代 10（完整命令系统） |
| --- | --- | --- |
| 匹配方式 | `if (line is "/clear")` 精确匹配 | `CommandParser` 解析 `/name args` |
| 大小写 | 敏感（仅 `/clear`） | 可能不敏感（待设计） |
| 参数 | 不支持（`/clear all` 不匹配） | `Parser` 拆分 name + args |
| 别名 | 无 | `Registry` 注册别名 |
| 帮助 | 无 | `/help` 列出所有命令 |
| 可扩展 | 硬编码，加命令改 App | `ICommand` 实现类自动注册 |

> 本迭代的 `/clear` 验收标准是"能清空历史重新开始"。硬编码 `if` 足够验证。迭代 10 引入命令系统时，`/clear` 迁移为 `ClearCommand : ICommand`，App 中的硬编码 `if` 删除。

### 4.5 多轮上下文与流式输出的协调

迭代 3 的流式输出是"单次请求 → 逐字打印 → 结束"。迭代 4 需在此基础上收集完整回复存入历史，关键协调点：

```
┌─ AddUser(line) ──────────────────────────────────────────────┐
│                                                               │
│  ToProviderMessages() → 快照 [User1, Assistant1, User2, ...]  │
│                                                               │
│  ┌─ await foreach (token in ChatStreamAsync) ──────────────┐ │
│  │  Console.Write(token)   ← 逐字打印给用户看               │ │
│  │  SB.Append(token)       ← 同步收集完整回复               │ │
│  └──────────────────────────────────────────────────────────┘ │
│                          │ foreach 正常结束                   │
│                          ▼                                     │
│  AddAssistant(SB.ToString())  ← 完整回复入历史                 │
│                                                               │
│  ┌─ 异常分支 ──────────────────────────────────────────────┐  │
│  │  catch: 不执行 AddAssistant                              │  │
│  │  → 历史中 User 消息在，无配对 Assistant                  │  │
│  │  → 语义正确：问了但没答上                                │  │
│  └──────────────────────────────────────────────────────────┘  │
└───────────────────────────────────────────────────────────────┘
```

**关键不变式**：
1. `AddAssistant` 只在 `await foreach` **正常结束**后执行——异常中途退出不追加残缺回复。
2. `ToProviderMessages()` 在 `AddUser` 之后取快照——Provider 不会看到本轮的 assistant 回复（它还没生成）。
3. `Console.Write` 与 `SB.Append` 在同一个 `await foreach` 循环内——打印与收集同步，无时序问题。

## 五、依赖变更

**无新增 NuGet 依赖。**

- `StringBuilder` / `List<T>` / `Math.Ceiling` 均为 .NET BCL 内置。
- `Spectre.Console` / `Microsoft.Extensions.Logging.Console` 已在迭代 1/2b 引入。

`ParrotCode.Net.csproj` / `ParrotCode.Net-xUnit.csproj`：**不变**。

> 与迭代 3 一致，零新依赖，纯代码实现。

## 六、配置文件

**无变化。** `example.parrotcode.yaml` / `.parrotcode.yaml` / `Config/Models.cs` 均不改。

- ConversationHistory 是 App 内部状态，不需要配置。
- TokenEstimator 是静态工具，不需要配置。
- `/clear` 是硬编码字符串，不需要配置。
- 上下文窗口大小配置（进阶练习的占比提示需要）留给进阶练习或迭代 9——本迭代不引入。

## 七、迁移说明（迭代 3 → 迭代 4）

| 迭代 3 | 迭代 4 | 处理 |
| --- | --- | --- |
| App 每轮 `new[] { Message(User, line) }` | App 维护 `ConversationHistory`，每轮 `ToProviderMessages()` | 多轮上下文 |
| App 流式输出不收集回复 | `StringBuilder` 收集 + `AddAssistant` | 完整回复入历史 |
| App 无 `/clear` | `if (line is "/clear")` 硬编码 | 最小命令 |
| `OpenAIProvider.BuildRequestBody` 内联 switch 角色映射 | 改用 `m.Role.ToOpenAiRoleString()` | 提取到 MessageExtensions |
| 无 `Conversation/` 目录 | 新增 `History.cs` / `TokenEstimator.cs` / `MessageExtensions.cs` | 新模块 |
| App 无历史日志 | 每轮记录 `历史 {Count} 条消息，约 {Tokens} tokens` | 调试可观测性 |

迁移后回归不变式：
- `active_provider: mock` 时，第一轮输入 `你好` → 逐字输出 `你好（mock）`（与迭代 3 一致）。
- 第二轮输入 `再见` → 逐字输出 `再见（mock）`（MockProvider 仍取最后一条 user 回显，历史不影响 mock 行为）。
- `/clear` 后历史清空，后续对话不受之前历史影响。
- Ctrl+C 仍干净退出。
- `exit` / `quit` / EOF 退出行为与迭代 3 一致。

> **MockProvider 多轮行为说明**：MockProvider 取 `messages.LastOrDefault(m => m.Role == User)` 回显，不受历史中其他消息影响。因此 mock 模式下多轮对话的每轮输出与单轮一致——这是 mock 的设计语义（固定回显最后一条 user），不反映"记忆"能力。"记忆"能力的验收依赖真实 LLM（DeepSeek）。

## 八、单元测试

### 8.1 `ConversationHistoryTests`（新增）

| 用例 | 操作 | 期望 |
| --- | --- | --- |
| `AddUser_IncrementsCount` | `AddUser("hello")` | `Count == 1` |
| `AddAssistant_IncrementsCount` | `AddAssistant("hi")` | `Count == 1` |
| `AddTool_IncrementsCount` | `AddTool("result")` | `Count == 1` |
| `AddUser_StoresUserMessage` | `AddUser("hello")` → `ToProviderMessages()` | 消息 Role=User, Content="hello" |
| `AddAssistant_StoresAssistantMessage` | `AddAssistant("hi")` → `ToProviderMessages()` | 消息 Role=Assistant, Content="hi" |
| `AddTool_StoresToolMessage` | `AddTool("result")` → `ToProviderMessages()` | 消息 Role=Tool, Content="result" |
| `MultipleAdds_MaintainOrder` | AddUser → AddAssistant → AddUser → `ToProviderMessages()` | 顺序为 [User, Assistant, User] |
| `Clear_EmptiesHistory` | AddUser + AddAssistant → `Clear()` | `Count == 0`，`ToProviderMessages()` 为空 |
| `Clear_OnEmptyHistory_NoOp` | `Clear()` on empty | `Count == 0`，不抛异常 |
| `ToProviderMessages_ReturnsSnapshot` | AddUser → 取快照 → AddAssistant | 快照只有 1 条（不含后加的 Assistant） |
| `ToProviderMessages_ModifyingReturnDoesNotAffectHistory` | AddUser → 取快照 → 修改返回的数组 | History 内部不变（`Count` 仍 1） |
| `ToProviderMessages_OnEmptyHistory_ReturnsEmpty` | 空 History → `ToProviderMessages()` | 空列表 |
| `EstimatedTokens_ZeroOnEmpty` | 空 History | `EstimatedTokens == 0` |
| `EstimatedTokens_IncreasesWithMessages` | AddUser("abc") → 记 token → AddUser("def") | token 数增加 |
| `EstimatedTokens_AfterClear_ReturnsZero` | AddUser → `Clear()` | `EstimatedTokens == 0` |
| `AddUser_EmptyContent_StoresEmptyMessage` | `AddUser("")` | `Count == 1`，Content="" |
| `AddUser_NullContent_Throws` | `AddUser(null!)` | 抛 `ArgumentNullException`（record 主构造器天然校验） |
| `RepeatedAdds_AccumulateCorrectly` | 3×AddUser + 2×AddAssistant | `Count == 5` |

> **快照验证要点**：`ToProviderMessages_ReturnsSnapshot` 验证返回的是快照而非 live view——取快照后再 Add，快照内容不变。这保证异步 Provider 调用期间历史修改不影响已传出的消息列表。
>
> **Null 内容处理**：`AddUser` / `AddAssistant` / `AddTool` 方法入口调用 `ArgumentNullException.ThrowIfNull(content)` 显式守卫。C# record 的非 nullable 参数仅编译期 warning，不在运行时自动抛异常——因此 Add 方法显式调用 `ThrowIfNull` 确保 null 传入时立即抛 `ArgumentNullException`。测试验证这一边界行为。

### 8.2 `TokenEstimatorTests`（新增）

| 用例 | 输入 | 期望 |
| --- | --- | --- |
| `Estimate_EmptyString_ReturnsZero` | `""` | 0 |
| `Estimate_NullString_ReturnsZero` | `null` | 0 |
| `Estimate_SingleChar_ReturnsOne` | `"a"` | 1（向上取整：1/3 → 1） |
| `Estimate_ThreeChars_ReturnsOne` | `"abc"` | 1（3/3 = 1） |
| `Estimate_FourChars_ReturnsTwo` | `"abcd"` | 2（4/3 → 向上取整 2） |
| `Estimate_ChineseChars` | `"你好"` | 1（2 字符 → 2/3 → 向上取整 1） |
| `Estimate_LongChineseText` | `"你好世界测试"` (6 字符) | 2（6/3 = 2） |
| `Estimate_MixedChineseEnglish` | `"hello你好"` (7 字符) | 3（7/3 → 向上取整 3） |
| `Estimate_SingleMessage` | `Message(User, "abc")` | 1 |
| `Estimate_MessageWithEmptyContent` | `Message(User, "")` | 0 |
| `Estimate_MessageList_SumsAll` | [Message(User,"abc"), Message(Assistant,"def")] | 2（1+1） |
| `Estimate_EmptyMessageList_ReturnsZero` | `[]` | 0 |
| `Estimate_MessageList_WithEmptyContentMessage` | [Message(User,"")] | 0 |
| `Estimate_LongText_GrowthPattern` | 3/6/9/12 字符 | 1/2/3/4（线性增长） |

> **估算公式验证**：`(int)Math.Ceiling(length / 3.0)`。测试覆盖边界：1 字符→1、3 字符→1、4 字符→2。验证向上取整行为。
>
> **中文验证**：C# `string.Length` 对中文返回 Unicode 字符数（非 UTF-8 字节数）。`"你好".Length == 2`。因此中文 token 估算 = 字符数 / 3，与英文同公式——这是"粗略近似"的体现（实际中文 token 率高于此估计，但量级可用）。

### 8.3 `MessageExtensionsTests`（新增）

| 用例 | 操作 | 期望 |
| --- | --- | --- |
| `ToOpenAiRoleString_System` | `MessageRole.System.ToOpenAiRoleString()` | `"system"` |
| `ToOpenAiRoleString_User` | `MessageRole.User.ToOpenAiRoleString()` | `"user"` |
| `ToOpenAiRoleString_Assistant` | `MessageRole.Assistant.ToOpenAiRoleString()` | `"assistant"` |
| `ToOpenAiRoleString_Tool` | `MessageRole.Tool.ToOpenAiRoleString()` | `"tool"` |
| `ToOpenAiRoleString_UnknownEnum_FallsBackToUser` | `(MessageRole)999.ToOpenAiRoleString()` | `"user"` |
| `EstimateTokens_OnMessage_DelegatesToEstimator` | `Message(User, "abc").EstimateTokens()` | 1（同 `TokenEstimator.Estimate`） |
| `EstimateTokens_OnMessageList_DelegatesToEstimator` | `[Message(User,"abc"), Message(Assistant,"def")].EstimateTokens()` | 2 |

### 8.4 `MockProviderTests`（补充多轮用例）

迭代 2a/2b/3 既有用例全部保留。补充多轮历史相关用例：

| 新用例 | 操作 | 期望 |
| --- | --- | --- |
| `ChatStreamAsync_WithMultiTurnHistory_EchoesLastUser` | 历史含 [User("第一"), Assistant("第一回复"), User("第二")] | 产出 `第二（mock）` |
| `ChatStreamAsync_WithFullMultiTurnHistory_EchoesLastUser` | 历史含 3 轮完整对话 + 第 4 轮 User | 产出第 4 轮 User 回显 |
| `ChatAsync_WithMultiTurnHistory_EchoesLastUser` | 同上但非流式 | 同上 |

> - MockProvider 的多轮行为：始终取最后一条 user 消息回显，历史中其他消息不影响。这验证了"多轮历史正确传递给 Provider"——如果历史传递有误（如只传了最后一条），MockProvider 仍能回显，但真实 LLM 会丢失上下文。因此多轮"记忆"的最终验收依赖真实 LLM（见 §9.4）。
> - 这些用例主要验证"ConversationHistory.ToProviderMessages() 的输出能被 Provider 正确消费"。

### 8.5 `OpenAIProviderTests`（回归验证）

迭代 3 既有用例全部保留，**不新增**。重构 `BuildRequestBody` 角色映射后，需确认：

| 回归点 | 期望 |
| --- | --- |
| 流式正常输出（3 chunk + DONE） | 产出 3 token（行为不变） |
| 非流式正常输出 | 返回完整回复（行为不变） |
| 请求体 role 字段正确 | `system`/`user`/`assistant`/`tool` 映射不变 |

> 角色映射从内联 switch 改为扩展方法调用，行为应完全一致。迭代 3 的 `OpenAIProviderTests` 全绿即证明无回归。

### 8.6 回归

- `dotnet test` 全绿（含迭代 1/2a/2b/3 既有 + 迭代 4 新增）。
- `dotnet run`（`active_provider: mock`）手测：连续输入 3 轮 → 每轮输出 `{输入}（mock）` → `/clear` → 继续输入正常。

## 九、验收标准

> 全部满足方可标记 `[已完成]`。每条尽量可被命令或肉眼验证。

### 9.1 构建与运行

- [ ] `ParrotCode.Net.csproj` 无改动（零新依赖）。
- [ ] `dotnet build` 无 error、无 warning。
- [ ] `dotnet test` 全绿（含 `ConversationHistoryTests` + `TokenEstimatorTests` + `MessageExtensionsTests` 新增 + `MockProviderTests` 补充 + 迭代 1/2/3 既有）。
- [ ] `dotnet run`（`active_provider: mock`）能启动，启动横幅与迭代 3 一致。

### 9.2 ConversationHistory

- [ ] `Conversation/History.cs` 定义 `ConversationHistory` 类。
- [ ] `AddUser(string)` 追加 `Message(User, content)`，`Count` 递增。
- [ ] `AddAssistant(string)` 追加 `Message(Assistant, content)`，`Count` 递增。
- [ ] `AddTool(string)` 追加 `Message(Tool, content)`，`Count` 递增。
- [ ] `ToProviderMessages()` 返回 `IReadOnlyList<Message>`，内容与添加顺序一致。
- [ ] `ToProviderMessages()` 返回快照——取快照后再 Add，快照内容不变。
- [ ] `Clear()` 清空历史，`Count` 归 0，`ToProviderMessages()` 返回空列表。
- [ ] `Clear()` 对空历史不抛异常。
- [ ] `EstimatedTokens` 反映当前历史的 token 估算值。
- [ ] `AddUser(null!)` 抛 `ArgumentNullException`。
- [ ] `ConversationHistory` **不含** system prompt（无 `AddSystem` 方法）。

### 9.3 TokenEstimator

- [ ] `Conversation/TokenEstimator.cs` 定义 `TokenEstimator` 静态类。
- [ ] `Estimate("")` 返回 0。
- [ ] `Estimate("a")` 返回 1（向上取整）。
- [ ] `Estimate("abc")` 返回 1（3/3=1）。
- [ ] `Estimate("abcd")` 返回 2（4/3 向上取整）。
- [ ] `Estimate(Message)` 等于 `Estimate(message.Content)`。
- [ ] `Estimate(IReadOnlyList<Message>)` 返回各消息 token 之和。
- [ ] 空消息列表返回 0。

### 9.4 多轮对话（核心验收）

- [ ] **mock 模式**：连续输入 3 轮（如 `你好` / `今天天气` / `再见`），每轮输出 `{输入}（mock）`，不崩溃。
- [ ] **DeepSeek 真实模式**（需 `DEEPSEEK_API_KEY`）：
  - 第 1 轮输入 `我叫张三，今年 25 岁` → AI 正常回复。
  - 第 2 轮输入 `我叫什么名字？` → AI 回答 `张三`（证明记得第 1 轮）。
  - 第 3 轮输入 `我今年多大？` → AI 回答 `25`（证明记得第 1 轮）。
- [ ] stderr 日志每轮显示 `历史 N 条消息，约 M tokens`，N 和 M 随轮次递增。
- [ ] 流式输出正常（逐字打印），非一次性输出整段。

### 9.5 `/clear` 命令

- [ ] 输入 `/clear` → 打印 `已清空对话历史。`，历史清空。
- [ ] `/clear` 后继续对话，AI 不记得 `/clear` 之前的内容（DeepSeek 验证）。
- [ ] 空历史时 `/clear` 不抛异常，仍打印确认。
- [ ] `/clear` 后 stderr 日志的 token 数归 0（下一轮结束后）。
- [ ] `/CLEAR`（大写）不触发清空，作为普通输入发给 LLM（大小写敏感，符合最小实现）。
- [ ] `/clear` 不调用 Provider（不产生 LLM 请求）。

### 9.6 异常处理与历史一致性

- [ ] 401 错误（key 无效）→ 打印 `认证失败：...`，**主循环继续**。
- [ ] 401 后输入新消息，历史中包含上一条 user（无配对 assistant）+ 新 user，LLM 正常回复（不崩溃）。
- [ ] 429 错误 → 打印 `请求过快：...`，主循环继续，历史同上。
- [ ] 5xx 错误 → 打印 `Provider 错误：...`，主循环继续。
- [ ] 网络断开 → 打印 `Provider 错误：无法连接到 ...`，主循环继续。
- [ ] 流式中途 Ctrl+C → 打印 `已取消。` 并退出，历史中无残缺 assistant 回复。
- [ ] Provider 异常后 `dotnet run` 不崩溃，下一轮正常工作。

### 9.7 敏感信息

- [ ] stderr 日志**不**出现 ApiKey 明文。
- [ ] 历史日志（`历史 N 条消息，约 M tokens`）**不**包含消息内容（只记数量与 token 数）。
- [ ] `/clear` 确认信息不泄露历史内容。

### 9.8 迁移与回归

- [ ] `IBaseProvider` 接口**不变**（迭代 3 的 `ChatAsync` + `ChatStreamAsync`）。
- [ ] `MockProvider` **不变**（迭代 3 行为保持）。
- [ ] `OpenAIProvider.BuildRequestBody` 角色映射改用 `ToOpenAiRoleString()`，行为不变。
- [ ] 迭代 3 的 `OpenAIProviderTests` 全绿（无回归）。
- [ ] `Program.cs` **不变**（History 由 App 内部管理，装配层不感知）。
- [ ] `Config/Models.cs` / `Config/Loader.cs` **不变**。
- [ ] `dotnet run`（mock）第一轮输入 `你好` → `你好（mock）`（回归不变式）。
- [ ] `exit` / `quit` / EOF / Ctrl+C 退出行为与迭代 3 一致。
- [ ] 日志/输出分离保持：`out.txt` 含流式输出不含日志，`err.txt` 含日志（含 `历史 N 条消息...`）不含回复正文。

### 9.9 跨平台

- [ ] Windows 上 `dotnet run`（mock + DeepSeek）多轮对话正常。
- [ ] macOS / Linux 上 `dotnet run`（mock + DeepSeek）多轮对话正常。
- [ ] `/clear` 在三个平台行为一致。

## 十、进阶练习（可选，不计入验收）

1. **上下文窗口占比提示**：在每轮结束后显示 `上下文：约 M tokens / N tokens (X%)`。需要知道上下文窗口大小——可硬编码常见模型（`deepseek-chat` = 64K、`gpt-4o-mini` = 128K），或给 `ProviderConfig` 加 `context_window` 可选字段。超过 70% 给黄色警告，超过 90% 给红色警告（为迭代 9 预热）。

2. **`/history` 命令**：打印当前历史的消息数与估算 token 数（最小实现：`if (line is "/history")` 硬编码）。可附带每条消息的 role + 内容前 20 字符预览。

3. **精确 token 计数**：引入 `Microsoft.ML.Tokenizers` 或手写 BPE 分词器，替换 `TokenEstimator` 的字符数近似。对比粗略估算与精确计数的差异（尤其在中文场景下）。

4. **历史消息展示**：`/show` 命令打印完整历史（role + content），用于调试多轮上下文是否正确累积。可加颜色区分 user（蓝）/ assistant（绿）/ tool（黄）。

5. **最大历史轮数限制**：当历史超过 N 轮（如 20 轮）时，自动丢弃最早的消息（FIFO 截断）。注意：不能截断到"半个工具调用"（tool_call 无配对 tool_result）——本迭代无工具所以无此问题，但为迭代 5/6 预热。

6. **system prompt 注入**：在 App 中加一个简单的 system prompt（如 `你是 ParrotCode.Net 的 AI 助手`），通过 `new[] { Message(System, prompt) }.Concat(history.ToProviderMessages())` 拼装。体会"History 不含 system prompt，调用前拼装"的设计。

## 十一、风险与注意事项

| 风险 | 缓解 |
| --- | --- |
| `ToProviderMessages()` 返回 live view 导致异步调用期间不一致 | 返回 `_messages.ToArray()` 快照；单测 §8.1 `ToProviderMessages_ReturnsSnapshot` 验证 |
| Provider 失败后历史中"有问无答"导致 LLM 困惑 | OpenAI API 接受连续 user 消息；模型能理解"上轮没答上"。语义正确，不做特殊处理 |
| 流式中途异常导致 `StringBuilder` 有残缺回复入历史 | `AddAssistant` 只在 `await foreach` 正常结束后执行；异常走 catch 不追加。§4.5 不变式保证 |
| `AddUser(null!)` 导致 `Message` record 持有 null Content | Add 方法入口显式调用 `ArgumentNullException.ThrowIfNull(content)` 守卫，null 传入立即抛异常。单测 §8.1 覆盖 |
| `/clear` 大小写敏感导致用户困惑 | 本迭代明确大小写敏感（最小实现）；迭代 10 命令系统可做大小写归一化。文档与验收 §9.5 明确 |
| TokenEstimator 对中文严重低估 | 6 个中文字符估算 2 token，实际约 6-12 token。但"粗略估算"定位是判断量级（10% vs 70%），差 3-6 倍不影响量级判断。迭代 9 可升级。§8.2 测试明确 |
| `EstimatedTokens` 每次 O(n) 计算影响性能 | 典型对话 < 100 条消息，O(n) 遍历 < 1μs。不做缓存——缓存失效逻辑增加复杂度，收益可忽略 |
| `OpenAIProvider` 重构破坏迭代 3 行为 | 纯角色映射提取，行为不变；迭代 3 的 `OpenAIProviderTests` 全绿即证明。§8.5 回归验证 |
| MockProvider 多轮不体现"记忆"导致验收误判 | MockProvider 设计为固定回显最后一条 user，不反映记忆。验收 §9.4 明确要求 DeepSeek 真实模式验证记忆能力 |
| 历史无限增长导致 token 爆窗 | 本迭代不做自动截断（迭代 9 负责）。日志显示 token 数供观察。进阶练习 5 可加 FIFO 截断 |
| `/clear` 误触（用户想输入 `/clear` 文本但被拦截） | `/clear` 是元命令，与 `exit`/`quit` 一致的语义。用户想发 `/clear` 文本给 LLM 的场景极少；迭代 10 可加 `/raw` 前缀转发 |
| `ConversationHistory` 不实现 IEnumerable 导致测试不便 | API 最小化原则；测试通过 `ToProviderMessages()` + `Count` 检查。如需遍历用 `ToProviderMessages()` |

## 十二、交付清单

- [ ] `ParrotCode.Net/Conversation/History.cs`（新增：`ConversationHistory`）
- [ ] `ParrotCode.Net/Conversation/TokenEstimator.cs`（新增：粗略 token 估算）
- [ ] `ParrotCode.Net/Conversation/MessageExtensions.cs`（新增：角色映射 + token 估算扩展）
- [ ] `ParrotCode.Net/Providers/OpenAIProvider.cs`（重构：`BuildRequestBody` 角色映射改用 `ToOpenAiRoleString()`）
- [ ] `ParrotCode.Net/App/App.cs`（改：维护 `ConversationHistory` + `/clear` + 流式收集回复 + 历史日志）
- [ ] `ParrotCode.Net-xUnit/ConversationHistoryTests.cs`（新增）
- [ ] `ParrotCode.Net-xUnit/TokenEstimatorTests.cs`（新增）
- [ ] `ParrotCode.Net-xUnit/MessageExtensionsTests.cs`（新增）
- [ ] `ParrotCode.Net-xUnit/MockProviderTests.cs`（补充多轮用例）
- [ ] 演示：mock 多轮 + `/clear` + DeepSeek 真实多轮（AI 记得前文）+ `dotnet test` 全绿截图
- [ ] 本文档状态改为 `[已完成]`
