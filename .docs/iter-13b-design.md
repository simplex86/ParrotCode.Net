# 迭代 13b：/skill 管理命令

> **状态**：[设计完成，待实现]
> **前置迭代**：12 [已完成]（Skill 系统：Loader + Registry + skill_loader + 两阶段加载）、13a [已完成]（Skill 目录化与三层加载）
> **并行子迭代**：13a（Skill 目录化与三层加载）——与 13b 正交，改动文件不重叠，可独立实现与验收
> **后续迭代**：14（子 Agent）、15（Hook 引擎）
> **目标**：提供 `/skill` 斜杠命令，支持 `list` / `info` / `activate` / `deactivate` 四个子命令，让用户在 TUI 中直接查看和管理已加载的 Skill，无需依赖 LLM 自主调用 `skill_loader`。`/skill activate <name>` 是 `/commit` 的泛化版本——手动激活任意 Skill 并触发 Agent round。

---

## 一、迭代目标

### 1.1 核心目标

迭代 12 提供了 `skill_loader` 工具让 LLM 自主激活 Skill，迭代 13a 让 Skill 支持目录结构和资源清单。但用户无法直接查看有哪些 Skill 可用、各 Skill 的详情如何，也无法手动激活/停用 Skill——一切依赖 LLM 的自主决策。本迭代提供 `/skill` 命令填补这一交互缺口。

1. **`/skill list`**：列出所有已加载的 Skill 概要（name + description + 来源层级 + 激活状态 + 资源数），让用户一眼看到有哪些 Skill 可用
2. **`/skill info <name>`**：查看指定 Skill 的详情（完整 Meta + 来源路径 + 目录路径 + 资源清单 + SOP 预览），让用户在激活前了解 Skill 内容
3. **`/skill activate <name>`**：手动激活指定 Skill，注入 SOP 到 history 并触发 Agent round——泛化版的 `/commit`，不限于特定 Skill
4. **`/skill deactivate <name>`**：停用已激活的 Skill，释放工具白名单约束

5. **`/skill` 无参数等价 `/skill list`**：降低使用门槛

### 1.2 本迭代要验证的关键问题

| 问题 | 验证方式 | 失败对策 |
|------|---------|---------|
| `/skill list` 是否展示全部 Skill（含未激活） | 单测：构造多个 Skill，调用 `SkillCommand` | `SkillExecutor.GetAll()` 委托 `SkillRegistry.GetAll()` |
| `/skill info` 是否展示 13a 的资源清单 | 单测：构造含资源的目录 Skill | `SkillDefinition.Resources` 遍历输出 |
| `/skill activate` 是否复用 `/commit` 的注入 + 触发机制 | 代码审查 + 单测 | 复用 `CommitCommand` 的 `History.AddUser` + `CommandResult.StartAgentRound` 模式 |
| `/skill activate` 对已激活 Skill 是否幂等 | 单测 | `SkillRegistry.Activate` 已幂等（迭代 12） |
| `/skill deactivate` 后工具白名单是否更新 | 单测 | `SkillExecutor.GetEffectiveToolFilter()` 重新计算 |
| Skill 系统未启用时是否友好提示 | 单测 | `context.SkillExecutor is null` 返回提示 |
| 无参数 `/skill` 是否默认 list | 单测 | 子命令解析默认 `"list"` |
| 子命令大小写不敏感 | 单测 | `ToLowerInvariant()` 统一小写化 |

### 1.3 非目标（明确不做）

- ❌ 不做 `/skill create` / `/skill edit` / `/skill delete`——Skill 是文件，用户直接编辑文件
- ❌ 不做 `/skill reload`——热重载留后续迭代
- ❌ 不做 `/skill enable` / `/skill disable`——启用/禁用由配置 `skills.enable` 控制
- ❌ 不做 Skill 的 Tab 补全——InputFieldView 的补全目前仅支持命令名，不支持子命令参数补全
- ❌ 不做 `/skill` 的别名（如 `/skills`）——本迭代只注册 `skill` 一个命令名

