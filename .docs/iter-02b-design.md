# 迭代 2b：配置系统 — 详细设计

> 状态：[进行中]
> 对应 `plan.md` 第二章「迭代 2」的后半部分。原迭代 2 拆分为 2a（Provider 抽象层，已完成）+ 2b（配置系统），本迭代为 2b。
> 前置：迭代 2a 已交付 `IBaseProvider` / `Message` / `ProviderConfig` / `ProviderFactory.Create` / `MockProvider`，App 已接收 `(IBaseProvider, ProviderConfig, ILogger, CT)`。本迭代在其上加配置加载层。

## 一、概述

把迭代 2a 中**硬编码**的 `ProviderConfig` 来源，替换为**从 YAML 加载**：三级发现（环境变量 > 项目目录 > 用户目录）+ YamlDotNet 解析 + 语义校验 + `${VAR}` 环境变量展开 + 带行号的错误报告。

本迭代**刻意保持**：
- **不做**真实 LLM HTTP 调用（迭代 3）。`openai`/`anthropic` 协议在工厂仍抛 `ProviderNotImplementedException`——因此本迭代运行时验证以 **mock** 配置为主；`example.parrotcode.yaml` 里 DeepSeek 为主推示例，但 2b 阶段把 `active_provider` 指向 deepseek 会得到"将在迭代 3 实现"提示并退出（这是预期行为，用于验证配置加载成功选中 + 工厂异常路径）。
- **不改** `IBaseProvider` / `Message` / `MockProvider` / `App` 的签名（2a 已立好）。本迭代只改 `Program` 装配来源 + 新增 `Config/` 三个文件 + 给 `ProviderFactory` 加 `CreateActive`。
- **不做**配置热重载、`Microsoft.Extensions.Configuration` 接入（进阶练习）。

> 命名说明：`plan.md` 中配置文件名写作 `parrotcode.yaml`（缺 `t`），而环境变量写作 `PARROTCODE_CONFIG`（含 `t`），二者不一致。本迭代统一采用 **`parrotcode`**（与项目名 `ParrotCode.Net`、环境变量 `PARROTCODE_CONFIG` 一致）。配置文件名为 `.parrotcode.yaml`（与 plan.md 字面一致，仅作文件名）。

## 二、学习目标

1. **配置三级发现**：`explicitPath` > 环境变量 > 项目目录 > 用户目录 > 默认，理解优先级与"不合并"语义。
2. **YAML 解析与错误报告**：YamlDotNet 反序列化 record + `YamlException` 行号捕获 + 自定义语义校验。
3. **环境变量展开 `${VAR}`**：降低敏感信息误提交风险，未设置时报清晰错误。
4. **敏感信息纪律**：ApiKey 只进 YAML/环境变量，不进代码、不进日志。
5. **工厂的配置驱动装配**：`CreateActive(AppConfig)` 按 `active_provider` 选中，体会"配置 → 路由"解耦。

## 三、范围

### 3.1 本迭代包含（In Scope）

| 项 | 说明 |
| --- | --- |
| 顶层配置模型 | `AppConfig`（`ActiveProvider` + `Providers`），追加到 `Config/Models.cs`（`ProviderConfig` 已在 2a 定义） |
| 配置异常 | `ConfigException`（带 `SourcePath` / `Line` / `Column`） |
| 配置加载器 | `ConfigLoader`：三级发现 + YamlDotNet 解析 + 校验 + `${VAR}` 展开 |
| 工厂扩展 | `ProviderFactory.CreateActive(AppConfig)`：按 `active_provider`（回退 `providers[0]`）选中并创建 |
| Program 装配 | `ConfigLoader.Load()` → `CreateActive`，加 `try/catch` 友好退出（退出码 1） |
| 示例配置 | `example.parrotcode.yaml`（tracked，DeepSeek 主推）+ `.parrotcode.yaml`（gitignored） |
| .gitignore | 忽略 `.parrotcode.yaml`，保留 `example.parrotcode.yaml` |
| 移除占位 | 删除 `appsettings.json` 及其 csproj `None` 项（被 YAML 取代） |
| 依赖 | 新增 `YamlDotNet` |

