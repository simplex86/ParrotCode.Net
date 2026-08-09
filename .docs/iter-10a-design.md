# 迭代 10a：斜杠命令系统骨架（Registry + Parser + Dispatcher + 6 个内置命令）

> **状态**：[设计完成，待实现]
> **前置迭代**：9 [已完成]、8c [已完成]、7c-3 [已完成]
> **父文档**：[iter-10-design.md](iter-10-design.md)（保留追溯）
> **后续迭代**：10b（JSONL 会话持久化）、10c（项目指令 + 端到端装配）
> **目标**：交付斜杠命令系统的完整骨架——注册中心 + 解析器 + 分发器 + `IUiControl` 抽象 + 6 个内置命令（`/help` `/clear` `/compress` `/mode` `/status` `/exit`）。`/session` 命令在本迭代以 stub 形式注册（返回"未启用"），10b 接入真实 `SessionStore`。替换 `TerminalApp.HandleUserInput` 的硬编码命令分发，Tab 补全数据源改为动态。

---

## 一、迭代目标

### 1.1 核心目标

把 `TerminalApp.HandleUserInput` 中硬编码的 `/exit` `/clear` `/help` 升级为**可扩展的命令系统**：

1. **命令骨架**：`CommandType` / `ICommand` / `CommandContext` / `CommandResult` 四个核心类型。
2. **注册中心**：`CommandRegistry` 支持手动注册 + 反射自动扫描 `ICommand` 实现类，含别名冲突检测。
3. **解析器**：`CommandParser` 把 `/name args` 解析为 `(命令名, 参数)`，支持双引号参数。
4. **分发器**：`CommandDispatcher` 判断 `/` 前缀路由——命令走 Registry，非命令回退 AI。
5. **UI 抽象**：`IUiControl` 接口隔离命令与 `TerminalApp` 具体类型，便于测试。
6. **6 个内置命令**：`/help` `/clear` `/compress` `/mode` `/status` `/exit`（`/session` 用 stub）。

**本迭代完成后**：用户可用 `/help` 查看命令、`/clear` 清空历史、`/compress` 手动压缩、`/mode strict` 切换安全等级、`/status` 查看配置、`/exit` 退出。`/session` 提示"会话持久化未启用（10b 接入）"。

### 1.2 本迭代要验证的关键问题

| 问题 | 验证方式 | 失败对策 |
|------|---------|---------|
| 命令替换硬编码后 `/exit` `/clear` `/help` 行为是否等价 | 端到端手动回归 | `ExitCommand` / `ClearCommand` / `HelpCommand` 保持等价语义 |
| 反射自动扫描是否会扫到测试程序集的 `ICommand` | 单测 | `AutoRegisterFromAssembly` 默认 `Assembly.GetExecutingAssembly()`（主程序集） |
| `HelpCommand` 需依赖注入无法无参构造，自动扫描是否崩溃 | 单测 | 手动注册后再自动扫描（已注册的按 Name 跳过） |
| `/mode` 切换后 `StatusBarView` 是否同步刷新 | 手动 | `IUiControl.UpdateSecurityLevel` + `RefreshStatusBar` |
| Agent 运行时输入命令是否被忽略 | 手动 | `HandleUserInput` 顶部检查 `_agentTask.IsCompleted` |
| Tab 补全数据源改为动态后是否仍能补全 `/quit` 别名 | 手动 | `GetAllNamesWithAliases` 含别名 |

### 1.3 非目标（明确不做）

- ❌ 不实现 `SessionStore` / `MessageDto`（10b）
- ❌ 不实现 `InstructionLoader` / 项目指令注入（10c）
- ❌ `/session` 命令只注册 stub，返回"未启用"提示
- ❌ 不改 `AgentLoop` 核心循环（system prompt 注入在 10c）
- ❌ 不改 `App.cs` 装配 `SessionStore` / `InstructionLoader`（10b/10c）
- ❌ 不做斜杠命令的参数校验框架（各命令自行校验）

### 1.4 与现有代码并存策略

本迭代**替换** `HandleUserInput` 硬编码，**新增**命令系统骨架：

- `TerminalApp.HandleUserInput` 改用 `CommandDispatcher.DispatchAsync`
- `InputFieldView._commands` 硬编码数组改为 `SetCommands` 动态注入
- `TerminalApp` 实现 `IUiControl` 接口
- `/session` stub：`SessionCommand` 在 10a 注册但 `SessionStore` 为 null，返回"未启用"

---

## 二、文件改动清单

### 2.1 新增文件（11 个）

```
Commands/
├── CommandType.cs              # enum { System, Hidden }
├── ICommand.cs                 # 命令接口
├── CommandContext.cs           # 执行上下文（聚合 History/Compressor/SecurityGuard/Ui 等）
├── CommandResult.cs            # 执行结果（Handled/ExitApp/Output）
├── CommandRegistry.cs          # 注册中心 + 反射自动扫描 + 别名冲突检测
├── CommandParser.cs            # /name args 解析 + SplitArgs
├── CommandDispatcher.cs        # / 前缀路由分发
└── Builtin/
    ├── HelpCommand.cs          # /help（依赖 Registry，手动注册）
    ├── ClearCommand.cs         # /clear（清历史+UI+ResetWarning）
    ├── CompressCommand.cs      # /compress（手动触发，熔断器 open 先 Reset）
    ├── ModeCommand.cs          # /mode [strict|normal|permissive]
    ├── StatusCommand.cs        # /status（配置概要，指令字段 10c 填充）
    ├── SessionCommand.cs       # /session stub（10b 接入真实 Store）
    └── ExitCommand.cs          # /exit /quit
Tui/
└── IUiControl.cs               # UI 抽象接口
```