### 1.4 与既有系统的衔接策略

- **复用 `CommitCommand` 的激活模式**：`/skill activate` 是 `/commit` 的泛化版——`CommitCommand` 固定激活 `"commit"`，`SkillCommand` 接受 `<name>` 参数。两者使用完全相同的注入路径：`AppendUserMessage` + `History.AddUser` + `UpdateTokenEstimate` + `StartAgentRound`
- **复用 `SkillExecutor`**：`/skill list` 需要获取所有 Skill，`SkillExecutor` 目前只有 `GetActive()`，新增 `GetAll()` 委托 `_registry.GetAll()`
- **`CommandContext` 零改动**：已有 `SkillExecutor?` 字段（迭代 12 新增），13b 无需扩展
- **`SkillRegistry` 零改动**：已有 `GetAll()` / `Get(name)` / `Activate` / `Deactivate` / `IsActive` / `GetActiveSkills`，完全够用
- **自动注册**：`SkillCommand` 无参构造，由 `CommandRegistry.AutoRegisterFromAssembly` 反射扫描注册（与 `CommitCommand` 一致）

---

## 二、文件改动清单

### 2.1 新增文件（1 个）

```
Commands/Builtin/SkillCommand.cs    # /skill 命令（list / info / activate / deactivate）
```

### 2.2 修改文件（1 个）

| 文件 | 改动 |
|------|------|
| `Skills/Executor.cs` | 新增 `GetAll()` 方法（委托 `_registry.GetAll()`），让命令层获取全部 Skill 列表 |

### 2.3 不变文件

- `Skills/Registry.cs`——**零改动**（已有 `GetAll()` / `Get()` / `Activate` / `Deactivate` / `IsActive`）
- `Skills/Models.cs`——**零改动**（`SkillDefinition` 已含 13a 的 `SkillDir` / `Resources`）
- `Skills/SkillTool.cs`——**零改动**
- `Commands/CommandContext.cs`——**零改动**（已有 `SkillExecutor?`）
- `Commands/CommandRegistry.cs`——**零改动**（反射自动扫描）
- `Commands/CommandResult.cs`——**零改动**（已有 `WithOutput` / `StartAgentRound`）
- `Agent/AgentLoop.cs`——**零改动**
- `Security/SecurityGuard.cs`——**零改动**
- `Tui/TerminalApp.cs`——**零改动**（命令分发走既有 `CommandDispatcher`）

---

## 三、详细设计

### 3.1 SkillExecutor 扩展（`Skills/Executor.cs`）

仅新增一个方法，委托给 `SkillRegistry.GetAll()`：

```csharp
/// <summary>
/// 所有已加载 Skill 的快照（迭代 13b 新增，/skill list 用）。
/// </summary>
public IReadOnlyCollection<SkillDefinition> GetAll() => _registry.GetAll();
```

> **设计说明**：`SkillExecutor` 是命令层访问 Skill 系统的唯一入口（`CommandContext.SkillExecutor`）。`GetAll()` 委托 `_registry.GetAll()`，不直接暴露 `SkillRegistry`，保持封装一致性。返回类型与 `SkillRegistry.GetAll()` 一致（`IReadOnlyCollection<SkillDefinition>`）。

### 3.2 SkillCommand（`Commands/Builtin/SkillCommand.cs`）

