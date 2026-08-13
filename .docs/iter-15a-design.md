# 迭代 15a：Hook 核心引擎（加载层 + 纯逻辑层）

> **状态**：[设计完成，待实现]
> **前置迭代**：14a [已完成]（角色系统）、14b [已完成]（SubAgentRunner + sub_agent 工具）
> **后续迭代**：15b（Hook 集成接入——sub_agent 动作 + SecureBatchToolExecutor + AgentLoop + 装配）
> **总览文档**：[iter-15-design.md](./iter-15-design.md)
> **关联文档**：[iter-15b-design.md](./iter-15b-design.md)

---

## 一、子迭代目标

### 1.1 核心目标

交付 Hook 引擎的核心逻辑层——6 个新文件全套交付，实现 12 种事件 + 4 种条件算子 + 3 种动作（shell/prompt_inject/http）的完整引擎。**零既有文件改动**，可独立 build + test 通过。

1. **数据模型**（`Models.cs`）：12 种 HookEvent 枚举 + 4 种 HookOperator + HookMatchMode + 4 种 HookActionType + ConditionRule / HookCondition / HookAction / HookControl / HookRule + InterceptEvents + HookConfigException

2. **条件评估器**（`Conditions.cs`）：ConditionEvaluator——exact/not/regex/glob 四种算子 + ALL/ANY 匹配模式 + dot-path 字段解析

3. **模板引擎**（`Templates.cs`）：TemplateEngine——`{{var}}` 占位符替换 + dot-path 支持 + 未定义变量→空字符串

4. **动作执行器**（`Actions.cs`）：ActionExecutor——shell（跨平台命令执行）/ prompt_inject（模板渲染）/ http（webhook 调用）+ 错误隔离 + SetSubAgentRunner 预留（15b 实现 sub_agent 动作）

5. **规则加载器**（`Loader.cs`）：HookLoader——两级 YAML 加载（全局 ~/.parrotcode/hooks.yaml + 项目 ./.parrotcode/hooks.yaml）+ 集中校验 + **枚举字符串解析**（snake_case → PascalCase）

6. **Hook 引擎**（`Engine.cs`）：HookEngine——FireAsync 触发事件 + 条件评估 + 动作执行 + once 跟踪 + ResetOnce

### 1.2 本子迭代要验证的关键问题

| 问题 | 验证方式 | 失败对策 |
|------|---------|---------|
| YamlDotNet 枚举反序列化 | 已验证（见第九节） | 枚举字段用 string + `[YamlIgnore]` 强类型属性，Loader 中 snake_case→PascalCase 解析 |
| 条件匹配 ALL/ANY 逻辑正确 | 单测：多规则组合 | `ConditionEvaluator.Evaluate` 按 match 模式聚合 |
| 模板变量替换正确 | 单测：`{{tool_name}}` / `{{params.path}}` | `TemplateEngine.Render` 正则替换 + dot-path 解析 |
| 拦截事件不允许 async | 单测：tool_pre_exec + async=true | `HookLoader` 校验阶段拒绝，抛 `HookConfigException` |
| `once: true` 规则只触发一次 | 单测：同一事件发两次 | `HookEngine._firedOnce` HashSet 跟踪 |
| Hook 失败不抛异常 | 单测：mock 动作抛异常 | `ActionExecutor.ExecuteAsync` try-catch 全包裹 |
| shell 动作跨平台 | 单测：Windows/Unix | Windows 用 cmd.exe，Unix 用 /bin/sh |
| 两级 YAML 合并加载 | 单测：global + project 各 1 条 | 返回 2 条规则 |
| 拦截事件返回拒绝原因 | 单测：tool_pre_exec + prompt_inject | `HookEngine.FireAsync` 返回渲染文本 |
| Regex 超时有效 | 单测：ReDoS 正则 + 超时 | `SafeRegexMatch` 传 `TimeSpan` 参数 |
| http 动作可 mock | 单测：mock HttpMessageHandler | `ActionExecutor` 构造函数接受 `HttpMessageHandler?` |

### 1.3 非目标（15a 明确不做）

- ❌ 不做 sub_agent 动作——依赖 SubAgentRunner（15b 实现）。`ActionExecutor.SetSubAgentRunner` 方法预留空壳，15b 填充
- ❌ 不做任何既有文件改动——15a 纯新增 `Hooks/` 目录
- ❌ 不做 Config/Models.cs 改动——HooksConfig 在 15b
- ❌ 不做 AgentLoop / SecureBatchToolExecutor / App / TerminalApp 改动——集成接入在 15b
- ❌ 不做 example.parrotcode.yaml 改动——配置节在 15b
- ❌ 不做端到端验收——15a 无运行时入口，仅单测验证

### 1.4 与既有系统的衔接策略

- **复用 YamlDotNet**：HookLoader 使用 `YamlDotNet.Serialization` + `UnderscoredNamingConvention`（与 SkillLoader / RoleLoader 一致）
- **复用三级目录模式**：HookLoader 两级加载（全局 ~/.parrotcode/ + 项目 ./.parrotcode/），与 SkillLoader 三级加载同构（Hook 无内置层级——规则全部用户定义）
- **不依赖任何既有类型**：15a 的 6 个文件自包含，不引用 AgentLoop / SecurityGuard / SubAgentRunner 等既有类型（`SetSubAgentRunner` 参数类型引用 `SubAgentRunner`，但 15a 只声明方法签名不实现逻辑——15b 填充实现）

---

## 二、文件改动清单

### 2.1 新增文件（6 个）

```
Hooks/
├── Models.cs                  # HookEvent(12) / HookOperator(4) / HookMatchMode / HookActionType(4) / ConditionRule / HookCondition / HookAction / HookControl / HookRule + InterceptEvents + HookConfigException
├── Conditions.cs              # ConditionEvaluator（exact/not/regex/glob + ALL/ANY + dot-path）
├── Templates.cs               # TemplateEngine（{{var}} dot-path 替换）
├── Actions.cs                 # ActionExecutor（shell/prompt_inject/http + 错误隔离 + SetSubAgentRunner 预留）
├── Loader.cs                  # HookLoader（两级 YAML 加载 + 集中校验 + 枚举字符串解析）
└── Engine.cs                  # HookEngine（FireAsync + once 跟踪 + ResetOnce）
```

### 2.2 修改文件

**无**——15a 零既有文件改动。

### 2.3 不变文件

- 全部既有文件——`Agent/` / `Security/` / `Tools/` / `Conversation/` / `SubAgent/` / `Skills/` / `Commands/` / `Mcp/` / `Config/` / `App/` / `Tui/` / `Storage/` / `Instructions/` / `Providers/` 全部 git diff 为空

---

## 三、详细设计

### 3.1 数据模型（`Hooks/Models.cs`）

