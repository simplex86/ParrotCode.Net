# 迭代 2：配置系统 + Provider 抽象层 — 详细设计

> 状态：[进行中]
> 对应 `plan.md` 第二章「迭代 2」章节，本文档在其基础上补充实现级细节与可执行的验收清单。

## 一、概述

在迭代 1 跑通「输入 → Provider → 输出」最小管线后，本迭代把**配置**与**Provider 抽象**两件事立起来：

1. **配置系统**：YAML 配置文件 + 三级发现（环境变量 > 项目目录 > 用户目录）+ 校验 + 带行号的错误报告。
2. **Provider 抽象层**：把迭代 1 的临时 `IChatProvider` 演进为协议无关的 `IBaseProvider`，并引入 `Message` / `ToolCall` 类型，为迭代 3（流式）、迭代 4（历史）、迭代 5（工具）打底。

本迭代**刻意保持**：
- **不做**流式输出（迭代 3）。
- **不做**真实 LLM HTTP 调用（迭代 3 的 `OpenAIProvider`）。`openai` / `anthropic` 协议在工厂里**显式抛 `ProviderNotImplementedException`**，给出"将在迭代 3 实现"的清晰提示，而非静默失败。
- **不做**多轮历史（迭代 4）。`IBaseProvider.ChatAsync` 形参虽为 `IReadOnlyList<Message>`，但本迭代 App 只构造单元素列表。
- **不做**工具调用闭环（迭代 5/6）。`ToolCall` 类型仅定义、不使用。

> 命名说明：`plan.md` 中配置文件名写作 `parrocode.yaml`（缺 `t`），而环境变量写作 `PARROTCODE_CONFIG`（含 `t`），二者不一致。本迭代统一采用 **`parrotcode`**（与项目名 `ParrotCode.Net`、环境变量 `PARROTCODE_CONFIG`、MewCode 的 `mewcode.yaml` 命名规律一致）。若需改回 `parrocode`，全局替换即可。

## 二、学习目标

1. **配置三级发现**：环境变量 > 项目目录 > 用户目录，理解优先级与回退语义。
2. **面向接口设计**：体会"协议无关"的 Provider 抽象如何让真实 LLM 接入（迭代 3）变成纯增量工作。
3. **YAML 解析与错误报告**：YamlDotNet 反序列化 + `YamlException` 行号捕获 + 自定义校验。
4. **敏感信息纪律**：ApiKey 只进 YAML/环境变量，不进代码、不进日志（掩码输出）。
5. **类型演进**：把临时接口平稳替换为正式抽象，同步迁移测试。

## 三、范围

### 3.1 本迭代包含（In Scope）

| 项 | 说明 |
| --- | --- |
| 配置数据模型 | `AppConfig` / `ProviderConfig`（record） |
| 配置加载器 | `ConfigLoader`：三级发现 + YamlDotNet 解析 + 校验 + `${VAR}` 环境变量展开 |
| 配置异常 | `ConfigException`（带 `SourcePath` / `Line` / `Column`） |
| Provider 抽象 | `IBaseProvider`（非流式），替代迭代 1 的 `IChatProvider` |
| 消息类型 | `Message` / `MessageRole` / `ToolCall`（仅定义，本迭代不用于工具调用） |
| Provider 工厂 | `ProviderFactory`：按 `Protocol` 路由；未实现协议抛 `ProviderNotImplementedException` |
| 示例配置 | `example.parrocode.yaml`（tracked）+ `.parrocode.yaml`（gitignored，用户实际配置） |
| App/Program 接入 | 启动时加载配置 → 选 active provider → 打印选中信息 → 主循环沿用迭代 1 |
| 迁移 | 移除 `IChatProvider`，`MockProvider` 改实现 `IBaseProvider`，更新其单测 |
| .gitignore | 忽略 `.parrocode.yaml`，保留 `example.parrocode.yaml` |

### 3.2 本迭代不包含（Out of Scope）

- 真实 LLM HTTP 调用、SSE 流式（迭代 3）
- `OpenAIProvider` / `AnthropicProvider` 实现（迭代 3）
- 多轮对话历史、`ConversationHistory`（迭代 4）
- 工具系统、`ToolCall` 的实际产生与执行（迭代 5/6）
- `Microsoft.Extensions.Configuration` / DI 容器接入（进阶练习，不强制）
- 配置热重载

