# 迭代 12：Skill 系统（Loader + Registry + skill_loader 工具 + 两阶段加载）

> **状态**：[设计完成，待实现]
> **前置迭代**：11 [已完成]（MCP 协议客户端）、10c [已完成]（项目指令注入）
> **后续迭代**：13（子 Agent）、14（Hook 引擎）
> **目标**：交付可编程 SOP（标准作业流程）系统——`SkillLoader`（三级目录扫描 + YAML frontmatter 解析）+ `SkillRegistry`（注册表 + 激活状态 + Phase 1 摘要）+ `skill_loader` 工具（Phase 2 按需加载 SOP）+ `SkillExecutor`（激活/工具白名单交集）+ `SkillConfig` + `App.cs` 端到端装配 + `/commit` 命令接入。本迭代完成后，Agent 能按可加载的 SOP 完成 Conventional Commits 等固定流程。

---

## 一、迭代目标

### 1.1 核心目标

让 Agent 按"可加载的操作手册"工作，避免把所有流程知识塞进 system prompt：

1. **Skill 文件格式**：YAML frontmatter（元数据）+ Markdown 正文（SOP）
   - frontmatter：`name` / `description` / `tools_allow` / `tools_deny`
   - 正文：可读的步骤说明，LLM 激活后按此执行

2. **三级目录扫描**（复用迭代 10c `InstructionLoader` 模式）：
   - 全局：`~/.parrotcode/skills/*.md`
   - 项目：`./.parrotcode/skills/*.md`
   - 内置：`Skills/Builtin/*.md`（随程序发布，兜底默认）
   - 同名 Skill 优先级：项目级 > 全局级 > 内置（项目级覆盖全局级，内置兜底）

3. **两阶段加载**（核心机制，避免 prompt 膨胀）：
   - **Phase 1（摘要注入）**：把所有 Skill 的 `name + description` 摘要拼到 system prompt，让 LLM 知道"有哪些 Skill 可调"
   - **Phase 2（SOP 按需加载）**：LLM 调 `skill_loader(name)` → SOP 正文作为 `ToolResult.Content` 返回 → 进入 `ConversationHistory` → 后续每轮 LLM 可见

4. **`/commit` 命令**：斜杠命令激活 commit Skill + 注入 SOP + 触发 Agent round

5. **工具白名单**：Skill 声明 `tools_allow` / `tools_deny`，多个 Skill 同时激活取交集，约束 Agent 在 SOP 执行期间可用的工具集

6. **`AgentLoop` 零改动**：SOP 通过 `skill_loader` 的 `ToolResult` 进入 history，复用迭代 6/9 的现有路径。不引入"轮次注入"新机制，不破坏 `BuildMessagesWithSystem` 的 `readonly _systemPrompt` 语义。

### 1.2 本迭代要验证的关键问题

| 问题 | 验证方式 | 失败对策 |
|------|---------|---------|
| Phase 1 摘要是否真的注入 system prompt | 单测 + 代码审查 | `TerminalApp` 构造 prompt 时拼接 `SkillRegistry.GetSummary()` |
| skill_loader 返回的 SOP 是否进入 history | 单测（mock ToolRegistry） | `ToolResult.Content` 即 SOP，`AgentLoop` 入历史逻辑已存在 |
| SOP 进入 history 后后续轮是否可见 | 端到端 | history 累积，`BuildMessagesWithSystem` 每轮快照含全部历史 |
| `tools_allow` / `tools_deny` 交集是否正确 | 单测 | `SkillExecutor` 计算激活 Skill 的交集 |
| Skill 文件 frontmatter 格式错误是否优雅降级 | 单测 | `Loader` 跳过 + 日志警告，不崩溃 |
| 同名 Skill 覆盖优先级是否正确 | 单测 | 项目级 > 全局级 > 内置 |
| `skill_loader` 是否被 `SecurityGuard` 豁免 | 单测 | 系统工具白名单（见 3.5） |
| 多 Skill 同时激活超过上限是否拒绝 | 单测 | `SkillConfig.MaxActiveSkills` 兜底 |
| `skills.enable: false` 时是否完全旁路 | 单测 | `App.cs` 注入空 `SkillRegistry`，`skill_loader` 不注册 |

### 1.3 非目标（明确不做）

- ❌ 不做 Skill 热重载（文件变化时自动重新加载）——后续迭代
- ❌ 不做 Skill 远程市场 / 拉取——Skill 是本地文件
- ❌ 不做 Skill 嵌套调用（Skill 内调 skill_loader 加载另一个 Skill）——`tools_deny` 默认禁用 `skill_loader` 自身
- ❌ 不做 Skill 执行沙箱——Skill 是 prompt（指导 LLM），不是可执行代码
- ❌ 不做 Skill 的 YAML 结构化步骤解析——本迭代是纯 Markdown 文本注入
- ❌ 不做 `/skill list` / `/skill deactivate` 等管理命令——本迭代仅 `/commit` 一个触发点；管理命令留后续