> **技术风险规避**：YamlDotNet 16.2.1 的 `UnderscoredNamingConvention` 只映射**属性名**（snake_case→PascalCase），不映射**枚举值**。YAML 中写 `event: tool_pre_exec` 会抛 `ArgumentException: Requested value 'tool_pre_exec' was not found`（已验证，见第九节）。
>
> **方案**：YAML 反序列化的类中枚举字段用 **string 类型**，加 `[YamlIgnore]` 标注的**强类型只读属性**供运行时使用。HookLoader.ValidateAndNormalize 中将 snake_case 字符串解析为枚举（`SnakeToPascal` + `Enum.TryParse`）。这与项目中 `Protocol` 字段的做法一致（字符串 + 手动校验，见 [Config/Loader.cs:137](../../ParrotCode.Net/Config/Loader.cs#L137)）。

```csharp
using System.Text.Json;
using YamlDotNet.Serialization;

namespace ParrotCode;

/// <summary>
/// Hook 事件类型（12 种，五类）。tool_pre_exec 是唯一的拦截事件。
/// </summary>
public enum HookEvent
{
    // 会话类
    SessionStart,       // 会话开始（用户输入前）
    SessionEnd,         // 会话结束（Agent 完成或取消后）

    // 轮次类
    RoundStart,         // ReAct 轮次开始
    RoundEnd,           // ReAct 轮次结束

    // 消息类
    MessagePreSend,     // 发送给 LLM 前（消息列表已构建）
    MessagePostReceive, // 收到 LLM 回复后（assistant 消息入历史）

    // 工具类
    ToolPreExec,        // 工具执行前（拦截——可返回拒绝原因）
    ToolPostExec,       // 工具执行后（通知——拿不到拒绝能力）

    // 系统类
    SystemStartup,      // 程序启动
    SystemShutdown,     // 程序关闭
    SystemError,        // 致命错误（AgentLoop ErrorEvent）
    SystemCompress      // 上下文压缩完成
}

/// <summary>
/// 拦截事件集合——这些事件的 prompt_inject 动作返回值作为拒绝原因。
/// 拦截事件不允许 async: true（必须同步返回结果）。
/// </summary>
internal static class InterceptEvents
{
    public static readonly HashSet<HookEvent> Set = new() { HookEvent.ToolPreExec };
}

/// <summary>
/// 条件匹配算子。
/// </summary>
public enum HookOperator
{
    /// <summary>精确相等（字符串比较）</summary>
    Exact,
    /// <summary>不等于</summary>
    Not,
    /// <summary>正则匹配（Regex.IsMatch）</summary>
    Regex,
    /// <summary>通配符匹配（支持 * 和 ?）</summary>
    Glob
}

/// <summary>
/// 条件匹配模式。
/// </summary>
public enum HookMatchMode
{
    /// <summary>所有规则都满足（默认）</summary>
    All,
    /// <summary>任一规则满足</summary>
    Any
}

/// <summary>
/// 动作类型。
/// </summary>
public enum HookActionType
{
    /// <summary>执行 shell 命令</summary>
    Shell,
    /// <summary>注入提示文本（拦截事件中作为拒绝原因）</summary>
    PromptInject,
    /// <summary>调用 HTTP webhook</summary>
    Http,
    /// <summary>起子 Agent 执行自动化任务（依赖迭代 14 SubAgentRunner，15b 实现）</summary>
    SubAgent
}

/// <summary>
/// 单条条件规则：字段路径 + 算子 + 目标值。
/// field 是 dot-path，如 "tool_name" / "params.path" / "round"。
/// Operator 是字符串（YAML 反序列化用），OperatorEnum 是解析后的强类型（Loader 填充）。
/// </summary>
public sealed class ConditionRule
{
    public string Field { get; set; } = string.Empty;

    /// <summary>YAML 中的算子字符串（exact/not/regex/glob）。Loader 解析为 OperatorEnum。</summary>
    public string Operator { get; set; } = "exact";

    public string Value { get; set; } = string.Empty;

    /// <summary>解析后的强类型算子（Loader.ValidateAndNormalize 填充，YAML 忽略）。</summary>
    [YamlIgnore]
    public HookOperator OperatorEnum { get; set; }
}

/// <summary>
/// 条件：match 模式 + 规则列表。null 或空规则列表 = 无条件触发。
/// Match 是字符串（YAML 反序列化用），MatchMode 是解析后的强类型（Loader 填充）。
/// </summary>
public sealed class HookCondition
{
    /// <summary>YAML 中的匹配模式字符串（ALL/ANY）。Loader 解析为 MatchMode。</summary>
    public string Match { get; set; } = "ALL";

    public List<ConditionRule> Rules { get; set; } = new();

    /// <summary>解析后的强类型匹配模式（Loader.ValidateAndNormalize 填充，YAML 忽略）。</summary>
    [YamlIgnore]
    public HookMatchMode MatchMode { get; set; }
}

/// <summary>
/// Hook 动作。根据 Type 使用不同字段。
/// Type 是字符串（YAML 反序列化用），ActionType 是解析后的强类型（Loader 填充）。
/// </summary>
public sealed class HookAction
{
    /// <summary>YAML 中的动作类型字符串（shell/prompt_inject/http/sub_agent）。Loader 解析为 ActionType。</summary>
    public string Type { get; set; } = string.Empty;

    // shell
    public string Command { get; set; } = string.Empty;

    // prompt_inject
    public string Text { get; set; } = string.Empty;

    // http
    public string Url { get; set; } = string.Empty;
    public string Method { get; set; } = "POST";
    public Dictionary<string, string> Headers { get; set; } = new();
    public string Body { get; set; } = string.Empty;

    // sub_agent（15b 实现）
    public string Task { get; set; } = string.Empty;
    public string Role { get; set; } = "general";
    public string Mode { get; set; } = "definitional";

    /// <summary>解析后的强类型动作类型（Loader.ValidateAndNormalize 填充，YAML 忽略）。</summary>
    [YamlIgnore]
    public HookActionType ActionType { get; set; }
}

/// <summary>
/// 控制选项。
/// </summary>
public sealed class HookControl
{
    /// <summary>只触发一次（触发后自动跳过后续匹配）。用 rule.Name 跟踪。</summary>
    public bool Once { get; set; }

    /// <summary>异步执行（fire-and-forget）。拦截事件禁止 async=true。</summary>
    public bool Async { get; set; }

    /// <summary>动作执行超时（秒）。默认 30。</summary>
    public double Timeout { get; set; } = 30.0;
}

/// <summary>
/// Hook 规则：事件 + 条件 + 动作列表 + 控制选项。
/// Event 是字符串（YAML 反序列化用），EventType 是解析后的强类型（Loader 填充）。
/// </summary>
public sealed class HookRule
{
    /// <summary>YAML 中的事件字符串（session_start/tool_pre_exec 等）。Loader 解析为 EventType。</summary>
    public string Event { get; set; } = string.Empty;

    public HookCondition? Condition { get; set; }
    public List<HookAction> Actions { get; set; } = new();
    public HookControl Control { get; set; } = new();
    public string Name { get; set; } = string.Empty;

    /// <summary>解析后的强类型事件（Loader.ValidateAndNormalize 填充，YAML 忽略）。</summary>
    [YamlIgnore]
    public HookEvent EventType { get; set; }

    /// <summary>是否为拦截事件。</summary>
    public bool IsIntercept => InterceptEvents.Set.Contains(EventType);
}

/// <summary>
/// Hook 配置异常（校验失败）。
/// </summary>
public sealed class HookConfigException : Exception
{
    public HookConfigException(string message) : base(message) { }
}
```

### 3.2 ConditionEvaluator（`Hooks/Conditions.cs`）

> **技术风险规避**：`Regex.IsMatch(input, pattern)` 静态方法无超时参数，永远不会抛 `RegexMatchTimeoutException`——原设计中 `catch (RegexMatchTimeoutException)` 是死代码。改用 `Regex.IsMatch(input, pattern, RegexOptions.None, TimeSpan)` 带超时重载（已验证，见第九节）。

```csharp
using System.Text.RegularExpressions;

namespace ParrotCode;

/// <summary>
/// 条件评估器：对上下文字典评估 Condition。
/// - null 或空规则列表 → 无条件 True
/// - ALL → 所有规则都满足
/// - ANY → 任一规则满足
/// dot-path 解析：field "params.path" → context["params"]["path"]
/// </summary>
public sealed class ConditionEvaluator
{
    /// <summary>
    /// 评估条件。null 或空规则 → True。
    /// 使用 Condition.MatchMode（Loader 解析后的强类型）。
    /// </summary>
    public bool Evaluate(HookCondition? condition, Dictionary<string, object?> context)
    {
        if (condition is null || condition.Rules.Count == 0)
            return true;

        var results = condition.Rules.Select(r => EvalRule(r, context));

        return condition.MatchMode == HookMatchMode.All
            ? results.All(x => x)
            : results.Any(x => x);
    }

    /// <summary>
    /// 使用 ConditionRule.OperatorEnum（Loader 解析后的强类型）。
    /// </summary>
    private bool EvalRule(ConditionRule rule, Dictionary<string, object?> context)
    {
        var actual = ResolveField(rule.Field, context)?.ToString() ?? string.Empty;
        var target = rule.Value;

        return rule.OperatorEnum switch
        {
            HookOperator.Exact => string.Equals(actual, target, StringComparison.Ordinal),
            HookOperator.Not => !string.Equals(actual, target, StringComparison.Ordinal),
            HookOperator.Glob => GlobMatch(actual, target),
            HookOperator.Regex => SafeRegexMatch(actual, target),
            _ => false
        };
    }

    /// <summary>
    /// dot-path 解析：a.b.c → context[a][b][c]。
    /// 中间节点非 Dictionary 或不存在 → 返回 null（空字符串语义）。
    /// </summary>
    private static object? ResolveField(string field, Dictionary<string, object?> context)
    {
        var parts = field.Split('.');
        object? current = context;

        foreach (var part in parts)
        {
            if (current is Dictionary<string, object?> dict && dict.TryGetValue(part, out var val))
            {
                current = val;
            }
            else
            {
                return null;
            }
        }

        return current;
    }

    /// <summary>
    /// 通配符匹配：* 匹配任意字符序列，? 匹配单个字符。
    /// 用 Regex 实现（将 glob 模式转正则）。
    /// </summary>
    private static bool GlobMatch(string input, string pattern)
    {
        var regexPattern = "^" + Regex.Escape(pattern)
            .Replace("\\*", ".*")
            .Replace("\\?", ".") + "$";
        return Regex.IsMatch(input, regexPattern, RegexOptions.CultureInvariant);
    }

    /// <summary>
    /// 安全正则匹配：正则语法错误或超时返回 False（不抛异常）。
    /// 使用带 TimeSpan 超时的重载——防止 ReDoS（正则灾难性回溯）。
    /// </summary>
    private static bool SafeRegexMatch(string input, string pattern)
    {
        try
        {
            return Regex.IsMatch(input, pattern, RegexOptions.None, TimeSpan.FromSeconds(1));
        }
        catch (RegexMatchTimeoutException)
        {
            return false;   // 正则回溯超时
        }
        catch (ArgumentException)
        {
            return false;   // 无效正则
        }
    }
}
```

### 3.3 TemplateEngine（`Hooks/Templates.cs`）

```csharp
using System.Text.RegularExpressions;

namespace ParrotCode;

/// <summary>
/// 模板变量替换：{{var}} 占位符从上下文字典替换。
/// 支持 dot-path：{{params.path}} → context["params"]["path"]。
/// 未定义变量 → 空字符串（永不抛异常）。
/// </summary>
public sealed class TemplateEngine
{
    private static readonly Regex VarRegex = new(
        @"\{\{(\w+(?:\.\w+)*)\}\}",
        RegexOptions.Compiled);

    /// <summary>
    /// 渲染模板。所有 {{var}} 替换为上下文中的值。
    /// </summary>
    public string Render(string template, Dictionary<string, object?> context)
    {
        if (string.IsNullOrEmpty(template))
            return string.Empty;

        return VarRegex.Replace(template, match =>
        {
            var path = match.Groups[1].Value;
            var value = ResolvePath(path, context);
            return value ?? string.Empty;
        });
    }

    /// <summary>
    /// dot-path 解析（与 ConditionEvaluator.ResolveField 同逻辑）。
    /// </summary>
    private static string? ResolvePath(string path, Dictionary<string, object?> context)
    {
        var parts = path.Split('.');
        object? current = context;

        foreach (var part in parts)
        {
            if (current is Dictionary<string, object?> dict && dict.TryGetValue(part, out var val))
            {
                current = val;
            }
            else
            {
                return null;
            }
        }

        return current?.ToString();
    }
}
```

### 3.4 ActionExecutor（`Hooks/Actions.cs`）

> **技术风险规避**：HttpClient 不是接口，单测中无法直接 mock HTTP 请求。构造函数改为接受 `HttpMessageHandler?`，内部构造 `HttpClient`。单测中注入 mock HttpMessageHandler 即可拦截请求（已验证，见第九节）。

> **15a 范围**：实现 shell / prompt_inject / http 三种动作 + SetSubAgentRunner 预留空方法。
> **15b 追加**：sub_agent 动作实现（填充 ExecSubAgentAsync）。

```csharp
using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.Extensions.Logging;

namespace ParrotCode;

/// <summary>
/// Hook 动作执行器（4 种动作 + 错误隔离）。
/// 所有动作的异常被捕获并记日志——Hook 失败不中断 Agent 主循环。
///
/// 15a 实现：shell / prompt_inject / http
/// 15b 追加：sub_agent（依赖 SubAgentRunner，通过 SetSubAgentRunner 注入）
/// </summary>
public sealed class ActionExecutor
{
    private readonly TemplateEngine _templates;
    private readonly HttpClient _httpClient;
    private readonly ILogger? _logger;

    // 15b 追加：sub_agent 动作用的子 Agent 运行器（setter 注入）
    private SubAgentRunner? _subAgentRunner;
    private BackgroundTaskManager? _backgroundTaskManager;
    private ConversationHistory? _parentHistory;

    /// <summary>
    /// 构造函数。接受 HttpMessageHandler（而非 HttpClient）以支持单测 mock。
    /// 生产环境传 null——内部用默认 HttpClientHandler。
    /// 单测中传 mock HttpMessageHandler 拦截 HTTP 请求。
    /// </summary>
    public ActionExecutor(TemplateEngine? templates = null,
                          HttpMessageHandler? handler = null,
                          ILogger? logger = null)
    {
        _templates = templates ?? new TemplateEngine();
        _httpClient = new HttpClient(handler ?? new HttpClientHandler());
        _logger = logger;
    }

    /// <summary>
    /// 注入 SubAgentRunner（15b 中 TerminalApp.RunAsync 调用）。
    /// 15a 中此方法为空壳——sub_agent 动作会记警告并跳过。
    /// </summary>
    public void SetSubAgentRunner(SubAgentRunner? runner,
                                   BackgroundTaskManager? backgroundManager = null,
                                   ConversationHistory? parentHistory = null)
    {
        // 15a：空实现（_subAgentRunner 保持 null，sub_agent 动作跳过）
        // 15b：取消注释填充实现
        // _subAgentRunner = runner;
        // _backgroundTaskManager = backgroundManager;
        // _parentHistory = parentHistory;
    }

    /// <summary>
    /// 执行单个动作。返回结果文本（可能为 null）。
    /// 异常被捕获——Hook 失败只记日志，不抛出。
    /// 使用 action.ActionType（Loader 解析后的强类型）。
    /// </summary>
    public async Task<string?> ExecuteAsync(
        HookAction action,
        Dictionary<string, object?> context,
        double timeoutSeconds = 30.0,
        CancellationToken cancellationToken = default)
    {
        try
        {
            return action.ActionType switch
            {
                HookActionType.Shell => await ExecShellAsync(action, context, timeoutSeconds, cancellationToken),
                HookActionType.PromptInject => ExecPromptInject(action, context),
                HookActionType.Http => await ExecHttpAsync(action, context, timeoutSeconds, cancellationToken),
                HookActionType.SubAgent => await ExecSubAgentAsync(action, context, cancellationToken),
                _ => null
            };
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Hook 动作 [{Type}] 执行失败", action.ActionType);
            return null;
        }
    }

    // ===== shell =====

    private async Task<string?> ExecShellAsync(
        HookAction action, Dictionary<string, object?> context,
        double timeoutSeconds, CancellationToken ct)
    {
        var command = _templates.Render(action.Command, context);
        if (string.IsNullOrWhiteSpace(command))
            return null;

        var (fileName, args) = PrepareShellCommand(command);

        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = args,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var proc = new Process { StartInfo = psi };
        proc.Start();

        var stdoutTask = proc.StandardOutput.ReadToEndAsync(ct);
        var stderrTask = proc.StandardError.ReadToEndAsync(ct);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));

        try
        {
            await proc.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            _logger?.LogWarning("Hook shell 动作超时（{Sec}s）：{Cmd}", timeoutSeconds, command);
            try { proc.Kill(); } catch { }
            return $"（超时 {timeoutSeconds}s）";
        }

        var stdout = await stdoutTask;
        var stderr = await stderrTask;

        var result = stdout;
        if (!string.IsNullOrEmpty(stderr))
            result += $"\n[stderr]\n{stderr}";

        return result.Length > 2000 ? result[..2000] + "\n...（截断）" : result;
    }

    private static (string FileName, string Args) PrepareShellCommand(string command)
    {
        if (OperatingSystem.IsWindows())
            return ("cmd.exe", $"/c \"{command}\"");
        return ("/bin/sh", $"-c \"{command}\"");
    }

    // ===== prompt_inject =====

    private string? ExecPromptInject(HookAction action, Dictionary<string, object?> context)
    {
        return _templates.Render(action.Text, context);
    }

    // ===== http =====

    private async Task<string?> ExecHttpAsync(
        HookAction action, Dictionary<string, object?> context,
        double timeoutSeconds, CancellationToken ct)
    {
        var url = _templates.Render(action.Url, context);
        if (string.IsNullOrWhiteSpace(url))
            return null;

        var body = string.IsNullOrEmpty(action.Body) ? null : _templates.Render(action.Body, context);

        using var req = new HttpRequestMessage(new(action.Method.ToUpperInvariant()), url);
        foreach (var (key, value) in action.Headers)
        {
            req.Headers.TryAddWithoutValidation(key, value);
        }
        if (body is not null)
        {
            req.Content = new StringContent(body, Encoding.UTF8);
            req.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));

        try
        {
            var resp = await _httpClient.SendAsync(req, cts.Token);
            var text = await resp.Content.ReadAsStringAsync(cts.Token);
            return text.Length > 2000 ? text[..2000] + "\n...（截断）" : text;
        }
        catch (OperationCanceledException)
        {
            _logger?.LogWarning("Hook http 动作超时（{Sec}s）：{Url}", timeoutSeconds, url);
            return $"（超时 {timeoutSeconds}s）";
        }
    }

    // ===== sub_agent（15b 实现）=====

    private Task<string?> ExecSubAgentAsync(
        HookAction action, Dictionary<string, object?> context, CancellationToken ct)
    {
        // 15a：sub_agent 动作未实现，记警告并跳过
        // 15b：取消下方注释填充实现
        if (_subAgentRunner is null)
        {
            _logger?.LogWarning("Hook sub_agent 动作未注入 SubAgentRunner，跳过（15a 未实现，需 15b 接入）");
            return Task.FromResult<string?>(null);
        }

        // 15b 实现代码见 iter-15b-design.md 第 3.1 节
        return Task.FromResult<string?>(null);
    }
}
```

**关键设计点**：

1. **错误隔离**：`ExecuteAsync` 的 switch 外层 try-catch 全包裹——任何动作异常转日志，返回 null，不抛出。
2. **shell 跨平台**：Windows 用 `cmd.exe /c`，Unix 用 `/bin/sh -c`。超时用 `CancellationTokenSource.CancelAfter` + `proc.Kill`。
3. **SetSubAgentRunner 预留**：15a 中方法体为空（注释说明 15b 填充）。sub_agent 动作因 `_subAgentRunner` 始终为 null 而走"跳过"分支。
4. **结果截断**：shell/http 结果截断到 2000 字符（防日志爆炸）。
5. **HttpClient 可测试**：构造函数接受 `HttpMessageHandler?`，单测中注入 mock handler 拦截 HTTP 请求。生产环境传 null 用默认 handler。
6. **强类型 ActionType**：使用 `action.ActionType`（Loader 解析后的枚举），而非 `action.Type`（YAML 字符串）。

### 3.5 HookLoader（`Hooks/Loader.cs`）

> **技术风险规避**：YAML 中的枚举值用 snake_case（如 `tool_pre_exec`），YamlDotNet 无法直接反序列化为枚举。HookRule / HookAction / HookCondition / ConditionRule 中的枚举字段均为 string 类型（YAML 反序列化），Loader.ValidateAndNormalize 中用 `SnakeToPascal` + `Enum.TryParse` 解析为强类型属性。

```csharp
using Microsoft.Extensions.Logging;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace ParrotCode;

/// <summary>
/// Hook 规则加载器：两级 YAML 加载 + 集中校验 + 枚举字符串解析。
/// 加载顺序（两者合并，都执行——project 不覆盖 global）：
/// 1. 全局 ~/.parrotcode/hooks.yaml
/// 2. 项目 ./.parrotcode/hooks.yaml
///
/// 配置文件格式（YAML，枚举值用 snake_case）：
/// hooks:
///   - name: git-stash-before-write
///     event: tool_pre_exec          # snake_case 字符串，Loader 解析为 HookEvent.ToolPreExec
///     condition:
///       match: ALL                  # 字符串，Loader 解析为 HookMatchMode.All
///       rules:
///         - field: tool_name
///           operator: exact         # 字符串，Loader 解析为 HookOperator.Exact
///           value: write_file
///     actions:
///       - type: shell               # 字符串，Loader 解析为 HookActionType.Shell
///         command: "git stash"
///     control:
///       once: false
///       async: false
///       timeout: 30
/// </summary>
public sealed class HookLoader
{
    private readonly string _projectRoot;
    private readonly string _userHome;
    private readonly ILogger? _logger;

    public HookLoader(string? projectRoot = null,
                      string? userHome = null,
                      ILogger? logger = null)
    {
        _projectRoot = projectRoot ?? Directory.GetCurrentDirectory();
        _userHome = userHome ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        _logger = logger;
    }

    /// <summary>
    /// 加载所有 Hook 规则（全局 + 项目合并）。
    /// 单个文件解析失败记录日志跳过，不中断整体加载。
    /// </summary>
    public IReadOnlyList<HookRule> Load()
    {
        var rules = new List<HookRule>();

        var globalPath = Path.Combine(_userHome, ".parrotcode", "hooks.yaml");
        var projectPath = Path.Combine(_projectRoot, ".parrotcode", "hooks.yaml");

        rules.AddRange(LoadFile(globalPath, "global"));
        rules.AddRange(LoadFile(projectPath, "project"));

        if (rules.Count > 0)
            _logger?.LogInformation("已加载 {Count} 条 Hook 规则", rules.Count);

        return rules;
    }

    private IReadOnlyList<HookRule> LoadFile(string path, string source)
    {
        if (!File.Exists(path))
            return Array.Empty<HookRule>();

        try
        {
            var raw = File.ReadAllText(path);
            var deserializer = new DeserializerBuilder()
                .WithNamingConvention(UnderscoredNamingConvention.Instance)
                .Build();

            var doc = deserializer.Deserialize<HookConfigFile>(raw);
            if (doc?.Hooks is null || doc.Hooks.Count == 0)
                return Array.Empty<HookRule>();

            var rules = new List<HookRule>(doc.Hooks.Count);
            for (var i = 0; i < doc.Hooks.Count; i++)
            {
                try
                {
                    var rule = ValidateAndNormalize(doc.Hooks[i], i);
                    rules.Add(rule);
                }
                catch (HookConfigException ex)
                {
                    _logger?.LogWarning("Hook 规则 [{Source}#{Index}] 无效：{Error}", source, i, ex.Message);
                }
            }

            return rules;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning("Hook 文件 [{Path}] 解析失败：{Error}", path, ex.Message);
            return Array.Empty<HookRule>();
        }
    }

    /// <summary>
    /// 校验并规范化单条规则。
    ///
    /// 校验项：
    /// - event 必填且有效（snake_case → PascalCase → 枚举）
    /// - actions 至少一个，每个 action 的 type 必须有效
    /// - shell 动作必须有 command
    /// - prompt_inject 动作必须有 text
    /// - http 动作必须有 url
    /// - sub_agent 动作必须有 task
    /// - 拦截事件不允许 async=true
    /// - name 为空时自动生成 "rule-{index}"
    /// - condition 中的 match / operator 字符串解析为枚举
    /// </summary>
    private static HookRule ValidateAndNormalize(HookRule rule, int index)
    {
        // name 默认值
        if (string.IsNullOrWhiteSpace(rule.Name))
            rule.Name = $"rule-{index}";

        // event 解析（snake_case → PascalCase → 枚举）
        if (string.IsNullOrWhiteSpace(rule.Event))
            throw new HookConfigException("event 字段不能为空");

        if (!Enum.TryParse<HookEvent>(SnakeToPascal(rule.Event), out var evt))
            throw new HookConfigException($"无效的 event 值: '{rule.Event}'");
        rule.EventType = evt;

        // actions 校验 + type 解析
        if (rule.Actions.Count == 0)
            throw new HookConfigException("至少需要一个 action");

        foreach (var action in rule.Actions)
        {
            if (!Enum.TryParse<HookActionType>(SnakeToPascal(action.Type), out var at))
                throw new HookConfigException($"无效的 action type: '{action.Type}'");
            action.ActionType = at;

            switch (action.ActionType)
            {
                case HookActionType.Shell when string.IsNullOrWhiteSpace(action.Command):
                    throw new HookConfigException($"action type 'shell' 缺少 'command' 字段");
                case HookActionType.PromptInject when string.IsNullOrWhiteSpace(action.Text):
                    throw new HookConfigException($"action type 'prompt_inject' 缺少 'text' 字段");
                case HookActionType.Http when string.IsNullOrWhiteSpace(action.Url):
                    throw new HookConfigException($"action type 'http' 缺少 'url' 字段");
                case HookActionType.SubAgent when string.IsNullOrWhiteSpace(action.Task):
                    throw new HookConfigException($"action type 'sub_agent' 缺少 'task' 字段");
            }
        }

        // 拦截事件不允许 async
        if (rule.IsIntercept && rule.Control.Async)
            throw new HookConfigException($"拦截事件 '{rule.Event}' 不允许 async=true");

        // condition 中的 match / operator 解析
        if (rule.Condition is not null)
        {
            if (!Enum.TryParse<HookMatchMode>(SnakeToPascal(rule.Condition.Match), out var mm))
                throw new HookConfigException($"无效的 match 值: '{rule.Condition.Match}'");
            rule.Condition.MatchMode = mm;

            foreach (var cr in rule.Condition.Rules)
            {
                if (!Enum.TryParse<HookOperator>(SnakeToPascal(cr.Operator), out var op))
                    throw new HookConfigException($"无效的 operator 值: '{cr.Operator}'");
                cr.OperatorEnum = op;
            }
        }

        return rule;
    }

    /// <summary>
    /// snake_case → PascalCase 转换。
    /// tool_pre_exec → ToolPreExec
    /// exact → Exact
    /// prompt_inject → PromptInject
    /// </summary>
    private static string SnakeToPascal(string snake)
    {
        if (string.IsNullOrEmpty(snake)) return snake;
        var parts = snake.Split('_');
        return string.Concat(parts.Select(p =>
            p.Length == 0 ? "" : char.ToUpperInvariant(p[0]) + p[1..].ToLowerInvariant()));
    }

    private sealed class HookConfigFile
    {
        public List<HookRule>? Hooks { get; set; }
    }
}
```

### 3.6 HookEngine（`Hooks/Engine.cs`）

```csharp
using Microsoft.Extensions.Logging;

namespace ParrotCode;

/// <summary>
/// Hook 引擎：注册规则，在生命周期节点触发事件。
///
/// 触发流程：
/// 1. 遍历规则，过滤 event 匹配（用 rule.EventType 强类型）
/// 2. once 检查（已触发过的跳过）
/// 3. 条件评估（ConditionEvaluator）
/// 4. 执行动作（ActionExecutor）
/// 5. 拦截事件收集 prompt_inject 返回值作为拒绝原因
///
/// 错误隔离：
/// - ActionExecutor 内部 try-catch，动作失败只记日志
/// - HookEngine 自身不抛异常——FireAsync 永远正常返回
/// </summary>
public sealed class HookEngine
{
    private readonly List<HookRule> _rules;
    private readonly ConditionEvaluator _conditions;
    private readonly ActionExecutor _actions;
    private readonly ILogger? _logger;

    /// <summary>once:true 已触发的规则名集合。</summary>
    private readonly HashSet<string> _firedOnce = new(StringComparer.Ordinal);

    public HookEngine(IReadOnlyList<HookRule> rules,
                      ActionExecutor? actions = null,
                      ILogger? logger = null)
    {
        _rules = rules ?? Array.Empty<HookRule>().ToList();
        _conditions = new ConditionEvaluator();
        _actions = actions ?? new ActionExecutor(logger: logger);
        _logger = logger;
    }

    /// <summary>
    /// 获取内部 ActionExecutor（供 15b 中 TerminalApp 调 SetSubAgentRunner）。
    /// </summary>
    public ActionExecutor Actions => _actions;

    /// <summary>
    /// 触发事件。执行所有匹配的规则。
    /// 
    /// 拦截事件（tool_pre_exec）：返回第一个 prompt_inject 动作的渲染文本作为拒绝原因。
    ///   调用方应把拒绝原因包装为 ToolResult.Fail 回灌 LLM。
    /// 非拦截事件：返回 null——动作结果被丢弃（仅记日志）。
    ///
    /// async=true 的规则：动作用 fire-and-forget 执行（不等待）。
    /// async=false 的规则：动作顺序 await 执行。
    /// </summary>
    public async Task<string?> FireAsync(
        HookEvent @event,
        Dictionary<string, object?>? context = null,
        CancellationToken cancellationToken = default)
    {
        var ctx = context ?? new Dictionary<string, object?>();
        ctx["_event"] = @event.ToString();

        string? rejection = null;

        foreach (var rule in _rules)
        {
            // 使用 EventType（Loader 解析后的强类型）
            if (rule.EventType != @event)
                continue;

            if (rule.Control.Once && _firedOnce.Contains(rule.Name))
                continue;

            if (!_conditions.Evaluate(rule.Condition, ctx))
                continue;

            if (rule.Control.Once)
                _firedOnce.Add(rule.Name);

            if (rule.Control.Async)
            {
                _ = FireActionsAsync(rule, ctx, cancellationToken);
            }
            else
            {
                var result = await FireActionsAsync(rule, ctx, cancellationToken);

                if (rule.IsIntercept && result is not null && rejection is null)
                    rejection = result;
            }
        }

        return rejection;
    }

    private async Task<string?> FireActionsAsync(
        HookRule rule, Dictionary<string, object?> ctx, CancellationToken ct)
    {
        string? firstResult = null;

        foreach (var action in rule.Actions)
        {
            var result = await _actions.ExecuteAsync(action, ctx, rule.Control.Timeout, ct);

            // 使用 ActionType（Loader 解析后的强类型）
            if (rule.IsIntercept && action.ActionType == HookActionType.PromptInject && result is not null && firstResult is null)
                firstResult = result;
        }

        return firstResult;
    }

    /// <summary>
    /// 清除 once 跟踪（新会话时调用）。
    /// </summary>
    public void ResetOnce() => _firedOnce.Clear();
}
```

---

## 四、验收标准

### 4.1 功能验收（单测）

#### ConditionEvaluator（16 项）

| # | 验收项 | 验证方法 | 通过标准 |
|---|--------|---------|---------|
| 1 | null 条件返回 True | `Evaluate(null, ctx)` | True |
| 2 | 空规则列表返回 True | `Evaluate(new(){Rules=new()}, ctx)` | True |
| 3 | exact 匹配（相等） | field=tool_name, exact, write_file | True |
| 4 | exact 不匹配（不等） | field=tool_name, exact, read_file | False |
| 5 | not 匹配（不等→True） | field=tool_name, not, write_file（实际 read_file） | True |
| 6 | glob 匹配 `*_file` | field=tool_name, glob, *_file（实际 write_file） | True |
| 7 | glob 单字符 `write?file` | field=tool_name, glob, write?file（实际 write_file） | True |
| 8 | regex 匹配 `^/etc/` | field=params.path, regex, ^/etc/（实际 /etc/passwd） | True |
| 9 | regex 不匹配 | field=params.path, regex, ^/etc/（实际 /home/user） | False |
| 10 | regex 无效模式返回 False | pattern=`[`（无效正则） | False，不抛异常 |
| 11 | regex 超时返回 False | ReDoS 正则 `(a+)+$` + 长输入 | False，不抛异常 |
| 12 | ALL 模式全部满足 | 2 条规则都满足 | True |
| 13 | ALL 模式部分不满足 | 1 条满足 1 条不满足 | False |
| 14 | ANY 模式任一满足 | 1 条满足 1 条不满足 | True |
| 15 | ANY 模式全部不满足 | 2 条都不满足 | False |
| 16 | dot-path 解析 `params.path` | context={params:{path:"/etc/passwd"}} | 解析到 /etc/passwd |

#### TemplateEngine（10 项）

| # | 验收项 | 验证方法 | 通过标准 |
|---|--------|---------|---------|
| 17 | 简单变量替换 | `{{tool_name}}`，ctx={tool_name:"write_file"} | "write_file" |
| 18 | dot-path 替换 | `{{params.path}}`，ctx={params:{path:"/etc"}} | "/etc" |
| 19 | 多变量替换 | `{{a}}-{{b}}`，ctx={a:"x",b:"y"} | "x-y" |
| 20 | 未定义变量→空字符串 | `{{undefined}}`，ctx={} | "" |
| 21 | 嵌套 dot-path | `{{a.b.c}}`，ctx={a:{b:{c:"deep"}}} | "deep" |
| 22 | 无占位符原样返回 | `"hello world"`，ctx={} | "hello world" |
| 23 | 空模板返回空 | `""`，ctx={} | "" |
| 24 | 变量值为 null→空字符串 | `{{x}}`，ctx={x:null} | "" |
| 25 | 变量值为数字→字符串 | `{{n}}`，ctx={n:42} | "42" |
| 26 | 变量值为 bool→字符串 | `{{b}}`，ctx={b:true} | "True" |

#### HookLoader（20 项）

| # | 验收项 | 验证方法 | 通过标准 |
|---|--------|---------|---------|
| 27 | 文件不存在返回空列表 | Load() 指向不存在文件 | 返回空列表，不抛异常 |
| 28 | 空文件返回空列表 | Load() 指向空文件 | 返回空列表 |
| 29 | 无 hooks 字段返回空列表 | YAML 只有其他字段 | 返回空列表 |
| 30 | 单条规则加载成功 | 1 条有效规则 | 返回 1 条 |
| 31 | 多条规则加载成功 | 3 条有效规则 | 返回 3 条 |
| 32 | 全局 + 项目合并 | global 1 条 + project 1 条 | 返回 2 条 |
| 33 | YAML 语法错误记日志返回空 | 损坏 YAML | 返回空列表，日志含错误 |
| 34 | event 字段缺失记日志跳过 | 规则无 event 字段 | 跳过该规则 |
| 35 | event 无效值记日志跳过 | event=unknown_event | 跳过该规则 |
| 36 | actions 为空记日志跳过 | 规则无 actions | 跳过该规则 |
| 37 | action type 无效记日志跳过 | type=unknown_type | 跳过该规则 |
| 38 | shell 缺 command 抛异常 | shell 动作无 command | HookConfigException |
| 39 | prompt_inject 缺 text 抛异常 | prompt_inject 无 text | HookConfigException |
| 40 | http 缺 url 抛异常 | http 无 url | HookConfigException |
| 41 | sub_agent 缺 task 抛异常 | sub_agent 无 task | HookConfigException |
| 42 | tool_pre_exec + async=true 抛异常 | 拦截事件 + async | HookConfigException |
| 43 | name 为空自动生成 | 规则无 name | Name = "rule-{index}" |
| 44 | control 缺省用默认值 | 无 control 字段 | once=false, async=false, timeout=30 |
| 45 | condition 缺省为 null | 无 condition 字段 | Condition=null（无条件触发） |
| 46 | 单条无效不影响其他 | 1 无效 + 1 有效 | 返回 1 条有效 |

#### HookEngine（14 项）

| # | 验收项 | 验证方法 | 通过标准 |
|---|--------|---------|---------|
| 47 | 无规则返回 null | FireAsync(any event) | null |
| 48 | 事件不匹配返回 null | 规则 event=A，触发 B | null，动作不执行 |
| 49 | 条件匹配触发动作 | 条件满足 + mock action | 动作被调用 |
| 50 | 条件不匹配不触发 | 条件不满足 | 动作不被调用 |
| 51 | 拦截事件返回拒绝原因 | tool_pre_exec + prompt_inject | 返回渲染文本 |
| 52 | 非拦截事件返回 null | round_start + prompt_inject | null |
| 53 | 多规则全部触发 | 2 条同事件规则 | 2 个动作都执行 |
| 54 | once=true 只触发一次 | 同事件发两次 | 第二次跳过 |
| 55 | once=false 每次触发 | 同事件发两次 | 两次都执行 |
| 56 | ResetOnce 清除跟踪 | once 触发后 ResetOnce | 再次触发成功 |
| 57 | async=true fire-and-forget | async 动作 | FireAsync 立即返回 |
| 58 | 动作异常不抛出 | mock 动作抛异常 | 返回 null，不抛出 |
| 59 | context 传 null 不崩溃 | FireAsync(event, null) | 内部创建空 dict |
| 60 | 多个拦截规则返回第一个 | 2 条拦截规则 | 返回第一个拒绝原因 |

#### ActionExecutor（15 项，15a 范围：shell/prompt_inject/http + sub_agent 跳过）

| # | 验收项 | 验证方法 | 通过标准 |
|---|--------|---------|---------|
| 61 | shell 执行成功 | `echo hello` | 返回 "hello" |
| 62 | shell 超时 | 跨平台慢命令（Windows: `timeout /t 5` / Unix: `sleep 5`）+ timeout=1 | 返回 "（超时 1s）" |
| 63 | shell stderr 捕获 | Windows: `echo err 1>&2` / Unix: `echo err >&2` | 返回含 [stderr] |
| 64 | shell 结果截断 | 输出 > 2000 字符 | 截断 + "...（截断）" |
| 65 | shell 空 command 返回 null | command="" | null |
| 66 | shell 跨平台 | Windows/Unix | Windows 用 cmd.exe，Unix 用 /bin/sh |
| 67 | prompt_inject 渲染模板 | text=`拒绝 {{tool_name}}` | "拒绝 write_file" |
| 68 | http 发 POST（mock） | mock HttpMessageHandler | 请求被发送 |
| 69 | http 发 GET（mock） | method=GET | GET 请求被发送 |
| 70 | http 带 headers | headers={X-Api-Key:xxx} | header 被设置 |
| 71 | http 带 body（模板渲染） | body=`{"tool":"{{tool_name}}"}` | body 被渲染 |
| 72 | http 超时 | mock 慢响应 + timeout=1 | 返回 "（超时 1s）" |
| 73 | http 结果截断 | 响应 > 2000 字符 | 截断 |
| 74 | sub_agent 未注入 runner 时跳过 | 不调 SetSubAgentRunner | 记警告，返回 null |
| 75 | 动作异常返回 null | mock 动作抛异常 | null，不抛出 |

### 4.2 工程验收

- `dotnet build` 0 error 0 warning
- 全部既有测试通过（15a 不碰任何既有文件，既有测试不受影响）
- 新增单测覆盖 Models / Conditions / Templates / Loader / Engine / Actions（75 项）
- 全部既有文件 git diff 为空（零改动验证）

### 4.3 代码质量

- 全部既有文件零改动
- `nullable` 引用类型开启，无 warning
- `async` 全链路，无 `.Result` / `.Wait()`
- `CancellationToken` 贯穿（ActionExecutor.ExecuteAsync + HookEngine.FireAsync）

---

## 五、测试清单

### 5.1 ConditionEvaluatorTests（16 项）

- null 条件返回 True
- 空规则列表返回 True
- exact 匹配（相等 → True，不等 → False）
- not 匹配（不等 → True，相等 → False）
- glob 匹配（`*_file` → 匹配 write_file）
- glob 单字符（`write?file` → 匹配 write_file）
- regex 匹配（`^/etc/` → 匹配 /etc/passwd）
- regex 无效模式返回 False（不抛异常）
- **regex 超时返回 False**（ReDoS 正则 `(a+)+$` + 长输入 `aaaaaa...!`，验证 `TimeSpan` 超时生效）
- ALL 模式全部满足 → True
- ALL 模式部分不满足 → False
- ANY 模式任一满足 → True
- ANY 模式全部不满足 → False
- dot-path 解析（`params.path` → 嵌套值）
- dot-path 中间节点不存在 → False
- dot-path 中间节点非 dict → False

### 5.2 TemplateEngineTests（10 项）

- 简单变量替换（`{{tool_name}}`）
- dot-path 替换（`{{params.path}}`）
- 多变量替换
- 未定义变量替换为空字符串
- 嵌套 dot-path（`{{a.b.c}}`）
- 无占位符的模板原样返回
- 空模板返回空字符串
- 变量值为 null 替换为空字符串
- 变量值为数字替换为字符串
- 变量值为 bool 替换为 "True"/"False"

### 5.3 HookLoaderTests（20 项）

- 文件不存在返回空列表
- 空文件返回空列表
- 无 hooks 字段返回空列表
- 单条规则加载成功
- 多条规则加载成功
- 全局 + 项目合并
- YAML 语法错误记日志返回空列表
- event 字段缺失记日志跳过
- **event 无效值记日志跳过**（event=unknown_event）
- actions 为空记日志跳过
- **action type 无效记日志跳过**（type=unknown_type）
- shell 缺 command 抛 HookConfigException
- prompt_inject 缺 text 抛 HookConfigException
- http 缺 url 抛 HookConfigException
- sub_agent 缺 task 抛 HookConfigException
- tool_pre_exec + async=true 抛 HookConfigException
- name 为空自动生成 "rule-{index}"
- control 字段缺省用默认值
- condition 字段缺省为 null
- 单条规则无效不影响其他规则

### 5.4 HookEngineTests（14 项）

- FireAsync 无规则返回 null
- FireAsync 事件不匹配返回 null
- FireAsync 条件匹配触发动作
- FireAsync 条件不匹配不触发
- FireAsync 拦截事件返回 prompt_inject 拒绝原因
- FireAsync 非拦截事件返回 null
- FireAsync 多规则全部触发
- FireAsync once=true 只触发一次
- FireAsync once=false 每次触发
- ResetOnce 清除跟踪后再次触发
- FireAsync async=true fire-and-forget
- FireAsync 动作异常不抛出
- FireAsync context 传 null 不崩溃
- FireAsync 多个拦截规则只返回第一个拒绝原因

### 5.5 ActionExecutorTests（15 项）

- shell 动作执行成功（`echo hello` → "hello"）
- **shell 动作超时**（跨平台：Windows 用 `timeout /t 5 /nobreak`，Unix 用 `sleep 5`，timeout=1 → "（超时 1s）"）
- shell 动作 stderr 捕获（Windows: `echo err 1>&2` / Unix: `echo err >&2`）
- shell 动作结果截断（> 2000 字符）
- shell 动作空 command 返回 null
- shell 动作跨平台（Windows cmd.exe / Unix /bin/sh）
- prompt_inject 渲染模板文本
- **http 动作发 POST 请求**（mock HttpMessageHandler 注入构造函数）
- http 动作发 GET 请求
- http 动作带 headers
- http 动作带 body（模板渲染）
- http 动作超时（mock 慢响应）
- http 动作结果截断
- sub_agent 动作未注入 runner 时记警告返回 null
- 动作抛异常时返回 null（不抛出）

---

## 六、风险与对策

| 风险 | 严重度 | 对策 |
|------|--------|------|
| **YamlDotNet 枚举反序列化失败** | 高（阻断） | 枚举字段用 string + `[YamlIgnore]` 强类型属性，Loader 中 `SnakeToPascal` + `Enum.TryParse` 解析。已验证（见第九节） |
| **Regex.IsMatch 无超时** | 中（功能缺陷） | `SafeRegexMatch` 改用 `Regex.IsMatch(input, pattern, RegexOptions.None, TimeSpan.FromSeconds(1))` 带超时重载 |
| **HttpClient 不可 mock** | 中（测试性） | `ActionExecutor` 构造函数接受 `HttpMessageHandler?` 而非 `HttpClient`，单测注入 mock handler |
| **shell 超时命令不跨平台** | 低（测试兼容） | 单测中按平台选命令：Windows `timeout /t N /nobreak`，Unix `sleep N` |
| shell 动作跨平台兼容性 | 低 | Windows 用 `cmd.exe /c`，Unix 用 `/bin/sh -c`。单测在两个平台各跑一次 |
| HttpClient 端口耗尽 | 低 | ActionExecutor 持有单个 HttpClient 实例（非每次 new） |
| sub_agent 动作在 15a 中不可用 | 低 | SetSubAgentRunner 空实现 + ExecSubAgentAsync 检查 null 跳过。15b 填充实现 |
| 正则注入风险（用户配置的 regex pattern） | 低 | SafeRegexMatch 捕获 ArgumentException 和 RegexMatchTimeoutException，无效/超时正则返回 False |
| shell 命令注入（用户配置的 command） | 低 | Hook 是用户主动配置的自动化，信任级别不同于 LLM 调用的工具。不经过 SecurityGuard |

---

## 七、与 15b 的衔接

本迭代（15a）交付的 6 个文件被 15b 直接消费：

- **HookEngine**：15b 中 TerminalApp 注入到 AgentLoop + SecureBatchToolExecutor
- **ActionExecutor**：15b 中填充 SetSubAgentRunner 实现 + ExecSubAgentAsync 实现
- **HookRule / HookEvent / HookAction 等**：15b 的 Config/装配直接引用。注意 15b 使用 `rule.EventType` / `action.ActionType`（强类型属性），不使用 `rule.Event` / `action.Type`（YAML 字符串）
- **HookLoader**：15b 中 App.cs 调用 Load() 加载规则
- **HookConfigException**：15b 的 Loader 校验复用

15a 的 `SetSubAgentRunner` 方法预留空壳，15b 取消注释填充实现。15a 的 `ExecSubAgentAsync` 检查 `_subAgentRunner is null` 走跳过分支，15b 填充实际逻辑后此分支不再触发（除非 runner 未注入）。

详见 [iter-15b-design.md](./iter-15b-design.md)。

---

## 八、交付检查清单

- [ ] `Hooks/Models.cs` 新增（HookEvent 12 枚举 + HookOperator 4 + HookMatchMode + HookActionType 4 + ConditionRule / HookCondition / HookAction / HookControl / HookRule + InterceptEvents + HookConfigException。**枚举字段用 string + `[YamlIgnore]` 强类型属性**）
- [ ] `Hooks/Conditions.cs` 新增 ConditionEvaluator（exact/not/regex/glob + ALL/ANY + dot-path。**SafeRegexMatch 用 `TimeSpan` 超时**）
- [ ] `Hooks/Templates.cs` 新增 TemplateEngine（{{var}} dot-path 替换）
- [ ] `Hooks/Actions.cs` 新增 ActionExecutor（shell/prompt_inject/http + 错误隔离 + SetSubAgentRunner 预留空方法。**构造函数接受 `HttpMessageHandler?`**）
- [ ] `Hooks/Loader.cs` 新增 HookLoader（两级 YAML + 集中校验 + **SnakeToPascal 枚举解析**）
- [ ] `Hooks/Engine.cs` 新增 HookEngine（FireAsync + once 跟踪 + ResetOnce。**用 EventType/ActionType/MatchMode/OperatorEnum 强类型**）
- [ ] 全部既有文件 git diff 为空
- [ ] 单测：ConditionEvaluator（16 项）+ TemplateEngine（10 项）+ HookLoader（20 项）+ HookEngine（14 项）+ ActionExecutor（15 项）
- [ ] `dotnet build` 0 error 0 warning
- [ ] 全部既有测试通过

---

## 九、技术风险验证记录

> 本节记录编码前已验证的技术风险及规避方案，避免实现时踩坑。

### 9.1 YamlDotNet 16.2.1 枚举反序列化（已验证，高风险）

**验证方式**：写了最小化测试，用 YamlDotNet 16.2.1 + `UnderscoredNamingConvention` 反序列化 snake_case 枚举值。

**验证结果**：

```
event: ToolPreExec     → ✅ 成功（PascalCase 直接匹配枚举名）
event: tool_pre_exec   → ❌ 失败：System.ArgumentException: Requested value 'tool_pre_exec' was not found
event: toolpreexec     → ✅ 成功（大小写不敏感，但无下划线才匹配）
```

**根因**：`UnderscoredNamingConvention` 只映射**属性名**（`snake_case` → `PascalCase`），不映射**枚举值**。YamlDotNet 内部用 `Enum.Parse(type, value, ignoreCase: true)` 解析枚举——大小写不敏感，但下划线是额外字符不匹配。

**规避方案**（已写入设计）：
- Models.cs 中枚举字段用 `string` 类型（YAML 反序列化用）+ `[YamlIgnore]` 强类型属性（运行时用）
- Loader.cs 中 `SnakeToPascal` 将 `tool_pre_exec` → `ToolPreExec`，再 `Enum.TryParse` 解析
- 与项目中 `Protocol` 字段做法一致（字符串 + 手动校验，见 [Config/Loader.cs:137](../../ParrotCode.Net/Config/Loader.cs#L137)）

### 9.2 Regex.IsMatch 无超时重载（已验证，中风险）

**验证方式**：确认 .NET 8 API 文档。

**验证结果**：
- `Regex.IsMatch(string input, string pattern)` — 静态方法，**无超时参数**，永远不会抛 `RegexMatchTimeoutException`
- `Regex.IsMatch(string input, string pattern, RegexOptions options, TimeSpan matchTimeout)` — 有超时参数，可以触发超时

**根因**：原设计中 `SafeRegexMatch` 用无超时的静态方法，`catch (RegexMatchTimeoutException)` 是死代码。用户配置的 ReDoS 正则（如 `(a+)+$`）会导致无限回溯。

**规避方案**（已写入设计）：
- `SafeRegexMatch` 改用 `Regex.IsMatch(input, pattern, RegexOptions.None, TimeSpan.FromSeconds(1))`
- 超时 1 秒——足够正常正则匹配，拦截灾难性回溯

### 9.3 HttpClient 单测不可 mock（已验证，中风险）

**验证方式**：确认 .NET 8 HttpClient 类设计。

**验证结果**：HttpClient 不是接口，不能直接 mock。但可以通过 `HttpMessageHandler` 间接 mock——HttpClient 的所有 HTTP 请求都委托给 `HttpMessageHandler.SendAsync`。

**规避方案**（已写入设计）：
- `ActionExecutor` 构造函数改为接受 `HttpMessageHandler? handler`（而非 `HttpClient? httpClient`）
- 内部 `new HttpClient(handler ?? new HttpClientHandler())`
- 单测中注入自定义 `HttpMessageHandler` 子类拦截请求

单测 mock 示例：

```csharp
// 单测中用的 mock HttpMessageHandler
public class MockHttpHandler : HttpMessageHandler
{
    private readonly string _responseBody;
    private readonly HttpStatusCode _statusCode;
    public HttpRequestMessage? LastRequest { get; private set; }

    public MockHttpHandler(string responseBody, HttpStatusCode statusCode = HttpStatusCode.OK)
    {
        _responseBody = responseBody;
        _statusCode = statusCode;
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        LastRequest = request;
        var resp = new HttpResponseMessage(_statusCode)
        {
            Content = new StringContent(_responseBody)
        };
        return Task.FromResult(resp);
    }
}

// 单测用法
var handler = new MockHttpHandler("ok");
var executor = new ActionExecutor(handler: handler);
// ... 执行 http 动作 ...
// 验证：handler.LastRequest.Should().NotBeNull();
```

### 9.4 Windows shell 超时命令不存在（已验证，低风险）

**验证方式**：确认 Windows cmd.exe 可用命令。

**验证结果**：
- `sleep` 是 Unix 命令，Windows cmd.exe 中不存在
- Windows 可用 `timeout /t N /nobreak`（延迟 N 秒，不可中断）
- Windows 也可用 `ping -n N 127.0.0.1`（延迟约 N-1 秒）

**规避方案**（已写入测试清单）：
- shell 超时单测中按平台选命令：
  - Windows: `timeout /t 5 /nobreak`（延迟约 5 秒）
  - Unix: `sleep 5`（延迟 5 秒）
- timeout 设为 1 秒，验证超时返回 "（超时 1s）"
