# 迭代 13a：Skill 目录化与三层加载

> **状态**：[设计完成，待实现]
> **前置迭代**：12 [已完成]（Skill 系统：Loader + Registry + skill_loader + 两阶段加载）
> **并行子迭代**：13b（/skill 管理命令）——与 13a 正交，改动文件不重叠，可独立实现与验收
> **后续迭代**：14（子 Agent）、15（Hook 引擎）
> **目标**：把 Skill 从单文件（`<name>.md`）升级为目录结构（`<name>/SKILL.md` + `scripts/` + `references/` + `assets/`），在不新增工具的前提下实现 **Phase 3 按需加载**——skill_loader 返回的 SOP 附带资源清单（绝对路径），LLM 按需用现有 `read_file` / `run_command` / `write_file` 访问子资源。完全向后兼容单文件 Skill。

---

## 一、迭代目标

### 1.1 核心目标

迭代 12 的两阶段加载解决了"SOP 正文不进 Phase 1 prompt"的问题，但 Skill 仍是单文件——若 SOP 需要配套的参考文档、脚本、模板资产，只能全部塞进 SKILL.md 正文，导致 prompt 膨胀。本迭代把 Skill 升级为目录结构，子资源**不随 SOP 加载**，只在 LLM 需要时通过现有工具按需访问。

1. **Skill 目录结构**：
   ```
   <name>/
   ├── SKILL.md          # 必须：frontmatter + SOP 正文（与迭代 12 单文件格式一致）
   ├── scripts/          # 可选：可执行脚本（.sh / .py / .ps1 ...），用 run_command 执行
   ├── references/       # 可选：参考文档（.md / .txt ...），用 read_file 读取
   └── assets/           # 可选：模板/数据资产（.json / .csv / .yaml ...），用 read_file / write_file 访问
   ```

2. **三层加载**（迭代 12 的两阶段 + 本迭代 Phase 3）：
   - **Phase 1（摘要注入）**：`name + description` → system prompt（迭代 12 已有，**不变**）
   - **Phase 2（SOP 按需加载）**：LLM 调 `skill_loader(name)` → SKILL.md 正文 + **资源清单**作为 `ToolResult.Content` 返回 → 进入 history（迭代 12 已有，**追加资源清单**）
   - **Phase 3（子资源按需加载）**：LLM 根据资源清单中的绝对路径，按需调 `read_file` / `run_command` / `write_file` 访问子资源（**本迭代新增，零新工具**）

3. **零新增工具**：references 用 `read_file`，scripts 用 `run_command`，assets 用 `read_file` / `write_file`——全部复用迭代 5/6 的现有工具，不引入 Skill 专用工具。

4. **向后兼容**：单文件 Skill（`<name>.md`）退化为"只有 SKILL.md、无子资源"的目录 Skill，旧 Skill 文件无需任何改动。

5. **Builtin 升级**：`commit.md` / `review.md` / `test.md` 升级为 `commit/SKILL.md` / `review/SKILL.md` / `test/SKILL.md`（仅迁移文件位置，内容不变，无子资源——验证目录格式与单文件等价）。

### 1.2 本迭代要验证的关键问题

| 问题 | 验证方式 | 失败对策 |
|------|---------|---------|
| 目录格式 Skill 是否正确解析 `SKILL.md` + 子资源 | 单测：构造含 scripts/references/assets 的目录 | `SkillLoader` 扫描 `<name>/SKILL.md` + 三个子目录 |
| 单文件 Skill 是否仍可用（向后兼容） | 单测：构造 `<name>.md` | `SkillLoader` 同时扫描 `*.md` 和 `*/SKILL.md` |
| 同级同名冲突（`commit.md` + `commit/SKILL.md` 同时存在） | 单测 | 目录格式优先 + 日志警告 |
| 资源清单是否随 SOP 注入 history | 单测：`SkillRegistry.Activate` 返回的 SopContent 含资源绝对路径 | `BuildSop` 追加资源清单段 |
| 子资源是否**不**进 Phase 1/2（仅清单路径进 Phase 2） | 单测：`GetSummary()` 不含资源路径；`BuildSop` 含清单但不含资源正文 | Phase 1 只含 name+description；Phase 2 只含 SKILL.md 正文 + 清单 |
| LLM 能否通过 `read_file` 读取 references | 端到端：构造含 references 的 Skill，激活后让 LLM 读取 | 资源清单含绝对路径，`read_file` 路径沙箱在 Normal 模式放行绝对路径 |
| LLM 能否通过 `run_command` 执行 scripts | 端到端：构造含 scripts 的 Skill，激活后让 LLM 执行 | `run_command` 走黑名单 + HITL，与用户主动执行同等信任 |
| 跨平台路径正确性 | 单测：Windows/Unix 路径分隔符 | `Path.Combine` + `Path.GetFullPath` |
| Skill 目录不存在子目录时不崩溃 | 单测：只有 SKILL.md 无子目录 | 子目录不存在静默跳过 |
| 三级扫描 + 同名覆盖对目录格式同样生效 | 单测：内置 `commit/SKILL.md` + 项目 `commit/SKILL.md` | 项目级覆盖内置级，与迭代 12 一致 |

