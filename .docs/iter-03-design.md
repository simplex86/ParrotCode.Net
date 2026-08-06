# 迭代 3：第一个 LLM Provider（OpenAI 兼容）+ 流式输出 — 详细设计

> 状态：[已完成]
> 对应 `plan.md` 第三章「迭代 3」，本文档在其基础上补充实现级细节与可执行的验收清单。
> 前置：迭代 2a 已交付 `IBaseProvider`（非流式 `ChatAsync`）/ `Message` / `ToolCall` / `ProviderFactory` / `MockProvider`；迭代 2b 已交付 `ConfigLoader` / `AppConfig` / `ProviderConfig` / `ProviderFactory.CreateActive`。本迭代在其上接入真实 LLM 与流式输出。

## 一、概述

迭代 2 交付了协议无关的 `IBaseProvider` 抽象与配置系统，但 `openai`/`anthropic` 协议在工厂里只抛 `ProviderNotImplementedException`。本迭代把 `openai` 协议**真正实现**出来：

1. **流式抽象**：给 `IBaseProvider` 扩展 `ChatStreamAsync`，返回 `IAsyncEnumerable<string>`，让 token 逐个到达。`MockProvider` 同步实现流式版本，保证无 API key 也能跑通流式管线。
2. **OpenAIProvider**：基于 `HttpClient` 调用 `/v1/chat/completions`（`stream=true`），逐行解析 SSE，把 `choices[0].delta.content` 通过 `IAsyncEnumerable` 产出。同时覆盖 OpenAI 官方与 DeepSeek 等 OpenAI 兼容服务（由 `BaseUrl` 区分端点）。
3. **App 流式渲染**：主循环改为 `await foreach` 消费 token 流，逐字刷新到控制台。
4. **错误转译**：HTTP 401/429/5xx 转成 `ProviderException` 层次异常，App 捕获后打印友好信息而非裸堆栈。

本迭代**刻意保持**：
- **不做**多轮对话历史（迭代 4）。每次仍构造单元素 `Message` 列表发给 LLM。
- **不做**工具调用产生与执行（迭代 5/6）。`ToolCall` 类型仍只定义不使用；请求体不含 `tools` 字段。
- **不做**TUI 流式渲染（迭代 7）。本迭代用 `Console.Write` 逐字输出，功能优先于美观。
- **不做**`AnthropicProvider` 实现（可选，见进阶练习）。工厂的 `anthropic` 分支仍抛 `ProviderNotImplementedException`。
- **不做**DeepSeek `reasoning_content` 的可视化（进阶练习）。SSE 解析仅提取 `content`，`reasoning_content` 字段被忽略但不报错。

> **拆分考量**：迭代 2 拆成了 2a（抽象层）+ 2b（配置层）。迭代 3 是否拆为 3a（流式抽象 + MockProvider 流式 + App 流式渲染）+ 3b（OpenAIProvider 真实实现）？
> - 拆分好处：3a 零外部依赖、无 API key 可跑通流式管线；3b 隔离 SSE 解析复杂度。
> - 不拆理由：流式抽象脱离真实 Provider 是空壳——SSE 解析本身就是本迭代的核心学习目标；MockProvider 流式实现仅 3 行代码，独立成迭代过于单薄。
> - **结论**：本迭代不拆分，作为整体设计。若 review 时认为范围过大，可再拆。

## 二、学习目标

1. **SSE 流式解析**：理解 Server-Sent Events 协议（`data:` 前缀 + 空行分隔 + `[DONE]` 终止），掌握 `HttpClient` 流式读取 + 逐行解析的 .NET 模式。
2. **`IAsyncEnumerable<T>` 模式**：用 `yield return` 构建异步流，体会"生产者-消费者"解耦；理解它与 `IObservable<T>` / `Channel<T>` 的取舍。
3. **HttpClient 流式请求**：`HttpCompletionOption.ResponseHeadersRead` + `ReadAsStreamAsync()` 的正确用法，避免缓冲整个响应。
4. **HTTP 错误转译**：把裸 `HttpRequestException` / 状态码转成语义化异常层次，让调用方按需捕获。
5. **DeepSeek 兼容**：理解 OpenAI 兼容协议的复用——同一套 `OpenAIProvider` 通过 `BaseUrl` 覆盖不同服务商，体会"协议无关抽象"的实际收益。
6. **配置必填校验闭环**：迭代 2b 延迟的 `openai`/`anthropic` 字段必填校验，在本迭代由 Provider 构造器补齐。

## 三、范围

### 3.1 本迭代包含（In Scope）

| 项 | 说明 |
| --- | --- |
| 流式接口 | `IBaseProvider` 扩展 `ChatStreamAsync(IReadOnlyList<Message>, CancellationToken) → IAsyncEnumerable<string>` |
| MockProvider 流式 | 实现 `ChatStreamAsync`，yield 单个 token（完整回复），保证无 key 可跑 |
| Provider 异常层次 | `ProviderException`（基类）+ `ProviderAuthException`（401）+ `ProviderRateLimitException`（429）+ `ProviderServerException`（5xx）+ `ProviderRequestException`（其他 4xx / 网络错误） |
| OpenAIProvider | `HttpClient` + `/v1/chat/completions?stream=true` + SSE 解析 + `content` delta 产出 |
| 配置必填校验 | `OpenAIProvider` 构造器校验 `model`/`base_url`/`api_key` 非空，空则抛 `ConfigException` |
| ProviderFactory 路由 | `openai` 分支从抛异常改为 `new OpenAIProvider(config)`；`anthropic` 仍抛 `ProviderNotImplementedException` |
| App 流式渲染 | 主循环改用 `await foreach` + `Console.Write` 逐字输出；异常按类型友好提示 |
| Program 装配 | 流式路径；`ProviderNotImplementedException` 的 catch 保留（针对 `anthropic`） |
| 单元测试 | `OpenAIProviderTests`（`HttpMessageHandler` mock，不打真实 API）+ `MockProviderTests` 补充流式用例 + `ProviderFactoryTests` 更新 `openai` 断言 |

### 3.2 本迭代不包含（Out of Scope）