### 3.2 本迭代不包含（Out of Scope）

- 真实 LLM HTTP 调用、SSE 流式、`OpenAIProvider`/`AnthropicProvider` 实现 → 迭代 3
- DeepSeek 的真实联调（2b 仅验证配置加载与工厂异常路径；真实调用在迭代 3）→ 迭代 3
- 多轮对话历史 → 迭代 4
- 工具调用产生与执行 → 迭代 5/6
- `Microsoft.Extensions.Configuration` / DI 容器接入、配置热重载、`logging.level` 入 YAML → 进阶练习
- `--config <path>` 命令行参数 → 进阶练习（`ConfigLoader.Load(explicitPath)` 已预留入口）

### 3.3 与迭代 2a 的衔接

| 2a 已交付 | 2b 处理 |
| --- | --- |
| `ProviderConfig`（`Config/Models.cs`） | **不重定义**；2b 在同文件追加 `AppConfig`，`ProviderConfig` 字段不变 |
| `ProviderFactory.Create(ProviderConfig)` | **保留**；2b 追加 `CreateActive(AppConfig)`（内部调 `Create`） |
| `IBaseProvider` / `Message` / `MockProvider` / `App` | **零改动** |
| `Program.cs` 硬编码 `ProviderConfig` | 改为 `ConfigLoader.Load()` → `CreateActive`，加 try/catch |
| `appsettings.json`（迭代 1 占位） | 删除（被 YAML 取代） |

## 四、架构设计

### 4.1 模块结构（2b 增量）

```
ParrotCode.Net/
├── Program.cs                 # 改：ConfigLoader.Load → CreateActive → 装配 App（加 try/catch）
├── App/
│   └── App.cs                 # 不变（2a 已接收 ProviderConfig）
├── Config/
│   ├── Models.cs              # 追加 AppConfig（ProviderConfig 已在）
│   ├── ConfigException.cs     # 新增：带 SourcePath/Line/Column
│   └── Loader.cs              # 新增：三级发现 + 解析 + 校验 + ${VAR} 展开
├── Providers/
│   └── ProviderFactory.cs     # 追加 CreateActive(AppConfig)
├── example.parrotcode.yaml     # 新增（tracked，DeepSeek 主推）
└── ParrotCode.Net.csproj      # 加 YamlDotNet；删 appsettings.json 项
```

> 命名空间约定沿用迭代 1/2a：所有源文件统一 `namespace ParrotCode`，文件夹仅作物理组织。

### 4.2 调用流程

```
┌─────────┐  PARROTCODE_CONFIG / .parrotcode.yaml / ~/.parrotcode/config.yaml
│  启动   │ ────────────────────────────────────────────────────▶ ┌──────────────┐
│ Program │ ◀──────────────────────── AppConfig (含 active+list) ┤ ConfigLoader │
└────┬────┘                                                       └──────────────┘
     │ ProviderFactory.CreateActive(config)
     ▼
┌──────────────────┐  选中的 ProviderConfig   ┌─────────────────┐
│ ProviderFactory  │ ───────────────────────▶ │ MockProvider 等 │ (IBaseProvider)
└──────────────────┘                          └─────────────────┘
     │ IBaseProvider + 选中的 ProviderConfig
     ▼
┌──────────┐  new[]{ Message(User, line) }   ┌──────────────┐
│   App    │ ──────────────────────────────▶ │ MockProvider │
│ (主循环) │ ◀──── string reply ──────────── └──────────────┘
└──────────┘
```

启动伪代码：

```
try config = ConfigLoader.Load()
catch ConfigException: 打印错误（含文件/行号）→ 退出码 1

try provider = ProviderFactory.CreateActive(config)
catch ProviderNotImplementedException: 提示"迭代 3 实现" → 退出码 1
catch ConfigException / ArgumentException: 打印错误 → 退出码 1

log "使用 provider={name} model={model} protocol={protocol}"  // 不含 api_key
app = new App(provider, 选中ProviderConfig, logger, ct)
await app.RunAsync()
```

### 4.3 关键类型设计

#### 4.3.1 `AppConfig`（追加到 `Config/Models.cs`）

