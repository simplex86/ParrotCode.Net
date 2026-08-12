# 迭代 14a：角色系统与三层工具过滤

> **状态**：[设计完成，待实现]
> **前置迭代**：12 [已完成]（Skill 系统）、13a [已完成]（Skill 目录化）、13b [已完成]（/skill 管理命令）
> **后续迭代**：14b（SubAgentRunner + sub_agent 工具 + 装配）
> **总览文档**：[iter-14-design.md](./iter-14-design.md)
> **关联文档**：[iter-14b-design.md](./iter-14b-design.md)

---

## 一、子迭代目标

### 1.1 核心目标

交付子 Agent 的角色加载层和工具过滤层——这是子 Agent 系统的"配置基座"，不涉及任何运行时执行。

1. **角色文件格式**：与 Skill 同构的 YAML frontmatter + Markdown 正文
2. **`RoleLoader` 三级扫描**：内置 → 全局 → 项目（后者覆盖前者同名），与 `SkillLoader` 同构
3. **`RoleRegistry`**：角色注册表，按名查找（无激活状态——角色是定义，不是运行时状态）
4. **`ToolFilter` 三层过滤**：从父 `ToolRegistry` 构建子 Agent 的过滤 `ToolRegistry`
   - 第 1 层（全局）：始终排除 `sub_agent`——禁止子 Agent 嵌套
   - 第 2 层（角色）：`tools_allow` / `tools_deny`
   - 第 3 层（模式）：Fork 模式额外排除 `skill_loader`
5. **3 个内置角色**：`explorer`（只读探索）/ `planner`（只读规划）/ `general`（通用）

### 1.2 非目标（14a 明确不做）

- ❌ 不做 `SubAgentRunner`（14b）
- ❌ 不做 `sub_agent` 工具（14b）
- ❌ 不做 `AgentLoop` 嵌套调用（14b）
- ❌ 不做 Config / 装配 / SecurityGuard 改动（14b）
- ❌ 不做端到端运行（14a 无运行时入口，仅单测验证）

### 1.3 与既有系统的衔接

- **复用 Skill 的 frontmatter 格式**：相同 YAML 字段（`name` / `description` / `tools_allow` / `tools_deny`）+ Markdown 正文
- **复用 Skill 的三级扫描模式**：`RoleLoader` 是 `SkillLoader` 的简化版（单文件、无目录化、无资源）
- **复用 `ToolRegistry`**：`ToolFilter.Build` 从父 `ToolRegistry` 构建过滤副本
- **零改动**：`ToolRegistry` / `ToolBase` / `Skills/` / `AgentLoop` / `AgentEvent`

---

## 二、文件改动清单

### 2.1 新增文件（5 个）

```
SubAgent/
├── Models.cs                  # RoleDefinition / RoleMeta / RoleSource（角色部分）
├── Filter.cs                  # ToolFilter（静态类，三层过滤构建 filtered ToolRegistry）
└── Roles/
    ├── RoleLoader.cs          # RoleLoader + RoleRegistry（三级扫描 + frontmatter 解析）
    └── Builtin/
        ├── explorer.md         # 探索角色 SOP
        ├── planner.md          # 规划角色 SOP
        └── general.md          # 通用角色 SOP
```

### 2.2 修改文件

无（14a 零修改既有文件）。

### 2.3 不变文件

- `Agent/AgentLoop.cs`——零改动
- `Tools/ToolRegistry.cs` / `ToolBase.cs`——零改动（复用）
- `Skills/`——零改动（角色系统独立）
- `ParrotCode.Net.csproj`——零改动（`**\*` glob 已递归，Builtin 角色文件自动包含）

---

## 三、详细设计

### 3.1 数据模型（`SubAgent/Models.cs` 角色部分）

```csharp
namespace ParrotCode;

/// <summary>
/// 角色元数据（对应角色文件 frontmatter）。与 SkillMeta 同构。
/// </summary>
public sealed class RoleMeta
{
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public List<string> ToolsAllow { get; set; } = new();
    public List<string> ToolsDeny { get; set; } = new();
}

/// <summary>
/// 完整角色定义：元数据 + SOP 正文 + 来源路径。
/// </summary>
public sealed record RoleDefinition
{
    public required RoleMeta Meta { get; init; }
    public string Body { get; init; } = string.Empty;
    public string SourcePath { get; init; } = string.Empty;
    public RoleSource Source { get; init; }
}

public enum RoleSource { Builtin, Global, Project }
```