### 1.3 非目标（明确不做）

- ❌ 不做 Skill 资源的热重载（文件变化时自动重新扫描）——后续迭代
- ❌ 不做 Skill 专用工具（如 `skill_read_resource` / `skill_run_script`）——复用现有 `read_file` / `run_command`
- ❌ 不做 scripts 的额外沙箱——复用 `SecurityGuard` 黑名单 + 路径沙箱 + HITL，信任级别 ≤ 用户主动 `run_command`
- ❌ 不做资源清单的懒加载（清单本身随 SOP 进 Phase 2，不延迟）——清单是路径列表，体量小（< 1KB），无需懒加载
- ❌ 不做 Skill 目录的嵌套 Skill（`<name>/SKILL.md` 内再嵌套 `<subname>/SKILL.md`）——Skill 是扁平结构
- ❌ 不做资源文件的类型校验（scripts/ 下放 .md 也不报错）——类型由目录名决定，文件内容不做校验
- ❌ 不做 `/skill` 管理命令（list / info / activate / deactivate）——属 13b 范围

### 1.4 与既有系统的衔接策略

- **复用迭代 12 的全部基础设施**：`SkillLoader` / `SkillRegistry` / `SkillTool` / `SkillExecutor` / `SkillConfig` 均保留，本迭代只**扩展**不重写
- **`AgentLoop` 零改动**：资源清单是 SOP 文本的一部分，作为 `ToolResult.Content` 走迭代 12 既有的"工具结果入 history"路径
- **`SecurityGuard` 零改动**：scripts 通过 `run_command` 执行天然走黑名单 + HITL；references/assets 通过 `read_file` / `write_file` 访问天然走路径沙箱——不需要 Skill 专用的安全例外
- **`ToolBase` / `ToolRegistry` 零改动**：不新增工具，`skill_loader` 的 schema 不变
- **csproj 零改动**：现有 `Skills\Builtin\**\*` glob 自动递归，子目录文件自动包含

---

## 二、文件改动清单

### 2.1 新增文件（3 个）

```
Skills/Builtin/
├── commit/SKILL.md           # 从 commit.md 迁移（内容不变）
├── review/SKILL.md           # 从 review.md 迁移（内容不变）
└── test/SKILL.md             # 从 test.md 迁移（内容不变）
```

### 2.2 删除文件（3 个）

```
Skills/Builtin/
├── commit.md                 # 迁移为 commit/SKILL.md
├── review.md                 # 迁移为 review/SKILL.md
└── test.md                   # 迁移为 test/SKILL.md
```

### 2.3 修改文件（3 个）

| 文件 | 改动 |
|------|------|
| `Skills/Models.cs` | 新增 `SkillResource` record + `SkillResourceKind` enum；`SkillDefinition` 新增 `SkillDir` + `Resources` 字段 |
| `Skills/Loader.cs` | `ScanDirectory` 改为双格式扫描（`*.md` 单文件 + `*/SKILL.md` 目录）；新增 `ParseDirectory` + `ScanResources` 方法 |
| `Skills/Registry.cs` | `BuildSop` 追加资源清单段（Phase 3：绝对路径按 kind 分组列出） |

### 2.4 不变文件

- `Skills/SkillTool.cs`——**零改动**（`skill_loader` 仍调 `registry.Activate`，返回的 SOP 自然含资源清单）
- `Skills/Executor.cs`——**零改动**（工具白名单交集逻辑与资源无关）
- `Agent/AgentLoop.cs`——**零改动**（SOP + 清单作为 ToolResult 入 history）
- `Security/SecurityGuard.cs` / `PathSandbox.cs`——**零改动**（scripts 走 `run_command` 天然受控）
- `Config/Models.cs`——**零改动**（`SkillConfig` 无需新字段）
- `Tui/TerminalApp.cs`——**零改动**（Phase 1 摘要拼接不含资源信息）
- `Commands/Builtin/CommitCommand.cs`——**零改动**
- `ParrotCode.Net.csproj`——**零改动**（`**\*` glob 已递归）

---

## 三、详细设计

### 3.1 Skill 目录结构规范

```
<skill-name>/
├── SKILL.md              # 必须：YAML frontmatter + Markdown SOP（格式同迭代 12 单文件）
├── scripts/              # 可选：可执行脚本
│   ├── clean.py          #   LLM 用 run_command("python <abs>/clean.py") 执行
│   └── build.sh          #   LLM 用 run_command("bash <abs>/build.sh") 执行
├── references/           # 可选：参考文档（可嵌套子目录）
│   ├── api-spec.md       #   LLM 用 read_file("<abs>/api-spec.md") 读取
│   └── examples/
│       └── demo.md       #   递归扫描，LLM 用 read_file("<abs>/examples/demo.md") 读取
└── assets/               # 可选：模板/数据资产
    └── template.json     #   LLM 用 read_file 读 / write_file 写
```