```csharp
namespace ParrotCode;

/// <summary>顶层配置，对应 .parrotcode.yaml 的根结构。</summary>
public sealed record AppConfig
{
    /// <summary>当前激活的 Provider 名称；为 null 时回退到 providers[0].name。</summary>
    public string? ActiveProvider { get; init; }

    /// <summary>Provider 列表。无配置文件时由 Loader 提供默认 mock 项。
    /// 用 IList 而非 IReadOnlyList：YamlDotNet 需要可变集合来填充（消费方仍按只读语义使用）。</summary>
    public IList<ProviderConfig> Providers { get; init; } = Array.Empty<ProviderConfig>();
}
```

> `ProviderConfig`（2a 已定义，5 字段）不动。YamlDotNet 反序列化 `AppConfig` 时，`providers` 节每项反序列化为 `ProviderConfig`。

#### 4.3.2 `ConfigException`（`Config/ConfigException.cs`）

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

#### 4.3.3 `ConfigLoader`（`Config/Loader.cs`）

```csharp
using System.Text.RegularExpressions;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace ParrotCode;

public static class ConfigLoader
{
    public const string EnvVar = "PARROTCODE_CONFIG";
    public const string CwdFileName = ".parrotcode.yaml";
    public const string UserDirName = ".parrotcode";
    public const string UserFileName = "config.yaml";

    /// <summary>按三级发现加载；无任何配置时返回默认 mock 配置。</summary>
    public static AppConfig Load() => Load(explicitPath: null);

    /// <summary>explicitPath 优先级最高（用于测试与未来 --config 参数）。</summary>
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

#### 4.3.4 `ProviderFactory.CreateActive`（追加到 `Providers/ProviderFactory.cs`）

```csharp
    /// <summary>按 active_provider（回退 providers[0]）选中并创建。</summary>
    public static IBaseProvider CreateActive(AppConfig appConfig)
    {
        ArgumentNullException.ThrowIfNull(appConfig);
        if (appConfig.Providers.Count == 0)
            throw new ConfigException("providers 不能为空");

        var name = appConfig.ActiveProvider ?? appConfig.Providers[0].Name;
        var pc = appConfig.Providers.FirstOrDefault(p => p.Name == name)
            ?? throw new ConfigException($"active_provider '{name}' 未在 providers 中定义");
        return Create(pc);
    }
```

> `CreateActive` 自包含空列表与命中校验（防御性，不依赖外部 `Validate`）。`ConfigLoader.Validate` 也会校验 `active_provider` 命中——双重校验：`Validate` 保证加载阶段早失败、`CreateActive` 保证工厂作为公开 API 的健壮性。

#### 4.3.5 `Program.cs` 装配（2b 版，替换 2a 的硬编码段）

```csharp
AppConfig config;
try
{
    config = ConfigLoader.Load();
}
catch (ConfigException ex)
{
    AnsiConsole.MarkupLine($"[red]配置错误：[/]{Markup.Escape(ex.Message)}");
    if (ex.SourcePath is not null)
        AnsiConsole.MarkupLine($"[grey]  文件：{Markup.Escape(ex.SourcePath)}[/]");
    if (ex.Line is not null)
        AnsiConsole.MarkupLine($"[grey]  行：{ex.Line}{(ex.Column is null ? "" : $"，列：{ex.Column}")}[/]");
    return 1;
}

ProviderConfig activeConfig;
IBaseProvider provider;
try
{
    provider = ProviderFactory.CreateActive(config);
    // CreateActive 内部未暴露选中的 ProviderConfig，需在外部重新解析以传给 App
    var activeName = config.ActiveProvider ?? config.Providers[0].Name;
    activeConfig = config.Providers.First(p => p.Name == activeName);
}
catch (ProviderNotImplementedException ex)
{
    AnsiConsole.MarkupLine($"[yellow]提示：[/]{Markup.Escape(ex.Message)}");
    return 1;
}
catch (ConfigException ex)
{
    AnsiConsole.MarkupLine($"[red]配置错误：[/]{Markup.Escape(ex.Message)}");
    return 1;
}
catch (ArgumentException ex)
{
    AnsiConsole.MarkupLine($"[red]配置错误：[/]{Markup.Escape(ex.Message)}");
    return 1;
}