### 2.2 修改文件（2 个）

| 文件 | 改动 |
|------|------|
| `Tui/TerminalApp.cs` | 实现 `IUiControl`；`HandleUserInput` 改用 `CommandDispatcher`；构造函数注入 `CommandRegistry`/`CommandDispatcher`；`BuildLayout` 后调 `SetCommands` |
| `Tui/InputFieldView.cs` | 新增 `SetCommands(IReadOnlyList<string>)`；`_commands` 改为动态；`CompleteCommand` 逻辑不变 |

### 2.3 不变文件

- `Agent/AgentLoop.cs`（system prompt 注入在 10c）
- `App/App.cs`（SessionStore/InstructionLoader 装配在 10b/10c）
- `Config/Models.cs`（SessionConfig/InstructionsConfig 在 10b/10c）

---

## 三、详细设计

### 3.1 CommandType 枚举

```csharp
// Commands/CommandType.cs
namespace ParrotCode;

/// <summary>
/// 命令类型。决定命令在 /help 中的可见性。
/// </summary>
public enum CommandType
{
    /// <summary>系统命令：在 /help 中可见，用户可直接调用。</summary>
    System,

    /// <summary>隐藏命令：不在 /help 中显示，但仍可调用（如内部调试命令）。</summary>
    Hidden
}
```

### 3.2 ICommand 接口

```csharp
// Commands/ICommand.cs
namespace ParrotCode;

/// <summary>
/// 斜杠命令接口。所有命令实现此接口，由 CommandRegistry 反射自动扫描注册。
/// 命令是同步逻辑（无 LLM 调用），通过 CommandContext 操作 UI/History/Compressor 等。
/// </summary>
public interface ICommand
{
    /// <summary>命令名（不含 / 前缀），如 "help" / "clear" / "session"。</summary>
    string Name { get; }

    /// <summary>命令描述（/help 展示用，简短一句话）。</summary>
    string Description { get; }

    /// <summary>命令类型（System 在 /help 可见，Hidden 不可见）。</summary>
    CommandType Type { get; }

    /// <summary>命令别名（不含 / 前缀），如 exit 的别名 ["quit"]。空列表表示无别名。</summary>
    IReadOnlyList<string> Aliases { get; }

    /// <summary>用法示例（/help 展示用，如 "/session save" / "/mode strict"）。</summary>
    string Usage { get; }

    /// <summary>
    /// 执行命令。返回 CommandResult。
    /// 命令不应抛异常——错误信息通过 CommandResult.Output 返回。
    /// </summary>
    Task<CommandResult> ExecuteAsync(CommandContext context);
}
```

### 3.3 CommandResult

```csharp
// Commands/CommandResult.cs
namespace ParrotCode;

public sealed record CommandResult
{
    /// <summary>命令是否被处理（true=已处理，false=未识别/未处理，回退到 AI）。</summary>
    public bool Handled { get; init; }

    /// <summary>命令输出文本（显示到 ChatView，null 表示无输出）。</summary>
    public string? Output { get; init; }

    /// <summary>是否请求退出应用（/exit /quit 设置）。</summary>
    public bool ExitApp { get; init; }

    public static CommandResult NotHandled => new() { Handled = false };
    public static CommandResult Ok => new() { Handled = true };
    public static CommandResult WithOutput(string output) => new() { Handled = true, Output = output };
    public static CommandResult Exit => new() { Handled = true, ExitApp = true };
}
```

### 3.4 CommandContext

```csharp
// Commands/CommandContext.cs
namespace ParrotCode;

/// <summary>
/// 命令执行上下文：封装命令执行时需要的所有依赖。
/// 命令通过此上下文操作 UI/History/Compressor/SecurityGuard 等，不直接依赖 TerminalApp。
/// </summary>
public sealed record CommandContext(
    ConversationHistory History,
    ContextCompressor? Compressor,
    SecurityGuard SecurityGuard,
    IUiControl Ui,
    SessionStore? SessionStore,       // 10a 中为 null（stub），10b 注入真实 Store
    CancellationToken Ct)
{
    /// <summary>当前 Provider 配置（/status 用）。必填。</summary>
    public ProviderConfig ProviderConfig { get; init; } = null!;

    /// <summary>当前 TUI 配置（/status 用）。必填。</summary>
    public TuiConfig TuiConfig { get; init; } = null!;

    /// <summary>当前 AgentConfig（/status 用）。必填。</summary>
    public AgentConfig AgentConfig { get; init; } = null!;

    /// <summary>项目指令加载概要（/status 显示，10c 填充）。</summary>
    public string? InstructionSummary { get; init; }

    /// <summary>原始输入行（含 / 前缀，便于错误提示引用与参数解析）。</summary>
    public string RawInput { get; init; } = string.Empty;
}
```