- 多轮对话历史、`ConversationHistory` → 迭代 4
- 工具调用（`tools` 请求字段、`tool_calls` 响应解析）→ 迭代 5/6
- TUI 流式渲染（Spectre `Live` / 无闪烁刷新）→ 迭代 7
- `AnthropicProvider` 实现 → 进阶练习或后续迭代
- DeepSeek `reasoning_content` 可视化 → 进阶练习
- `IHttpClientFactory` / DI 容器接入 → 后续迭代
- 请求重试 / 指数退避 → 后续迭代（本迭代 429 直接报错）
- Token 计数 / 上下文窗口管理 → 迭代 4/9

### 3.3 DeepSeek 兼容定位（延续 2a §3.3）

DeepSeek 官方 API 完全兼容 OpenAI 格式，**不引入独立协议**。本迭代落实 2a 文档的后续计划：

| 2a 计划点 | 本迭代落实 |
| --- | --- |
| `OpenAIProvider` 统一处理 OpenAI 官方与 DeepSeek | 实现 `OpenAIProvider`，通过 `BaseUrl` 区分端点 |
| `deepseek-reasoner` 响应中的 `reasoning_content` 字段 | SSE 解析仅取 `content`，`reasoning_content` 被安全忽略；可视化作为进阶练习 |
| 联调以 DeepSeek 为主 | 验收标准以 DeepSeek 为主要真实联调目标 |

> DeepSeek API 差异细节：`deepseek-chat` 与标准 OpenAI 无差异；`deepseek-reasoner` 的 delta 中多出 `reasoning_content` 字段（思考链），`content` 仍为最终输出。本迭代的 JSON 解析只读 `choices[0].delta.content`，多余字段自动忽略，因此天然兼容。

## 四、架构设计

### 4.1 模块结构（迭代 3 增量）

```
ParrotCode.Net/
├── Program.cs                 # 改：流式装配（catch 保留 ProviderNotImplementedException）
├── App/
│   └── App.cs                  # 改：await foreach 流式渲染 + 异常分类处理
├── Config/
│   └── Models.cs              # 不变（AppConfig / ProviderConfig 来自 2a/2b）
├── Providers/
│   ├── IBaseProvider.cs        # 改：追加 ChatStreamAsync
│   ├── MessageTypes.cs        # 不变（Message / ToolCall 来自 2a）
│   ├── MockProvider.cs        # 改：追加 ChatStreamAsync 实现
│   ├── ProviderFactory.cs     # 改：openai 分支 → new OpenAIProvider(config)
│   ├── ProviderException.cs   # 新增：异常层次（ProviderException + 4 子类）
│   ├── OpenAIProvider.cs      # 新增：HttpClient + SSE 解析 + 流式产出
│   └── OpenAIJsonModels.cs    # 新增：请求/响应 DTO（internal record）
```

> 命名空间约定沿用迭代 1/2：所有源文件统一 `namespace ParrotCode`，文件夹仅作物理组织。

### 4.2 调用流程（流式）

```
┌─────────┐  PARROTCODE_CONFIG / .parrotcode.yaml / ~/.parrotcode/config.yaml
│ Program │ ───────────────────────────────────────────────▶ ┌──────────────┐
│  入口   │ ◀────────────── AppConfig ──────────────────────┤ ConfigLoader │
└────┬────┘                                                  └──────────────┘
     │ ProviderFactory.CreateActive(config)
     ▼
┌──────────────────┐  ProviderConfig(protocol=openai)   ┌──────────────────┐
│ ProviderFactory  │ ─────────────────────────────────▶ │ OpenAIProvider   │ : IBaseProvider
└──────────────────┘                                     │  + HttpClient    │
     │ IBaseProvider                                      │  + SSE Parser    │
     ▼                                                    └──────────────────┘
┌──────────┐  new[]{ Message(User, line) }                     │
│   App    │ ──────────────────────────────────────────────▶   │ POST /v1/chat/completions
│ (主循环) │ ◀──── IAsyncEnumerable<string> (token 流) ────────┤ stream=true
└──────────┘                                                  │
   │ Console.Write(token) 逐字输出                             ▼
   ▼                                                    data: {"choices":[{"delta":{"content":"你"}}]}
(用户实时看到逐字刷新)                                     data: {"choices":[{"delta":{"content":"好"}}]}
                                                          data: [DONE]
```

### 4.3 关键类型设计

#### 4.3.1 `IBaseProvider` 扩展（`Providers/IBaseProvider.cs`）

```csharp
namespace ParrotCode;

public interface IBaseProvider
{
    /// <summary>
    /// 非流式聊天：返回完整回复。用于不需要实时反馈的场景（如迭代 9 摘要）。
    /// </summary>
    Task<string> ChatAsync(IReadOnlyList<Message> messages, CancellationToken cancellationToken);

    /// <summary>
    /// 流式聊天：逐个产出 token（文本片段）。
    /// token 可能是单个字符、词或一段文本——由 Provider/LLM 决定粒度，消费方不应假设。
    /// 迭代 3 仅产出文本 token；迭代 5/6 可能演进返回类型以承载 ToolCall。
    /// </summary>
    IAsyncEnumerable<string> ChatStreamAsync(IReadOnlyList<Message> messages, CancellationToken cancellationToken);
}
```

> **为什么把 `ChatStreamAsync` 放在 `IBaseProvider` 而非新建 `IStreamingProvider` 子接口？**
> - 所有真实 LLM Provider 都支持流式（OpenAI / Anthropic / DeepSeek 均原生支持），流式是 Agent 实时反馈的基石，不是可选能力。
> - 放在基接口让 App 始终走流式路径，无需 `if (provider is IStreamingProvider)` 类型判断，简化主循环。
> - MockProvider 流式实现仅 3 行（yield 完整回复），不是负担。
> - `ChatAsync` 保留：用于不需要实时反馈的场景（如迭代 9 的摘要生成），且让迭代 2b 的测试零改动通过。
>
> **为什么返回 `IAsyncEnumerable<string>` 而非 `IAsyncEnumerable<StreamChunk>`？**
> - 迭代 3 的学习目标是 SSE 文本流式，`string` 足够。
> - `ToolCall` 的流式语义更复杂（参数是 JSON 片段增量），强行预留形状可能返工。
> - 迭代 5/6 接入工具时再评估是否演进为 `IAsyncEnumerable<StreamChunk>`（含 `TextDelta` / `ToolCallDelta` 变体），届时所有实现一起改——但本迭代的 SSE 解析逻辑可复用。
> - 与迭代 2 的"预留 ToolCall 类型但不用"不同：此处返回类型是**接口契约**，改动影响面大，YAGNI 优先。