### 1.4 与既有系统的衔接策略

- **复用 `InstructionLoader` 的三级扫描模式**：`SkillLoader` 是同构实现，区别仅在文件格式（frontmatter + 正文）和覆盖语义（指令是合并追加，Skill 是同名覆盖）
- **复用 `ToolBase` / `ToolRegistry`**：`skill_loader` 是普通工具，注册到 `ToolRegistry`，schema 注入走 `ToOpenAiSchemas()`
- **复用 `ToolResult` → `history` 路径**：SOP 作为工具结果入历史，迭代 9 截断/压缩对它一视同仁（SOP 通常 < 2KB，不会触发截断）
- **`AgentLoop` 零改动**：与 10c 一致，只改调用方（`TerminalApp` 构造 prompt 时拼 Skill 摘要）
- **条件注入**：`skills.enable: false` 时 `App.cs` 注入空 `SkillRegistry` + 不注册 `skill_loader`（参考 `SessionStore` / `InstructionLoader` 的 `enable` 模式）

---

## 二、文件改动清单

### 2.1 新增文件（9 个）

```
Skills/
├── Models.cs              # SkillMeta / SkillDefinition / SkillActivateResult
├── Loader.cs              # 三级目录扫描 + YAML frontmatter 解析
├── Registry.cs            # 注册表 + 激活状态 + GetSummary() / GetActiveSop()
├── SkillTool.cs           # skill_loader 工具（继承 ToolBase）
├── Executor.cs            # 激活/停用 + 工具白名单交集
└── Builtin/
    ├── commit.md          # Conventional Commits SOP
    ├── review.md          # 代码审查 SOP
    └── test.md            # 测试生成 SOP
```

### 2.2 修改文件（6 个）

| 文件 | 改动 |
|------|------|
| `Config/Models.cs` | 新增 `SkillConfig` + `AppConfig.Skills` |
| `App/App.cs` | 构造 `SkillLoader` 加载 → `SkillRegistry` → `SkillExecutor` → `SkillTool` 注册到 `ToolRegistry`；条件注入（`enable: false` 时空实现）；注入 `TerminalApp` |
| `Tui/TerminalApp.cs` | 构造函数加 `SkillRegistry` 参数；拼接 `_systemPromptWithInstructions` 时追加 Skill 摘要；`CommandContext` 填充 `SkillExecutor` |
| `Commands/CommandContext.cs` | 新增 `SkillExecutor?` 字段（`/commit` 命令用） |
| `Commands/Builtin/CommitCommand.cs` | **新增**（见 3.9，算新增文件） |
| `example.parrotcode.yaml` | 新增 `skills:` 配置节示例 |
| `Security/SecurityGuard.cs` | 系统工具豁免（若现有机制不足，加 `IsSystemTool` 判断；见 3.5） |

### 2.3 不变文件

- `Agent/AgentLoop.cs`——零改动（SOP 走 ToolResult → history，复用现有路径）
- `Tools/ToolBase.cs` / `ToolRegistry.cs` / `ToolExecutor.cs`——复用
- `Instructions/InstructionLoader.cs`——Skill 是同构新模块，不改指令加载器

---

## 三、详细设计

### 3.1 Skill 文件格式

```markdown
---
name: commit
description: 按 Conventional Commits 规范提交代码。当用户要求提交/commit 时调用。
tools_allow:
  - read_file
  - write_file
  - run_command
  - grep
  - glob
tools_deny:
  - skill_loader
---

# Commit SOP

执行 git 提交时遵循以下步骤：

1. 调用 run_command 执行 `git status` 查看变更
2. 用 read_file 查看修改过的关键文件，理解变更意图
3. 按 Conventional Commits 规范生成 message：
   - feat: 新功能
   - fix: 修复 bug
   - docs: 文档
   - refactor: 重构
4. 执行 `git add -A` 然后 `git commit -m "<message>"`
5. 不要执行 push，除非用户明确要求
```

**格式约定**：
- frontmatter 必须在文件首，以 `---` 包围
- `name` 必填，与文件名建议一致（如 `commit.md` → `name: commit`）
- `description` 必填，写给 LLM 看，说明"何时调用此 Skill"
- `tools_allow` / `tools_deny` 可选，工具名列表
- 正文（`---` 之后）即 SOP，纯 Markdown，注入给 LLM

### 3.2 数据模型（`Skills/Models.cs`）