## 四、架构设计

### 4.1 模块结构

```
ParrotCode.Net/
├── Program.cs                 # 入口：加载配置 → 选 provider → 装配 App
├── App/
│   └── App.cs                 # 主循环（改用 IBaseProvider + 单元素 Message 列表）
├── Config/
│   ├── Models.cs              # AppConfig / ProviderConfig (record)
│   ├── ConfigException.cs     # 带 SourcePath/Line/Column 的配置异常
│   └── Loader.cs              # 三级发现 + YamlDotNet 解析 + 校验 + ${VAR} 展开
├── Providers/
│   ├── IBaseProvider.cs       # 协议无关抽象（替代 IChatProvider）
│   ├── MockProvider.cs        # 改实现 IBaseProvider
│   ├── MessageTypes.cs        # MessageRole / Message / ToolCall
│   └── ProviderFactory.cs     # 按 Protocol 路由 + 按 Name 解析 active
├── example.parrocode.yaml     # 示例配置（tracked）
└── ParrotCode.Net.csproj      # 新增 YamlDotNet；移除 appsettings.json 项
```

> 命名空间约定：沿用迭代 1 的决定——所有源文件统一 `namespace ParrotCode`，`Config/` / `Providers/` / `App/` 仅作文件夹组织，不开子命名空间（受 IDE 自动重构约束，避免 `ParrotCode.Net.*` 被折叠引起的来回改动）。

### 4.2 调用流程

```
┌─────────┐  PARROTCODE_CONFIG / .parrocode.yaml / ~/.parrotcode/config.yaml
│  启动   │ ────────────────────────────────────────────────────▶ ┌──────────────┐
│ Program │ ◀──────────────────────── AppConfig (含 active+list) ┤ ConfigLoader │
└────┬────┘                                                       └──────────────┘
     │ ProviderFactory.CreateActive(config)
     ▼
┌──────────────┐  ProviderConfig(protocol=mock)   ┌──────────────┐
│ ProviderFactory │ ────────────────────────────▶ │ MockProvider │ (IBaseProvider)
└──────────────┘                                  └──────────────┘
     │ IBaseProvider + 选中的 ProviderConfig
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
config = try ConfigLoader.Load() catch ConfigException: 打印错误 → 退出码 1
providerConfig = 选中 active（active_provider ?? providers[0].name）
provider = try ProviderFactory.Create(providerConfig)
           catch ProviderNotImplementedException: 提示"迭代 3 实现" → 退出码 1
log "使用 provider={name} model={model} protocol={protocol}"  // 不含 api_key
app = new App(provider, providerConfig, logger, ct)
await app.RunAsync()
```

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
/// 迭代 2 仅含非流式方法；流式（ChatStreamAsync 返回 IAsyncEnumerable&lt;...&gt;）在迭代 3 加入。
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

#### 4.3.3 配置模型（`Config/Models.cs`）

```csharp
namespace ParrotCode;

/// <summary>顶层配置，对应 .parrocode.yaml 的根结构。</summary>
public sealed record AppConfig
{
    /// <summary>当前激活的 Provider 名称；为 null 时回退到 providers[0].name。</summary>
    public string? ActiveProvider { get; init; }

    /// <summary>Provider 列表。无配置文件时由 Loader 提供默认 mock 项。</summary>
    public IReadOnlyList<ProviderConfig> Providers { get; init; } = Array.Empty<ProviderConfig>();
}

/// <summary>单个 Provider 配置。Protocol 决定由哪个 Provider 实现处理。</summary>
public sealed record ProviderConfig
{
    public string Name { get; init; } = string.Empty;
    public string Protocol { get; init; } = string.Empty;   // mock | openai | anthropic
    public string Model { get; init; } = string.Empty;
    public string BaseUrl { get; init; } = string.Empty;
    public string ApiKey { get; init; } = string.Empty;
}
```

> YamlDotNet 对 record 的反序列化需 `DeserializerBuilder` 默认配置即可（13+ 已支持 record 的 init 属性）。若遇兼容问题，退路是改为 `{ get; set; }` 的 class——类型形状不变，不影响调用方。