**约定**：
- `SKILL.md` 文件名固定（大写），缺失则目录被跳过 + 日志警告
- `scripts/` / `references/` / `assets/` 三个子目录名固定（小写），均可选
- 子目录内文件名不限，递归扫描所有文件（含嵌套子目录）
- 跳过隐藏文件（`.` 开头，如 `.DS_Store`）和符号链接（安全考虑）
- `SKILL.md` 的 frontmatter `name` 应与目录名一致（如 `commit/SKILL.md` → `name: commit`），不一致时以 frontmatter `name` 为准（与迭代 12 一致）

### 3.2 数据模型扩展（`Skills/Models.cs`）

```csharp
/// <summary>
/// Skill 子资源类型（Phase 3 按需加载）。
/// 类型由所在子目录决定，不校验文件扩展名。
/// </summary>
public enum SkillResourceKind
{
    /// <summary>scripts/ 下的脚本，用 run_command 执行。</summary>
    Script,
    /// <summary>references/ 下的参考文档，用 read_file 读取。</summary>
    Reference,
    /// <summary>assets/ 下的资产，用 read_file / write_file 访问。</summary>
    Asset
}

/// <summary>
/// Skill 子资源（Phase 3 按需加载项）。
/// 不随 SOP 正文加载，只在资源清单中列出绝对路径，LLM 按需用现有工具访问。
/// </summary>
public sealed record SkillResource
{
    /// <summary>资源类型（决定 LLM 应使用哪个工具访问）。</summary>
    public required SkillResourceKind Kind { get; init; }

    /// <summary>相对 SkillDir 的路径（如 "scripts/clean.py"），用于清单可读性。</summary>
    public required string RelativePath { get; init; }

    /// <summary>绝对路径（LLM 传给 read_file / run_command 的值）。</summary>
    public required string AbsolutePath { get; init; }
}
```

`SkillDefinition` 新增两个字段（其余字段不变）：

```csharp
public sealed record SkillDefinition
{
    // ... 既有字段不变（Meta / Body / SourcePath / Source）...

    /// <summary>
    /// Skill 目录绝对路径（目录格式 Skill 才有，单文件 Skill 为 null）。
    /// 用于扫描子资源 + 调试定位。
    /// </summary>
    public string? SkillDir { get; init; }

    /// <summary>
    /// 子资源列表（Phase 3 按需加载项）。
    /// 单文件 Skill 或无子目录的目录 Skill 均为空列表。
    /// </summary>
    public IReadOnlyList<SkillResource> Resources { get; init; } = Array.Empty<SkillResource>();
}
```

> **设计说明**：`SkillDir` 为 null 表示单文件 Skill（向后兼容标记）；`Resources` 为空列表表示无子资源（无论是单文件还是无子目录的目录 Skill）。两个字段组合可区分三种形态：单文件（`SkillDir=null, Resources=[]`）、无子资源目录（`SkillDir=<path>, Resources=[]`）、有子资源目录（`SkillDir=<path>, Resources=[...]`）。

### 3.3 SkillLoader 改造（`Skills/Loader.cs`）

核心改动：`ScanDirectory` 从"只扫 `*.md`"改为"先扫 `*.md`（向后兼容），再扫 `*/SKILL.md`（目录格式，同名覆盖单文件版本）"。