#### 4.3.2 `MockProvider` 流式实现（`Providers/MockProvider.cs`）

```csharp
public sealed class MockProvider : IBaseProvider
{
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
        // MockProvider 不模拟逐字延迟，一次性产出完整回复。
        // 消费方（App）的 await foreach 逻辑与真实 Provider 一致，验证流式管线正确性。
        yield return $"{content}（mock）";
        await Task.CompletedTask;
    }
}
```

> - `[EnumeratorCancellation]` 让 `CancellationToken` 在 `await foreach` 取消时正确传播到迭代器。
> - `yield return` 一次性产出——不模拟逐字延迟，避免拖慢测试。真实 Provider 的"逐字感"来自 SSE 的自然节流。
> - `ChatAsync` 保留原实现（`Task.FromResult`），不改为"聚合 stream"——保持迭代 2a 测试的同步完成语义不变（`ChatAsync_ReturnsSuccessfullyCompletedTask` 仍过）。

#### 4.3.3 `ProviderException` 异常层次（`Providers/ProviderException.cs`）

```csharp
namespace ParrotCode;

/// <summary>
/// Provider 调用异常基类。所有 HTTP / 网络错误转译为 ProviderException 层次，
/// 让调用方按语义捕获而非检查状态码或 HttpRequestException。
/// </summary>
public class ProviderException : Exception
{
    public int? StatusCode { get; }

    public ProviderException(string message, int? statusCode = null, Exception? inner = null)
        : base(message, inner)
    {
        StatusCode = statusCode;
    }
}

/// <summary>认证失败（401）：ApiKey 无效或缺失。</summary>
public sealed class ProviderAuthException : ProviderException
{
    public ProviderAuthException(string message, Exception? inner = null)
        : base(message, 401, inner) { }
}

/// <summary>速率限制（429）：请求过快。</summary>
public sealed class ProviderRateLimitException : ProviderException
{
    public ProviderRateLimitException(string message, Exception? inner = null)
        : base(message, 429, inner) { }
}

/// <summary>服务端错误（5xx）：Provider 内部故障。</summary>
public sealed class ProviderServerException : ProviderException
{
    public ProviderServerException(string message, int statusCode, Exception? inner = null)
        : base(message, statusCode, inner) { }
}

/// <summary>其他请求错误：网络故障 / 未知状态码 / SSE 解析失败。</summary>
public sealed class ProviderRequestException : ProviderException
{
    public ProviderRequestException(string message, int? statusCode = null, Exception? inner = null)
        : base(message, statusCode, inner) { }
}
```

> - App 主循环 `catch (ProviderException)` 即可覆盖所有 Provider 错误，按子类型给不同提示。
> - 状态码保留在基类，方便日志与未来重试逻辑（迭代 3 不做重试）。
> - `ProviderAuthException` / `ProviderRateLimitException` 是 App 可能给差异化提示的高频错误；5xx 归 `ProviderServerException`；其余归 `ProviderRequestException`。
> - 不为每种状态码建子类——418 之类的无关状态码归 `ProviderRequestException` 即可。

#### 4.3.4 `OpenAIProvider`（`Providers/OpenAIProvider.cs`）

```csharp
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;

namespace ParrotCode;

/// <summary>
/// OpenAI 兼容协议 Provider。通过 BaseUrl 覆盖 OpenAI 官方与 DeepSeek 等兼容服务。
/// 流式：POST /v1/chat/completions (stream=true) → SSE 逐行解析 → yield content delta。
/// </summary>
public sealed class OpenAIProvider : IBaseProvider
{
    private readonly ProviderConfig _config;
    private readonly HttpClient _httpClient;

    public OpenAIProvider(ProviderConfig config)
    {
        // 配置必填校验（迭代 2b 延迟到此处）
        if (string.IsNullOrWhiteSpace(config.Model))
            throw new ConfigException($"provider '{config.Name}' 的 model 不能为空");
        if (string.IsNullOrWhiteSpace(config.BaseUrl))
            throw new ConfigException($"provider '{config.Name}' 的 base_url 不能为空");
        if (string.IsNullOrWhiteSpace(config.ApiKey))
            throw new ConfigException($"provider '{config.Name}' 的 api_key 不能为空");

        _config = config;
        _httpClient = new HttpClient
        {
            BaseAddress = new Uri(config.BaseUrl),
            Timeout = TimeSpan.FromMinutes(5)  // 流式响应可能持续较长
        };
        _httpClient.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", config.ApiKey);
    }

    // —— 非流式 ——
    public async Task<string> ChatAsync(IReadOnlyList<Message> messages, CancellationToken cancellationToken)
    {
        var body = BuildRequestBody(messages, stream: false);
        using var response = await SendAsync(body, cancellationToken);
        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement
            .GetProperty("choices")[0]
            .GetProperty("message")
            .GetProperty("content")
            .GetString() ?? string.Empty;
    }

    // —— 流式 ——
    public async IAsyncEnumerable<string> ChatStreamAsync(
        IReadOnlyList<Message> messages,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var body = BuildRequestBody(messages, stream: true);
        using var response = await SendAsync(body, cancellationToken);

        // ResponseHeadersRead：读到响应头即返回，不缓冲整个 body（流式必需）
        using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(stream);

        while (!cancellationToken.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(cancellationToken);
            if (line is null) break;  // 流结束

            // SSE 协议：空行是事件分隔符，跳过
            if (string.IsNullOrEmpty(line)) continue;
            // 其他前缀（event: / id: / 注释 :...）本迭代不处理，跳过
            if (!line.StartsWith("data: ")) continue;

            var data = line["data: ".Length..];
            if (data == "[DONE]") break;  // OpenAI 流终止标记

            // 解析 JSON，提取 choices[0].delta.content
            using var doc = JsonDocument.Parse(data);
            var choices = doc.RootElement.GetProperty("choices");
            if (choices.GetArrayLength() == 0) continue;

            var delta = choices[0].GetProperty("delta");
            if (delta.TryGetProperty("content", out var contentEl))
            {
                var text = contentEl.GetString();
                if (!string.IsNullOrEmpty(text))
                    yield return text;
            }
            // reasoning_content（DeepSeek-reasoner）被自然忽略：只读 content
        }
    }

    // —— 内部方法 ——

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

        var request = new
        {
            model = _config.Model,
            messages = msgArray,
            stream
        };

        return JsonSerializer.Serialize(request);
    }

    private async Task<HttpResponseMessage> SendAsync(string jsonBody, CancellationToken cancellationToken)
    {
        using var content = new StringContent(jsonBody, Encoding.UTF8, "application/json");
        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/chat/completions")
        {
            Content = content
        };

        HttpResponseMessage response;
        try
        {
            // HttpCompletionOption.ResponseHeadersRead：流式请求的关键——读到头即返回
            response = await _httpClient.SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        }
        catch (HttpRequestException ex)
        {
            throw new ProviderRequestException(
                $"无法连接到 {_config.BaseUrl}：{ex.Message}", null, ex);
        }
        catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            // 非用户取消的超时
            throw new ProviderRequestException(
                $"请求超时（{_httpClient.Timeout.TotalSeconds}s）", null, ex);
        }

        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync(cancellationToken);
            var msg = FormatError((int)response.StatusCode, errorBody);
            response.Dispose();
            throw (int)response.StatusCode switch
            {
                401 => new ProviderAuthException(msg),
                429 => new ProviderRateLimitException(msg),
                >= 500 => new ProviderServerException(msg, (int)response.StatusCode),
                _ => new ProviderRequestException(msg, (int)response.StatusCode)
            };
        }

        return response;
    }

    private static string FormatError(int statusCode, string errorBody)
    {
        // 尝试提取 OpenAI 错误格式 {"error":{"message":"..."}}
        try
        {
            using var doc = JsonDocument.Parse(errorBody);
            if (doc.RootElement.TryGetProperty("error", out var errEl)
                && errEl.TryGetProperty("message", out var msgEl))
            {
                return $"HTTP {statusCode}: {msgEl.GetString()}";
            }
        }
        catch { /* 非 JSON 错误体，用原文 */ }

        return string.IsNullOrWhiteSpace(errorBody)
            ? $"HTTP {statusCode}"
            : $"HTTP {statusCode}: {errorBody}";
    }
}
```

