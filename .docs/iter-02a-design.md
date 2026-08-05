# 迭代 2a：Provider 抽象层 — 详细设计

> 状态：[进行中]
> 对应 `plan.md` 第二章「迭代 2」的前半部分。原迭代 2 拆分为 2a（Provider 抽象层）+ 2b（配置系统），本迭代为 2a。

## 一、概述

把迭代 1 的临时 `IChatProvider` 演进为协议无关的 `IBaseProvider`，并引入 `Message` / `ToolCall` 类型与 `ProviderFactory` 路由，为迭代 3（流式 + 真实 LLM）、迭代 4（历史）、迭代 5/6（工具）打底。

本迭代是**纯类型演进**，不引入任何新依赖、不碰配置加载：
- `ProviderConfig` 作为工厂入参载体在本迭代引入，但**暂时硬编码**传入；来源由迭代 2b 的 `ConfigLoader` 接管。
- `ToolCall` 类型**仅定义、不使用**（工具调用闭环在迭代 5/6）。
- `IBaseProvider.ChatAsync` 形参为 `IReadOnlyList<Message>`，但本迭代 App 只构造单元素列表（多轮历史在迭代 4）。

**拆分动机**：Provider 抽象演进与配置系统（YamlDotNet + 三级发现 + 校验）各自独立、各有可运行交付物。拆开后 2a 零新依赖、风险小、测试聚焦；2b 专注配置加载，YamlDotNet 兼容性问题不阻塞抽象演进。

## 二、学习目标

1. **面向接口设计**：体会"协议无关"的 Provider 抽象如何让真实 LLM 接入（迭代 3）变成纯增量工作。
2. **类型演进**：把临时接口平稳替换为正式抽象，同步迁移测试，保持行为不变。
3. **工厂路由模式**：用 `switch` 表达式按 `Protocol` 路由，未支持协议显式抛异常，体会"开放-封闭"的权衡。
4. **为未来预留**：`Message.ToolCalls`、`ToolCall` 类型在不用时先定义，理解"预留形状"与"过度设计"的边界。

## 三、范围

### 3.1 本迭代包含（In Scope）

| 项 | 说明 |
| --- | --- |
| Provider 抽象 | `IBaseProvider`（非流式），替代迭代 1 的 `IChatProvider` |
| 消息类型 | `MessageRole` / `Message` / `ToolCall`（仅定义，本迭代不用于工具调用） |
| Provider 配置载体 | `ProviderConfig`（纯 record，5 字段）。本迭代硬编码传入，2b 接 YAML |
| Provider 工厂 | `ProviderFactory.Create(ProviderConfig)`：按 `Protocol` 路由；未实现协议抛 `ProviderNotImplementedException` |
| MockProvider 迁移 | 改实现 `IBaseProvider`；入参 `IReadOnlyList<Message>`，取最后一条 user 回显 |
| App/Program 接入 | App 多收一个 `ProviderConfig` 用于启动横幅；Program 硬编码 mock 配置装配 |
| 迁移 | 删除 `IChatProvider`，`MockProvider` / `App` / 测试同步迁移 |

### 3.2 本迭代不包含（Out of Scope）

- 配置加载（三级发现、YamlDotNet、`${VAR}` 展开、行号错误）→ 迭代 2b
- `AppConfig` 顶层配置模型、`ConfigLoader`、`ConfigException` → 迭代 2b
- `ProviderFactory.CreateActive(AppConfig)` → 迭代 2b（依赖 `AppConfig`）
- 真实 LLM HTTP 调用、SSE 流式 → 迭代 3
- 多轮对话历史 → 迭代 4
- 工具调用产生与执行 → 迭代 5/6
- 任何新 NuGet 依赖（`YamlDotNet` 是 2b 的事）

> 关键判据：本迭代 `csproj` **不变**，`dotnet build` / `dotnet test` / `dotnet run` 的命令面与迭代 1 一致。

## 四、架构设计

### 4.1 模块结构