logger.LogInformation("使用 provider={Name} model={Model} protocol={Protocol}",
    activeConfig.Name, activeConfig.Model, activeConfig.Protocol);  // 注意：不记 ApiKey

var app = new App(provider, activeConfig, logger, cts.Token);
await app.RunAsync();
return 0;
```

> `CreateActive` 返回 `IBaseProvider` 但不返回选中的 `ProviderConfig`，而 `App` 启动横幅需要它。这里在 catch 外重新解析 `activeName`/`activeConfig`。若嫌重复，可让 `CreateActive` 改返回 `(IBaseProvider, ProviderConfig)` 元组——但为保持 2a 的 `Create(ProviderConfig)` 风格一致，2b 采用外部解析。实现时若觉得元组更干净，可调整（不影响测试）。

### 4.4 配置三级发现

`ResolvePath(explicitPath)` 顺序（先命中先用，**不合并**）：

| 优先级 | 来源 | 路径 | 不存在时 |
| --- | --- | --- | --- |
| 0（最高） | `explicitPath` 参数 | 调用方指定 | 抛 `ConfigException("指定的配置文件不存在: ...")` |
| 1 | 环境变量 | `$PARROTCODE_CONFIG` 指向的路径 | 抛 `ConfigException("环境变量 PARROTCODE_CONFIG 指向的文件不存在: ...")` |
| 2 | 项目目录 | `$(pwd)/.parrotcode.yaml` | 继续下一级 |
| 3 | 用户目录 | `~/.parrotcode/config.yaml` | 返回 null → 用默认 mock 配置 |

> - 环境变量与 `explicitPath` 指向不存在路径是**错误**（用户明确意图落空），不是静默回退。
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
        .WithNamingConvention(UnderscoredNamingConvention.Instance)  // snake_case ↔ PascalCase
        .Build();
    try
    {
        return deserializer.Deserialize<AppConfig>(text)
            ?? throw new ConfigException("配置文件内容为 null", path);
    }
    catch (YamlException ex)
    {
        throw new ConfigException(
            $"YAML 解析失败: {ex.Message}",
            path, ex.Start.Line, ex.Start.Column, ex);
    }
}
```

> **命名约定**：YAML 字段用 `snake_case`（`active_provider` / `base_url` / `api_key`），C# 属性用 PascalCase。`UnderscoredNamingConvention` 把 `base_url` ↔ `BaseUrl`。错配会导致字段全 null（被校验拦截，见 §4.7 规则 2/3）。

错误报告格式（用户可见，走 stdout/Spectre）：

```
配置错误：YAML 解析失败: (Line: 5, Col: 3) ...
  文件：D:\proj\.parrotcode.yaml
  行：5，列：3
```

### 4.6 环境变量展开 `${VAR}`

对 `ProviderConfig` 的所有字符串字段（`Name` / `Protocol` / `Model` / `BaseUrl` / `ApiKey`）展开 `${VAR_NAME}`：

```csharp
private static string ExpandEnv(string value, string fieldPath, string sourcePath)
{
    if (string.IsNullOrEmpty(value) || !value.Contains("${")) return value;
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

- 鼓励 `api_key: ${DEEPSEEK_API_KEY}` 而非明文，降低误提交风险。
- 未设置/空 → `ConfigException`，指明引用字段路径（如 `providers[0].api_key`）。
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

> 迭代 2b **不**校验 `openai`/`anthropic` 的 `model`/`base_url`/`api_key` 必填——因为本迭代它们不会被实例化（工厂会抛 `ProviderNotImplementedException`）。必填校验留到迭代 3 实现真实 Provider 时再加，避免本迭代对未实现协议过度约束。

### 4.8 敏感信息处理

- **ApiKey 来源**：YAML 字段或 `${ENV_VAR}`，不进代码。
- **日志掩码**：任何地方记录 provider 信息时，只记 `Name` / `Model` / `Protocol`，**禁止**记 `ApiKey`。若需提示 key 状态，掩码为 `sk-***1234`（前缀 + 后 4 位）或仅记"已设置/未设置"。
- **.gitignore**：忽略 `.parrotcode.yaml` 与 `~/.parrotcode/`，保留 `example.parrotcode.yaml`。
- **错误信息**：配置错误信息只显示字段路径，不回显 ApiKey 原文。

## 五、依赖变更

`ParrotCode.Net.csproj`：

```xml
<!-- 新增 -->
<PackageReference Include="YamlDotNet" Version="16.*" />