> **设计要点说明**：
>
> - **`HttpCompletionOption.ResponseHeadersRead`**：流式请求的核心。默认 `SendAsync` 会缓冲整个响应体再返回——对 SSE 流来说要等到全部 token 到齐才返回，失去流式意义。`ResponseHeadersRead` 读到响应头即返回，响应体由 `ReadAsStreamAsync` 逐块读取。
> - **`StreamReader.ReadLineAsync`**：SSE 是行分隔协议，`ReadLineAsync` 天然按 `\n` 切行。OpenAI 的 SSE 每行一个 `data:` 或空行。
> - **`[DONE]` 终止**：OpenAI 流以 `data: [DONE]` 标记结束，DeepSeek 同。
> - **`reasoning_content` 安全忽略**：JSON 解析只取 `delta.content`，`reasoning_content` 作为多余字段被 `JsonDocument` 自然跳过。
> - **取消传播**：`[EnumeratorCancellation]` + 循环内检查 `cancellationToken.IsCancellationRequested` + `ReadLineAsync(ct)` 三重保障。
> - **非流式 `ChatAsync`**：用 `stream: false` 请求，从 `choices[0].message.content` 取完整回复。不聚合流——非流式请求更高效且语义清晰。
> - **HttpClient 生命周期**：每个 Provider 实例持有一个 `HttpClient`，App 生命周期内单例。不实现 `IDisposable`（App 退出即回收）。后续引入 `IHttpClientFactory` 时再调整。
> - **超时**：5 分钟。流式响应可能持续较长（长文本生成），默认 100s 不够。`HttpClient.Timeout` 对 `ResponseHeadersRead` 模式只约束"读到头的时间"，不影响流式读取阶段。

#### 4.3.5 `ProviderFactory` 路由更新（`Providers/ProviderFactory.cs`）

```csharp
public static class ProviderFactory
{
    public static IBaseProvider Create(ProviderConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        return config.Protocol switch
        {
            "mock" => new MockProvider(),
            // 迭代 3 实现：OpenAI 官方 + DeepSeek 等 OpenAI 兼容服务，由 BaseUrl 区分端点
            "openai" => new OpenAIProvider(config),
            // anthropic 协议暂未实现（可选进阶练习）
            "anthropic" => throw new ProviderNotImplementedException(config),
            _ => throw new ArgumentException($"不支持的协议: {config.Protocol} (provider={config.Name})")
        };
    }

    // CreateActive 不变（2b 已实现）
    public static IBaseProvider CreateActive(AppConfig appConfig) { /* 不变 */ }
}
```

> - `ProviderNotImplementedException` 的消息改为："将在后续迭代实现，本迭代支持 mock/openai。"（去掉"迭代 3"字样，因为 openai 已实现）。
> - `openai` 分支不再抛异常，`CreateActive` 选中 `openai` 协议的 provider 时正常返回 `OpenAIProvider`。
> - `Create` 可能抛 `ConfigException`（来自 `OpenAIProvider` 构造器的必填校验）。`Program` 的 `try/catch` 已有 `ConfigException` 处理（2b），无需新增。

#### 4.3.6 `App` 流式渲染（`App/App.cs`）

```csharp
internal sealed class App(IBaseProvider provider, ProviderConfig providerConfig,
    ILogger logger, CancellationToken ct)
{
    public async Task RunAsync()
    {
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
            if (line is null) break;
            if (line is "exit" or "quit") break;
            if (string.IsNullOrWhiteSpace(line)) continue;

            AnsiConsole.MarkupLine($"[grey]你：[/]{Markup.Escape(line)}");
            logger.LogInformation("调用 provider（流式），输入长度 {Len}", line.Length);

            try
            {
                var messages = new[] { new Message(MessageRole.User, line) };
                AnsiConsole.Markup("[green]AI：[/]");
                // 流式逐字输出：Console.Write 直接写 stdout，不经过 Spectre 缓冲
                await foreach (var token in provider.ChatStreamAsync(messages, ct))
                {
                    Console.Write(token);
                }
                Console.WriteLine();  // 回复结束换行
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

> **流式渲染说明**：
> - 用 `Console.Write(token)` 逐字输出而非 `AnsiConsole.Markup`——Spectre.Console 的 Markup 会缓冲整行，不适合逐 token 追加。`Console.Write` 直接写 stdout，与 Spectre 共用同一输出流。
> - token 可能含 `[` `]` 等字符——**不**做 Markup 转义（会破坏流式体验）。如果 LLM 返回含 `[red]` 的文本会显示异常，但这是流式渲染的已知限制，TUI（迭代 7）会解决。
> - `"AI："` 前缀用 `AnsiConsole.Markup` 输出（带颜色），之后切换到 `Console.Write` 逐字追加。
> - 异常分四档：取消 / 认证 / 限流 / 其他 Provider 错误，各给差异化提示。
> - 启动横幅根据 `protocol` 显示 `mock 模式` 或 `stream 模式`。

### 4.4 SSE 解析细节

#### 4.4.1 OpenAI / DeepSeek SSE 格式

```
data: {"id":"chatcmpl-xxx","object":"chat.completion.chunk","choices":[{"index":0,"delta":{"role":"assistant","content":""},"finish_reason":null}]}