```
ParrotCode.Net/
├── Program.cs                 # 入口：硬编码 ProviderConfig → Factory.Create → 装配 App
├── App/
│   └── App.cs                 # 主循环（改用 IBaseProvider + 单元素 Message 列表 + 启动横幅）
├── Config/
│   └── Models.cs              # ProviderConfig (record)。本迭代仅此一类；2b 追加 AppConfig
└── Providers/
    ├── IBaseProvider.cs       # 协议无关抽象（替代 IChatProvider）
    ├── MessageTypes.cs        # MessageRole / Message / ToolCall
    ├── ProviderFactory.cs     # 按 Protocol 路由 + ProviderNotImplementedException
    └── MockProvider.cs        # 改实现 IBaseProvider
```

> - `ProviderConfig` 虽由本迭代引入（工厂入参载体），但其归属是配置模型，故直接放 `Config/Models.cs`；2b 在同文件追加 `AppConfig` 并新增 `Loader.cs` / `ConfigException.cs`，无需移动 `ProviderConfig`。
> - 命名空间约定沿用迭代 1：所有源文件统一 `namespace ParrotCode`，文件夹仅作物理组织。

### 4.2 调用流程

```
┌─────────┐  硬编码 ProviderConfig{Name=mock,Protocol=mock,Model=mock-1}
│ Program │ ─────────────────────────────────────────────────▶ ┌─────────────────┐
│  入口   │ ◀──────────────── IBaseProvider (MockProvider) ────┤ ProviderFactory │
└────┬────┘                                                     └─────────────────┘
     │ IBaseProvider + ProviderConfig
     ▼
┌──────────┐  new[]{ Message(User, line) }   ┌──────────────┐
│   App    │ ──────────────────────────────▶ │ MockProvider │
│ (主循环) │ ◀──── string reply ──────────── └──────────────┘
└──────────┘
   │ Spectre.Console 着色输出 stdout
   ▼
```

启动伪代码：

```
providerConfig = new ProviderConfig { Name="mock", Protocol="mock", Model="mock-1" }
provider = ProviderFactory.Create(providerConfig)   // 2a 硬编码 mock，必返回 MockProvider
app = new App(provider, providerConfig, logger, ct)
await app.RunAsync()
```

> 2a 硬编码 `protocol=mock`，工厂必走 `mock` 分支返回 `MockProvider`，不会抛异常。因此 Program **不加** try/catch（避免走不到的代码）。2b 接配置后，`protocol` 可能是 `openai`/`anthropic`，届时再加 `ProviderNotImplementedException` / `ArgumentException` 的友好退出处理。

### 4.3 关键类型设计

#### 4.3.1 消息类型（`Providers/MessageTypes.cs`）

```csharp
using System.Text.Json;

namespace ParrotCode;

public enum MessageRole { System, User, Assistant, Tool }

/// <summary>
/// 协议中性的消息。Content 为文本；ToolCalls 仅 assistant 消息可能非空。
/// 本迭代仅用到 Role=User + Content；ToolCalls 字段为迭代 5/6 预留。
/// </summary>
public sealed record Message(MessageRole Role, string Content)
{
    public IReadOnlyList<ToolCall>? ToolCalls { get; init; }
}

/// <summary>
/// LLM 发起的工具调用。Input 为原始 JSON（保留协议细节，由 Provider 层解释）。
/// 本迭代仅定义，不产生也不执行。
/// </summary>
public sealed record ToolCall(string Id, string Name, JsonElement Input);
```

> 设计动机：`Message` 用 record + init 预留 `ToolCalls`，避免迭代 5 再改签名。`ToolCall.Input` 用 `JsonElement`（而非 `string`）保留结构化语义，与 `plan.md` 一致。

#### 4.3.2 `IBaseProvider`（`Providers/IBaseProvider.cs`）