```csharp
using System.Text;

namespace ParrotCode;

/// <summary>
/// /skill 命令（迭代 13b）：管理已加载的 Skill。
/// 子命令：
///   /skill                等价 /skill list
///   /skill list           列出所有 Skill 概要
///   /skill info <name>    查看指定 Skill 详情
///   /skill activate <name>  激活 Skill + 注入 SOP + 触发 Agent round
///   /skill deactivate <name>  停用 Skill
/// 无参构造，由 CommandRegistry.AutoRegisterFromAssembly 自动扫描注册。
/// </summary>
public sealed class SkillCommand : ICommand
{
    public string Name => "skill";
    public string Description => "管理 Skill（list / info / activate / deactivate）";
    public CommandType Type => CommandType.System;
    public IReadOnlyList<string> Aliases => Array.Empty<string>();
    public string Usage => "/skill [list | info <name> | activate <name> | deactivate <name>]";

    public Task<CommandResult> ExecuteAsync(CommandContext context)
    {
        if (context.SkillExecutor is null)
            return Task.FromResult(CommandResult.WithOutput(
                "[!] Skill 系统未启用（配置 skills.enable: false）"));

        // 解析子命令：/skill [subcommand] [args...]
        var parts = context.RawInput.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
        var subcommand = parts.Length > 1 ? parts[1].ToLowerInvariant() : "list";
        var arg = parts.Length > 2 ? parts[2].Trim() : null;

        return subcommand switch
        {
            "list"       => Task.FromResult(ListSkills(context.SkillExecutor)),
            "info"       => Task.FromResult(ShowInfo(context.SkillExecutor, arg)),
            "activate"   => ActivateSkill(context.SkillExecutor, arg, context),
            "deactivate" => Task.FromResult(DeactivateSkill(context.SkillExecutor, arg)),
            _            => Task.FromResult(CommandResult.WithOutput(
                               $"[!] 未知子命令：{subcommand}\n用法：{Usage}"))
        };
    }

    // ---- /skill list ----

    private static CommandResult ListSkills(SkillExecutor executor)
    {
        var all = executor.GetAll();
        if (all.Count == 0)
            return CommandResult.WithOutput("[i] 未加载任何 Skill");

        var sb = new StringBuilder();
        sb.AppendLine($"=== 已加载 Skill（{all.Count}）===");
        foreach (var def in all.OrderBy(d => d.Meta.Name, StringComparer.Ordinal))
        {
            var active = executor.IsActive(def.Meta.Name) ? "[*]" : "[ ]";
            var resourceHint = def.Resources.Count > 0 ? $"（{def.Resources.Count} 资源）" : "";
            sb.AppendLine($"{active} {def.Meta.Name}{resourceHint} — {def.Meta.Description}");
            sb.AppendLine($"     来源：{def.Source}");
        }
        sb.AppendLine();
        sb.AppendLine("[*] = 已激活  [ ] = 未激活");
        return CommandResult.WithOutput(sb.ToString());
    }

    // ---- /skill info <name> ----

    private static CommandResult ShowInfo(SkillExecutor executor, string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return CommandResult.WithOutput("[!] 用法：/skill info <name>");

        var def = executor.GetAll().FirstOrDefault(
            d => string.Equals(d.Meta.Name, name, StringComparison.Ordinal));
        if (def is null)
            return CommandResult.WithOutput($"[!] 未找到 Skill：{name}");

        var sb = new StringBuilder();
        sb.AppendLine($"=== Skill: {def.Meta.Name} ===");
        sb.AppendLine($"描述: {def.Meta.Description}");
        sb.AppendLine($"来源: {def.Source}（{def.SourcePath}）");
        sb.AppendLine($"状态: {(executor.IsActive(def.Meta.Name) ? "已激活" : "未激活")}");

        if (def.SkillDir is not null)
            sb.AppendLine($"目录: {def.SkillDir}");
        else
            sb.AppendLine("目录: （单文件格式）");

        if (def.Meta.ToolsAllow.Count > 0)
            sb.AppendLine($"可用工具: {string.Join(", ", def.Meta.ToolsAllow)}");
        if (def.Meta.ToolsDeny.Count > 0)
            sb.AppendLine($"禁用工具: {string.Join(", ", def.Meta.ToolsDeny)}");

        // 资源清单（13a 的 SkillResource）
        if (def.Resources.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine($"资源（{def.Resources.Count}）:");
            foreach (var res in def.Resources
                         .OrderBy(r => r.Kind).ThenBy(r => r.RelativePath, StringComparer.Ordinal))
            {
                var kindTag = res.Kind switch
                {
                    SkillResourceKind.Script    => "Script",
                    SkillResourceKind.Reference => "Reference",
                    SkillResourceKind.Asset     => "Asset",
                    _                            => res.Kind.ToString()
                };
                sb.AppendLine($"  [{kindTag}] {res.RelativePath}");
            }
        }

        // SOP 预览（前 10 行）
        sb.AppendLine();
        sb.AppendLine("SOP 预览:");
        var previewLines = def.Body.Split('\n', StringSplitOptions.None).Take(10);
        foreach (var line in previewLines)
            sb.AppendLine($"  {line}");
        if (def.Body.Count(c => c == '\n') >= 10)
            sb.AppendLine("  ...（更多内容请激活后查看）");

        return CommandResult.WithOutput(sb.ToString());
    }

    // ---- /skill activate <name> ----

    private static Task<CommandResult> ActivateSkill(
        SkillExecutor executor, string? name, CommandContext context)
    {
        if (string.IsNullOrWhiteSpace(name))
            return Task.FromResult(CommandResult.WithOutput(
                "[!] 用法：/skill activate <name>"));

        var result = executor.Activate(name);
        if (!result.Success)
            return Task.FromResult(CommandResult.WithOutput(
                $"[!] {result.Error}"));

        // 复用 CommitCommand 的注入模式：SOP 作为 user 消息注入 history + 触发 Agent round
        var prompt = $"请按以下 Skill 流程执行：\n\n{result.SopContent}";
        context.Ui.AppendUserMessage($"/skill activate {name}");
        context.History.AddUser(prompt);
        context.Ui.UpdateTokenEstimate(context.History.EstimatedTokens);

        return Task.FromResult(CommandResult.StartAgentRound(
            $"[i] 已激活 Skill {name}，开始执行..."));
    }

    // ---- /skill deactivate <name> ----

    private static CommandResult DeactivateSkill(SkillExecutor executor, string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return CommandResult.WithOutput("[!] 用法：/skill deactivate <name>");

        var wasActive = executor.Deactivate(name);
        if (!wasActive)
            return CommandResult.WithOutput(
                $"[i] Skill {name} 未处于激活状态");

        return CommandResult.WithOutput($"[i] 已停用 Skill {name}");
    }
}
```