data: {"id":"chatcmpl-xxx","object":"chat.completion.chunk","choices":[{"index":0,"delta":{"content":"你"},"finish_reason":null}]}

data: {"id":"chatcmpl-xxx","object":"chat.completion.chunk","choices":[{"index":0,"delta":{"content":"好"},"finish_reason":null}]}

data: {"id":"chatcmpl-xxx","object":"chat.completion.chunk","choices":[{"index":0,"delta":{},"finish_reason":"stop"}]}

data: [DONE]
```

DeepSeek-reasoner 在思考阶段多出 `reasoning_content`：

```
data: {"choices":[{"delta":{"reasoning_content":"让我想想..."}}]}

data: {"choices":[{"delta":{"content":"你好"}}]}

data: [DONE]
```

#### 4.4.2 解析规则

| 行内容 | 处理 |
| --- | --- |
| 空行 | SSE 事件分隔符，跳过 |
| `data: [DONE]` | 流终止，`break` |
| `data: {...}` | JSON 解析，提取 `choices[0].delta.content`，非空则 `yield return` |
| `data:`（无空格） | 兼容处理：跳过 `data:` 后取剩余 |
| `event:` / `id:` / `retry:` | SSE 控制行，本迭代不处理，跳过 |
| `:` 开头 | SSE 注释（心跳保活），跳过 |
| 其他 | 跳过（不报错，容错优先） |

> - `choices` 可能为空数组（OpenAI 偶尔发空 choices 的心跳包），需判 `GetArrayLength() == 0`。
> - `delta.content` 可能为 `null`（首个 chunk 通常只有 `role`）或空字符串，需判 `!string.IsNullOrEmpty`。
> - `delta` 可能不含 `content` 属性（只有 `role` 或 `finish_reason`），用 `TryGetProperty` 安全取值。

#### 4.4.3 请求体格式

```json
{
  "model": "deepseek-chat",
  "messages": [
    {"role": "user", "content": "写一首关于秋天的诗"}
  ],
  "stream": true
}
```

- 不含 `tools` / `tool_choice`（迭代 5/6）。
- 不含 `temperature` / `max_tokens` 等可选参数（本迭代用 Provider 默认值）。
- `MessageRole` 映射：System→`"system"` / User→`"user"` / Assistant→`"assistant"` / Tool→`"tool"`。

### 4.5 配置必填校验

迭代 2b §4.7 明确延迟 `openai`/`anthropic` 的 `model`/`base_url`/`api_key` 必填校验到迭代 3。本迭代由 `OpenAIProvider` 构造器补齐：

| 校验 | 错误信息 | 异常类型 |
| --- | --- | --- |
| `model` 非空 | `provider '{name}' 的 model 不能为空` | `ConfigException` |
| `base_url` 非空 | `provider '{name}' 的 base_url 不能为空` | `ConfigException` |
| `api_key` 非空 | `provider '{name}' 的 api_key 不能为空` | `ConfigException` |

> - 用 `ConfigException` 而非 `ProviderException`——这是配置问题而非运行时 HTTP 错误，复用 2b 的异常类型让 `Program` 的 `catch (ConfigException)` 统一处理。
> - `ConfigLoader.Validate` **不**增加这些校验——配置层不应耦合 Provider 特定的必填规则。工厂调 `new OpenAIProvider(config)` 时由 Provider 自己校验，职责清晰。
> - `anthropic` 协议的必填校验留到 `AnthropicProvider` 实现时（进阶练习），本迭代 `anthropic` 仍走 `ProviderNotImplementedException`。

## 五、依赖变更

**无新增 NuGet 依赖。**

- `HttpClient` / `StreamReader` / `JsonDocument` / `IAsyncEnumerable` 均为 .NET BCL 内置。
- `Spectre.Console` / `YamlDotNet` / `Microsoft.Extensions.Logging.Console` 已在迭代 1/2b 引入。

`ParrotCode.Net.csproj` / `ParrotCode.Net-xUnit.csproj`：**不变**。

> 这是迭代 3 可聚焦于 SSE 解析学习的前提：零新依赖，纯代码实现。

## 六、配置文件

### 6.1 `example.parrotcode.yaml` 不变

迭代 2b 已配好 DeepSeek 主推配置（`active_provider: deepseek`、`protocol: openai`、`base_url: https://api.deepseek.com/v1`、`api_key: ${DEEPSEEK_API_KEY}`）。本迭代**不改** example——2b 的配置形状已完全满足迭代 3 需求。

### 6.2 联调准备

真实联调需设置环境变量并创建 `.parrotcode.yaml`（gitignored）：

```bash
# 设置 DeepSeek API Key
export DEEPSEEK_API_KEY=sk-xxxxxxxxxxxxxxxxxxxxxxxx

# 从 example 复制配置
cp ParrotCode.Net/example.parrotcode.yaml ParrotCode.Net/.parrotcode.yaml

# 运行（DeepSeek 为 active_provider）
dotnet run --project ParrotCode.Net
```

> - `.parrotcode.yaml` 已被 `.gitignore` 忽略（2b），不会误提交 key。
> - 无 key 时可把 `active_provider` 改为 `mock` 跑通流式管线（MockProvider 流式）。

## 七、迁移说明（迭代 2b → 迭代 3）