> **说明**：14a 只定义角色相关的类型。`SubAgentRequest` / `SubAgentResult` / `SubAgentMode` 等 SubAgent 运行时类型在 14b 的 `Models.cs` 中追加（同文件不同区域）。

### 3.2 角色文件格式

角色文件用与 Skill 相同的 YAML frontmatter + Markdown 正文格式：

```markdown
---
name: explorer
description: 探索项目结构，理解代码组织，报告关键发现。
tools_allow:
  - read_file
  - glob
  - grep
  - run_command
tools_deny:
  - write_file
  - edit_file
  - sub_agent
  - skill_loader
---

# Explorer 角色

你是项目探索专家。你的任务是快速理解项目结构和代码组织。
...
```

**格式约定**（与 Skill 一致）：
- frontmatter 必须在文件首，以 `---` 包围
- `name` 必填，与文件名建议一致（如 `explorer.md` → `name: explorer`）
- `description` 必填，写给主 Agent 看，说明"何时用此角色"
- `tools_allow` / `tools_deny` 可选，工具名列表
- 正文即角色 SOP，注入为子 Agent 的 system prompt（Definitional 模式）

### 3.3 RoleLoader + RoleRegistry（`SubAgent/Roles/RoleLoader.cs`）

`RoleLoader` 是 `SkillLoader` 的简化版——单文件格式、三级扫描、同名覆盖，无目录化、无资源。

```csharp
using System.IO;
using System.Text.RegularExpressions;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;
using Microsoft.Extensions.Logging;

namespace ParrotCode;

/// <summary>
/// 角色加载器：三级目录扫描 + YAML frontmatter 解析。
/// 加载顺序（后者覆盖前者同名）：
/// 1. 内置 SubAgent/Roles/Builtin/*.md
/// 2. 全局 ~/.parrotcode/roles/*.md
/// 3. 项目 ./.parrotcode/roles/*.md
/// </summary>
public sealed class RoleLoader
{
    private readonly string _projectRoot;
    private readonly string _userHome;
    private readonly string _builtinDir;
    private readonly ILogger? _logger;

    public RoleLoader(string? projectRoot = null,
                      string? userHome = null,
                      string? builtinDir = null,
                      ILogger? logger = null)
    {
        _projectRoot = projectRoot ?? Directory.GetCurrentDirectory();
        _userHome = userHome ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        _builtinDir = builtinDir ?? Path.Combine(AppContext.BaseDirectory, "SubAgent", "Roles", "Builtin");
        _logger = logger;
    }

    public IReadOnlyDictionary<string, RoleDefinition> Load()
    {
        var byName = new Dictionary<string, RoleDefinition>(StringComparer.Ordinal);

        // 1. 内置（兜底）
        ScanDirectory(_builtinDir, RoleSource.Builtin, byName);
        // 2. 全局
        ScanDirectory(Path.Combine(_userHome, ".parrotcode", "roles"), RoleSource.Global, byName);
        // 3. 项目
        ScanDirectory(Path.Combine(_projectRoot, ".parrotcode", "roles"), RoleSource.Project, byName);

        return byName;
    }

    private void ScanDirectory(string dir, RoleSource source, Dictionary<string, RoleDefinition> byName)
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
                _logger?.LogWarning("角色文件解析失败 {File}：{Error}", file, ex.Message);
            }
        }
    }

    private RoleDefinition? ParseFile(string path, RoleSource source)
    {
        var raw = File.ReadAllText(path);
        var match = FrontmatterRegex.Match(raw);
        if (!match.Success)
        {
            _logger?.LogWarning("角色文件缺少 frontmatter：{File}", path);
            return null;
        }

        var yaml = match.Groups[1].Value;
        var body = match.Groups[2].Value.TrimStart('\r', '\n');

        var deserializer = new DeserializerBuilder()
            .WithNamingConvention(UnderscoredNamingConvention.Instance)
            .Build();
        var meta = deserializer.Deserialize<RoleMeta>(yaml);

        if (string.IsNullOrWhiteSpace(meta.Name))
        {
            _logger?.LogWarning("角色文件缺少 name：{File}", path);
            return null;
        }

        return new RoleDefinition
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

/// <summary>
/// 角色注册表：管理已加载角色。无激活状态（角色是定义，不是运行时状态）。
/// 比 SkillRegistry 简单——无 maxActive / 无 Activate / 无 Deactivate。
/// </summary>
public sealed class RoleRegistry
{
    private readonly Dictionary<string, RoleDefinition> _roles;

    public RoleRegistry(IReadOnlyDictionary<string, RoleDefinition> roles)
    {
        _roles = new Dictionary<string, RoleDefinition>(roles, StringComparer.Ordinal);
    }

    public bool HasRoles => _roles.Count > 0;

    public RoleDefinition? Get(string name)
    {
        _roles.TryGetValue(name, out var def);
        return def;
    }

    public IReadOnlyCollection<RoleDefinition> GetAll() => _roles.Values.ToList().AsReadOnly();
}
```