```csharp
/// <summary>
/// 扫描指定目录下的 Skill：先扫单文件 *.md（向后兼容），再扫目录 */SKILL.md（目录格式）。
/// 同名时目录格式覆盖单文件版本（+ 警告日志）。
/// 目录不存在静默跳过；单个 Skill 解析失败记录日志跳过，不中断整体加载。
/// </summary>
private void ScanDirectory(string dir, SkillSource source, Dictionary<string, SkillDefinition> byName)
{
    if (!Directory.Exists(dir)) return;

    // 1. 扫描单文件格式 *.md（向后兼容）
    foreach (var file in Directory.GetFiles(dir, "*.md"))
    {
        // 跳过目录格式产生的 SKILL.md（它在子目录里，不会被 GetFiles(dir, "*.md") 扫到，
        // 但防御性地跳过名为 SKILL.md 的顶层文件，避免误解析）
        if (Path.GetFileName(file) == "SKILL.md") continue;

        try
        {
            var def = ParseFile(file, source);
            if (def is not null)
            {
                byName[def.Meta.Name] = def;
                _logger?.LogDebug("已加载单文件 Skill {Name}（{Source}）：{File}", def.Meta.Name, source, file);
            }
        }
        catch (Exception ex)
        {
            _logger?.LogWarning("Skill 文件解析失败 {File}：{Error}", file, ex.Message);
        }
    }

    // 2. 扫描目录格式 <name>/SKILL.md
    foreach (var subDir in Directory.GetDirectories(dir))
    {
        var skillFile = Path.Combine(subDir, "SKILL.md");
        if (!File.Exists(skillFile)) continue;

        try
        {
            var def = ParseDirectory(subDir, skillFile, source);
            if (def is not null)
            {
                // 同名冲突检测：单文件版本已存在则警告（目录格式优先）
                if (byName.TryGetValue(def.Meta.Name, out var existing) && existing.Source == source)
                {
                    _logger?.LogWarning("Skill {Name} 在 {Source} 层级同时存在单文件和目录格式，使用目录格式（{Dir}）",
                        def.Meta.Name, source, subDir);
                }
                byName[def.Meta.Name] = def;
                _logger?.LogDebug("已加载目录 Skill {Name}（{Source}）：{Dir}（{Count} 个资源）",
                    def.Meta.Name, source, subDir, def.Resources.Count);
            }
        }
        catch (Exception ex)
        {
            _logger?.LogWarning("Skill 目录解析失败 {Dir}：{Error}", subDir, ex.Message);
        }
    }
}

/// <summary>
/// 解析目录格式 Skill：先复用 ParseFile 解析 SKILL.md，再扫描子目录资源。
/// </summary>
private SkillDefinition? ParseDirectory(string skillDir, string skillFile, SkillSource source)
{
    var def = ParseFile(skillFile, source);
    if (def is null) return null;

    var resources = new List<SkillResource>();
    ScanResources(skillDir, "scripts", SkillResourceKind.Script, resources);
    ScanResources(skillDir, "references", SkillResourceKind.Reference, resources);
    ScanResources(skillDir, "assets", SkillResourceKind.Asset, resources);

    return def with { SkillDir = skillDir, Resources = resources };
}

/// <summary>
/// 递归扫描子目录下的所有文件（跳过隐藏文件和符号链接）。
/// </summary>
private void ScanResources(string skillDir, string subDirName, SkillResourceKind kind, List<SkillResource> resources)
{
    var subDir = Path.Combine(skillDir, subDirName);
    if (!Directory.Exists(subDir)) return;

    foreach (var file in Directory.GetFiles(subDir, "*", SearchOption.AllDirectories))
    {
        var fileName = Path.GetFileName(file);
        // 跳过隐藏文件（.DS_Store / .gitkeep 等）
        if (fileName.StartsWith(".")) continue;

        var relativePath = Path.GetRelativePath(skillDir, file);
        var absolutePath = Path.GetFullPath(file);

        resources.Add(new SkillResource
        {
            Kind = kind,
            RelativePath = relativePath,
            AbsolutePath = absolutePath
        });
    }
}
```

**关键点**：
- `ParseFile` 方法**零改动**（已正确解析 frontmatter + 正文，返回 `SkillDefinition`）
- 目录格式的 `SourcePath` 指向 `SKILL.md` 文件（与单文件一致，调试用）
- `SkillDir` 指向目录本身（资源扫描根）
- 同级同名冲突：目录格式优先 + 警告日志（不报错，向后兼容场景下用户可能正在迁移）
- 跨级同名覆盖：项目级目录 > 全局级单文件 / 目录 > 内置级单文件 / 目录（扫描顺序不变）

### 3.4 SkillRegistry.BuildSop 改造（`Skills/Registry.cs`）

唯一改动：`BuildSop` 在 SOP 正文后追加资源清单段（如果有资源）。

```csharp
/// <summary>
/// 构建注入 history 的 SOP 文本（含元信息 + 资源清单）。
/// 资源清单是 Phase 3 的入口：LLM 看到绝对路径后按需用现有工具访问。
/// </summary>
private static string BuildSop(SkillDefinition def)
{
    var sb = new StringBuilder();
    sb.AppendLine($"# Skill: {def.Meta.Name}");
    sb.AppendLine();
    sb.AppendLine(def.Body);

    if (def.Meta.ToolsAllow.Count > 0)
        sb.AppendLine().AppendLine($"**可用工具**：{string.Join(", ", def.Meta.ToolsAllow)}");
    if (def.Meta.ToolsDeny.Count > 0)
        sb.AppendLine().AppendLine($"**禁用工具**：{string.Join(", ", def.Meta.ToolsDeny)}");

    // Phase 3：资源清单（仅有资源时追加）
    if (def.Resources.Count > 0)
    {
        sb.AppendLine();
        sb.AppendLine("## 资源清单（按需访问，勿一次性全部读取）");
        sb.AppendLine("以下资源不随 SOP 加载，请根据 SOP 步骤按需用现有工具访问：");

        var byKind = def.Resources.GroupBy(r => r.Kind).OrderBy(g => (int)g.Key);
        foreach (var group in byKind)
        {
            var (kindName, toolHint) = group.Key switch
            {
                SkillResourceKind.Script    => ("脚本", "用 run_command 执行"),
                SkillResourceKind.Reference => ("参考文档", "用 read_file 读取"),
                SkillResourceKind.Asset     => ("资产", "用 read_file / write_file 访问"),
                _                            => (group.Key.ToString(), "")
            };
            sb.AppendLine();
            sb.AppendLine($"### {kindName}（{toolHint}）");
            foreach (var res in group.OrderBy(r => r.RelativePath, StringComparer.Ordinal))
            {
                sb.AppendLine($"- {res.AbsolutePath}");
            }
        }
    }

    return sb.ToString();
}
```

