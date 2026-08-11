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
        var bodyLines = def.Body.Split('\n', StringSplitOptions.None);
        var previewLines = bodyLines.Take(10);
        foreach (var line in previewLines)
            sb.AppendLine($"  {line}");
        if (bodyLines.Length > 10)
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