> **注**：`CommandContext.SessionStore` 在 10a 中为 null，`SessionCommand` 检测到 null 返回"未启用"。10b 装配真实 `SessionStore` 后自动生效，无需改 `SessionCommand` 代码。

### 3.5 CommandRegistry（注册中心 + 反射自动扫描）

```csharp
// Commands/CommandRegistry.cs
using System.Reflection;
using Microsoft.Extensions.Logging;

namespace ParrotCode;

/// <summary>
/// 命令注册中心：管理所有已注册的 ICommand。
/// 支持手动注册 + 反射自动扫描程序集中所有 ICommand 实现类。
/// 别名冲突检测：注册时检查 Name 和所有 Aliases 是否已被占用。
/// </summary>
public sealed class CommandRegistry
{
    private readonly Dictionary<string, ICommand> _commands = new(StringComparer.OrdinalIgnoreCase);
    private readonly ILogger? _logger;

    public CommandRegistry(ILogger? logger = null) => _logger = logger;

    /// <summary>已注册的命令数（含别名不重复计算）。</summary>
    public int Count => _commands.Values.Distinct().Count();

    /// <summary>
    /// 手动注册命令。Name 和所有 Aliases 必须唯一，冲突抛 InvalidOperationException。
    /// </summary>
    public void Register(ICommand command)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (_commands.ContainsKey(command.Name))
        {
            var existing = _commands[command.Name];
            throw new InvalidOperationException(
                $"命令名 '{command.Name}' 冲突：已由 {existing.GetType().Name} 注册");
        }

        foreach (var alias in command.Aliases)
        {
            if (_commands.ContainsKey(alias))
            {
                var existing = _commands[alias];
                throw new InvalidOperationException(
                    $"别名 '{alias}' 冲突：已由 {existing.GetType().Name} 注册");
            }
        }

        _commands[command.Name] = command;
        foreach (var alias in command.Aliases)
            _commands[alias] = command;
    }

    /// <summary>
    /// 反射自动扫描程序集中所有 ICommand 实现类并注册。
    /// 跳过接口和抽象类，用无参构造函数实例化。
    /// 已注册的（按 Name 判断）跳过——支持"手动注册后再自动扫描"模式。
    /// </summary>
    public void AutoRegisterFromAssembly(Assembly? assembly = null)
    {
        assembly ??= Assembly.GetExecutingAssembly();
        var commandTypes = assembly.GetTypes()
            .Where(t => typeof(ICommand).IsAssignableFrom(t)
                        && t is { IsInterface: false, IsAbstract: false }
                        && t.GetConstructor(Type.EmptyTypes) is not null);

        foreach (var type in commandTypes)
        {
            try
            {
                var command = (ICommand)Activator.CreateInstance(type)!;
                // 已手动注册的跳过（如 HelpCommand）
                if (_commands.ContainsKey(command.Name))
                {
                    _logger?.LogDebug("命令 {Name} 已手动注册，跳过自动扫描", command.Name);
                    continue;
                }
                Register(command);
                _logger?.LogDebug("自动注册命令 {Name} ({Type})", command.Name, type.Name);
            }
            catch (InvalidOperationException ex)
            {
                _logger?.LogWarning(ex, "自动注册命令 {Type} 失败（可能已手动注册）", type.Name);
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "自动注册命令 {Type} 失败", type.Name);
            }
        }
    }

    public ICommand? Find(string nameOrAlias)
        => _commands.TryGetValue(nameOrAlias, out var cmd) ? cmd : null;

    public IReadOnlyList<ICommand> GetAll() => _commands.Values.Distinct().ToList();

    /// <summary>获取所有命令名（含别名），供 Tab 补全用。</summary>
    public IReadOnlyList<string> GetAllNamesWithAliases() => _commands.Keys.ToList();

    public IReadOnlyList<ICommand> GetVisibleCommands()
        => GetAll().Where(c => c.Type == CommandType.System).ToList();
}
```

### 3.6 CommandParser（解析器）

```csharp
// Commands/CommandParser.cs
using System.Text.RegularExpressions;

namespace ParrotCode;

/// <summary>
/// 斜杠命令解析器：把用户输入行解析为 (命令名, 参数字符串)。
/// 规则：
/// - 行首必须是 '/' 才视为命令（否则返回 null，走 AI）
/// - 命令名 = '/' 后到第一个空格前的部分（大小写不敏感）
/// - 参数 = 第一个空格后的全部内容（保留原始大小写与空格）
/// </summary>
public static class CommandParser
{
    /// <summary>
    /// 解析输入行。返回 (命令名, 参数)；非命令（不以 / 开头）返回 null。
    /// </summary>
    public static (string Name, string Args)? Parse(string line)
    {
        if (string.IsNullOrEmpty(line) || line[0] != '/')
            return null;

        var body = line[1..];
        var spaceIdx = body.IndexOf(' ');
        if (spaceIdx < 0)
            return (body, string.Empty);

        var name = body[..spaceIdx];
        var args = body[(spaceIdx + 1)..];
        return (name, args);
    }

    /// <summary>
    /// 把参数字符串按空格分割为参数数组（支持引号包裹的含空格参数）。
    /// "/session save my-session" → ["save", "my-session"]
    /// "/mode \"strict mode\"" → ["strict mode"]
    /// </summary>
    public static IReadOnlyList<string> SplitArgs(string args)
    {
        if (string.IsNullOrWhiteSpace(args))
            return Array.Empty<string>();

        var result = new List<string>();
        var matches = Regex.Matches(args, @"(?:""([^""]*)""|(\S+))");
        foreach (Match m in matches)
            result.Add(m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value);
        return result;
    }
}
```