```csharp
using YamlDotNet.Serialization;

namespace ParrotCode;

/// <summary>
/// Skill 元数据（对应 frontmatter）。
/// 用 YamlDotNet 反序列化，字段名 yaml snake_case 自动映射（参考 ConfigLoader）。
/// </summary>
public sealed class SkillMeta
{
    [YamlMember(Alias = "name")] public string Name { get; set; } = string.Empty;
    [YamlMember(Alias = "description")] public string Description { get; set; } = string.Empty;
    [YamlMember(Alias = "tools_allow")] public List<string> ToolsAllow { get; set; } = new();
    [YamlMember(Alias = "tools_deny")] public List<string> ToolsDeny { get; set; } = new();
}

/// <summary>
/// 完整 Skill 定义：元数据 + SOP 正文 + 来源路径。
/// </summary>
public sealed record SkillDefinition
{
    /// <summary>元数据（frontmatter 解析结果）。</summary>
    public required SkillMeta Meta { get; init; }

    /// <summary>SOP 正文（frontmatter 之后的 Markdown）。</summary>
    public string Body { get; init; } = string.Empty;

    /// <summary>来源文件绝对路径（调试/日志用）。</summary>
    public string SourcePath { get; init; } = string.Empty;

    /// <summary>来源层级（用于覆盖优先级判定与 /status 显示）。</summary>
    public SkillSource Source { get; init; }
}

public enum SkillSource { Builtin, Global, Project }

/// <summary>
/// 激活/停用操作的结果。
/// </summary>
public sealed record SkillActivateResult
{
    public bool Success { get; init; }
    public string? SkillName { get; init; }
    public string? SopContent { get; init; }   // 激活成功时返回完整 SOP（注入 history）
    public string? Error { get; init; }         // 失败原因
}
```

### 3.3 SkillLoader（`Skills/Loader.cs`）

复用 `InstructionLoader` 的三级扫描骨架，区别：
1. 扫描目录下的 `*.md`（非单个固定文件）
2. 解析 frontmatter（YamlDotNet）+ 提取正文
3. 同名覆盖（项目级覆盖全局级，全局级覆盖内置）

```csharp
using System.IO;
using System.Text.RegularExpressions;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;
using Microsoft.Extensions.Logging;

namespace ParrotCode;

/// <summary>
/// Skill 加载器：三级目录扫描 + YAML frontmatter 解析。
/// 加载顺序（后者覆盖前者同名）：
/// 1. 内置 Skills/Builtin/*.md
/// 2. 全局 ~/.parrotcode/skills/*.md
/// 3. 项目 ./.parrotcode/skills/*.md
/// </summary>
public sealed class SkillLoader
{
    private readonly string _projectRoot;
    private readonly string _userHome;
    private readonly string _builtinDir;
    private readonly ILogger? _logger;

    public SkillLoader(string? projectRoot = null,
                       string? userHome = null,
                       string? builtinDir = null,
                       ILogger? logger = null)
    {
        _projectRoot = projectRoot ?? Directory.GetCurrentDirectory();
        _userHome = userHome ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        _builtinDir = builtinDir ?? Path.Combine(AppContext.BaseDirectory, "Skills", "Builtin");
        _logger = logger;
    }

    /// <summary>
    /// 加载所有 Skill。同名按 项目 > 全局 > 内置 覆盖。
    /// </summary>
    public IReadOnlyDictionary<string, SkillDefinition> Load()
    {
        var byName = new Dictionary<string, SkillDefinition>(StringComparer.Ordinal);

        // 1. 内置（兜底）
        ScanDirectory(_builtinDir, SkillSource.Builtin, byName);
        // 2. 全局
        ScanDirectory(Path.Combine(_userHome, ".parrotcode", "skills"), SkillSource.Global, byName);
        // 3. 项目
        ScanDirectory(Path.Combine(_projectRoot, ".parrotcode", "skills"), SkillSource.Project, byName);

        return byName;
    }

    private void ScanDirectory(string dir, SkillSource source, Dictionary<string, SkillDefinition> byName)
    {
        if (!Directory.Exists(dir)) return;
        foreach (var file in Directory.GetFiles(dir, "*.md"))
        {
            try
            {
                var def = ParseFile(file, source);
                if (def is not null)
                    byName[def.Meta.Name] = def;  // 后者覆盖前者
            }
            catch (Exception ex)
            {
                _logger?.LogWarning("Skill 文件解析失败 {File}：{Error}", file, ex.Message);
            }
        }
    }

    /// <summary>
    /// 解析单个 Skill 文件：分离 frontmatter + 正文。
    /// frontmatter 不存在或格式错误返回 null（跳过）。
    /// </summary>
    private SkillDefinition? ParseFile(string path, SkillSource source)
    {
        var raw = File.ReadAllText(path);
        var match = FrontmatterRegex.Match(raw);
        if (!match.Success)
        {
            _logger?.LogWarning("Skill 文件缺少 frontmatter：{File}", path);
            return null;
        }

        var yaml = match.Groups[1].Value;
        var body = match.Groups[2].Value.TrimStart('\r', '\n');

        var deserializer = new DeserializerBuilder()
            .WithNamingConvention(UnderscoredNamingConvention.Instance)
            .Build();
        var meta = deserializer.Deserialize<SkillMeta>(yaml);

        if (string.IsNullOrWhiteSpace(meta.Name))
        {
            _logger?.LogWarning("Skill 文件缺少 name：{File}", path);
            return null;
        }

        return new SkillDefinition
        {
            Meta = meta,
            Body = body,
            SourcePath = path,
            Source = source
        };
    }

    private static readonly Regex FrontmatterRegex =
        new(@"^---\s*\r?\n(.*?)\r?\n---\s*\r?\n(.*)$",
            RegexOptions.Singleline | RegexOptions.Compiled);
}
```