```csharp
namespace ParrotCode;

/// <summary>
/// 协议无关的 Provider 抽象。
/// 迭代 2a 仅含非流式方法；流式（ChatStreamAsync 返回 IAsyncEnumerable&lt;...&gt;）在迭代 3 加入。
/// </summary>
public interface IBaseProvider
{
    Task<string> ChatAsync(IReadOnlyList<Message> messages, CancellationToken cancellationToken);
}
```

> 为什么形参是 `IReadOnlyList<Message>` 而非单条 `Message`？
> - 引入 `Message` 类型是本迭代的交付物之一。
> - 列表签名让接口在迭代 4（历史）零改动——迭代 4 只是把"单元素列表"换成"`ConversationHistory.ToProviderMessages()`"。
> - "非流式版本"指的是返回 `string`（vs 迭代 3 的 `IAsyncEnumerable`），与入参形状无关。

#### 4.3.3 `ProviderConfig`（`Config/Models.cs`）

```csharp
namespace ParrotCode;

/// <summary>
/// 单个 Provider 配置。Protocol 决定由哪个 Provider 实现处理。
/// 迭代 2a：作为工厂入参载体，由 Program 硬编码传入；BaseUrl/ApiKey 暂未使用。
/// 迭代 2b：由 ConfigLoader 从 YAML 加载，BaseUrl/ApiKey 启用。
/// </summary>
public sealed record ProviderConfig
{
    public string Name { get; init; } = string.Empty;
    public string Protocol { get; init; } = string.Empty;   // mock | openai | anthropic
    public string Model { get; init; } = string.Empty;
    public string BaseUrl { get; init; } = string.Empty;    // 2a 未用，2b 启用
    public string ApiKey { get; init; } = string.Empty;     // 2a 未用，2b 启用
}
```

> 2a 一次性定义完整 5 字段，让 record 形状在 2b 稳定不变；2a 硬编码只填 `Name` / `Protocol` / `Model`，`BaseUrl` / `ApiKey` 留空字符串。

#### 4.3.4 `ProviderFactory`（`Providers/ProviderFactory.cs`）

```csharp
namespace ParrotCode;

public static class ProviderFactory
{
    public static IBaseProvider Create(ProviderConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        return config.Protocol switch
        {
            "mock" => new MockProvider(),
            "openai" or "anthropic" => throw new ProviderNotImplementedException(config),
            _ => throw new ArgumentException(
                $"不支持的协议: {config.Protocol} (provider={config.Name})")
        };
    }
}

public sealed class ProviderNotImplementedException : NotSupportedException
{
    public ProviderNotImplementedException(ProviderConfig config)
        : base($"Provider '{config.Name}' (protocol={config.Protocol}) 将在迭代 3 实现，本迭代仅支持 mock。") { }
}
```

> - 2a **不实现** `CreateActive(AppConfig)`——它依赖 2b 的 `AppConfig`。本迭代调用方直接 `Create(providerConfig)`。
> - `openai`/`anthropic` 显式抛 `ProviderNotImplementedException`（而非返回 stub），让"未实现"状态在调用点立即暴露，避免静默错误。
> - 2a 硬编码 `mock` 时走不到异常分支，但工厂的完整路由逻辑（含异常）一次性立起来，2b/迭代 3 接入时工厂零改动。

#### 4.3.5 `MockProvider`（改实现 `IBaseProvider`）

```csharp
namespace ParrotCode;

public sealed class MockProvider : IBaseProvider
{
    public Task<string> ChatAsync(IReadOnlyList<Message> messages, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var lastUser = messages.LastOrDefault(m => m.Role == MessageRole.User);
        var content = lastUser?.Content ?? string.Empty;
        return Task.FromResult($"{content}（mock）");
    }
}
```

- 行为对齐迭代 1：输入 `你好` → 输出 `你好（mock）`。差别仅在于入参从 `string` 变为 `IReadOnlyList<Message>`，取最后一条 user 消息的 Content 回显。
- 空列表 / 无 user 消息 → 返回 `（mock）`（与迭代 1 空输入行为一致）。

#### 4.3.6 `App` 与 `Program` 改动