### 3.7 CommandDispatcher（分发器）

```csharp
// Commands/CommandDispatcher.cs
namespace ParrotCode;

/// <summary>
/// 命令分发器：判断输入是否为命令，若是则查找并执行。
/// "/" 前缀 → 查 Registry → 执行 ICommand.ExecuteAsync
/// 非 "/" 前缀 → 返回 CommandResult.NotHandled（回退到 AI）
/// 命令未找到 → 返回 WithOutput("未知命令: xxx，输入 /help 查看可用命令")
/// 命令抛异常 → 返回 WithOutput("[!] 执行命令失败...")，不崩溃应用
/// </summary>
public sealed class CommandDispatcher
{
    private readonly CommandRegistry _registry;

    public CommandDispatcher(CommandRegistry registry)
        => _registry = registry ?? throw new ArgumentNullException(nameof(registry));

    public async Task<CommandResult> DispatchAsync(string line, CommandContext context, CancellationToken ct)
    {
        var parsed = CommandParser.Parse(line);
        if (parsed is null)
            return CommandResult.NotHandled;

        var (name, _) = parsed.Value;
        var command = _registry.Find(name);
        if (command is null)
            return CommandResult.WithOutput($"未知命令: /{name}，输入 /help 查看可用命令");

        var ctx = context with { RawInput = line, Ct = ct };

        try
        {
            return await command.ExecuteAsync(ctx);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return CommandResult.WithOutput($"[!] 执行命令 /{name} 失败：{ex.Message}");
        }
    }
}
```

### 3.8 IUiControl 抽象接口

```csharp
// Tui/IUiControl.cs
namespace ParrotCode;

/// <summary>
/// UI 抽象接口：命令通过此接口操作 UI，不直接依赖 TerminalApp。
/// 仅暴露命令需要的能力，遵循接口隔离原则。
/// </summary>
public interface IUiControl
{
    void AppendStaticMessage(string text);
    void AppendUserMessage(string text);
    void ClearMessages();
    void RefreshStatusBar();
    void UpdateTokenEstimate(int estimatedTokens);
    void UpdateSecurityLevel(SecurityLevel level);
    void RequestExit();
}
```

### 3.9 内置命令实现

#### HelpCommand（需依赖注入，手动注册）

```csharp
// Commands/Builtin/HelpCommand.cs
using System.Text;

namespace ParrotCode.Commands.Builtin;

public sealed class HelpCommand : ICommand
{
    private readonly CommandRegistry _registry;

    public HelpCommand(CommandRegistry registry) => _registry = registry;

    public string Name => "help";
    public string Description => "显示可用命令列表";
    public CommandType Type => CommandType.System;
    public IReadOnlyList<string> Aliases => new[] { "?" };
    public string Usage => "/help";

    public Task<CommandResult> ExecuteAsync(CommandContext context)
    {
        var sb = new StringBuilder();
        sb.AppendLine("可用命令：");
        foreach (var cmd in _registry.GetVisibleCommands().OrderBy(c => c.Name))
            sb.AppendLine($"  {cmd.Usage,-20} {cmd.Description}");
        sb.AppendLine();
        sb.AppendLine("提示：输入消息与 AI 对话；/ 开头走命令。");
        return Task.FromResult(CommandResult.WithOutput(sb.ToString()));
    }
}
```

#### ClearCommand

```csharp
// Commands/Builtin/ClearCommand.cs
namespace ParrotCode.Commands.Builtin;

/// <summary>
/// /clear：清空对话历史 + UI + 重置压缩器警告。
/// 不重置熔断器（熔断器是跨轮状态，/clear 只清历史）。
/// </summary>
public sealed class ClearCommand : ICommand
{
    public string Name => "clear";
    public string Description => "清空对话历史，重新开始";
    public CommandType Type => CommandType.System;
    public IReadOnlyList<string> Aliases => Array.Empty<string>();
    public string Usage => "/clear";

    public Task<CommandResult> ExecuteAsync(CommandContext context)
    {
        context.History.Clear();
        context.Ui.ClearMessages();
        context.Compressor?.ResetWarning();
        context.Ui.UpdateTokenEstimate(0);
        return Task.FromResult(CommandResult.Ok);
    }
}
```

#### CompressCommand