**关键设计点**：

1. **子命令解析**：`context.RawInput.Split(' ', 3, ...)` 最多分 3 段——`/skill` + 子命令 + 参数。子命令 `ToLowerInvariant()` 大小写不敏感。`/skill` 无参数默认 `list`。
2. **`/skill list` 输出格式**：每行一个 Skill，`[*]` / `[ ]` 标记激活状态，资源数以 `（N 资源）` 提示，来源标注层级（Builtin / Global / Project）。
3. **`/skill info` SOP 预览**：只显示 Body 前 10 行，避免刷屏。完整 SOP 通过 `activate` 注入 history 后 LLM 可见。资源清单展示 `RelativePath`（可读性优于绝对路径）。
4. **`/skill activate` 复用 `/commit` 模式**：`AppendUserMessage` + `History.AddUser` + `UpdateTokenEstimate` + `StartAgentRound`——与 `CommitCommand` 完全一致的注入路径，只是 Skill 名由参数指定。
5. **`/skill deactivate`**：纯状态操作，不触发 Agent round。停用后下一轮 LLM 的工具白名单自动更新（`GetEffectiveToolFilter` 重新计算）。
6. **错误前缀**：`[!]` 警告、`[i]` 信息——与 `CommitCommand` 的前缀风格一致（参考 project memory 中 U+26A0 显示为 ? 的教训）。

### 3.3 `/skill list` 输出示例

```
=== 已加载 Skill（4）===
[*] commit — 按 Conventional Commits 规范提交代码。当用户要求提交/commit 时调用。
     来源：Builtin
[ ] review — 审查代码变更并提供质量反馈。当用户要求 review/审查代码时调用。
     来源：Builtin
[ ] test — 为指定代码生成单元测试。当用户要求写测试/生成测试时调用。
     来源：Builtin
[ ] xlsx-cleaner（3 资源）— 清洗 Excel 文件数据
     来源：Project

[*] = 已激活  [ ] = 未激活
```