**资源清单注入示例**：

```
# Skill: xlsx-cleaner

[clean Excel SOP 正文...]

**可用工具**：read_file, write_file, run_command

## 资源清单（按需访问，勿一次性全部读取）
以下资源不随 SOP 加载，请根据 SOP 步骤按需用现有工具访问：

### 脚本（用 run_command 执行）
- /home/user/.parrotcode/skills/xlsx-cleaner/scripts/clean.py

### 参考文档（用 read_file 读取）
- /home/user/.parrotcode/skills/xlsx-cleaner/references/format-spec.md

### 资产（用 read_file / write_file 访问）
- /home/user/.parrotcode/skills/xlsx-cleaner/assets/template.json
```

### 3.5 SkillTool（`Skills/SkillTool.cs`）— 零改动

`skill_loader` 工具**零改动**。它仍调 `_registry.Activate(name)`，返回的 `SopContent` 自然包含资源清单（如果 Skill 有资源）。LLM 在后续轮次看到清单中的绝对路径，按需调用 `read_file` / `run_command` / `write_file`。

```csharp
// 迭代 13a 无需改动 SkillTool，以下为迭代 12 既有代码（仅展示逻辑确认）
public override Task<ToolResult> ExecuteAsync(JsonElement input, CancellationToken ct)
{
    var name = GetRequiredString(input, "name", out var error);
    if (error is not null) return Task.FromResult(ToolResult.Fail(error));

    var result = _registry.Activate(name);
    if (!result.Success) return Task.FromResult(ToolResult.Fail(result.Error ?? "激活失败"));

    // SopContent 现在可能含资源清单（Phase 3 入口），作为 ToolResult 入 history
    return Task.FromResult(ToolResult.Ok(result.SopContent ?? string.Empty));
}
```

### 3.6 安全模型（零额外沙箱）

子资源访问复用现有安全层，**不引入 Skill 专用安全例外**：

| 资源类型 | 访问工具 | 安全层行为 | 说明 |
|---------|---------|-----------|------|
| scripts/ | `run_command` | 黑名单 + HITL（Normal/Strict） | 与用户主动 `run_command` 同等信任级别 |
| references/ | `read_file` | 路径沙箱（Strict 限制白名单内，Normal 放行绝对路径） | Normal 模式下可读项目外路径（Skill 目录在 `~/.parrotcode/` 或 `.parrotcode/`） |
| assets/ | `read_file` / `write_file` | 路径沙箱 + HITL（write 在 Normal 触发确认） | 同普通文件读写 |

**PathSandbox 与 Skill 目录的兼容性**（基于迭代 8a 实现）：
- **Normal 模式**（默认）：`..` 越界检测 + 绝对路径放行 → Skill 目录（无论在 `~/.parrotcode/skills/` 还是 `./.parrotcode/skills/`）的绝对路径均可 `read_file`
- **Strict 模式**：路径必须在 `ProjectRoot + AllowPaths` 子树内 → 项目级 Skill（`.parrotcode/skills/`）可用；全局级 Skill（`~/.parrotcode/skills/`）需把目录加入 `AllowPaths` 或切 Normal 模式
- **Permissive 模式**：不检查路径 → 全部放行

**设计决策**：不在 `PathSandbox` 的 `AllowPaths` 中自动注入 Skill 目录。理由：
1. 保持安全层对 Skill 系统的无感知（`SecurityGuard` 零改动）
2. Strict 模式本就是"最小权限"档位，要求用户显式声明信任的路径
3. Skill 信任级别 ≤ 用户主动 `run_command`——用户在 Normal 模式下主动 `run_command` 也受 HITL 约束，Skill scripts 同理

### 3.7 Builtin Skill 升级

三个内置 Skill 从单文件迁移为目录格式（仅文件位置变化，内容完全不变）：

```
# 迁移前（迭代 12）
Skills/Builtin/
├── commit.md
├── review.md
└── test.md

# 迁移后（迭代 13a）
Skills/Builtin/
├── commit/
│   └── SKILL.md     # 内容 = 原 commit.md
├── review/
│   └── SKILL.md     # 内容 = 原 review.md
└── test/
    └── SKILL.md     # 内容 = 原 test.md
```