#### 4.3.4 `ConfigException`（`Config/ConfigException.cs`）

```csharp
namespace ParrotCode;

public sealed class ConfigException : Exception
{
    public string? SourcePath { get; }
    public int? Line { get; }
    public int? Column { get; }

    public ConfigException(string message, string? sourcePath = null,
        int? line = null, int? column = null, Exception? inner = null)
        : base(message, inner)
    {
        SourcePath = sourcePath;
        Line = line;
        Column = column;
    }
}
```

- YAML 语法错误：`Line`/`Column` 来自 `YamlException.Start`。
- 自定义校验错误（反序列化后的语义校验）：`Line` 为 null，但 `Message` 内带字段路径（如 `providers[1].name`）。

#### 4.3.5 `ConfigLoader`（`Config/Loader.cs`）

```csharp
namespace ParrotCode;

public static class ConfigLoader
{
    public const string EnvVar = "PARROTCODE_CONFIG";
    public const string CwdFileName = ".parrocode.yaml";
    public const string UserDirName = ".parrotcode";
    public const string UserFileName = "config.yaml";

    /// <summary>按三级发现加载；无任何配置时返回默认 mock 配置。</summary>
    public static AppConfig Load() => Load(explicitPath: null);

    /// <summary>explicitPath 优先级最高（用于测试与 --config 参数）。</summary>
    public static AppConfig Load(string? explicitPath)
    {
        var path = ResolvePath(explicitPath);     // 可能返回 null
        if (path is null) return Default();
        var config = Parse(path);                 // YamlDotNet + 行号捕获
        config = ExpandEnv(config, path);         // ${VAR} 展开
        return Validate(config, path);            // 语义校验
    }

    private static string? ResolvePath(string? explicitPath) { /* §4.4 */ }
    private static AppConfig Parse(string path) { /* §4.5 */ }
    private static AppConfig ExpandEnv(AppConfig config, string path) { /* §4.6 */ }
    private static AppConfig Validate(AppConfig config, string? path) { /* §4.7 */ }
    private static AppConfig Default() => new()
    {
        ActiveProvider = "mock",
        Providers = new[]
        {
            new ProviderConfig { Name = "mock", Protocol = "mock", Model = "mock-1" }
        }
    };
}
```

#### 4.3.6 `ProviderFactory`（`Providers/ProviderFactory.cs`）

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

    /// <summary>按 active_provider（回退 providers[0]）选中并创建。</summary>
    public static IBaseProvider CreateActive(AppConfig appConfig)
    {
        var name = appConfig.ActiveProvider ?? appConfig.Providers.FirstOrDefault()?.Name;
        var pc = appConfig.Providers.FirstOrDefault(p => p.Name == name)
            ?? throw new ConfigException($"active_provider '{name}' 未在 providers 中定义");
        return Create(pc);
    }
}

public sealed class ProviderNotImplementedException : NotSupportedException
{
    public ProviderNotImplementedException(ProviderConfig config)
        : base($"Provider '{config.Name}' (protocol={config.Protocol}) 将在迭代 3 实现，本迭代仅支持 mock。") { }
}
```

#### 4.3.7 `MockProvider`（改实现 `IBaseProvider`）

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

#### 4.3.8 `App` 与 `Program` 改动

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

`Program.cs` 顶层装配（节选）：

```csharp
AppConfig config;
try { config = ConfigLoader.Load(); }
catch (ConfigException ex)
{
    AnsiConsole.MarkupLine($"[red]配置错误：[/]{Markup.Escape(ex.Message)}");
    if (ex.SourcePath is not null)
        AnsiConsole.MarkupLine($"[grey]  文件：{Markup.Escape(ex.SourcePath)}[/]");
    if (ex.Line is not null)
        AnsiConsole.MarkupLine($"[grey]  行：{ex.Line}{(ex.Column is null ? "" : $",列：{ex.Column}")}[/]");
    return 1;
}

var activeConfig = config.Providers.First(p =>
    p.Name == (config.ActiveProvider ?? config.Providers[0].Name));

