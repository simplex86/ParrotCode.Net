# 迭代 13：Skill 目录化与三层加载 + /skill 管理命令

> **状态**：[设计完成，待实现]
> **前置迭代**：12 [已完成]（Skill 系统：Loader + Registry + skill_loader + 两阶段加载）
> **后续迭代**：14（子 Agent）、15（Hook 引擎）
> **拆分说明**：本迭代拆分为两个正交的子迭代，各自有独立设计文档：
> - **[13a：Skill 目录化与三层加载](./iter-13a-design.md)**——加载层改造
> - **[13b：/skill 管理命令](./iter-13b-design.md)**——交互层增强

---

## 子迭代概览

| | 13a：目录化与三层加载 | 13b：/skill 管理命令 |
|---|---|---|
| **设计文档** | [iter-13a-design.md](./iter-13a-design.md) | [iter-13b-design.md](./iter-13b-design.md) |
| **层次** | 加载层（Loader / Models / Registry） | 交互层（Commands） |
| **改动文件** | `Skills/Models.cs`、`Skills/Loader.cs`、`Skills/Registry.cs` + 3 个 Builtin 迁移 | `Skills/Executor.cs`（加 `GetAll()`）、`Commands/Builtin/SkillCommand.cs`（新增） |
| **风险** | 中（改 Loader 核心扫描逻辑） | 低（纯增量，不改既有路径） |
| **独立验收** | `/commit` 回归 + 单测目录扫描/资源清单 | `/skill list` / `info` / `activate` / `deactivate` 输出正确 |
| **零改动** | `AgentLoop` / `SecurityGuard` / `SkillTool` / `SkillExecutor` / `csproj` | `AgentLoop` / `SecurityGuard` / `SkillRegistry` / `SkillTool` / `CommandContext` |

两者改动文件不重叠（13a 改 `Skills/`，13b 改 `Commands/`），验收路径独立。13b 概念上依赖 13a（list 可展示资源数/目录信息更丰富），但技术上仅依赖迭代 12 的 `SkillRegistry`。

---

## 13b 设计摘要

详见 [iter-13b-design.md](./iter-13b-design.md)。核心要点：

- **4 个子命令**：`/skill list` / `info <name>` / `activate <name>` / `deactivate <name>`，无参数默认 list，子命令大小写不敏感
- **`/skill activate` 复用 `/commit` 注入模式**：`AppendUserMessage` + `History.AddUser` + `StartAgentRound`
- **`SkillExecutor` 唯一扩展**：新增 `GetAll()` 委托 `_registry.GetAll()`
- **零改动**：`SkillRegistry` / `CommandContext` / `AgentLoop` / `SecurityGuard` / `SkillTool`
- **25 项功能验收标准**（含大小写不敏感、幂等激活、maxActive 上限等边界情况）