三个内置 Skill 均无子资源（`scripts/` / `references/` / `assets/` 均无），用于验证目录格式与单文件格式的等价性。带子资源的 Skill 由测试 fixture 和进阶练习覆盖。

> **csproj 无需改动**：`<Content Include="Skills\Builtin\**\*" CopyToOutputDirectory="PreserveNewest" />` 的 `**\*` glob 已递归匹配子目录文件。

---

## 四、三层加载时序

```
启动期：
  App.RunAsync
    └─ SkillLoader.Load()
         └─ ScanDirectory（三级 × 双格式）
              ├─ 扫描 *.md（单文件，向后兼容）
              └─ 扫描 */SKILL.md（目录格式）
                   └─ ParseDirectory
                        ├─ ParseFile(SKILL.md) → SkillDefinition
                        └─ ScanResources(scripts/ | references/ | assets/) → SkillResource[]
                        └─ def with { SkillDir, Resources }
    └─ SkillRegistry(skills)  ← 与迭代 12 一致
    └─ TerminalApp
         └─ BuildSystemPrompt()
              └─ SkillRegistry.GetSummary()  ← Phase 1：仅 name + description（不含资源路径）

运行期（用户对话）：
  用户输入 → AgentLoop.RunAsync
    └─ LLM 决定调用 skill_loader(name="xlsx-cleaner")
         └─ SkillTool.ExecuteAsync
              └─ SkillRegistry.Activate("xlsx-cleaner")
                   └─ BuildSop(def)
                        ├─ "# Skill: xlsx-cleaner\n[SOP 正文]"
                        ├─ "**可用工具**：read_file, write_file, run_command"
                        └─ "## 资源清单\n### 脚本\n- /abs/scripts/clean.py\n### 参考文档\n- /abs/references/spec.md"
                   └─ 返回 SkillActivateResult{ SopContent = [SOP + 清单] }
              └─ ToolResult.Ok(sopContent)  ← Phase 2：SOP + 资源清单入 history
    └─ 后续轮 LLM 看到 SOP + 清单
         └─ LLM 按 SOP 步骤，按需调用：
              ├─ read_file("/abs/references/spec.md")     ← Phase 3：读参考文档
              ├─ run_command("python /abs/scripts/clean.py data.xlsx")  ← Phase 3：执行脚本
              └─ write_file("/abs/assets/result.json", ...)  ← Phase 3：写资产
         └─ 每个子资源访问都是独立的工具调用，走 AgentLoop 既有路径 + SecurityGuard 既有检查
```

**关键不变量**：
- `AgentLoop` 全程零改动
- `SecurityGuard` 全程零改动
- `skill_loader` 工具代码零改动
- Phase 3 不引入新工具、新事件、新拦截点——完全是 LLM 基于清单的自主工具调用

---

## 五、验收标准

### 5.1 功能验收