IBaseProvider provider;
try { provider = ProviderFactory.Create(activeConfig); }
catch (ProviderNotImplementedException ex)
{
    AnsiConsole.MarkupLine($"[yellow]提示：[/]{Markup.Escape(ex.Message)}");
    return 1;
}
catch (ArgumentException ex)
{
    AnsiConsole.MarkupLine($"[red]配置错误：[/]{Markup.Escape(ex.Message)}");
    return 1;
}

logger.LogInformation("已加载配置，provider={Name} model={Model} protocol={Protocol}",
    activeConfig.Name, activeConfig.Model, activeConfig.Protocol);  // 注意：不记 ApiKey

var app = new App(provider, activeConfig, logger, cts.Token);
await app.RunAsync();
return 0;
```

### 4.4 配置三级发现

`ResolvePath(explicitPath)` 顺序（先命中先用，**不合并**）：

| 优先级 | 来源 | 路径 | 不存在时 |
| --- | --- | --- | --- |
| 0（最高） | `explicitPath` 参数 | 调用方指定 | 抛 `ConfigException("指定的配置文件不存在: ...")` |
| 1 | 环境变量 | `$PARROTCODE_CONFIG` 指向的路径 | 抛 `ConfigException("环境变量 PARROTCODE_CONFIG 指向的文件不存在: ...")` |
| 2 | 项目目录 | `$(pwd)/.parrocode.yaml` | 继续下一级 |
| 3 | 用户目录 | `~/.parrotcode/config.yaml` | 返回 null → 用默认 mock 配置 |

> - 环境变量与 explicitPath 指向不存在路径是**错误**（用户明确意图落空），不是静默回退。
> - 用户目录用 `Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)`，跨平台。
> - 三级**不合并**：与 MewCode 一致，避免配置拼装带来的歧义。

### 4.5 YAML 解析与错误报告

```csharp
private static AppConfig Parse(string path)
{
    var text = File.ReadAllText(path);
    if (string.IsNullOrWhiteSpace(text))
        throw new ConfigException("配置文件为空", path);  // 空文件视为错误，不静默回退默认

    var deserializer = new DeserializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)  // YAML 用 snake_case? 见下注
        .Build();
    try
    {
        return deserializer.Deserialize<AppConfig>(text) ?? throw new ConfigException("配置文件内容为 null", path);
    }
    catch (YamlException ex)
    {
        throw new ConfigException(
            $"YAML 解析失败: {ex.Message}",
            path, ex.Start.Line, ex.Start.Column, ex);
    }
}
```

> **命名约定**：YAML 字段用 `snake_case`（`active_provider` / `base_url` / `api_key`），C# 属性用 PascalCase。需 `WithNamingConvention(UnderscoredNamingConvention.Instance)` 把 `base_url` ↔ `BaseUrl`。上例用 `CamelCaseNamingConvention` 仅示意，**实现时用 `UnderscoredNamingConvention`**。

错误报告格式（用户可见，走 stdout/Spectre）：

```
配置错误：YAML 解析失败: (Line: 5, Col: 3) ...
  文件：D:\proj\.parrocode.yaml
  行：5,列：3