### 3.4 SkillRegistry（`Skills/Registry.cs`）

```csharp
using System.Text;
using Microsoft.Extensions.Logging;

namespace ParrotCode;

/// <summary>
/// Skill 注册表：管理已加载 Skill + 激活状态 + Phase 1 摘要生成。
/// 线程安全：激活/停用加锁（AgentLoop 单线程消费，但 /commit 命令也访问）。
/// </summary>
public sealed class SkillRegistry
{
    private readonly Dictionary<string, SkillDefinition> _skills;
    private readonly HashSet<string> _active = new(StringComparer.Ordinal);
    private readonly int _maxActive;
    private readonly object _lock = new();
    private readonly ILogger? _logger;

    public SkillRegistry(IReadOnlyDictionary<string, SkillDefinition> skills,
                         int maxActive = 3,
                         ILogger? logger = null)
    {
        _skills = new Dictionary<string, SkillDefinition>(skills, StringComparer.Ordinal);
        _maxActive = maxActive;
        _logger = logger;
    }

    /// <summary>是否启用（空 Registry 也算启用但无 Skill）。</summary>
    public bool HasSkills => _skills.Count > 0;

    /// <summary>获取 Skill（不存在返回 null）。</summary>
    public SkillDefinition? Get(string name)
    {
        _skills.TryGetValue(name, out var def);
        return def;
    }

    /// <summary>激活 Skill。返回 SkillActivateResult（含 SOP 内容用于注入 history）。</summary>
    public SkillActivateResult Activate(string name)
    {
        lock (_lock)
        {
            if (!_skills.TryGetValue(name, out var def))
                return new SkillActivateResult { Success = false, Error = $"未找到 Skill：{name}" };

            if (_active.Contains(name))
            {
                // 已激活，直接返回 SOP（幂等）
                return new SkillActivateResult
                {
                    Success = true,
                    SkillName = name,
                    SopContent = BuildSop(def)
                };
            }

            if (_active.Count >= _maxActive)
                return new SkillActivateResult
                {
                    Success = false,
                    Error = $"已激活 {_active.Count} 个 Skill，达到上限 {_maxActive}，请先停用"
                };

            _active.Add(name);
            _logger?.LogInformation("激活 Skill：{Name}（来源 {Source}）", name, def.Source);
            return new SkillActivateResult
            {
                Success = true,
                SkillName = name,
                SopContent = BuildSop(def)
            };
        }
    }

    /// <summary>停用 Skill。</summary>
    public bool Deactivate(string name)
    {
        lock (_lock) { return _active.Remove(name); }
    }

    public bool IsActive(string name) { lock (_lock) { return _active.Contains(name); } }

    /// <summary>当前激活的 Skill 列表快照。</summary>
    public IReadOnlyList<SkillDefinition> GetActiveSkills()
    {
        lock (_lock)
        {
            return _active.Select(n => _skills[n]).ToList();
        }
    }

    /// <summary>
    /// Phase 1 摘要：注入 system prompt，让 LLM 知道有哪些 Skill 可调。
    /// 格式：
    ///   ## 可用 Skills
    ///   - commit: 按 Conventional Commits 规范提交代码
    ///   - review: 代码审查 SOP
    ///   调用 skill_loader(name) 加载完整 SOP。
    /// </summary>
    public string GetSummary()
    {
        if (_skills.Count == 0) return string.Empty;
        var sb = new StringBuilder();
        sb.AppendLine("## 可用 Skills");
        sb.AppendLine("（调用 `skill_loader` 工具加载完整 SOP 后按其指引工作）");
        foreach (var def in _skills.Values.OrderBy(d => d.Meta.Name))
        {
            sb.AppendLine($"- {def.Meta.Name}: {def.Meta.Description}");
        }
        return sb.ToString();
    }

    /// <summary>构建注入 history 的 SOP 文本（含 frontmatter 元信息提示）。</summary>
    private static string BuildSop(SkillDefinition def)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# Skill: {def.Meta.Name}");
        sb.AppendLine();
        sb.AppendLine(def.Body);
        if (def.Meta.ToolsAllow.Count > 0)
            sb.AppendLine().AppendLine($"可用工具：{string.Join(", ", def.Meta.ToolsAllow)}");
        if (def.Meta.ToolsDeny.Count > 0)
            sb.AppendLine().AppendLine($"禁用工具：{string.Join(", ", def.Meta.ToolsDeny)}");
        return sb.ToString();
    }
}
```

### 3.5 SkillTool（`Skills/SkillTool.cs`）— `skill_loader` 工具