| # | 验收项 | 验证方法 | 通过标准 |
|---|--------|---------|---------|
| 1 | 目录格式 Skill 解析 | 单测：构造 `xlsx-cleaner/SKILL.md` + `scripts/clean.py` + `references/spec.md` + `assets/template.json` | `SkillDefinition.SkillDir` 非空，`Resources` 含 3 项，`Kind` 分别为 Script/Reference/Asset |
| 2 | SKILL.md 缺失跳过 | 单测：目录无 `SKILL.md` | 目录被跳过 + 日志警告，不崩溃 |
| 3 | 子目录不存在时不崩溃 | 单测：目录只有 `SKILL.md`，无 `scripts/` / `references/` / `assets/` | `Resources` 为空列表，`SkillDir` 非空 |
| 4 | 单文件向后兼容 | 单测：构造 `legacy.md`（迭代 12 格式） | `SkillDir` 为 null，`Resources` 为空，`Body` / `Meta` 正确解析 |
| 5 | 同级同名冲突（目录优先） | 单测：同级同时有 `commit.md` 和 `commit/SKILL.md` | 目录版本覆盖单文件版本 + 警告日志，`SkillDir` 非空 |
| 6 | 三级同名覆盖对目录格式生效 | 单测：内置 `commit/SKILL.md` + 项目 `commit/SKILL.md` | 项目级覆盖内置级，`Source == Project` |
| 7 | 三级同名覆盖跨格式生效 | 单测：内置 `commit.md` + 项目 `commit/SKILL.md` | 项目级目录覆盖内置级单文件 |
| 8 | 资源清单随 SOP 注入 history | 单测：`Activate("xlsx-cleaner")` 返回的 `SopContent` 含 `## 资源清单` + 绝对路径 | 清单按 kind 分组，每组列出绝对路径 |
| 9 | 资源清单不进 Phase 1 | 单测：`GetSummary()` 输出 | 仅含 `name: description`，不含资源路径、不含 `## 资源清单` |
| 10 | 资源正文不进 Phase 2 | 单测：`BuildSop` 输出 | 含资源**清单**（路径），不含资源**正文**（文件内容） |
| 11 | 递归扫描嵌套子目录 | 单测：`references/api/v1.md` | `Resources` 含 `references/api/v1.md`，`RelativePath` 正确 |
| 12 | 隐藏文件跳过 | 单测：`scripts/.DS_Store` | `Resources` 不含该文件 |
| 13 | 跨平台路径 | 单测：Windows（`\`）+ Unix（`/`）分隔符 | `AbsolutePath` 用 `Path.GetFullPath` 规范化，跨平台一致 |
| 14 | LLM 通过 read_file 读 references | 端到端：构造含 references 的 Skill，激活后让 LLM 读取 | LLM 用清单中的绝对路径调 `read_file`，成功读取内容 |
| 15 | LLM 通过 run_command 执行 scripts | 端到端：构造含 scripts 的 Skill，激活后让 LLM 执行 | LLM 用清单中的绝对路径调 `run_command`，脚本执行 + 结果回传 |
| 16 | LLM 通过 read_file/write_file 访问 assets | 端到端：构造含 assets 的 Skill，激活后让 LLM 读写 | `read_file` 读资产内容；`write_file` 在 Normal 模式触发 HITL |
| 17 | Builtin Skill 目录格式可用 | 端到端：`/commit` 触发 | commit Skill 正常激活，SOP 正确注入（与迭代 12 行为一致） |
| 18 | 无子资源的目录 Skill 与单文件等价 | 单测：同样内容分别存为 `x.md` 和 `x/SKILL.md` | `Meta` / `Body` 一致，差异仅在 `SkillDir`（null vs 非空）和 `Resources`（均空） |

### 5.2 工程验收

- `dotnet build` 0 error 0 warning
- 全部既有测试通过（迭代 12 的 Skill 测试需因 `SkillDefinition` 新字段可能需微调，但行为不变）
- 新增单测覆盖 Loader 目录扫描 + Registry 资源清单（见六）
- 跨平台路径：`SkillLoader` 全程用 `Path.Combine` / `Path.GetFullPath` / `Path.GetRelativePath`

### 5.3 代码质量

- `AgentLoop.cs` 零改动（git diff 验证）
- `SecurityGuard.cs` / `PathSandbox.cs` 零改动
- `SkillTool.cs` 零改动
- `SkillExecutor.cs` 零改动
- `nullable` 引用类型开启，无 warning（`SkillDir` 为 `string?`，`Resources` 为非空 `IReadOnlyList<>` 默认空数组）
- `async` 全链路，无 `.Result` / `.Wait()`
- `CancellationToken` 贯穿（本迭代无新异步方法，Loader/Registry 均同步）

---

## 六、测试清单

### 6.1 SkillLoaderTests 扩展（目录扫描）

**新增用例**（迭代 12 既有用例保留，因双格式扫描逻辑向后兼容）：

- 目录格式解析：`<name>/SKILL.md` + 三个子目录 → `SkillDir` 非空，`Resources` 含全部文件
- 单文件向后兼容：`<name>.md` → `SkillDir` 为 null，`Resources` 为空
- 同级同名冲突：`commit.md` + `commit/SKILL.md` 同级 → 目录版本优先 + 警告日志
- 跨级同名覆盖（目录 vs 目录）：内置 `commit/SKILL.md` + 项目 `commit/SKILL.md` → 项目级胜
- 跨级同名覆盖（单文件 vs 目录）：内置 `commit.md` + 项目 `commit/SKILL.md` → 项目级目录胜
- SKILL.md 缺失：目录存在但无 `SKILL.md` → 跳过 + 警告
- 子目录全缺失：只有 `SKILL.md` → `Resources` 为空，不崩溃
- 递归扫描：`references/api/v1.md` → `RelativePath = "references/api/v1.md"`
- 隐藏文件跳过：`scripts/.DS_Store` → 不在 `Resources` 中
- 顶层 `SKILL.md` 跳过：扫描目录下的 `SKILL.md`（非子目录内）→ 跳过（避免误解析）
- 资源类型映射：`scripts/` → Script，`references/` → Reference，`assets/` → Asset
- 绝对路径正确性：`AbsolutePath` 为 `Path.GetFullPath` 规范化后的路径

### 6.2 SkillRegistryTests 扩展（资源清单）

**新增用例**：

- `BuildSop`（通过 `Activate` 间接验证）含资源清单：`SopContent` 含 `## 资源清单`
- 清单按 kind 分组：Script / Reference / Asset 各有 `###` 标题
- 清单含绝对路径：每个资源一行 `- <abs-path>`
- 无资源的 Skill：`SopContent` 不含 `## 资源清单` 段
- `GetSummary` 不含资源路径（Phase 1 隔离验证）

### 6.3 端到端测试