```csharp
// Commands/Builtin/CompressCommand.cs
namespace ParrotCode.Commands.Builtin;

/// <summary>
/// /compress：手动触发上下文压缩。
/// 即使熔断器 open 也允许手动触发（与自动触发不同）。
/// 手动触发成功后熔断器保持关闭（先 Reset 再触发）。
/// </summary>
public sealed class CompressCommand : ICommand
{
    public string Name => "compress";
    public string Description => "手动触发上下文压缩（摘要历史）";
    public CommandType Type => CommandType.System;
    public IReadOnlyList<string> Aliases => Array.Empty<string>();
    public string Usage => "/compress";

    public async Task<CommandResult> ExecuteAsync(CommandContext context)
    {
        if (context.Compressor is null)
            return CommandResult.WithOutput("[!] 上下文压缩未启用");

        if (context.Compressor.CircuitOpen)
            context.Compressor.ResetCircuit();

        var result = await context.Compressor.CheckAndCompressAsync(context.History, context.Ct);

        if (result.WasCompressed)
        {
            context.Ui.UpdateTokenEstimate(context.History.EstimatedTokens);
            return CommandResult.WithOutput(
                $"[压缩] 已压缩 {result.MessagesCompressed} 条消息，节省约 {result.EstimatedTokensSaved} tokens");
        }

        if (result.CircuitOpen)
            return CommandResult.WithOutput("[!] 压缩失败，熔断器已打开（摘要连续失败）");

        return CommandResult.WithOutput("[i] 当前无需压缩（token 未达阈值）");
    }
}
```

#### ModeCommand

```csharp
// Commands/Builtin/ModeCommand.cs
namespace ParrotCode.Commands.Builtin;

/// <summary>
/// /mode [strict|normal|permissive]：查看或切换安全等级。
/// 无参数 → 显示当前等级；有参数 → 切换。
/// </summary>
public sealed class ModeCommand : ICommand
{
    public string Name => "mode";
    public string Description => "查看或切换安全等级（strict/normal/permissive）";
    public CommandType Type => CommandType.System;
    public IReadOnlyList<string> Aliases => Array.Empty<string>();
    public string Usage => "/mode [strict|normal|permissive]";

    public Task<CommandResult> ExecuteAsync(CommandContext context)
    {
        var parts = context.RawInput.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        var modeArg = parts.Length > 1 ? parts[1].Trim() : null;

        if (string.IsNullOrEmpty(modeArg))
        {
            return Task.FromResult(CommandResult.WithOutput(
                $"当前安全等级：{context.SecurityGuard.Level}（可选：strict / normal / permissive）"));
        }

        var newLevel = SecurityLevelParser.Parse(modeArg);
        context.SecurityGuard.Level = newLevel;
        context.Ui.UpdateSecurityLevel(newLevel);
        context.Ui.RefreshStatusBar();

        return Task.FromResult(CommandResult.WithOutput($"安全等级已切换为：{newLevel}"));
    }
}
```

#### StatusCommand

```csharp
// Commands/Builtin/StatusCommand.cs
using System.Text;

namespace ParrotCode.Commands.Builtin;

/// <summary>
/// /status：显示当前配置概要。
/// 指令字段在 10c 填充（10a 中 InstructionSummary 为 null，显示"未加载"）。
/// </summary>
public sealed class StatusCommand : ICommand
{
    public string Name => "status";
    public string Description => "显示当前配置概要";
    public CommandType Type => CommandType.System;
    public IReadOnlyList<string> Aliases => Array.Empty<string>();
    public string Usage => "/status";

    public Task<CommandResult> ExecuteAsync(CommandContext context)
    {
        var sb = new StringBuilder();
        sb.AppendLine("=== 当前配置 ===");
        sb.AppendLine($"Provider: {context.ProviderConfig.Name} ({context.ProviderConfig.Protocol})");
        sb.AppendLine($"Model: {context.ProviderConfig.Model}");
        sb.AppendLine($"安全等级: {context.SecurityGuard.Level}");
        sb.AppendLine($"最大轮次: {context.AgentConfig.MaxRounds ?? 10}");
        sb.AppendLine($"工具并发: {context.AgentConfig.MaxParallelism ?? 5}");
        sb.AppendLine($"上下文窗口: {context.TuiConfig.ContextWindowTokens ?? 64000}");

        if (context.Compressor is not null)
        {
            sb.AppendLine($"历史消息数: {context.History.Count}");
            sb.AppendLine($"估算 tokens: {context.History.EstimatedTokens}");
            sb.AppendLine($"压缩熔断器: {(context.Compressor.CircuitOpen ? "打开（已禁用自动压缩）" : "正常")}");
        }

        // 10a 中 SessionStore 为 null，显示"未启用"；10b 注入后显示路径
        if (context.SessionStore is not null)
            sb.AppendLine($"会话存储: {context.SessionStore.StorageDir}");
        else
            sb.AppendLine("会话存储: 未启用");

        // 10a 中 InstructionSummary 为 null；10c 填充
        sb.AppendLine($"项目指令: {context.InstructionSummary ?? "未加载"}");

        return Task.FromResult(CommandResult.WithOutput(sb.ToString()));
    }
}
```

#### SessionCommand（10a stub）

```csharp
// Commands/Builtin/SessionCommand.cs
namespace ParrotCode.Commands.Builtin;

/// <summary>
/// /session save|load|list|current：会话持久化。
/// 10a：SessionStore 为 null，所有子命令返回"未启用"提示。
/// 10b：注入真实 SessionStore 后自动生效，无需改本命令代码。
/// </summary>
public sealed class SessionCommand : ICommand
{
    public string Name => "session";
    public string Description => "会话持久化（save/load/list/current）";
    public CommandType Type => CommandType.System;
    public IReadOnlyList<string> Aliases => new[] { "sessions" };
    public string Usage => "/session save|load <id>|list|current";

    public Task<CommandResult> ExecuteAsync(CommandContext context)
    {
        if (context.SessionStore is null)
            return Task.FromResult(CommandResult.WithOutput(
                "[!] 会话持久化未启用（迭代 10b 接入）"));

        // 10b 在此实现 save/load/list/current 子命令分发
        // 10a 永远不会走到这里（SessionStore 为 null）
        return Task.FromResult(CommandResult.WithOutput("[!] 会话持久化未启用"));
    }
}
```