### 3.4 ToolFilter（`SubAgent/Filter.cs`）

三层工具过滤，从父 `ToolRegistry` 构建过滤副本：

```csharp
using System.Collections.Generic;

namespace ParrotCode;

/// <summary>
/// 工具过滤器：从父 ToolRegistry 构建子 Agent 的过滤 ToolRegistry。
/// 三层过滤（全部在构建时一次性完成）：
///   1. 全局：始终排除 sub_agent（禁止嵌套）
///   2. 角色：role.ToolsAllow（白名单，声明了才取交集）/ role.ToolsDeny（黑名单并集）
///   3. 模式：Fork 模式额外排除 skill_loader（子 Agent 不加载 Skill）
///
/// 注意：14a 只定义 ToolFilter 类，SubAgentMode 枚举在 14b 的 Models.cs 中定义。
/// 14a 单测通过 mock SubAgentMode 值验证过滤逻辑（SubAgentMode 是简单枚举，无依赖）。
/// </summary>
public static class ToolFilter
{
    /// <summary>
    /// 构建过滤后的 ToolRegistry。
    /// </summary>
    public static ToolRegistry Build(ToolRegistry parent, RoleDefinition role, SubAgentMode mode)
    {
        var filtered = new ToolRegistry();

        // 第 1 层 + 第 3 层：全局禁止 + 模式约束
        var deny = new HashSet<string>(StringComparer.Ordinal) { "sub_agent" };
        if (mode == SubAgentMode.Fork)
            deny.Add("skill_loader");

        // 第 2 层：角色 tools_deny 并入 deny
        foreach (var t in role.Meta.ToolsDeny)
            deny.Add(t);

        // 第 2 层：角色 tools_allow（白名单，空表示不限制）
        var allow = role.Meta.ToolsAllow.Count > 0
            ? new HashSet<string>(role.Meta.ToolsAllow, StringComparer.Ordinal)
            : null;

        foreach (var tool in parent.GetAll())
        {
            if (deny.Contains(tool.Name)) continue;
            if (allow is not null && !allow.Contains(tool.Name)) continue;
            filtered.Register(tool);
        }

        return filtered;
    }
}
```

**关键点**：
- `sub_agent` 始终在 deny 列表（第 1 层全局禁嵌套）——即使角色 frontmatter 没声明 `tools_deny: sub_agent`，过滤器也强制排除
- Fork 模式额外排除 `skill_loader`——子 Agent 不加载 Skill，保持聚焦
- 角色 `tools_allow` 为空表示"不限制"（与 Skill 语义一致）；非空时取白名单交集
- 角色 `tools_deny` 与全局 deny 取并集

> **14a 编码注**：`SubAgentMode` 枚举在 14b 才定义。14a 编码时需先在 `Models.cs` 定义 `SubAgentMode` 枚举（仅枚举，不含其他 SubAgent 运行时类型），让 `ToolFilter` 可编译。或者在 14a 就把 `SubAgentMode` 枚举一起定义——它是无依赖的简单枚举。

### 3.5 内置角色文件

#### explorer.md

```markdown
---
name: explorer
description: 探索项目结构，理解代码组织，报告关键发现。只读角色，不修改任何文件。
tools_allow:
  - read_file
  - glob
  - grep
  - run_command
tools_deny:
  - write_file
  - edit_file
  - sub_agent
  - skill_loader
---

# Explorer 角色

你是项目探索专家。你的任务是快速理解项目结构和代码组织，不做任何修改。

## 工作方式

1. 先用 `glob` 或 `run_command`（执行 `ls` / `find` / `dir`）了解顶层目录结构
2. 用 `read_file` 读取关键配置文件（如 .csproj / package.json / README / Makefile 等）
3. 用 `grep` 搜索关键模式（如类名、接口、入口点、依赖声明）
4. 保持聚焦——只探索与任务相关的部分，不要漫无目的地翻阅所有文件

## 报告格式

完成后输出结构化报告（不超过 500 字）：

- **项目类型与技术栈**
- **目录结构概览**（树状，最多 3 层深度）
- **关键文件与职责**（列出最重要的 5-10 个文件）
- **值得注意的模式或约定**（如命名规范、架构分层）
```