- 构造含 scripts + references + assets 的 Skill 目录 → 激活 → SOP 含清单
- 模拟 LLM 用 `read_file` 读取 references 中的文件（mock 或真实文件）
- 模拟 LLM 用 `run_command` 执行 scripts 中的脚本
- Builtin `/commit` 仍正常工作（目录格式迁移无回归）

---

## 七、风险与对策

| 风险 | 对策 |
|------|------|
| Strict 模式下全局 Skill 的 resources 不可读 | 设计预期行为：Strict 要求最小权限，用户需把 Skill 目录加入 `AllowPaths` 或切 Normal 模式。文档说明，不自动放行 |
| 资源清单过长导致 SOP 膨胀 | 清单仅含路径（每行约 80 字符），典型 Skill < 10 个资源 < 1KB，远低于截断阈值。若极端情况，后续迭代可加分页 |
| LLM 不主动读资源清单（Phase 3 不触发） | SOP 正文应明确指引"请参考资源清单中的文档"。SKILL.md 作者责任，不在框架层强制 |
| scripts 含恶意命令 | 与用户主动 `run_command` 同等信任——黑名单 + HITL 拦截。Skill 是受信任内容（类似 `PARROTCODE.md`），不额外加沙箱 |
| 递归扫描遇到符号链接导致无限循环 | `Directory.GetFiles` 不解析符号链接（.NET 默认不跟随 reparse points）。若需严格防御，可加 `FileAttributes.ReparsePoint` 检查（本迭代暂不引入） |
| 迁移 Builtin 后旧测试依赖 `commit.md` 路径失败 | 测试用 `AppContext.BaseDirectory` 定位 Builtin 目录，不硬编码文件名。若有硬编码，测试改为构造临时目录（参考 `SkillLoaderTests` 现有模式） |
| `SkillDefinition` 是 record，`with` 表达式创建新实例 | `ParseFile` 返回的 `SkillDefinition` 被 `ParseDirectory` 用 `with` 追加 `SkillDir` + `Resources`——record 的预期用法，无副作用 |
| Windows 上 `scripts/*.sh` 无法直接执行 | 资源清单只提供路径，执行方式由 SKILL.md 正文指导（如 `bash <path>` / `python <path>`）。框架不负责脚本执行，LLM 根据文件扩展名和 SOP 指引选择解释器 |

---

## 八、与后续迭代的衔接

- **13b（/skill 管理命令）**：本迭代新增的 `SkillDefinition.SkillDir` / `Resources` 字段为 13b 的 `/skill info` 命令提供展示数据（资源清单、目录路径）。13b 仅需在 `SkillExecutor` 加 `GetAll()` 方法，不改 13a 的任何文件。
- **迭代 14（子 Agent）**：Skill 的 `tools_allow` 可包含 `sub_agent`，让 Skill 委派子任务。目录格式 Skill 的 `references/` 可放角色定义文件（如 `references/explorer-role.md`），子 Agent 加载时复用 `read_file`。本迭代不实现 `sub_agent` 工具，但资源清单机制已为后续扩展铺路。
- **迭代 15（Hook 引擎）**：`tool_pre_exec` Hook 可针对 Skill scripts 做额外检查（如限制脚本执行时间）。本迭代的 scripts 走 `run_command` 天然经过 `tool_pre_exec`，Hook 引擎接入后自动生效，无需额外改造。

---

## 九、交付检查清单

- [ ] `Skills/Models.cs` 新增 `SkillResource` + `SkillResourceKind`；`SkillDefinition` 加 `SkillDir` + `Resources`
- [ ] `Skills/Loader.cs` `ScanDirectory` 双格式扫描 + `ParseDirectory` + `ScanResources`
- [ ] `Skills/Registry.cs` `BuildSop` 追加资源清单段
- [ ] `Skills/Builtin/commit.md` → `commit/SKILL.md`（内容不变）
- [ ] `Skills/Builtin/review.md` → `review/SKILL.md`（内容不变）
- [ ] `Skills/Builtin/test.md` → `test/SKILL.md`（内容不变）
- [ ] `AgentLoop.cs` git diff 为空（零改动验证）
- [ ] `SecurityGuard.cs` / `PathSandbox.cs` git diff 为空
- [ ] `SkillTool.cs` git diff 为空
- [ ] `SkillExecutor.cs` git diff 为空
- [ ] `ParrotCode.Net.csproj` git diff 为空
- [ ] 单测：Loader 目录扫描（双格式 + 递归 + 隐藏文件跳过 + 同名冲突）
- [ ] 单测：Registry 资源清单注入（含清单 / 不含清单 / Phase 1 隔离）
- [ ] `dotnet build` 0 error 0 warning
- [ ] 全部既有测试通过（含迭代 12 的 Skill 测试）
- [ ] 端到端：`/commit` 跑通（Builtin 目录格式迁移无回归）