| 2b | 迭代 3 | 处理 |
| --- | --- | --- |
| `IBaseProvider` 仅 `ChatAsync` | 追加 `ChatStreamAsync` | 接口扩展；`MockProvider` + `OpenAIProvider` 均实现 |
| `MockProvider : IBaseProvider` | 追加 `ChatStreamAsync` | yield 单 token；`ChatAsync` 不变 |
| `ProviderFactory` openai 分支抛异常 | 改为 `new OpenAIProvider(config)` | 工厂路由实现 |
| `ProviderNotImplementedException` 消息含"迭代 3" | 改为"后续迭代" | openai 已实现，消息泛化 |
| App 用 `ChatAsync` | 改用 `ChatStreamAsync` + `await foreach` | 流式渲染 |
| App `catch (Exception)` | 追加 `ProviderAuthException` / `ProviderRateLimitException` / `ProviderException` | 友好错误分类 |
| 无 `OpenAIProvider` | 新增 | SSE 解析 + HttpClient |
| 无 `ProviderException` 层次 | 新增 | 异常转译 |
| `ConfigLoader.Validate` 不校验 openai 必填 | `OpenAIProvider` 构造器校验 | 职责归属 Provider |
| `ProviderFactoryTests` 断言 openai 抛异常 | 改为返回 `OpenAIProvider` | 更新断言 |
| `MockProviderTests` | 追加流式用例 | 覆盖 `ChatStreamAsync` |

迁移后回归不变式：
- `active_provider: mock` 时，`dotnet run` 输入 `你好` → 逐字输出 `你好（mock）`（行为与 2b 一致，仅输出方式从整行变流式）。
- `active_provider: deepseek` 时，从 2b 的"将提示迭代 3 实现"变为真实流式输出。
- Ctrl+C 仍干净退出。

## 八、单元测试

### 8.1 `OpenAIProviderTests`（新增）

用自定义 `HttpMessageHandler` mock HTTP 响应，不打真实 API。

| 用例 | Mock 响应 | 期望 |
| --- | --- | --- |
| 构造器 `model` 为空 | — | 抛 `ConfigException`，消息含 `model` |
| 构造器 `base_url` 为空 | — | 抛 `ConfigException`，消息含 `base_url` |
| 构造器 `api_key` 为空 | — | 抛 `ConfigException`，消息含 `api_key` |
| 流式正常输出 | 3 个 `data:` chunk + `[DONE]` | `ChatStreamAsync` 产出 3 个 token，拼接为完整文本 |
| 流式首 chunk 无 content（只有 role） | `{"delta":{"role":"assistant"}}` | 不产出 token，不报错 |
| 流式空 choices 心跳 | `{"choices":[]}` | 跳过，不报错 |
| 流式 `[DONE]` 终止 | `data: [DONE]` | 流正常结束，不产出额外 token |
| 流式含 `reasoning_content`（DeepSeek-reasoner） | `{"delta":{"reasoning_content":"思考","content":""}}` | 仅取 `content`（空），不产出 token，不报错 |
| 流式混合 reasoning + content | reasoning chunk 后跟 content chunk | 仅产出 content chunk 的文本 |
| HTTP 401 | `{"error":{"message":"Invalid API Key"}}` | 抛 `ProviderAuthException`，消息含 API Key |
| HTTP 429 | `{"error":{"message":"Rate limit exceeded"}}` | 抛 `ProviderRateLimitException` |
| HTTP 500 | `{"error":{"message":"Internal error"}}` | 抛 `ProviderServerException`，`StatusCode==500` |
| HTTP 400 | `{"error":{"message":"Bad request"}}` | 抛 `ProviderRequestException`，`StatusCode==400` |
| 网络连接失败 | `HttpRequestException` | 抛 `ProviderRequestException`，消息含 `无法连接` |
| 请求超时 | `TaskCanceledException`（非用户取消） | 抛 `ProviderRequestException`，消息含 `超时` |
| 已取消的 CancellationToken | — | 抛 `OperationCanceledException` |
| 非流式正常输出 | `{"choices":[{"message":{"content":"你好"}}]}` | `ChatAsync` 返回 `"你好"` |
| 非流式 HTTP 401 | 401 响应 | 抛 `ProviderAuthException` |
| SSE 行无 `data:` 前缀（如 `event: ping`） | 跳过 | 不产出 token，不报错 |
| SSE 注释行（`: keepalive`） | 跳过 | 不产出 token，不报错 |

> **测试隔离**：自定义 `FakeHttpHandler : HttpMessageHandler`，构造时注入预设响应（状态码 + SSE body）。`OpenAIProvider` 构造器接收 `ProviderConfig`（含 `BaseUrl`/`ApiKey`/`Model`），测试中 `ProviderConfig` 用合法占位值（如 `BaseUrl=http://localhost`、`ApiKey=test-key`、`Model=test-model`），通过 `HttpMessageHandler` mock 真实 HTTP。
>
> **FakeHttpHandler 设计**：
> ```csharp
> internal sealed class FakeHttpHandler : HttpMessageHandler
> {
>     private readonly HttpStatusCode _statusCode;
>     private readonly string _responseBody;
>     public FakeHttpHandler(HttpStatusCode statusCode, string responseBody)
>     {
>         _statusCode = statusCode;
>         _responseBody = responseBody;
>     }
>     protected override Task<HttpResponseMessage> SendAsync(
>         HttpRequestMessage request, CancellationToken ct)
>     {
>         var content = new StringContent(_responseBody, Encoding.UTF8, "text/event-stream");
>         return Task.FromResult(new HttpResponseMessage(_statusCode) { Content = content });
>     }
> }
> ```
> OpenAIProvider 需要支持注入 HttpMessageHandler——通过内部构造器或 `internal static OpenAIProvider ForTest(config, handler)` 工厂方法。测试项目用 `InternalsVisibleTo` 访问。

### 8.2 `MockProviderTests`（补充流式用例）

迭代 2a/2b 既有用例（`ChatAsync`）全部保留。新增 `ChatStreamAsync` 用例：

| 新用例 | 期望 |
| --- | --- |
| `ChatStreamAsync` 输入 `你好` | 产出 1 个 token `你好（mock）` |
| `ChatStreamAsync` 空消息列表 | 产出 1 个 token `（mock）` |
| `ChatStreamAsync` 多条 user | 产出最后一条 user 回显 |
| `ChatStreamAsync` 已取消 token | 抛 `OperationCanceledException` |
| `ChatStreamAsync` 产出可被 `await foreach` 正常消费 | 拼接后等于 `ChatAsync` 的返回值 |

> 流式与非流式行为一致性验证：`ChatStreamAsync` 产出的 token 拼接后应等于 `ChatAsync` 的返回值。