`App` 构造改为接收 `IBaseProvider` + 选中的 `ProviderConfig`：

```csharp
internal sealed class App(IBaseProvider provider, ProviderConfig providerConfig,
    ILogger logger, CancellationToken ct)
{
    public async Task RunAsync()
    {
        AnsiConsole.MarkupLine(
            $"[grey]ParrotCode.Net[/] [green]mock 模式[/] | " +
            $"provider=[cyan]{Markup.Escape(providerConfig.Name)}[/] " +
            $"model=[cyan]{Markup.Escape(providerConfig.Model)}[/] " +
            $"protocol=[cyan]{Markup.Escape(providerConfig.Protocol)}[/]");
        // 主循环同迭代 1，唯调用处改为：
        // var messages = new[] { new Message(MessageRole.User, line) };
        // var reply = await provider.ChatAsync(messages, ct);
        ...
    }
}
```

`Program.cs` 顶层装配（节选，相对迭代 1 仅替换 provider 装配段）：

```csharp
// logger / cts 装配同迭代 1，不重复
var providerConfig = new ProviderConfig
{
    Name = "mock",
    Protocol = "mock",
    Model = "mock-1"
};
var provider = ProviderFactory.Create(providerConfig);
logger.LogInformation("使用 provider={Name} model={Model} protocol={Protocol}",
    providerConfig.Name, providerConfig.Model, providerConfig.Protocol);

var app = new App(provider, providerConfig, logger, cts.Token);
await app.RunAsync();
```

> `logger` 仍只记 `Name` / `Model` / `Protocol`，不记 `ApiKey`（本迭代虽无 key，但作为纪律提前遵守，2b 启用 key 时沿用）。

## 五、依赖变更

**无。** `ParrotCode.Net.csproj` 不动；`ParrotCode.Net-xUnit.csproj` 不动。

> 这是 2a 可独立成迭代的关键证据：零新依赖、零构建配置变化，纯代码演进。

## 六、配置文件

**无变化。** 迭代 1 的 `appsettings.json` 占位在本迭代**保留不动**（2b 才用 YAML 取代它）。

> 不在 2a 删 `appsettings.json`：删除时机与"引入 YAML"绑定，放 2b 一起做，避免 2a 与 2b 之间出现"无任何配置文件"的中间态语义空白。

## 七、迁移说明（迭代 1 → 迭代 2a）

| 迭代 1 | 迭代 2a | 处理 |
| --- | --- | --- |
| `Providers/IChatProvider.cs` | `Providers/IBaseProvider.cs` | **删除** `IChatProvider`，新增 `IBaseProvider`（不保留 obsolete 壳） |
| `MockProvider : IChatProvider` | `MockProvider : IBaseProvider` | 改实现；入参 `string` → `IReadOnlyList<Message>`，取最后一条 user 回显 |
| `App(IChatProvider, ILogger, CT)` | `App(IBaseProvider, ProviderConfig, ILogger, CT)` | 多传一个 `ProviderConfig` 用于启动横幅 |
| `Program.cs` 直接 `new MockProvider()` | `new ProviderConfig{...}` → `ProviderFactory.Create(...)` | 装配走工厂；`ProviderConfig` 硬编码 |
| `MockProviderTests.cs`（9 用例，签名 `ChatAsync(string, CT)`） | 改为 `ChatAsync(new[]{Message(User, ...)}, CT)` | **全部用例同步迁移**，断言不变（仍是 `{输入}（mock）`） |

迁移后回归不变式：`dotnet run` 输入 `你好` 仍输出 `你好（mock）`；Ctrl+C 仍干净退出。

## 八、单元测试

### 8.1 `MockProviderTests`（迁移 + 补充）

迭代 1 的 9 个用例改为构造 `new[] { new Message(MessageRole.User, input) }` 后调用，断言保持 `{input}（mock）`。补充：