#### ExitCommand

```csharp
// Commands/Builtin/ExitCommand.cs
namespace ParrotCode.Commands.Builtin;

public sealed class ExitCommand : ICommand
{
    public string Name => "exit";
    public string Description => "退出 ParrotCode";
    public CommandType Type => CommandType.System;
    public IReadOnlyList<string> Aliases => new[] { "quit" };
    public string Usage => "/exit";

    public Task<CommandResult> ExecuteAsync(CommandContext context)
    {
        context.Ui.RequestExit();
        return Task.FromResult(CommandResult.Exit);
    }
}
```

### 3.10 TerminalApp 扩展（实现 IUiControl + 命令系统装配）

```csharp
// Tui/TerminalApp.cs — 扩展要点

internal sealed class TerminalApp : IUiControl, IDisposable
{
    private readonly CommandRegistry _commandRegistry;
    private readonly CommandDispatcher _commandDispatcher;
    // 10a：SessionStore 为 null；10b 注入真实 Store
    private readonly SessionStore? _sessionStore;
    // 10a：无指令；10c 注入 InstructionResult
    private const string? _instructionSummary = null;

    public TerminalApp(/* 既有参数 */, SessionStore? sessionStore, ILogger? logger, CancellationToken ct)
    {
        // ... 既有赋值 ...
        _sessionStore = sessionStore;

        // 构造命令系统
        _commandRegistry = new CommandRegistry(logger);
        // 手动注册需依赖注入的命令
        _commandRegistry.Register(new HelpCommand(_commandRegistry));
        // 反射自动注册其余无参构造的命令（HelpCommand 已注册会跳过）
        _commandRegistry.AutoRegisterFromAssembly();
        _commandDispatcher = new CommandDispatcher(_commandRegistry);
    }

    private async void HandleUserInput(string line)
    {
        if (string.IsNullOrWhiteSpace(line)) return;

        // Agent 正在运行时忽略新输入（命令和对话都忽略，避免状态竞争）
        if (_agentTask is not null && !_agentTask.IsCompleted) return;

        // 命令分发
        var context = BuildCommandContext();
        var dispatchResult = await _commandDispatcher.DispatchAsync(line, context, _ct);
        if (dispatchResult.Handled)
        {
            if (dispatchResult.Output is not null)
                _chatView!.AppendStaticMessage(dispatchResult.Output);
            if (dispatchResult.ExitApp)
                Application.RequestStop(_top!);
            return;
        }

        // 非命令 → 走 AI
        _chatView!.AppendUserMessage(line);
        _history!.AddUser(line);
        _statusBarView!.CurrentRound = 0;
        _statusBarView.EstimatedTokens = _history.EstimatedTokens;
        StartAgentRound();
    }

    private CommandContext BuildCommandContext() => new(
        History: _history!,
        Compressor: _compressor,
        SecurityGuard: _securityGuard,
        Ui: this,
        SessionStore: _sessionStore,    // 10a: null
        Ct: _ct)
    {
        ProviderConfig = _providerConfig,
        TuiConfig = _tuiConfig,
        AgentConfig = _agentConfig,
        InstructionSummary = _instructionSummary,  // 10a: null
    };

    // BuildLayout 末尾：初始化 Tab 补全数据源
    // _inputFieldView.SetCommands(_commandRegistry.GetAllNamesWithAliases());

    // ===== IUiControl 实现 =====
    void IUiControl.AppendStaticMessage(string text) => _chatView!.AppendStaticMessage(text);
    void IUiControl.AppendUserMessage(string text) => _chatView!.AppendUserMessage(text);
    void IUiControl.ClearMessages() => _chatView!.ClearMessages();
    void IUiControl.RefreshStatusBar() => _statusBarView!.Update(_providerConfig, _securityGuard.Level, _tuiConfig, _registry!);
    void IUiControl.UpdateTokenEstimate(int t) => _statusBarView!.EstimatedTokens = t;
    void IUiControl.UpdateSecurityLevel(SecurityLevel level) => _securityLevel = level;
    void IUiControl.RequestExit() => Application.RequestStop(_top!);
}
```

### 3.11 InputFieldView 扩展（Tab 补全动态化）

```csharp
// Tui/InputFieldView.cs — 改造要点

internal sealed class InputFieldView : TextField
{
    private List<string> _commands = new() { "/clear", "/exit", "/quit", "/help", "/status" };

    /// <summary>设置命令名列表（含 / 前缀），供 Tab 补全。</summary>
    public void SetCommands(IReadOnlyList<string> commandNames)
    {
        _commands = commandNames
            .Select(n => n.StartsWith('/') ? n : "/" + n)
            .OrderBy(n => n)
            .ToList();
    }

    // CompleteCommand 方法不变（数据源 _commands 已改为动态）
}
```

---

## 四、验收标准

### 4.1 编译与测试