#### planner.md

```markdown
---
name: planner
description: 分析需求，制定实施计划，不执行修改。只读角色，专注规划。
tools_allow:
  - read_file
  - glob
  - grep
tools_deny:
  - write_file
  - edit_file
  - run_command
  - sub_agent
  - skill_loader
---

# Planner 角色

你是技术规划专家。你的任务是分析需求并制定分步实施计划，不执行任何修改。

## 工作方式

1. 用 `read_file` / `grep` 理解现有代码结构与架构
2. 分析任务需求与现有代码的关系
3. 识别需要新增、修改、删除的文件
4. 制定有序的实施步骤
5. 评估风险与边界情况

## 报告格式

完成后输出结构化计划（不超过 500 字）：

- **需求理解**（一句话概括任务目标）
- **影响范围分析**（需新增/修改的文件列表）
- **实施步骤**（有序列表，每步可独立验证）
- **风险与注意事项**
```

#### general.md

```markdown
---
name: general
description: 通用子 Agent，可使用读/写工具完成各类子任务。
tools_deny:
  - sub_agent
  - skill_loader
---

# General 角色

你是通用子 Agent，可以执行各种子任务，包括读写文件和执行命令。

## 工作方式

1. 理解分配的任务目标
2. 使用可用工具高效完成任务
3. 遵守安全策略（路径沙箱、黑名单）
4. 完成后输出结构化报告

## 报告格式

完成后输出（不超过 500 字）：

- **执行摘要**（任务完成情况）
- **关键操作与结果**（做了什么、结果如何）
- **产出物**（如有：新建/修改的文件列表）
```

---

## 四、验收标准

### 4.1 功能验收

| # | 验收项 | 验证方法 | 通过标准 |
|---|--------|---------|---------|
| 1 | RoleLoader 三级扫描 | 单测：仅内置 / 仅全局 / 仅项目 / 三者并存 | 三级目录的角色都被加载 |
| 2 | 同名覆盖优先级 | 单测：内置 `explorer.md` + 项目 `explorer.md` | 项目级覆盖内置级，`Source == Project` |
| 3 | frontmatter 解析 | 单测：合法文件 | `Meta.Name` / `Meta.Description` / `Meta.ToolsAllow` / `Meta.ToolsDeny` 正确 |
| 4 | 缺 frontmatter 优雅降级 | 单测：无 `---` 包围 | 跳过该文件，日志警告，不崩溃 |
| 5 | name 空优雅降级 | 单测：`name: ""` | 跳过该文件，日志警告 |
| 6 | YAML 语法错优雅降级 | 单测：非法 YAML | 跳过该文件，日志警告，不崩溃 |
| 7 | 目录不存在不崩溃 | 单测：`builtinDir` 指向不存在路径 | 返回空字典 |
| 8 | `tools_allow` / `tools_deny` 列表解析 | 单测：多元素列表 | 列表正确反序列化 |
| 9 | `AppContext.BaseDirectory` 定位 Builtin | 单测：默认 `builtinDir` | 路径含 `SubAgent/Roles/Builtin` |
| 10 | RoleRegistry.Get 存在 | 单测 | 返回 `RoleDefinition` |
| 11 | RoleRegistry.Get 不存在 | 单测 | 返回 null |
| 12 | RoleRegistry.GetAll | 单测 | 返回全部角色 |
| 13 | RoleRegistry.HasRoles 空列表 | 单测 | false |
| 14 | RoleRegistry.HasRoles 非空 | 单测 | true |
| 15 | ToolFilter 全局排除 sub_agent | 单测：任意角色 | filteredRegistry 无 `sub_agent` |
| 16 | explorer 角色白名单 | 单测：`ToolFilter.Build(parent, explorer, Definitional)` | 含 read_file/glob/grep/run_command，不含 write_file/edit_file/sub_agent/skill_loader |
| 17 | planner 角色白名单 | 单测：`ToolFilter.Build(parent, planner, Definitional)` | 含 read_file/glob/grep，不含 run_command/write_file |
| 18 | general 角色继承 | 单测：`ToolFilter.Build(parent, general, Definitional)` | 含父全部工具（除 sub_agent/skill_loader） |
| 19 | Fork 模式排除 skill_loader | 单测：`ToolFilter.Build(parent, general, Fork)` | 不含 skill_loader |
| 20 | Definitional 模式不排除 skill_loader | 单测：`ToolFilter.Build(parent, general, Definitional)` | 含 skill_loader（仅角色 tools_deny 决定） |
| 21 | 角色 tools_deny 并入全局 deny | 单测：custom 角色声明 `tools_deny: [read_file]` | filteredRegistry 无 read_file |
| 22 | 角色 tools_allow 空时不限制 | 单测：general 角色（无 tools_allow） | 继承父全部工具（除 deny 列表） |
| 23 | 父 ToolRegistry 的 MCP 工具也被过滤 | 单测：parent 含 `filesystem-echo` | 按名匹配，受 allow/deny 规则 |