```csharp
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace ParrotCode;

/// <summary>
/// skill_loader 工具：LLM 调用此工具按需加载 Skill SOP（Phase 2）。
/// 激活成功后 SOP 作为 ToolResult.Content 返回，进入 history，后续轮可见。
/// 系统工具：豁免 SecurityGuard 拦截（见 3.8）。
/// </summary>
public sealed class SkillTool : ToolBase
{
    public override string Name => "skill_loader";
    public override string Description =>
        "加载并激活指定 Skill 的标准作业流程(SOP)。调用后 SOP 内容会注入对话,后续轮次 Agent 按此 SOP 工作。";
    public override ToolCategory Category => ToolCategory.Read;  // 幂等、无副作用、可并发
    public override IReadOnlyList<ToolParameter> Parameters { get; } = new[]
    {
        new ToolParameter("name", "string", "要加载的 Skill 名称（如 commit / review）", required: true)
    };

    private readonly SkillRegistry _registry;

    public SkillTool(SkillRegistry registry) { _registry = registry; }

    public override Task<ToolResult> ExecuteAsync(JsonElement input, CancellationToken ct)
    {
        var name = GetRequiredString(input, "name", out var error);
        if (error is not null)
            return Task.FromResult(ToolResult.Fail(error));

        var result = _registry.Activate(name);
        if (!result.Success)
            return Task.FromResult(ToolResult.Fail(result.Error ?? "激活失败"));

        // SOP 作为工具结果返回，AgentLoop 会把它入 history（后续轮 LLM 可见）
        return Task.FromResult(ToolResult.Ok(result.SopContent ?? string.Empty));
    }
}
```

**系统工具豁免**：`SecurityGuard.CheckAsync` 增加 `skill_loader` 到系统工具白名单（与 MCP 内部工具同等处理）。若现有 `SecurityGuard` 无系统工具豁免机制，则在 `SecurityGuard` 加：

```csharp
private static readonly HashSet<string> SystemTools = new(StringComparer.Ordinal)
{
    "skill_loader"
};

// CheckAsync 开头：
if (SystemTools.Contains(toolName)) return ToolResult.Ok();  // 系统工具放行
```

### 3.6 SkillExecutor（`Skills/Executor.cs`）— 激活 + 工具白名单交集

```csharp
namespace ParrotCode;

/// <summary>
/// Skill 执行器：激活/停用 + 计算激活 Skill 的工具白名单交集。
/// /commit 命令通过此执行器激活 Skill。
/// 注意：工具白名单的"实际拦截"在 SecurityGuard/BatchToolExecutor 层（迭代 14 Hook 引擎再统一接）；
/// 本迭代 Executor 仅计算交集并暴露给查询方，不强制拦截。
/// </summary>
public sealed class SkillExecutor
{
    private readonly SkillRegistry _registry;

    public SkillExecutor(SkillRegistry registry) { _registry = registry; }

    public SkillActivateResult Activate(string name) => _registry.Activate(name);
    public bool Deactivate(string name) => _registry.Deactivate(name);
    public IReadOnlyList<SkillDefinition> GetActive() => _registry.GetActiveSkills();

    /// <summary>
    /// 计算当前激活 Skill 的工具白名单交集。
    /// 规则：
    ///   - tools_deny 并集（任一 Skill 禁用则禁用）
    ///   - tools_allow 交集（仅当所有激活 Skill 都声明了 tools_allow 时取交集；
    ///     任一未声明 tools_allow 表示不限制，则整体不限制）
    /// </summary>
    public (IReadOnlyList<string> Allowed, IReadOnlyList<string> Denied) GetEffectiveToolFilter()
    {
        var active = _registry.GetActiveSkills();
        if (active.Count == 0)
            return (Array.Empty<string>(), Array.Empty<string>());

        var denied = active.SelectMany(d => d.Meta.ToolsDeny).Distinct().ToList();

        // 仅当所有激活 Skill 都声明了 tools_allow 时取交集
        if (active.All(d => d.Meta.ToolsAllow.Count > 0))
        {
            var allowed = active
                .Select(d => d.Meta.ToolsAllow.ToHashSet())
                .Aggregate((a, b) => { a.IntersectWith(b); return a; })
                .ToList();
            return (allowed, denied);
        }
        return (Array.Empty<string>(), denied);  // 有 Skill 不限制 → 整体不限制
    }
}
```

> **设计说明**：工具白名单的"实际拦截"留待迭代 14 Hook 引擎统一接入（`tool_pre_exec` 事件）。本迭代 `SkillExecutor` 只计算交集并暴露，不强制拦截，避免与 `SecurityGuard` 职责重叠。这样 Skill 系统独立可验收，Hook 引擎接入时再串起来。

### 3.7 SkillConfig（`Config/Models.cs` 新增）

```csharp
/// <summary>
/// Skill 系统配置（迭代 12 新增）。null 时用默认值。
/// </summary>
public sealed record SkillConfig
{
    /// <summary>是否启用 Skill 系统。默认 true。false 时 skill_loader 不注册，/commit 返回"未启用"。</summary>
    public bool? Enable { get; init; }

    /// <summary>同时激活的 Skill 上限。默认 3。防止 LLM 激活过多 Skill 污染上下文。</summary>
    public int? MaxActiveSkills { get; init; }
}
```