| 编号 | 标准 | 验证方式 |
|------|------|---------|
| 10a-01 | `dotnet build` 0 错误 0 警告 | 编译 |
| 10a-02 | 全量测试全绿（现有 + 10a 新增） | `dotnet test` |
| 10a-03 | `CommandRegistryTests` 全绿 | `dotnet test` |
| 10a-04 | `CommandParserTests` 全绿 | `dotnet test` |
| 10a-05 | `CommandDispatcherTests` 全绿 | `dotnet test` |
| 10a-06 | 各内置命令测试全绿 | `dotnet test` |
| 10a-07 | 现有 `AgentLoopTests` / `CompressorTests` 不回归 | `dotnet test` |

### 4.2 CommandRegistry

| 编号 | 标准 | 验证方式 |
|------|------|---------|
| 10a-10 | `Register` 后 `Find(name)` 返回该命令 | 单测 |
| 10a-11 | 注册别名后 `Find(alias)` 返回同一命令实例 | 单测 |
| 10a-12 | 重复注册同名命令抛 `InvalidOperationException` | 单测 |
| 10a-13 | 别名冲突抛 `InvalidOperationException` | 单测 |
| 10a-14 | `AutoRegisterFromAssembly` 自动扫描 `ICommand` 实现类 | 单测 |
| 10a-15 | `AutoRegisterFromAssembly` 跳过接口和抽象类 | 单测 |
| 10a-16 | `AutoRegisterFromAssembly` 已手动注册的命令跳过 | 单测 |
| 10a-17 | `AutoRegisterFromAssembly` 无参构造失败的类跳过不崩溃 | 单测 |
| 10a-18 | `GetVisibleCommands` 只返回 `Type == System` | 单测 |
| 10a-19 | `GetAllNamesWithAliases` 含命令名和别名 | 单测 |
| 10a-20 | `Find` 未找到返回 null | 单测 |
| 10a-21 | 大小写不敏感查找（`/HELP` 等价 `/help`） | 单测 |

### 4.3 CommandParser

| 编号 | 标准 | 验证方式 |
|------|------|---------|
| 10a-25 | 非 `/` 开头返回 null | 单测 |
| 10a-26 | 空字符串返回 null | 单测 |
| 10a-27 | `/clear` → `("clear", "")` | 单测 |
| 10a-28 | `/mode strict` → `("mode", "strict")` | 单测 |
| 10a-29 | `/session save my-title` → `("session", "save my-title")` | 单测 |
| 10a-30 | `/help` 末尾无空格 → `("help", "")` | 单测 |
| 10a-31 | `SplitArgs` 正确分割空格分隔参数 | 单测 |
| 10a-32 | `SplitArgs` 支持双引号包裹含空格参数 | 单测 |
| 10a-33 | `SplitArgs` 空字符串返回空数组 | 单测 |

### 4.4 CommandDispatcher

| 编号 | 标准 | 验证方式 |
|------|------|---------|
| 10a-35 | 非 `/` 前缀返回 `NotHandled` | 单测 |
| 10a-36 | 已注册命令正确分发并返回结果 | 单测（mock ICommand） |
| 10a-37 | 未注册命令返回 `WithOutput("未知命令...")` | 单测 |
| 10a-38 | 命令抛异常时返回 `WithOutput("[!] 执行命令失败...")` | 单测 |
| 10a-39 | 命令异常不传播到调用方 | 单测 |
| 10a-40 | CancellationToken 取消时向上传播 `OperationCanceledException` | 单测 |

### 4.5 内置命令

| 编号 | 标准 | 验证方式 |
|------|------|---------|
| 10a-45 | `/help` 输出含所有可见命令的 Usage + Description | 单测 |
| 10a-46 | `/help` 不列出 Hidden 命令 | 单测 |
| 10a-47 | `/clear` 清空 `History` 和 `Ui` | 单测（mock IUiControl） |
| 10a-48 | `/clear` 调用 `Compressor.ResetWarning` | 单测 |
| 10a-49 | `/compress` 手动触发 `CheckAndCompressAsync` | 单测 |
| 10a-50 | `/compress` 熔断器 open 时先 Reset 再触发 | 单测 |
| 10a-51 | `/compress` 成功后输出压缩消息 | 单测 |
| 10a-52 | `/compress` 无需压缩时输出"当前无需压缩" | 单测 |
| 10a-53 | `/mode` 无参数显示当前等级 | 单测 |
| 10a-54 | `/mode strict` 切换 `SecurityGuard.Level` 为 Strict | 单测 |
| 10a-55 | `/mode normal` / `/mode permissive` 分别切换 | 单测 |
| 10a-56 | `/mode invalid` 无效值回退 Normal | 单测 |
| 10a-57 | `/mode` 切换后调用 `Ui.UpdateSecurityLevel` + `Ui.RefreshStatusBar` | 单测 |
| 10a-58 | `/status` 输出含 provider/model/security/rounds/tokens | 单测 |
| 10a-59 | `/status` SessionStore 为 null 时显示"未启用" | 单测 |
| 10a-60 | `/status` InstructionSummary 为 null 时显示"未加载" | 单测 |
| 10a-61 | `/session`（stub）返回"未启用"提示 | 单测 |
| 10a-62 | `/exit` 返回 `ExitApp=true` | 单测 |
| 10a-63 | `/quit`（别名）等价 `/exit` | 单测 |