| 新用例 | 期望 |
| --- | --- |
| 空消息列表 | 返回 `（mock）` |
| 列表只有 assistant 消息（无 user） | 返回 `（mock）` |
| 列表含多条 user | 回显**最后一条** user 的 Content |
| 列表首条 system + 末条 user | 回显末条 user 的 Content（system 不影响回显） |

### 8.2 `ProviderFactoryTests`（新增）

| 用例 | 期望 |
| --- | --- |
| `protocol=mock` | 返回 `MockProvider` 实例 |
| `protocol=openai` | 抛 `ProviderNotImplementedException`，消息含"迭代 3" |
| `protocol=anthropic` | 抛 `ProviderNotImplementedException` |
| `protocol=foo`（未知） | 抛 `ArgumentException`，消息含"不支持的协议" |
| `protocol=""`（空串） | 抛 `ArgumentException`（走 `default` 分支） |
| `Create(null)` | 抛 `ArgumentNullException` |

### 8.3 回归

- `dotnet test` 全绿。
- `dotnet run` 手测：输入 `你好` → `你好（mock）`。

> 本迭代**不新增** `ConfigLoaderTests`（配置系统在 2b）。

## 九、验收标准

> 全部满足方可标记 `[已完成]`。每条尽量可被命令或肉眼验证。

### 9.1 构建与运行

- [ ] `ParrotCode.Net.csproj` 无改动（零新依赖）。
- [ ] `dotnet build` 无 error、无 warning。
- [ ] `dotnet test` 全绿（含迁移后的 `MockProviderTests` 与新增 `ProviderFactoryTests`）。
- [ ] `dotnet run` 能启动，启动横幅显示 `provider= mock model= mock-1 protocol= mock`。

### 9.2 接口演进

- [ ] `Providers/IChatProvider.cs` 已删除。
- [ ] `Providers/IBaseProvider.cs` 已新增，仅一个非流式方法 `ChatAsync(IReadOnlyList<Message>, CancellationToken)`。
- [ ] `MockProvider : IBaseProvider`（不再实现 `IChatProvider`）。
- [ ] 全代码库无 `IChatProvider` 残留引用（`Grep` 验证）。

### 9.3 类型定义

- [ ] `Providers/MessageTypes.cs` 定义 `MessageRole` / `Message` / `ToolCall`。
- [ ] `Message` 含 `ToolCalls` init 属性（本迭代不用，但形状存在）。
- [ ] `Config/Models.cs` 定义 `ProviderConfig`（5 字段：Name/Protocol/Model/BaseUrl/ApiKey）。

### 9.4 工厂路由

- [ ] `ProviderFactory.Create` 对 `mock` 返回 `MockProvider`。
- [ ] 对 `openai` / `anthropic` 抛 `ProviderNotImplementedException`（消息含"迭代 3"）。
- [ ] 对未知/空协议抛 `ArgumentException`（消息含"不支持的协议"）。
- [ ] 对 `null` 入参抛 `ArgumentNullException`。
- [ ] 工厂**不**含 `CreateActive` 方法（留给 2b）。

### 9.5 MockProvider 行为

- [ ] 输入 `你好`（单条 user 消息）→ 输出 `你好（mock）`。
- [ ] 空消息列表 → 输出 `（mock）`。
- [ ] 多条 user 消息 → 回显最后一条 user 的 Content。
- [ ] 已取消的 `CancellationToken` → 抛 `OperationCanceledException`。

### 9.6 回归与不变式

- [ ] `dotnet run` 输入 `你好` → 输出 `你好（mock）`（迭代 1 行为保持）。
- [ ] 输入 `exit` / `quit` / `Ctrl+Z`+回车（Windows）/ `Ctrl+D`（Unix）退出行为与迭代 1 一致。
- [ ] `Ctrl+C` 1 秒内退出，不抛未处理异常堆栈。
- [ ] 空行不调用 Provider，重新显示 `> ` 提示符。
- [ ] 日志/输出分离保持：`ParrotCode.Net.exe > out.txt 2> err.txt`，`out.txt` 含 AI 回复不含日志，`err.txt` 含日志（`使用 provider=...`）不含回复正文。