### 8.3 `ProviderFactoryTests`（更新）

迭代 2a/2b 既有用例中，`Create_WithOpenAiProtocol_ThrowsProviderNotImplemented` 需**更新**：

| 旧用例 | 新用例 | 变更 |
| --- | --- | --- |
| `Create(openai)` 抛 `ProviderNotImplementedException` | `Create(openai)` 返回 `OpenAIProvider` | 断言从 `Throw` 改为 `BeOfType<OpenAIProvider>` |
| `CreateActive` active 命中 openai 抛异常 | 返回 `OpenAIProvider` | 同上 |

> - `Create(anthropic)` 仍抛 `ProviderNotImplementedException`（不变）。
> - `CreateActive_ActiveHitsOpenAi` 需提供完整 `ProviderConfig`（含 `model`/`base_url`/`api_key`），否则 `OpenAIProvider` 构造器抛 `ConfigException`。
> - 补充：`Create(openai)` 且 `model` 为空 → 抛 `ConfigException`（构造器校验）。

### 8.4 回归

- `dotnet test` 全绿（含 2a/2b 既有 + 迭代 3 新增/更新）。
- `dotnet run`（`active_provider: mock`）手测：输入 `你好` → 逐字输出 `你好（mock）`。

## 九、验收标准

> 全部满足方可标记 `[已完成]`。每条尽量可被命令或肉眼验证。

### 9.1 构建与运行

- [ ] `ParrotCode.Net.csproj` 无改动（零新依赖）。
- [ ] `dotnet build` 无 error、无 warning。
- [ ] `dotnet test` 全绿（含 `OpenAIProviderTests` 新增 + `MockProviderTests` 补充 + `ProviderFactoryTests` 更新）。
- [ ] `dotnet run`（`active_provider: mock`）能启动，启动横幅显示 `mock 模式 | provider= mock model= mock-1 protocol= mock`。

### 9.2 流式接口

- [ ] `IBaseProvider` 含 `ChatStreamAsync` 方法，返回 `IAsyncEnumerable<string>`。
- [ ] `MockProvider` 实现 `ChatStreamAsync`，`await foreach` 可正常消费。
- [ ] `MockProvider.ChatStreamAsync` 产出 token 拼接后等于 `ChatAsync` 返回值。
- [ ] `MockProvider.ChatAsync` 行为与 2b 一致（`ChatAsync_ReturnsSuccessfullyCompletedTask` 仍过）。

### 9.3 OpenAIProvider 实现

- [ ] `ProviderFactory.Create(protocol=openai)` 返回 `OpenAIProvider` 实例（不再抛异常）。
- [ ] `OpenAIProvider` 构造器 `model` 为空 → 抛 `ConfigException`，消息含 `model`。
- [ ] `OpenAIProvider` 构造器 `base_url` 为空 → 抛 `ConfigException`，消息含 `base_url`。
- [ ] `OpenAIProvider` 构造器 `api_key` 为空 → 抛 `ConfigException`，消息含 `api_key`。
- [ ] SSE 解析：3 个 `data:` chunk + `[DONE]` → 产出 3 个 token。
- [ ] SSE 首 chunk 无 `content`（只有 `role`）→ 不产出 token，不报错。
- [ ] SSE 空 `choices` 心跳 → 跳过，不报错。
- [ ] SSE 含 `reasoning_content` → 仅取 `content`，不报错。
- [ ] 非流式 `ChatAsync` 从 `choices[0].message.content` 取完整回复。

### 9.4 异常转译

- [ ] HTTP 401 → `ProviderAuthException`，消息含错误描述。
- [ ] HTTP 429 → `ProviderRateLimitException`。
- [ ] HTTP 5xx → `ProviderServerException`，`StatusCode` 为对应 5xx 码。
- [ ] HTTP 4xx（非 401/429）→ `ProviderRequestException`。
- [ ] 网络连接失败 → `ProviderRequestException`，消息含 `无法连接`。
- [ ] 请求超时（非用户取消）→ `ProviderRequestException`，消息含 `超时`。
- [ ] OpenAI 错误体 `{"error":{"message":"..."}}` 的 message 被提取到异常消息。
- [ ] 非 JSON 错误体原样附加到异常消息。

### 9.5 App 流式渲染

- [ ] `active_provider: mock`，输入 `你好` → 逐字输出 `你好（mock）`（非整行一次性）。
- [ ] `active_provider: deepseek`（设了 `DEEPSEEK_API_KEY`），输入 `写一首关于秋天的诗` → 逐字流式输出诗的内容。
- [ ] 流式输出过程中按 Ctrl+C → 输出中断，打印 `已取消。` 并退出（不崩溃）。
- [ ] 401 错误（key 无效）→ 打印 `认证失败：...` + `请检查 api_key 配置。`，主循环继续。
- [ ] 429 错误 → 打印 `请求过快：...` + `请稍后重试。`，主循环继续。
- [ ] 5xx 错误 → 打印 `Provider 错误：...`，主循环继续。
- [ ] 网络断开 → 打印 `Provider 错误：无法连接到 ...`，主循环继续。

### 9.6 敏感信息

- [ ] `dotnet run` 全程 stderr 日志**不**出现 ApiKey 明文。
- [ ] 启动横幅与错误信息**不**回显 ApiKey。
- [ ] `OpenAIProvider` 的 `Authorization` 头在日志中不输出。
- [ ] 配置错误信息（如 `api_key 不能为空`）不回显 key 值。

### 9.7 迁移与回归

- [ ] `IBaseProvider` 同时有 `ChatAsync`（2a）与 `ChatStreamAsync`（迭代 3）。
- [ ] `MockProvider` 实现两个方法，`ChatAsync` 行为与 2b 一致。
- [ ] `ProviderFactory` 的 `openai` 分支返回 `OpenAIProvider`；`anthropic` 仍抛 `ProviderNotImplementedException`。
- [ ] `ProviderNotImplementedException` 消息不再含"迭代 3"（改为"后续迭代"）。
- [ ] `dotnet run`（mock）输入 `你好` → `你好（mock）`（回归不变式）。
- [ ] 输入 `exit` / `quit` / EOF / `Ctrl+C` 退出行为与 2b 一致。
- [ ] 日志/输出分离保持：`out.txt` 含流式输出不含日志，`err.txt` 含日志不含回复正文。
- [ ] `ConfigLoader` / `AppConfig` / `ProviderConfig` 与 2b 相比**无改动**。