### 3.4 `/skill info` 输出示例

```
=== Skill: xlsx-cleaner ===
描述: 清洗 Excel 文件数据
来源: Project（D:\project\.parrotcode\skills\xlsx-cleaner\SKILL.md）
状态: 未激活
目录: D:\project\.parrotcode\skills\xlsx-cleaner
可用工具: read_file, write_file, run_command
禁用工具: skill_loader

资源（3）:
  [Script] scripts/clean.py
  [Reference] references/format-spec.md
  [Asset] assets/template.json

SOP 预览:
  # Excel 清洗流程

  1. 用 read_file 读取输入文件路径
  2. 用 run_command 执行 scripts/clean.py 处理数据
  3. 用 write_file 输出清洗后的结果
  ...
```

### 3.5 `/skill activate` 与 `/commit` 的对比

| | `/commit` | `/skill activate <name>` |
|---|---|---|
| Skill 名 | 硬编码 `"commit"` | 参数 `<name>` 指定 |
| 注入路径 | `AppendUserMessage` + `AddUser` + `UpdateTokenEstimate` + `StartAgentRound` | 完全一致 |
| UI 显示 | `/commit` | `/skill activate <name>` |
| prompt 前缀 | `"请按以下流程执行提交：\n\n"` | `"请按以下 Skill 流程执行：\n\n"` |
| Skill 未启用 | `[!] Skill 系统未启用` | 完全一致 |
| Skill 不存在 | `[!] 未找到 Skill：commit` | `[!] 未找到 Skill：{name}` |

> `/commit` 可视为 `/skill activate commit` 的快捷方式。两者并存，`/commit` 为高频操作提供短路径，`/skill activate` 为泛化操作提供完整入口。

---

## 四、验收标准

### 4.1 功能验收

| # | 验收项 | 验证方法 | 通过标准 |
|---|--------|---------|---------|
| 1 | `/skill list` 列出所有 Skill | 端到端：输入 `/skill list` | 输出含全部已加载 Skill 的 name + description + 来源 + 激活状态 + 资源数 |
| 2 | `/skill` 无参数等价 list | 端到端：输入 `/skill` | 输出与 `/skill list` 一致 |
| 3 | `/skill list` 空列表提示 | 单测：`SkillExecutor` 无 Skill | 输出"未加载任何 Skill" |
| 4 | `/skill list` 激活状态标记 | 单测：激活一个 Skill 后 list | 激活的为 `[*]`，未激活的为 `[ ]` |
| 5 | `/skill list` 资源数显示 | 单测：构造含 3 资源的目录 Skill | 该 Skill 行含 `（3 资源）` |
| 6 | `/skill list` 按名称排序 | 单测：构造多个 Skill | 输出按 name 字母序排列 |
| 7 | `/skill info <name>` 显示详情 | 端到端：输入 `/skill info commit` | 输出含描述/来源/状态/目录/工具/资源/SOP 预览 |
| 8 | `/skill info` 缺 name 参数 | 单测：`/skill info` 无参数 | 输出"用法：/skill info <name>" |
| 9 | `/skill info` 不存在的 name | 单测：`/skill info nonexistent` | 输出"未找到 Skill：nonexistent" |
| 10 | `/skill info` 单文件 Skill 无目录 | 单测：`/skill info legacy`（单文件，`SkillDir=null`） | "目录：（单文件格式）"，资源段不显示 |
| 11 | `/skill info` 资源清单展示 RelativePath | 单测：含资源的目录 Skill | 资源行含 `[Script] scripts/clean.py` 格式 |
| 12 | `/skill info` SOP 预览截断 | 单测：Body > 10 行 | 只显示前 10 行 + "更多内容请激活后查看" |
| 13 | `/skill activate <name>` 激活 + 触发 | 端到端：输入 `/skill activate review` | `StartAgent=true`，SOP 注入 history，Agent round 启动 |
| 14 | `/skill activate` 缺 name 参数 | 单测：`/skill activate` 无参数 | 输出"用法：/skill activate <name>" |
| 15 | `/skill activate` 不存在的 name | 单测：`/skill activate nonexistent` | 输出"未找到 Skill：nonexistent"，`StartAgent=false` |
| 16 | `/skill activate` 已激活幂等 | 单测：激活后再次 activate | `StartAgent=true`，SOP 再次注入（幂等返回 SOP） |
| 17 | `/skill activate` 达到上限 | 单测：maxActive=1，激活第二个 | 输出"达到上限"错误，`StartAgent=false` |
| 18 | `/skill deactivate <name>` 停用 | 端到端：激活后 `/skill deactivate review` | 输出"已停用 Skill review"，`IsActive` 返回 false |
| 19 | `/skill deactivate` 未激活的 name | 单测：`/skill deactivate review`（未激活） | 输出"Skill review 未处于激活状态" |
| 20 | `/skill deactivate` 缺 name 参数 | 单测：`/skill deactivate` 无参数 | 输出"用法：/skill deactivate <name>" |
| 21 | `/skill deactivate` 不触发 Agent | 单测：成功 deactivate | `StartAgent=false` |
| 22 | 未知子命令提示用法 | 单测：`/skill unknown` | 输出"未知子命令：unknown" + 用法 |
| 23 | 子命令大小写不敏感 | 单测：`/skill LIST` | 输出与 `/skill list` 一致 |
| 24 | Skill 系统未启用 | 单测：`context.SkillExecutor is null` | 输出"Skill 系统未启用" |
| 25 | `/skill activate` 与 `/commit` 行为等价 | 代码审查 | `activate("commit")` 的注入路径与 `CommitCommand` 一致 |