<!-- 移除 appsettings.json 项（占位配置被 YAML 取代） -->
<!-- 删除类似以下项（实现时按 csproj 实际内容对照）：
<None Update="appsettings.json">
  <CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory>
</None>
-->
```

> 版本号实现时锁定当时最新稳定版（设计文档写 `16.*`，csproj 锁具体版本）。

`ParrotCode.Net-xUnit.csproj`：无新增依赖（`FluentAssertions` + `xUnit` 已在）。

## 六、配置文件

### 6.1 `example.parrotcode.yaml`（tracked，DeepSeek 主推）

```yaml
# ParrotCode.Net 配置示例。复制为 .parrotcode.yaml 后按需修改。
# .parrotcode.yaml 已被 .gitignore 忽略，不会被提交。
#
# DeepSeek 为主测试目标（OpenAI 兼容协议，迭代 3 起可真实联调）；
# OpenAI / Anthropic 保留兼容，需各自 key。

# 当前激活的 provider 名称（对应下方 providers 中某项的 name）。
# 省略时回退到 providers[0]。
active_provider: deepseek

providers:
  - name: deepseek
    protocol: openai
    model: deepseek-chat
    base_url: https://api.deepseek.com/v1
    api_key: ${DEEPSEEK_API_KEY}

  - name: mock
    protocol: mock
    model: mock-1

  - name: openai
    protocol: openai
    model: gpt-4o-mini
    base_url: https://api.openai.com/v1
    api_key: ${OPENAI_API_KEY}

  - name: claude
    protocol: anthropic
    model: claude-3-5-sonnet-20241022
    base_url: https://api.anthropic.com
    api_key: ${ANTHROPIC_API_KEY}