### 9.8 跨平台

- [ ] Windows 上 `dotnet run`（mock + DeepSeek）正常。
- [ ] macOS / Linux 上 `dotnet run`（mock + DeepSeek）正常。
- [ ] SSE 解析跨平台一致（`\n` 行分隔，`StreamReader` 抽象平台差异）。

## 十、进阶练习（可选，不计入验收）

1. **DeepSeek `reasoning_content` 可视化**：在 SSE 解析中额外提取 `delta.reasoning_content`，以灰色输出到控制台（思考过程用淡色，正式回复用默认色）。体会"reasoning vs output"分离。可扩展 `IAsyncEnumerable<string>` 为 `IAsyncEnumerable<StreamChunk>`（含 `TextDelta` / `ReasoningDelta` 变体）。

2. **`AnthropicProvider` 实现**：`/v1/messages` + `stream=true`，对比 OpenAI 的 SSE 格式差异（Anthropic 用 `event: content_block_delta` + `data: {"type":"content_block_delta","delta":{"type":"text_delta","text":"..."}}`，而非 OpenAI 的 `data: {"choices":[{"delta":{"content":"..."}}]}`）。体会协议抽象的边界。

3. **请求重试 + 指数退避**：对 429 / 5xx 自动重试（最多 3 次，间隔 1s/2s/4s），用 `Polly` 或手写。注意流式请求的重试只能在"读到头之前"重试——已开始流式输出后不能重试。

4. **`IHttpClientFactory` 接入**：引入 `Microsoft.Extensions.DependencyInjection` + `Microsoft.Extensions.Http`，用 `IHttpClientFactory` 管理 `HttpClient` 生命周期。对比手写 `new HttpClient()` 的差异。

5. **流式输出缓冲优化**：用 `Spectre.Console` 的 `Live` 显示器实现无闪烁逐字刷新，对比 `Console.Write` 的效果差异（为迭代 7 TUI 预热）。

6. **Token 计数**：在流式过程中累计输出 token 数，结束时打印 `（123 tokens）`。用字符数 / 3 粗略估算（为迭代 4/9 预热）。

## 十一、风险与注意事项

| 风险 | 缓解 |
| --- | --- |
| `HttpCompletionOption.ResponseHeadersRead` 误用导致流式失效 | §4.3.4 代码 + 单测 §8.1 验证：mock 多 chunk 响应产出多个 token（若误用默认模式，会等全部到齐一次性产出） |
| `HttpClient.Timeout` 在流式模式下误杀长响应 | 设为 5 分钟；理解 `Timeout` 对 `ResponseHeadersRead` 只约束"读到头的时间"。单测用 mock 不涉及真实超时 |
| SSE 解析在 Windows 换行符 `\r\n` 下异常 | `StreamReader.ReadLineAsync` 自动处理 `\r\n` / `\n`；单测 mock 用 `\n`（SSE 标准），实际响应也可能 `\r\n`，`ReadLineAsync` 兼容 |
| `reasoning_content` 字段导致 JSON 解析报错 | `JsonDocument` 默认忽略多余属性；`TryGetProperty("content", ...)` 安全取值。单测 §8.1 覆盖 |
| DeepSeek 真实联调需要 API key + 网络 | mock 模式可跑通全部流式管线（§9.1-9.4）；真实联调作为 §9.5 的 DeepSeek 用例，需手动验证 |
| `OpenAIProvider` 测试需注入 `HttpMessageHandler` | 通过 `internal` 构造器或工厂方法 + `InternalsVisibleTo`；不暴露公开测试 API |
| `ProviderException` 层次过度设计 | 仅 4 个子类覆盖高频错误（401/429/5xx/其他）；App 的 `catch (ProviderException)` 兜底，不过度细分 |
| `Console.Write` 与 `AnsiConsole.Markup` 混用导致输出乱序 | 两者都写 stdout，同一进程内顺序一致；Spectre 的 Markup 在 `Console.Write` 之前调用即可。TUI（迭代 7）会统一 |
| 流式输出含 `[` `]` 字符被 Spectre 误解析 | `Console.Write` 不经 Markup 解析，原样输出；只有 `AnsiConsole.Markup` 会解析。流式 token 用 `Console.Write` 安全 |
| `HttpClient` 不 Dispose 导致资源泄漏 | App 生命周期内单例，退出即回收；后续引入 `IHttpClientFactory` 时统一管理。本迭代不实现 `IDisposable` |
| 取消传播不完整（流式中途取消） | `[EnumeratorCancellation]` + 循环内 `IsCancellationRequested` 检查 + `ReadLineAsync(ct)` 三重保障；单测覆盖已取消 token |
| `ProviderNotImplementedException` 消息变更破坏 2b 测试 | 更新 `ProviderFactoryTests` 中 `Create_WithOpenAiProtocol` 断言；`anthropic` 仍抛异常，消息改为"后续迭代" |

## 十二、交付清单

- [ ] `ParrotCode.Net/Providers/IBaseProvider.cs`（追加 `ChatStreamAsync`）
- [ ] `ParrotCode.Net/Providers/MockProvider.cs`（追加 `ChatStreamAsync` 实现）
- [ ] `ParrotCode.Net/Providers/ProviderException.cs`（新增：异常层次）
- [ ] `ParrotCode.Net/Providers/OpenAIProvider.cs`（新增：SSE 解析 + HttpClient）
- [ ] `ParrotCode.Net/Providers/ProviderFactory.cs`（openai 分支改为 `new OpenAIProvider(config)`；异常消息泛化）
- [ ] `ParrotCode.Net/App/App.cs`（流式渲染 + 异常分类处理）
- [ ] `ParrotCode.Net/Program.cs`（装配不变，catch 保留）
- [ ] `ParrotCode.Net-xUnit/OpenAIProviderTests.cs`（新增：HttpMessageHandler mock）
- [ ] `ParrotCode.Net-xUnit/MockProviderTests.cs`（补充 `ChatStreamAsync` 用例）
- [ ] `ParrotCode.Net-xUnit/ProviderFactoryTests.cs`（更新 `openai` 断言）
- [ ] 演示：mock 流式输出 + DeepSeek 真实流式输出 + 401 友好错误截图 + `dotnet test` 全绿截图
- [ ] 本文档状态改为 `[已完成]`