### 4.2 工程验收

- `dotnet build` 0 error 0 warning
- 全部既有测试通过（含迭代 12/13a 的测试）
- 新增单测覆盖 SkillCommand 的 4 个子命令 + 边界情况（见五）
- `SkillExecutor.GetAll()` 委托正确，不引入状态不一致

### 4.3 代码质量

- `SkillRegistry.cs` 零改动
- `CommandContext.cs` 零改动
- `AgentLoop.cs` 零改动
- `SecurityGuard.cs` 零改动
- `SkillTool.cs` 零改动
- `nullable` 引用类型开启，无 warning
- `async` 全链路，无 `.Result` / `.Wait()`
- `SkillCommand` 无状态（所有方法 `static`），线程安全

---

## 五、测试清单

### 5.1 SkillCommandTests（新建）

**list 子命令**：
- `/skill list`：多 Skill 列表输出（含 name / description / 来源 / 激活标记 / 资源数）
- `/skill list`：空列表提示"未加载任何 Skill"
- `/skill list`：激活状态标记正确（`[*]` vs `[ ]`）
- `/skill list`：资源数显示（`（N 资源）`）
- `/skill list`：按 name 字母序排列

**info 子命令**：
- `/skill info <name>`：完整详情输出（含资源清单 + SOP 预览）
- `/skill info`：缺 name 参数提示用法
- `/skill info`：不存在的 name 提示"未找到"
- `/skill info`：单文件 Skill（`SkillDir=null`）显示"单文件格式"
- `/skill info`：目录 Skill 显示 `SkillDir` 绝对路径
- `/skill info`：SOP 预览截断（> 10 行显示"更多内容"）

**activate 子命令**：
- `/skill activate <name>`：成功激活 + `StartAgent=true` + history 含 SOP
- `/skill activate`：缺 name 参数提示用法
- `/skill activate`：不存在的 name 返回错误 + `StartAgent=false`
- `/skill activate`：已激活 Skill 幂等（`StartAgent=true`，SOP 再次注入）
- `/skill activate`：达到 maxActive 上限返回错误