```

> - DeepSeek 置首位且作 `active_provider`，体现"主测试目标"定位。
> - DeepSeek 与 OpenAI 同为 `protocol: openai`，由 `base_url` 区分端点（见 2a 文档 §3.3）。
> - 2b 阶段若把 `active_provider` 指向 `deepseek`，工厂抛 `ProviderNotImplementedException`（迭代 3 实现后可用）；指向 `mock` 则正常跑。
> - 所有 `api_key` 用 `${...}` 占位，无明文。

### 6.2 `.parrotcode.yaml`（gitignored，用户实际配置）

用户按需从 example 复制并填写真实 key（或设环境变量）。本迭代验收阶段会临时创建/删除它。

### 6.3 移除 `appsettings.json`

迭代 1 的 `appsettings.json` 是占位，本迭代被 YAML 取代。日志配置仍保留在 `Program.cs` 代码中（迭代 1 已如此）。若后续想让日志级别走配置，放进 `.parrotcode.yaml` 的 `logging` 节即可，不在本迭代做。

## 七、迁移说明（迭代 2a → 迭代 2b）

| 2a | 2b | 处理 |
| --- | --- | --- |
| `Program.cs` 硬编码 `new ProviderConfig{...}` + `Factory.Create` | `ConfigLoader.Load()` + `Factory.CreateActive(config)` | 装配走配置；加 try/catch 友好退出 |
| 无 `AppConfig` | `Config/Models.cs` 追加 `AppConfig` | `ProviderConfig` 不动 |
| 无 `ConfigLoader` / `ConfigException` | 新增 `Config/Loader.cs` / `ConfigException.cs` | — |
| `ProviderFactory.Create` | 追加 `CreateActive(AppConfig)` | `Create` 保留 |
| `appsettings.json` | 删除 + csproj 移除对应 `None` 项 | 被 YAML 取代 |
| `App` / `MockProvider` / `IBaseProvider` / `Message` | **零改动** | 2a 已立好 |

迁移后回归不变式：无任何配置文件时 `dotnet run` 仍走默认 mock，输入 `你好` → `你好（mock）`；Ctrl+C 仍干净退出。

## 八、单元测试

### 8.1 `ConfigLoaderTests`（新增）

| 用例 | 期望 |
| --- | --- |
| 无任何配置文件（env/cwd/home 均无） | 返回默认 mock 配置，`ActiveProvider=="mock"`，1 个 provider |
| `explicitPath` 指向合法 YAML | 加载该文件 |
| `explicitPath` 指向不存在文件 | 抛 `ConfigException`，消息含路径 |
| `PARROTCODE_CONFIG` 指向合法文件 | 优先于 cwd/home |
| `PARROTCODE_CONFIG` 指向不存在文件 | 抛 `ConfigException` |
| cwd 有 `.parrotcode.yaml` | 优先于 home |
| 仅 home 有 `~/.parrotcode/config.yaml` | 加载它（用 explicitPath 模拟，避免污染本机用户目录） |
| YAML 语法错误（缩进/非法字符） | 抛 `ConfigException`，`Line` 非 null |
| YAML 缺 `name` 字段 | 抛 `ConfigException`，消息含 `providers[0].name` |
| 两个 provider 同名 | 抛 `ConfigException`，消息含重复名 |
| `active_provider` 指向不存在 name | 抛 `ConfigException` |
| `active_provider` 为 null | 回退 `providers[0]`，不报错 |
| `api_key: ${MY_KEY}`，`MY_KEY` 已设置 | `ApiKey == 环境变量值` |
| `api_key: ${MY_KEY}`，`MY_KEY` 未设置 | 抛 `ConfigException`，消息含 `MY_KEY` 与字段路径 |
| 空文件（仅空白） | 抛 `ConfigException("配置文件为空")` |
| `protocol: foo` | 抛 `ConfigException`，消息含允许集合 |

> 测试隔离：每个用例建临时目录写 `.parrotcode.yaml`，用 `ConfigLoader.Load(explicitPath)` 注入，避免污染开发者本机配置。涉及环境变量的用例用 `Environment.SetEnvironmentVariable` 设置并在 `finally` 还原；涉及 home 目录的用 `explicitPath` 模拟，不真写用户目录。

### 8.2 `ProviderFactoryTests`（2a 已有 6 用例，2b 补充 CreateActive）

| 新增用例 | 期望 |
| --- | --- |
| `CreateActive`，`active_provider` 命中 mock | 返回 `MockProvider` |
| `CreateActive`，`active_provider` 命中 openai | 抛 `ProviderNotImplementedException` |
| `CreateActive`，`active_provider` 未命中 | 抛 `ConfigException`，消息含 active_provider 名 |
| `CreateActive`，`active_provider` 为 null | 回退 `providers[0]`，返回其对应实例 |
| `CreateActive`，`providers` 为空 | 抛 `ConfigException("providers 不能为空")` |
| `CreateActive(null)` | 抛 `ArgumentNullException` |

### 8.3 回归

- `dotnet test` 全绿（含 2a 的 `MockProviderTests` / `ProviderFactoryTests` 既有用例 + 2b 新增）。
- `dotnet run`（无配置文件）手测：输入 `你好` → `你好（mock）`。

## 九、验收标准

> 全部满足方可标记 `[已完成]`。每条尽量可被命令或肉眼验证。

### 9.1 构建与运行

- [ ] `ParrotCode.Net.csproj` 新增 `YamlDotNet`；移除 `appsettings.json` 的 `None` 项。
- [ ] `dotnet build` 无 error、无 warning。
- [ ] `dotnet test` 全绿（含 2a 既有 + 2b 新增 `ConfigLoaderTests` / `ProviderFactoryTests` 补充）。
- [ ] `dotnet run`（无配置文件）能启动，启动横幅显示 `provider= mock model= mock-1 protocol= mock`。

### 9.2 配置三级发现

- [ ] 删除 cwd 与 home 配置、未设环境变量：`dotnet run` 用默认 mock 配置正常工作。
- [ ] 在 cwd 放 `.parrotcode.yaml`（`active_provider: mock` + 一个 mock provider），程序读取它（横幅显示新 name/model）。
- [ ] 设 `PARROTCODE_CONFIG` 指向另一份 YAML：优先级高于 cwd 的 `.parrotcode.yaml`。
- [ ] `PARROTCODE_CONFIG` 指向不存在的路径：程序打印"环境变量 ... 指向的文件不存在"并退出码 1。
- [ ] 删除 cwd 配置，在 `~/.parrotcode/config.yaml` 放一份：程序读取它。
- [ ] 跨平台：Windows 与 macOS/Linux 下用户目录发现均工作。

### 9.3 YAML 错误报告（带行号）

- [ ] 故意把 `.parrotcode.yaml` 写成缩进错误（如 `providers:` 下项不缩进）：错误信息包含**行号**（如"行：3"）。
- [ ] 故意漏写 `name` 字段：错误信息指明 `providers[0].name`。
- [ ] 两个 provider 同名：错误信息指明重复的 name 与索引。
- [ ] `active_provider: foo`（foo 不存在）：错误信息指明 `active_provider 'foo'`。
- [ ] 空文件：报"配置文件为空"。
- [ ] `protocol: foo`：报"不支持（允许: mock/openai/anthropic）"。

### 9.4 Provider 切换（含 DeepSeek 主推）

- [ ] `example.parrotcode.yaml` 含 `deepseek` 配置（`protocol: openai`、`base_url: https://api.deepseek.com/v1`、`api_key: ${DEEPSEEK_API_KEY}`），且 `active_provider: deepseek`。
- [ ] 配置两个 mock provider（`mock-a` / `mock-b`，不同 model），切换 `active_provider`，启动横幅显示对应 name/model。
- [ ] 把 `.parrotcode.yaml` 的 `active_provider` 指向一个 `protocol: openai` 的 provider（如 deepseek）：程序打印"将在迭代 3 实现，本迭代仅支持 mock"并退出码 1（不崩溃、不静默）——验证配置加载成功选中 + 工厂异常路径。
- [ ] `active_provider` 省略：回退 `providers[0]`，stderr 有 warning 日志。