`AppConfig` 新增字段：

```csharp
/// <summary>Skill 系统配置（迭代 12 新增）。null 时用默认值。</summary>
public SkillConfig? Skills { get; init; }
```

`example.parrotcode.yaml` 新增：

```yaml
# 迭代 12 新增：Skill 系统（可编程 SOP）
skills:
  enable: true                     # 是否启用 Skill 系统（false 时 skill_loader 不注册）
  max_active_skills: 3            # 同时激活的 Skill 上限
```

### 3.8 App.cs 端到端装配

参考 `InstructionLoader` / `SessionStore` 的条件注入模式：

```csharp
// 【迭代 12】构造 Skill 系统
var skillConfig = _config.Skills ?? new SkillConfig();
SkillRegistry? skillRegistry = null;
SkillExecutor? skillExecutor = null;
if (skillConfig.Enable ?? true)
{
    var skillLoader = new SkillLoader(projectRoot: projectRoot, logger: _logger);
    var skills = skillLoader.Load();
    skillRegistry = new SkillRegistry(skills,
                                      maxActive: skillConfig.MaxActiveSkills ?? 3,
                                      logger: _logger);
    skillExecutor = new SkillExecutor(skillRegistry);

    // 注册 skill_loader 工具到主 ToolRegistry（在 TerminalApp 中统一注册，
    // 与 MCP 工具一同加入；此处仅准备好 SkillTool 实例由 TerminalApp 取用）
    _logger?.LogInformation("已加载 {Count} 个 Skill", skills.Count);
}
```

`TerminalApp` 构造函数新增 `SkillRegistry?` 和 `SkillExecutor?` 参数；在注册工具阶段：

```csharp
// TerminalApp.RunAsync 中注册工具时
if (_skillRegistry is not null)
{
    _toolRegistry.Register(new SkillTool(_skillRegistry));
}
```

### 3.9 system prompt 注入（Phase 1）

`TerminalApp` 构造 system prompt 时，在项目指令之后追加 Skill 摘要：

```csharp
// TerminalApp 构造时拼接（参考 10c 的 _systemPromptWithInstructions）
private string BuildSystemPrompt()
{
    var sb = new StringBuilder(_agentConfig.SystemPrompt ?? DefaultSystemPrompt);
    if (_instructions.HasInstructions)
        sb.AppendLine().AppendLine("## 项目指令").Append(_instructions.Content);
    if (_skillRegistry is not null)
    {
        var summary = _skillRegistry.GetSummary();
        if (!string.IsNullOrEmpty(summary))
            sb.AppendLine().Append(summary);
    }
    return sb.ToString();
}
```

每轮 `BuildMessagesWithSystem` 用此 prompt（已含 Skill 摘要），LLM 看到 `skill_loader` 工具 + 摘要 → 自主决定调用。

### 3.10 `/commit` 命令（`Commands/Builtin/CommitCommand.cs`）

```csharp
using System.Threading.Tasks;

namespace ParrotCode;

/// <summary>
/// /commit 命令：激活 commit Skill + 注入 SOP + 触发 Agent round。
/// 若 Skill 系统未启用或 commit Skill 不存在，返回错误提示。
/// </summary>
public sealed class CommitCommand : ICommand
{
    public string Name => "commit";
    public string Description => "激活 commit Skill，按 Conventional Commits 流程提交";
    public CommandType Type => CommandType.System;
    public IReadOnlyList<string> Aliases => Array.Empty<string>();
    public string Usage => "/commit";

    public async Task<CommandResult> ExecuteAsync(CommandContext context)
    {
        if (context.SkillExecutor is null)
            return CommandResult.Output("Skill 系统未启用（skills.enable: false）");

        var result = context.SkillExecutor.Activate("commit");
        if (!result.Success)
            return CommandResult.Output(result.Error ?? "commit Skill 激活失败");

        // 把 SOP 作为 user 消息注入 history，触发 Agent round
        var prompt = $"请按以下流程执行提交：\n\n{result.SopContent}";
        context.History.AddUser(prompt);
        await context.Ui.TriggerAgentRoundAsync(prompt);  // 复用现有触发机制（见下）
        return CommandResult.Output("已激活 commit Skill，开始提交流程...");
    }
}
```

> **触发机制说明**：`IUiControl.TriggerAgentRoundAsync` 的具体形态需对齐现有命令如何启动 Agent（参考 `/mode` 等命令的实现）。若现有命令通过往 `History` 加 user 消息后由 `TerminalApp` 主循环自然触发，则 `/commit` 同样只需 `History.AddUser` 即可，`TriggerAgentRoundAsync` 调用可省略。实现时以现有命令模式为准。

### 3.11 CommandContext 扩展

```csharp
public sealed record CommandContext(
    ConversationHistory History,
    ContextCompressor? Compressor,
    SecurityGuard SecurityGuard,
    IUiControl Ui,
    SessionStore? SessionStore,
    SkillExecutor? SkillExecutor,   // 迭代 12 新增
    CancellationToken Ct)
{
    // ... 既有扩展属性不变
}
```