### 4.2 工程验收

- `dotnet build` 0 error 0 warning
- 全部既有测试通过（含迭代 12/13a/13b 的测试）
- 新增单测覆盖 RoleLoader / RoleRegistry / ToolFilter（见 14a.五）
- 跨平台路径：`RoleLoader` 用 `Path.Combine`，`~` 用 `Environment.SpecialFolder.UserProfile`（与 `SkillLoader` 一致）

### 4.3 代码质量

- `ToolRegistry.cs` / `ToolBase.cs` / `AgentLoop.cs` / `AgentEvent.cs` / `Skills/` 零改动
- `nullable` 引用类型开启，无 warning
- `ToolFilter.Build` 是纯函数（无副作用，相同输入相同输出）

---

## 五、测试清单

### 5.1 RoleLoaderTests

- 三级目录扫描：仅内置 / 仅全局 / 仅项目 / 三者并存
- 同名覆盖优先级：项目 > 全局 > 内置
- frontmatter 解析：合法 / 缺 frontmatter / name 空 / YAML 语法错 / 正文为空
- 目录不存在时不崩溃
- `tools_allow` / `tools_deny` 列表解析
- `AppContext.BaseDirectory` 定位 Builtin 目录
- 自定义 `projectRoot` / `userHome` / `builtinDir` 参数生效

### 5.2 RoleRegistryTests

- `Get` 存在 / 不存在
- `GetAll` 返回全部角色
- `HasRoles` 空列表 / 非空列表
- 构造函数拷贝输入字典（修改输入不影响 registry）

### 5.3 ToolFilterTests

- 全局排除 sub_agent（无论角色是否声明）
- explorer 角色 tools_allow 白名单（只含 read_file/glob/grep/run_command）
- planner 角色 tools_allow 白名单（只含 read_file/glob/grep）
- general 角色 tools_deny（排除 sub_agent/skill_loader，其余继承）
- Fork 模式额外排除 skill_loader
- Definitional 模式不排除 skill_loader（仅角色 tools_deny 决定）
- 角色 tools_deny 并入全局 deny
- 角色 tools_allow 为空时不限制（general 角色）
- 父 ToolRegistry 中的 MCP 工具也被过滤（按名匹配）
- 空父 ToolRegistry → 空 filteredRegistry

---

## 六、风险与对策

| 风险 | 对策 |
|------|------|
| `SubAgentMode` 枚举在 14b 才定义，14a 的 `ToolFilter` 无法编译 | 14a 编码时先在 `Models.cs` 定义 `SubAgentMode` 枚举（无依赖简单枚举，不引入 14b 其他类型） |
| 角色文件恶意内容（prompt injection） | 角色是受信任内容（类似 Skill / `PARROTCODE.md`），与项目指令同等信任级别，不做额外防护 |
| `RoleLoader` 的 Builtin 目录路径在发布后变化 | 用 `AppContext.BaseDirectory` 定位（与 `SkillLoader` 一致），与可执行文件同目录 |
| YamlDotNet 反序列化 `RoleMeta` 列表字段失败 | 与 Skill 同构，已验证可行；单测覆盖多元素列表 |

---

## 七、与 14b 的衔接

本迭代（14a）交付的角色系统和工具过滤是 14b 的前置依赖：
- `RoleRegistry` 被 `SubAgentRunner` 消费（按名查找角色定义）
- `ToolFilter.Build` 被 `SubAgentRunner` 调用（从父 ToolRegistry 构建子 Agent 的过滤 ToolRegistry）
- `SubAgentMode` 枚举在本迭代定义（供 ToolFilter 使用），14b 的 SubAgentRequest/Result 复用

详见 [iter-14b-design.md](./iter-14b-design.md)。