### 9.5 环境变量展开

- [ ] `api_key: ${MY_KEY}`，设 `MY_KEY=secret123`：加载成功，`ApiKey` 为 `secret123`（单测断言；运行时不打印 key）。
- [ ] 未设 `MY_KEY`：报"环境变量 'MY_KEY' 未设置（引用于 providers[0].api_key）"。
- [ ] `${MY_KEY}` 也用于 `base_url` / `model` 时同样展开。

### 9.6 敏感信息

- [ ] `dotnet run` 全程 stderr 日志**不**出现 ApiKey 明文。
- [ ] 启动横幅与错误信息**不**回显 ApiKey。
- [ ] `.gitignore` 包含 `.parrotcode.yaml`；`example.parrotcode.yaml` 不被忽略。
- [ ] `example.parrotcode.yaml` 中所有 `api_key` 用 `${...}` 占位，无明文 key。

### 9.7 迁移与回归

- [ ] `appsettings.json` 已删除，csproj 对应 `None` 项已移除。
- [ ] `Config/Models.cs` 含 `AppConfig` 与 `ProviderConfig`（后者来自 2a，未重定义）。
- [ ] `ProviderFactory` 同时有 `Create`（2a）与 `CreateActive`（2b）。
- [ ] `App` / `MockProvider` / `IBaseProvider` / `Message` 与 2a 相比**无改动**。
- [ ] `dotnet run`（无配置）输入 `你好` → 输出 `你好（mock）`（迭代 1/2a 行为保持）。
- [ ] 输入 `exit` / `quit` / `Ctrl+Z`+回车 / `Ctrl+C` 退出行为与 2a 一致。
- [ ] 日志/输出分离保持：`out.txt` 只含用户可见输出，`err.txt` 只含日志（沿用迭代 1 §7.5 验证方式）。

### 9.8 健壮性

- [ ] 配置加载失败（语法/校验/环境变量）：程序打印友好错误（含文件/行号）并退出码 1，**不**抛未处理异常堆栈。
- [ ] `CreateActive` 抛 `ProviderNotImplementedException`（active 指向 openai/anthropic）：打印提示并退出码 1，不崩溃。
- [ ] 默认 mock 配置下，迭代 1 的健壮性验收（MockProvider 抛异常时主循环继续）仍成立。