---

## 四、两阶段加载时序

```
启动期：
  App.RunAsync
    └─ SkillLoader.Load() → 三级扫描 → Dictionary<name, SkillDefinition>
    └─ SkillRegistry(skills)
    └─ SkillExecutor(registry)
    └─ TerminalApp(... skillRegistry, skillExecutor ...)
         └─ BuildSystemPrompt()
              ├─ 默认 prompt
              ├─ + 项目指令（10c）
              └─ + SkillRegistry.GetSummary()   ← Phase 1 摘要注入
         └─ ToolRegistry.Register(SkillTool)    ← skill_loader 工具注册

运行期（用户对话）：
  用户输入 → AgentLoop.RunAsync
    └─ 每轮 BuildMessagesWithSystem
         └─ system prompt 含 Skill 摘要（Phase 1）
    └─ LLM 决定调用 skill_loader(name="commit")
         └─ SkillTool.ExecuteAsync
              └─ SkillRegistry.Activate("commit")
                   └─ 返回 SkillActivateResult{ SopContent = "# Skill: commit\n..." }
              └─ ToolResult.Ok(sopContent)
         └─ AgentLoop 把 ToolResult 入 history            ← Phase 2 SOP 进入 history
    └─ 后续轮 BuildMessagesWithSystem
         └─ system prompt + history(含 SOP) → LLM 按 SOP 工作且后续每轮可见
```

**关键不变量**：`AgentLoop` 全程零改动。SOP 作为 `ToolResult.Content` 走迭代 6 既有的"工具结果入 history"路径，迭代 9 的截断/压缩对它一视同仁（SOP 通常 < 2KB，远低于截断阈值）。

---

## 五、验收标准

### 5.1 功能验收

| # | 验收项 | 验证方法 | 通过标准 |
|---|--------|---------|---------|
| 1 | `/commit` 触发 commit Skill | 端到端：输入 `/commit` | Agent 按 Conventional Commits 流程工作（读 status、生成规范 message、git add+commit、不 push） |
| 2 | Phase 1 摘要注入 system prompt | 单测：`SkillRegistry.GetSummary()` 输出含所有 Skill name+description | LLM 调用 `skill_loader` 前 system prompt 已含可调 Skill 列表 |
| 3 | Phase 2 SOP 进入 history | 单测：`SkillTool.ExecuteAsync` 返回 `ToolResult.Ok(sop)`，`Success=true`，`Content` 含 SOP 正文 | skill_loader 调用后 history 含 SOP，后续轮可见 |
| 4 | 三级目录扫描 + 同名覆盖 | 单测：内置 `commit.md` + 项目 `.parrotcode/skills/commit.md` 同时存在 | 项目级覆盖内置级，`SkillRegistry.Get("commit").Source == Project` |
| 5 | frontmatter 解析 | 单测：合法 frontmatter + 正文 | `Meta.Name/Description/ToolsAllow/ToolsDeny` 正确，`Body` 为正文 |
| 6 | 格式错误优雅降级 | 单测：缺 frontmatter / name 为空 / YAML 语法错 | 跳过该文件，日志警告，不崩溃，其他 Skill 正常加载 |
| 7 | `skills.enable: false` 旁路 | 端到端：配置 false | `skill_loader` 未注册，`/commit` 返回"Skill 系统未启用" |
| 8 | `skill_loader` 豁免拦截 | 单测：`SecurityGuard.CheckAsync("skill_loader")` 放行 | 不被 SecurityGuard 拦截 |
| 9 | 多 Skill 激活上限 | 单测：激活第 4 个（默认上限 3） | 返回失败 + 错误提示 |
| 10 | 工具白名单交集 | 单测：激活 2 个 Skill，各声明 `tools_allow` | `GetEffectiveToolFilter().Allowed` 为交集 |
| 11 | `tools_deny` 并集 | 单测：2 个 Skill 各 deny 不同工具 | `Denied` 为并集 |
| 12 | 已激活 Skill 重复激活幂等 | 单测：激活后再次 `Activate` | 返回 `Success=true` + SOP，不重复计数 |

### 5.2 工程验收

- `dotnet build` 0 error 0 warning
- 全部既有测试通过
- 新增单测覆盖 Loader / Registry / SkillTool / Executor（见六）
- 跨平台路径：`SkillLoader` 用 `Path.Combine`，`~` 用 `Environment.SpecialFolder.UserProfile`（参考 `InstructionLoader`）

### 5.3 代码质量

- `AgentLoop.cs` 零改动（git diff 验证）
- `ToolBase` / `ToolRegistry` 不改契约（仅 `SkillTool` 继承）
- `nullable` 引用类型开启，无 warning
- `async` 全链路，无 `.Result` / `.Wait()`
- `CancellationToken` 贯穿

---

## 六、测试清单