```

### 4.6 环境变量展开 `${VAR}`

对 `ProviderConfig` 的所有字符串字段（`Name` / `Protocol` / `Model` / `BaseUrl` / `ApiKey`）展开 `${VAR_NAME}`：

```csharp
private static string ExpandEnv(string value, string fieldPath, string sourcePath)
{
    if (string.IsNullOrEmpty(value) || !value.Contains("${")) return value;
    // 正则 \$\{([A-Z0-9_]+)\} 逐个替换
    return Regex.Replace(value, @"\$\{([A-Z0-9_]+)\}", m =>
    {
        var name = m.Groups[1].Value;
        var v = Environment.GetEnvironmentVariable(name);
        if (string.IsNullOrEmpty(v))
            throw new ConfigException($"环境变量 '{name}' 未设置（引用于 {fieldPath}）", sourcePath);
        return v;
    });
}
```

- 鼓励 `api_key: ${OPENAI_API_KEY}` 而非明文，降低误提交风险。
- 未设置/空 → `ConfigException`，指明引用字段路径（如 `providers[1].api_key`）。
- 仅做一层展开（不递归），避免循环。

### 4.7 校验（`Validate`）

反序列化 + 展开后的语义校验，失败抛 `ConfigException`（`Line` 为 null，`Message` 带字段路径）：

| # | 规则 | 错误信息示例 |
| --- | --- | --- |
| 1 | `providers` 非空 | `providers 不能为空` |
| 2 | 每个 provider 的 `name` 非空 | `providers[1].name 不能为空` |
| 3 | 每个 provider 的 `protocol` 非空 | `providers[0].protocol 不能为空` |
| 4 | `protocol` 在 `{mock, openai, anthropic}` 内 | `providers[0].protocol 'foo' 不支持（允许: mock/openai/anthropic）` |
| 5 | `name` 不重复（大小写敏感） | `providers 名称重复: 'openai' (providers[0] 与 providers[2])` |
| 6 | `active_provider` 为 null → 回退 `providers[0].name`，记 warning 日志 | （非错误） |
| 7 | `active_provider` 非空时必须命中某 `name` | `active_provider 'foo' 未在 providers 中定义` |

> 迭代 2 **不**校验 `openai`/`anthropic` 的 `model`/`base_url`/`api_key` 必填——因为本迭代它们不会被实例化（工厂会抛 `ProviderNotImplementedException`）。必填校验留到迭代 3 实现真实 Provider 时再加，避免本迭代对未实现协议过度约束。

### 4.8 敏感信息处理

- **ApiKey 来源**：YAML 字段或 `${ENV_VAR}`，不进代码。
- **日志掩码**：任何地方记录 provider 信息时，只记 `Name` / `Model` / `Protocol`，**禁止**记 `ApiKey`。若需提示 key 状态，掩码为 `sk-***1234`（前缀 + 后 4 位）或仅记"已设置/未设置"。
- **.gitignore**：忽略 `.parrocode.yaml` 与 `~/.parrotcode/`，保留 `example.parrocode.yaml`。
- **错误信息**：配置错误信息只显示字段路径，不回显 ApiKey 原文。

## 五、依赖变更

`ParrotCode.Net.csproj`：

```xml
<!-- 新增 -->
<PackageReference Include="YamlDotNet" Version="16.*" />

<!-- 移除 appsettings.json 项（占位配置被 YAML 取代） -->
<!-- <None Update="appsettings.json"> ... </None> 删除 -->
```

> 版本号实现时锁定当时最新稳定版（迭代 1 风格：设计文档写 `16.*`，csproj 锁具体版本）。

`ParrotCode.Net-xUnit.csproj`：无新增依赖（`FluentAssertions` + `xUnit` 已在）。

## 六、配置文件

### 6.1 `example.parrocode.yaml`（tracked）

```yaml
# ParrotCode.Net 配置示例。复制为 .parrocode.yaml 后按需修改。
# .parrocode.yaml 已被 .gitignore 忽略，不会被提交。

# 当前激活的 provider 名称（对应下方 providers 中某项的 name）。
# 省略时回退到 providers[0]。
active_provider: mock

providers:
  - name: mock
    protocol: mock
    model: mock-1

  - name: openai
    protocol: openai
    model: gpt-4o-mini
    base_url: https://api.openai.com/v1
    api_key: ${OPENAI_API_KEY}

  - name: deepseek
    protocol: openai
    model: deepseek-chat
    base_url: https://api.deepseek.com/v1
    api_key: ${DEEPSEEK_API_KEY}