## 十、进阶练习（可选，不计入验收）

1. 用 `Microsoft.Extensions.Configuration` 把 YAML 接入 `IConfiguration`，对比手写 `ConfigLoader` 的差异，体会"约定优于手写"。
2. 给 `ConfigLoader` 加 `--config <path>` 命令行参数（接 `explicitPath`，`Load(explicitPath)` 已预留）。
3. 让 `logging.level` 进 YAML，动态调日志级别。
4. ApiKey 掩码工具：实现 `ApiKeyMask(string)` → `sk-***1234`，在所有日志/错误信息中统一过一遍。
5. 让 `CreateActive` 返回 `(IBaseProvider, ProviderConfig)` 元组，消除 §4.3.5 中 `activeConfig` 的外部重复解析。

## 十一、风险与注意事项

| 风险 | 缓解 |
| --- | --- |
| YamlDotNet 对 record 的反序列化兼容性 | 默认用 record + init；若失败退路为 `{ get; set; }` class，类型形状不变（`ProviderConfig`/`AppConfig` 均如此） |
| 命名约定错配（YAML `snake_case` vs C# `PascalCase`）导致字段全 null | `WithNamingConvention(UnderscoredNamingConvention.Instance)`；单测 §8.1 覆盖"缺字段报错"反例 |
| 环境变量展开 `${VAR}` 与 YAML 里本就含 `$` 的值冲突 | 仅匹配 `${[A-Z0-9_]+}`，普通 `$` 不动；单测覆盖 |
| 测试污染开发者本机 `~/.parrotcode/config.yaml` | 涉及 home 的用例用 `explicitPath` 注入临时文件，不真写用户目录；`PARROTCODE_CONFIG` 用例在 `finally` 还原 |
| ApiKey 误进日志 | §4.8 约定 + §9.6 验收；代码 review 时 grep `ApiKey` 在日志调用处的出现 |
| `appsettings.json` 删除后日志配置丢失 | 日志配置在 `Program.cs` 代码中（迭代 1 已如此），不依赖 `appsettings.json`；删除不影响日志 |
| 2b 阶段 DeepSeek 配置不能真实联调 | 这是预期：2b 验证配置系统，真实调用在迭代 3。`active=deepseek` 走工厂异常路径，验证加载成功；§9.4 明确此行为 |
| `CreateActive` 不返回选中的 `ProviderConfig`，Program 需重复解析 | §4.3.5 已说明；进阶练习 5 提供元组改进方案。当前重复解析可接受（`config.Providers.First(p => p.Name == activeName)`） |
| 配置文件编码（BOM/UTF-8） | `File.ReadAllText` 默认 UTF-8；中文 YAML 需以 UTF-8 保存，验收时验证中文 provider name 显示正常 |

## 十二、交付清单

- [ ] `ParrotCode.Net/Config/Models.cs`（追加 `AppConfig`，`ProviderConfig` 来自 2a 不动）
- [ ] `ParrotCode.Net/Config/ConfigException.cs`（新增）
- [ ] `ParrotCode.Net/Config/Loader.cs`（新增）
- [ ] `ParrotCode.Net/Providers/ProviderFactory.cs`（追加 `CreateActive`）
- [ ] `ParrotCode.Net/Program.cs`（装配走配置 + try/catch）
- [ ] `ParrotCode.Net/example.parrotcode.yaml`（DeepSeek 主推）
- [ ] `ParrotCode.Net/ParrotCode.Net.csproj`（加 YamlDotNet，删 appsettings 项）
- [ ] 删除 `ParrotCode.Net/appsettings.json`
- [ ] `ParrotCode.Net-xUnit/ConfigLoaderTests.cs`（新增）
- [ ] `ParrotCode.Net-xUnit/ProviderFactoryTests.cs`（补充 CreateActive 用例）
- [ ] `.gitignore` 加 `.parrotcode.yaml`
- [ ] 演示：三级发现切换 + YAML 错误截图 + `dotnet test` 全绿截图
- [ ] 本文档状态改为 `[已完成]`