### 4.6 IUiControl + TerminalApp 集成

| 编号 | 标准 | 验证方式 |
|------|------|---------|
| 10a-70 | `TerminalApp` 实现 `IUiControl` 所有方法 | 编译 |
| 10a-71 | `HandleUserInput` 用 `CommandDispatcher` 分发 | 代码审查 |
| 10a-72 | `/exit` 通过 `IUiControl.RequestExit` 退出 | 手动 |
| 10a-73 | `/clear` 清空对话区 + 历史 + 重置压缩器警告 | 手动 |
| 10a-74 | `/mode strict` 后状态栏显示 `security=Strict` | 手动 |
| 10a-75 | `/status` 输出当前配置概要（会话/指令字段为"未启用/未加载"） | 手动 |
| 10a-76 | `/help` 列出所有命令 | 手动 |
| 10a-77 | `/compress` 手动触发压缩，输出压缩结果 | 手动 |
| 10a-78 | 未知命令 `/foobar` 输出"未知命令"提示 | 手动 |
| 10a-79 | 非命令输入正常走 AI 对话 | 手动 |
| 10a-80 | Agent 运行时输入被忽略（命令和对话都忽略） | 手动 |

### 4.7 InputFieldView Tab 补全

| 编号 | 标准 | 验证方式 |
|------|------|---------|
| 10a-85 | `SetCommands` 设置命令名列表 | 单测 |
| 10a-86 | Tab 补全从动态列表查找 | 单测 |
| 10a-87 | 唯一匹配自动填充 | 单测 |
| 10a-88 | 多匹配不填充 | 单测 |
| 10a-89 | 补全列表含别名（如 `/quit`） | 手动 |

### 4.8 回归

| 编号 | 标准 | 验证方式 |
|------|------|---------|
| 10a-95 | 迭代 9 功能不受影响（截断/摘要/熔断） | 手动回归 |
| 10a-96 | 迭代 8c 功能不受影响（安全层/HITL） | 手动回归 |
| 10a-97 | 迭代 7c 功能不受影响（流式渲染/Spinner） | 手动回归 |

---

## 五、测试计划

| 测试文件 | 覆盖范围 | 预估用例数 |
|---------|---------|-----------|
| `CommandRegistryTests.cs` | 注册/查找/别名/冲突/反射扫描/可见性 | ~12 |
| `CommandParserTests.cs` | Parse + SplitArgs 各种输入 | ~9 |
| `CommandDispatcherTests.cs` | 分发/未注册/异常/取消 | ~6 |
| `HelpCommandTests.cs` | 列出命令/Hidden 过滤 | ~3 |
| `ClearCommandTests.cs` | 清空 History/UI/ResetWarning | ~3 |
| `CompressCommandTests.cs` | 手动触发/熔断器 reset/无阈值 | ~4 |
| `ModeCommandTests.cs` | 查看/切换/无效值/刷新状态栏 | ~5 |
| `StatusCommandTests.cs` | 输出格式/null 字段处理 | ~3 |
| `SessionCommandTests.cs`（stub） | 未启用提示 | ~1 |
| `ExitCommandTests.cs` | ExitApp=true | ~2 |
| `InputFieldViewTests.cs`（补充） | SetCommands/动态补全 | ~4 |

---

## 六、实施步骤

1. 新建 `Commands/` 目录下 7 个核心文件 + `Builtin/` 7 个命令文件
2. 新建 `Tui/IUiControl.cs`
3. 新建各命令的测试文件（用 mock IUiControl / mock ICommand）
4. `Tui/TerminalApp.cs` 实现 `IUiControl`；`HandleUserInput` 改用 `CommandDispatcher`
5. `Tui/InputFieldView.cs` 加 `SetCommands`；`TerminalApp.BuildLayout` 后调用
6. 验证：`dotnet build` 0 警告 + `dotnet test` 全绿
7. 端到端手动验收（10a-70 ~ 10a-97）
8. 标记迭代 10a [已完成]

---

## 七、风险与对策

| 风险 | 概率 | 影响 | 对策 |
|------|------|------|------|
| 反射扫描扫到测试程序集的 ICommand | 低 | 低 | 默认 `Assembly.GetExecutingAssembly()`（主程序集） |
| `HelpCommand` 无参构造失败导致自动扫描崩溃 | 中 | 中 | 先手动注册 `HelpCommand`，再自动扫描（已注册的跳过） |
| 命令替换硬编码后行为不一致 | 低 | 中 | `Exit/Clear/Help` 保持等价语义；端到端回归 |
| `/mode` 切换后 `StatusBarView` 不同步 | 低 | 低 | `IUiControl.UpdateSecurityLevel` + `RefreshStatusBar` 双调用 |
| Agent 运行时命令与对话竞争 | 低 | 中 | `HandleUserInput` 顶部检查 `_agentTask.IsCompleted`，命令和对话都忽略 |
| `SessionCommand` stub 与 10b 实现切换遗漏 | 低 | 低 | stub 只判 `SessionStore is null`；10b 注入非 null 自动走真实逻辑，无需改 stub 代码 |

---

**文档结束**。状态：[设计完成，待实现]