```

### 6.2 `.parrocode.yaml`（gitignored，用户实际配置）

用户按需从 example 复制并填写真实 key。本迭代验收阶段会临时创建/删除它。

### 6.3 移除 `appsettings.json`

迭代 1 的 `appsettings.json` 是占位，本迭代被 YAML 取代。日志配置仍保留在 `Program.cs` 代码中（迭代 1 已如此）。若后续想让日志级别走配置，放进 `.parrocode.yaml` 的 `logging` 节即可，不在本迭代做。

## 七、迁移说明（迭代 1 → 迭代 2）

| 迭代 1 | 迭代 2 | 处理 |
| --- | --- | --- |
| `Providers/IChatProvider.cs` | `Providers/IBaseProvider.cs` | **删除** `IChatProvider`，新增 `IBaseProvider`（不保留 obsolete 壳） |
| `MockProvider : IChatProvider` | `MockProvider : IBaseProvider` | 改实现；入参 `string` → `IReadOnlyList<Message>`，取最后一条 user 回显 |
| `App(IChatProvider, ILogger, CT)` | `App(IBaseProvider, ProviderConfig, ILogger, CT)` | 多传一个 `ProviderConfig` 用于启动横幅 |
| `Program.cs` 直接 `new MockProvider()` | `ConfigLoader.Load()` → `ProviderFactory.CreateActive()` | 装配走配置 |
| `appsettings.json` | `.parrocode.yaml` + `example.parrocode.yaml` | 删除 `appsettings.json` 及其 csproj 项 |
| `MockProviderTests.cs`（9 个用例，签名 `ChatAsync(string, CT)`） | 改为 `ChatAsync(new[]{Message(User, ...)}, CT)` | **全部用例同步迁移**，断言不变（仍是 `{输入}（mock）`） |

迁移后回归不变式：`dotnet run` 输入 `你好` 仍输出 `你好（mock）`；Ctrl+C 仍干净退出。

## 八、单元测试

### 8.1 `ConfigLoaderTests`（新增）

| 用例 | 期望 |
| --- | --- |
| 无任何配置文件（env/cwd/home 均无） | 返回默认 mock 配置，`ActiveProvider=="mock"`，1 个 provider |
| `explicitPath` 指向合法 YAML | 加载该文件 |
| `explicitPath` 指向不存在文件 | 抛 `ConfigException`，消息含路径 |
| `PARROTCODE_CONFIG` 指向合法文件 | 优先于 cwd/home |
| `PARROTCODE_CONFIG` 指向不存在文件 | 抛 `ConfigException` |
| cwd 有 `.parrocode.yaml` | 优先于 home |
| 仅 home 有 `~/.parrotcode/config.yaml` | 加载它 |
| YAML 语法错误（缩进/非法字符） | 抛 `ConfigException`，`Line` 非 null |
| YAML 缺 `name` 字段 | 抛 `ConfigException`，消息含 `providers[0].name` |
| 两个 provider 同名 | 抛 `ConfigException`，消息含重复名 |
| `active_provider` 指向不存在 name | 抛 `ConfigException` |
| `active_provider` 为 null | 回退 `providers[0]`，不报错 |
| `api_key: ${MY_KEY}`，`MY_KEY` 已设置 | `ApiKey == 环境变量值` |
| `api_key: ${MY_KEY}`，`MY_KEY` 未设置 | 抛 `ConfigException`，消息含 `MY_KEY` 与字段路径 |
| 空文件（仅空白） | 抛 `ConfigException("配置文件为空")` |
| `protocol: foo` | 抛 `ConfigException`，消息含允许集合 |

> 测试用临时目录隔离：每个用例建临时文件夹写 `.parrocode.yaml`，用 `ConfigLoader.Load(explicitPath)` 注入，避免污染开发者本机配置。涉及环境变量的用例用 `Environment.SetEnvironmentVariable` 设置并在 finally 还原。

### 8.2 `ProviderFactoryTests`（新增）

| 用例 | 期望 |
| --- | --- |
| `protocol=mock` | 返回 `MockProvider` 实例 |
| `protocol=openai` | 抛 `ProviderNotImplementedException`，消息含"迭代 3" |
| `protocol=anthropic` | 抛 `ProviderNotImplementedException` |
| `protocol=foo` | 抛 `ArgumentException`，消息含"不支持的协议" |
| `CreateActive`，`active_provider` 命中 | 返回对应 provider 实例 |
| `CreateActive`，`active_provider` 未命中 | 抛 `ConfigException` |
| `Create(null)` | 抛 `ArgumentNullException` |

### 8.3 `MockProviderTests`（迁移）

迭代 1 的 9 个用例改为构造 `new[] { new Message(MessageRole.User, input) }` 后调用，断言保持 `{input}（mock）`。补充：

| 新用例 | 期望 |
| --- | --- |
| 空消息列表 | 返回 `（mock）` |
| 列表只有 assistant 消息 | 返回 `（mock）`（无 user） |
| 列表含多条 user | 回显**最后一条** user 的 Content |

### 8.4 回归

- `dotnet test` 全绿。
- `dotnet run` 手测：输入 `你好` → `你好（mock）`。

## 九、验收标准

> 全部满足方可标记 `[已完成]`。每条尽量可被命令或肉眼验证。

### 9.1 构建与运行

- [ ] `dotnet build` 无 error、无 warning。
- [ ] `dotnet test` 全绿（含迁移后的 `MockProviderTests` 与新增 `ConfigLoaderTests` / `ProviderFactoryTests`）。
- [ ] `dotnet run` 能启动，启动横幅显示 `provider= mock model= mock-1 protocol= mock`。

### 9.2 配置三级发现

- [ ] 删除 cwd 与 home 配置、未设环境变量：`dotnet run` 用默认 mock 配置正常工作。
- [ ] 在 cwd 放 `.parrocode.yaml`（`active_provider: mock` + 一个 mock provider），程序读取它。
- [ ] 设 `PARROTCODE_CONFIG` 指向另一份 YAML：优先级高于 cwd 的 `.parrocode.yaml`（横幅显示新文件里的 name/model）。
- [ ] `PARROTCODE_CONFIG` 指向不存在的路径：程序打印"环境变量 ... 指向的文件不存在"并退出码 1。
- [ ] 删除 cwd 配置，在 `~/.parrotcode/config.yaml` 放一份：程序读取它。
- [ ] 跨平台：Windows 与 macOS/Linux 下用户目录发现均工作。

### 9.3 YAML 错误报告（带行号）

- [ ] 故意把 `.parrocode.yaml` 写成缩进错误（如 `providers:` 下项不缩进）：错误信息包含**行号**（如"行：3"）。
- [ ] 故意漏写 `name` 字段：错误信息指明 `providers[0].name`。
- [ ] 两个 provider 同名：错误信息指明重复的 name 与索引。
- [ ] `active_provider: foo`（foo 不存在）：错误信息指明 `active_provider 'foo'`。
- [ ] 空文件：报"配置文件为空"。
- [ ] `protocol: foo`：报"不支持（允许: mock/openai/anthropic）"。

### 9.4 Provider 切换

- [ ] 配置两个 mock provider（`mock-a` / `mock-b`，不同 model），切换 `active_provider`，启动横幅显示对应 name/model。
- [ ] 把 `active_provider` 指向一个 `protocol: openai` 的 provider：程序打印"将在迭代 3 实现，本迭代仅支持 mock"并退出码 1（不崩溃、不静默）。
- [ ] `active_provider` 省略：回退 `providers[0]`，stderr 有 warning 日志。

### 9.5 环境变量展开

- [ ] `api_key: ${MY_KEY}`，设 `MY_KEY=secret123`：加载成功，`ApiKey` 为 `secret123`。
- [ ] 未设 `MY_KEY`：报"环境变量 'MY_KEY' 未设置（引用于 providers[0].api_key）"。
- [ ] `${MY_KEY}` 也用于 `base_url` / `model` 时同样展开。

### 9.6 敏感信息

- [ ] `dotnet run` 全程 stderr 日志**不**出现 ApiKey 明文。
- [ ] 启动横幅与错误信息**不**回显 ApiKey。
- [ ] `.gitignore` 包含 `.parrocode.yaml`；`example.parrocode.yaml` 不被忽略。
- [ ] `example.parrocode.yaml` 中 `api_key` 全部用 `${...}` 占位，无明文 key。

### 9.7 迁移与回归

- [ ] `IChatProvider.cs` 已删除；`IBaseProvider.cs` 已新增。
- [ ] `MockProvider : IBaseProvider`。
- [ ] `appsettings.json` 已删除，csproj 对应 `None` 项已移除。
- [ ] `dotnet run` 输入 `你好` → 输出 `你好（mock）`（迭代 1 行为保持）。
- [ ] 输入 `exit` / `quit` / `Ctrl+Z`+回车 / `Ctrl+C` 退出行为与迭代 1 一致。
- [ ] 日志/输出分离保持：`out.txt` 只含用户可见输出，`err.txt` 只含日志（沿用迭代 1 §7.5 验证方式）。

### 9.8 健壮性

- [ ] 配置加载/Provider 创建失败时，程序打印友好错误并退出码 1，**不**抛未处理异常堆栈。
- [ ] 默认 mock 配置下，迭代 1 的健壮性验收（MockProvider 抛异常时主循环继续）仍成立。

## 十、进阶练习（可选，不计入验收）

1. 用 `Microsoft.Extensions.Configuration` 把 YAML 接入 `IConfiguration`，对比手写 `ConfigLoader` 的差异，体会"约定优于手写"。
2. 给 `ConfigLoader` 加 `--config <path>` 命令行参数（接 `explicitPath`）。
3. 让 `logging.level` 进 YAML，动态调日志级别。
4. ApiKey 掩码工具：实现 `ApiKeyMask(string)` → `sk-***1234`，在所有日志/错误信息中统一过一遍。

## 十一、风险与注意事项

| 风险 | 缓解 |
| --- | --- |
| YamlDotNet 对 record 的反序列化兼容性 | 默认用 record + init；若失败退路为 `{ get; set; }` class，类型形状不变（见 §4.3.3 注） |
| 命名约定错配（YAML `snake_case` vs C# `PascalCase`）导致字段全 null | `WithNamingConvention(UnderscoredNamingConvention.Instance)`；单测 §8.1 覆盖"缺字段报错"反例 |
| 环境变量展开 `${VAR}` 与 YAML 里本就含 `$` 的值冲突 | 仅匹配 `${[A-Z0-9_]+}`，普通 `$` 不动；单测覆盖 |
| 测试污染开发者本机 `~/.parrotcode/config.yaml` | 涉及 home 的用例用 `explicitPath` 注入临时文件，不真写用户目录；`PARROTCODE_CONFIG` 用例在 finally 还原 |
| ApiKey 误进日志 | §4.8 约定 + §9.6 验收；代码 review 时 grep `ApiKey` 在日志调用处的出现 |
| `IChatProvider` 删除导致外部引用 | 本项目无外部消费者；内部仅 `MockProvider` / `App` / 测试，一并迁移 |
| 配置文件编码（BOM/UTF-8） | `File.ReadAllText` 默认 UTF-8；中文 YAML 需以 UTF-8 保存，验收时验证中文 provider name 显示正常 |
| `active_provider` 大小写敏感导致用户困惑 | 本迭代大小写敏感（与 `name` 一致）；文档示例用小写，不引入 case-insensitive 以免与 name 重复检测规则不一致 |

## 十二、交付清单

- [ ] `ParrotCode.Net/Config/Models.cs`
- [ ] `ParrotCode.Net/Config/ConfigException.cs`
- [ ] `ParrotCode.Net/Config/Loader.cs`
- [ ] `ParrotCode.Net/Providers/IBaseProvider.cs`
- [ ] `ParrotCode.Net/Providers/MessageTypes.cs`
- [ ] `ParrotCode.Net/Providers/ProviderFactory.cs`
- [ ] `ParrotCode.Net/Providers/MockProvider.cs`（改为 `IBaseProvider`）
- [ ] `ParrotCode.Net/App/App.cs`（改用 `IBaseProvider` + `ProviderConfig`）
- [ ] `ParrotCode.Net/Program.cs`（装配走配置）
- [ ] `ParrotCode.Net/example.parrocode.yaml`
- [ ] `ParrotCode.Net/ParrotCode.Net.csproj`（加 YamlDotNet，删 appsettings 项）
- [ ] 删除 `ParrotCode.Net/Providers/IChatProvider.cs`、`ParrotCode.Net/appsettings.json`
- [ ] `ParrotCode.Net-xUnit/ConfigLoaderTests.cs`
- [ ] `ParrotCode.Net-xUnit/ProviderFactoryTests.cs`
- [ ] `ParrotCode.Net-xUnit/MockProviderTests.cs`（迁移）
- [ ] `.gitignore` 加 `.parrocode.yaml`
- [ ] 演示：三级发现切换 + YAML 错误截图 + `dotnet test` 全绿截图
- [ ] 本文档状态改为 `[已完成]`