### 6.1 SkillLoaderTests（Loader）
- 三级目录扫描：仅内置 / 仅全局 / 仅项目 / 三者并存
- 同名覆盖优先级：项目 > 全局 > 内置
- frontmatter 解析：合法 / 缺 frontmatter / name 空 / YAML 语法错 / 正文为空
- 目录不存在时不崩溃
- `tools_allow` / `tools_deny` 列表解析

### 6.2 SkillRegistryTests（Registry + 激活状态）
- `GetSummary` 输出格式（含所有 Skill name+description）
- `Activate` 成功返回 SOP
- `Activate` 不存在 Skill 返回失败
- `Activate` 超过 `MaxActiveSkills` 返回失败
- 重复 `Activate` 幂等
- `Deactivate` 后可重新激活
- `GetActiveSkills` 快照正确

### 6.3 SkillToolTests（skill_loader 工具）
- `ExecuteAsync` 合法 name 返回 `ToolResult.Ok(sop)`
- 缺 name 参数返回 `ToolResult.Fail`
- 不存在 name 返回 `ToolResult.Fail`
- schema 生成（`ToOpenAiSchema` / `ToAnthropicSchema`）含 name/description/parameters
- `Category == Read`

### 6.4 SkillExecutorTests（工具白名单交集）
- 无激活 Skill：`Allowed` / `Denied` 均空
- 单 Skill 激活：`Allowed` = 该 Skill 的 `tools_allow`
- 两 Skill 都声明 `tools_allow`：交集
- 一 Skill 声明 `tools_allow`、一 Skill 不声明：不限制（`Allowed` 空）
- `tools_deny` 并集

### 6.5 SkillConfigTests
- 默认值：`Enable ?? true` / `MaxActiveSkills ?? 3`
- YAML 加载：`skills.enable: false` 正确反序列化

### 6.6 端到端
- `/commit` 命令激活 commit Skill，history 含 SOP
- `skills.enable: false` 时 `/commit` 返回提示
- system prompt 含 Skill 摘要（Phase 1 验证）

---

## 七、风险与对策

| 风险 | 对策 |
|------|------|
| LLM 不主动调 `skill_loader`，Phase 2 不触发 | Phase 1 摘要里明确写"调用 skill_loader 加载 SOP"；`/commit` 命令作为强触发点绕过 LLM 自主决策 |
| SOP 被 `ContextCompressor` 压缩掉 | SOP 通常 < 2KB，远低于 `per_result_threshold`(50KB)；且作为 tool result 在 history 中，与普通工具结果同等对待。若需"永不被压缩"，迭代 14 Hook 引擎可标记保护（非本迭代范围） |
| `skill_loader` 被 `SecurityGuard` 误拦 | 系统工具白名单豁免（3.5） |
| 多 Skill 工具白名单交集过严导致 Agent 无法工作 | `tools_allow` 是可选的，不声明即不限制；交集仅在所有激活 Skill 都声明时生效 |
| Skill 文件恶意内容（prompt injection） | Skill 是受信任内容（类似 `.cursorrules` / `PARROTCODE.md`），与项目指令同等信任级别，不做额外防护 |
| `SkillLoader` 的 `Builtin` 目录路径在发布后变化 | 用 `AppContext.BaseDirectory` 定位（见 3.3），与可执行文件同目录 |
| `/commit` 触发 Agent round 的机制与现有命令不一致 | 实现时对齐现有命令（`/mode` 等）的触发方式，必要时调整 `CommitCommand` |

---

## 八、与后续迭代的衔接

- **迭代 13（子 Agent）**：Skill 的 `tools_allow` 可包含 `sub_agent`，让 Skill 委派子任务。本迭代不实现 `sub_agent` 工具，但 `tools_allow` 列表已支持该名字。
- **迭代 14（Hook 引擎）**：`SkillExecutor.GetEffectiveToolFilter` 的输出在迭代 14 接入 `tool_pre_exec` Hook，实现真正的工具白名单拦截。本迭代只计算不拦截，保持模块独立。

---

## 九、交付检查清单

- [ ] `Skills/` 目录下 5 个 .cs 文件 + 3 个 Builtin .md
- [ ] `Models.cs` 新增 `SkillConfig` + `AppConfig.Skills`
- [ ] `App.cs` 条件装配 `SkillLoader` → `SkillRegistry` → `SkillExecutor`
- [ ] `TerminalApp` 构造函数加 `SkillRegistry?` / `SkillExecutor?`，system prompt 拼摘要，注册 `skill_loader`
- [ ] `CommandContext` 加 `SkillExecutor?`
- [ ] `CommitCommand` 实现
- [ ] `SecurityGuard` 系统工具豁免 `skill_loader`
- [ ] `example.parrotcode.yaml` 加 `skills:` 节
- [ ] `AgentLoop.cs` git diff 为空（零改动验证）
- [ ] 单测：Loader / Registry / SkillTool / Executor / Config 全覆盖
- [ ] `dotnet build` 0 error 0 warning
- [ ] 全部既有测试通过
- [ ] 端到端：`/commit` 跑通 Conventional Commits 流程