### 9.7 健壮性

- [ ] 临时改 `MockProvider` 抛 `throw new Exception("boom")`，程序打印 `错误：boom` 并**继续**循环（迭代 1 验收点保持）。
- [ ] 日志记录该异常堆栈到 stderr。

### 9.8 跨平台

- [ ] Windows 上 `dotnet run` 正常（Ctrl+C 退出）。
- [ ] macOS / Linux 上 `dotnet run` 正常（Ctrl+C 退出，Ctrl+D 退出）。

## 十、进阶练习（可选，不计入验收）

1. **思考题**：迭代 3 要加流式 `ChatStreamAsync` 返回 `IAsyncEnumerable<string>`，是改 `IBaseProvider` 接口（所有实现必须实现）还是新增 `IStreamingProvider` 子接口？写一段对比分析，为迭代 3 决策预热。
2. 给 `Message` 加一个 `static Message User(string)` / `Message Assistant(string)` 工厂方法，体会与主构造器的取舍。
3. 让 `MockProvider` 通过构造参数接收自定义后缀（如 `（mock-v2）`），为 2b 多 mock provider 切换演示铺路。

## 十一、风险与注意事项

| 风险 | 缓解 |
| --- | --- |
| `IChatProvider` 删除导致引用断裂 | 本项目无外部消费者；内部仅 `MockProvider` / `App` / 测试，一并迁移；§9.2 用 Grep 验证无残留 |
| `IReadOnlyList<Message>` 签名被认为"过早设计" | 论证：为迭代 4 零改动接口；引入 `Message` 本就是 2a 交付物；不算过度设计 |
| `ToolCall` / `Message.ToolCalls` 定义了不用，显得多余 | 文档明确标注"迭代 5/6 预留"；若 review 时强烈反对，可推迟到迭代 5 再引入——但 `plan.md` 迭代 2 明确要求 `ToolCall` 类型，故保留 |
| `ProviderConfig` 硬编码传入像是"配置"的味道 | 明确标注为 2a 临时手段，2b 接 `ConfigLoader`；`Config/Models.cs` 归属正确，2b 零移动 |
| `ProviderNotImplementedException` 在 2a 走不到 | 工厂路由逻辑一次性立完整，2b/迭代 3 零改动工厂；单测 §8.2 覆盖异常分支，保证逻辑正确 |
| 2a 与 2b 之间 `appsettings.json` 仍占位 | 保留不动，避免中间态语义空白；2b 统一用 YAML 取代 |
| Program 不加 try/catch 与 2b 不一致 | 2a 硬编码 mock 不会失败，加 try/catch 是走不到的代码；2b 接配置后补友好退出，文档已说明 |

## 十二、交付清单

- [ ] `ParrotCode.Net/Config/Models.cs`（仅 `ProviderConfig`）
- [ ] `ParrotCode.Net/Providers/IBaseProvider.cs`
- [ ] `ParrotCode.Net/Providers/MessageTypes.cs`
- [ ] `ParrotCode.Net/Providers/ProviderFactory.cs`（含 `ProviderNotImplementedException`）
- [ ] `ParrotCode.Net/Providers/MockProvider.cs`（改为 `IBaseProvider`）
- [ ] `ParrotCode.Net/App/App.cs`（改用 `IBaseProvider` + `ProviderConfig` + 启动横幅）
- [ ] `ParrotCode.Net/Program.cs`（硬编码 `ProviderConfig` + `Factory.Create` 装配）
- [ ] 删除 `ParrotCode.Net/Providers/IChatProvider.cs`
- [ ] `ParrotCode.Net-xUnit/MockProviderTests.cs`（迁移 9 用例 + 补充 4 用例）
- [ ] `ParrotCode.Net-xUnit/ProviderFactoryTests.cs`（新增）
- [ ] 演示：`dotnet run` 交互 + `dotnet test` 全绿截图
- [ ] 本文档状态改为 `[已完成]`