**deactivate 子命令**：
- `/skill deactivate <name>`：成功停用 + `IsActive` 返回 false + `StartAgent=false`
- `/skill deactivate`：未激活的 name 提示"未处于激活状态"
- `/skill deactivate`：缺 name 参数提示用法

**边界与错误处理**：
- `/skill`（无参数）：等价 list
- `/skill LIST`：大小写不敏感，等价 list
- `/skill unknown`：未知子命令提示用法
- `context.SkillExecutor is null`：返回"未启用"提示
- `Command_Metadata_Correct`：Name / Type / Aliases / Usage 正确

### 5.2 SkillExecutorTests 扩展

- `GetAll()` 返回全部 Skill（含未激活）
- `GetAll()` 返回空列表（无 Skill）
- `GetAll()` 与 `GetActive()` 的差异（全部 vs 仅激活）

---

## 六、风险与对策

| 风险 | 对策 |
|------|------|
| `/skill activate` 与 `/commit` 行为不一致 | `SkillCommand.ActivateSkill` 完全复用 `CommitCommand` 的注入模式（`AppendUserMessage` + `AddUser` + `UpdateTokenEstimate` + `StartAgentRound`），代码审查验证 |
| `/skill list` 输出过长（Skill 很多） | 典型场景 < 10 个 Skill，每行约 80 字符，总输出 < 1KB。若极端情况，后续可加分页 |
| `/skill info` SOP 预览暴露敏感内容 | Skill 是受信任内容（类似 `PARROTCODE.md`），预览仅前 10 行，与 `/skill activate` 注入完整 SOP 的信任级别一致 |
| `SkillExecutor.GetAll()` 暴露 `SkillDefinition` 含 `SkillDir` 绝对路径 | 路径仅显示在 `/skill info` 输出中（调试/定位用），不注入 history，不传给 LLM |
| 子命令名大小写敏感 | `ToLowerInvariant()` 统一小写化，`/skill LIST` 和 `/skill list` 等价 |
| `/skill` 与未来 `/skills` 别名冲突 | 本迭代不注册 `skills` 别名，留后续按需添加 |
| `/skill activate` 重复注入 SOP 到 history | 这是预期行为——与 `/commit` 一致，每次激活都注入最新 SOP。已激活的 Skill 重复 activate 也是幂等注入（`SkillRegistry.Activate` 幂等返回 SOP） |

---

## 七、与后续迭代的衔接

- **迭代 14（子 Agent）**：`/skill activate` 可激活含 `sub_agent` 工具的 Skill，让 Agent 按角色 SOP 委派子任务。`/skill list` 可展示角色类 Skill 的概要。
- **迭代 15（Hook 引擎）**：`/skill activate` / `deactivate` 可触发 `skill_activated` / `skill_deactivated` 生命周期事件（若 Hook 引擎定义这些事件），实现"激活 Skill 时自动加载依赖"等自动化。

---

## 八、交付检查清单

- [ ] `Skills/Executor.cs` 新增 `GetAll()` 方法
- [ ] `Commands/Builtin/SkillCommand.cs` 新增（list / info / activate / deactivate）
- [ ] `SkillRegistry.cs` git diff 为空
- [ ] `CommandContext.cs` git diff 为空
- [ ] `AgentLoop.cs` git diff 为空
- [ ] `SecurityGuard.cs` git diff 为空
- [ ] `SkillTool.cs` git diff 为空
- [ ] 单测：SkillCommand 4 个子命令 + 边界情况（缺参数 / 不存在 / 未启用 / 未知子命令 / 大小写）
- [ ] 单测：SkillExecutor.GetAll()
- [ ] `dotnet build` 0 error 0 warning
- [ ] 全部既有测试通过
- [ ] 端到端：`/skill list` 列出全部 Skill
- [ ] 端到端：`/skill info commit` 显示详情
- [ ] 端到端：`/skill activate review` 激活并触发 Agent round
- [ ] 端到端：`/skill deactivate review` 停用
