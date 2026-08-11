namespace ParrotCode;

/// <summary>
/// /commit 命令（迭代 12）：激活 commit Skill + 注入 SOP 到 history + 触发 Agent round。
/// 若 Skill 系统未启用（skills.enable: false）或 commit Skill 不存在，返回错误提示。
/// 无参构造，由 CommandRegistry.AutoRegisterFromAssembly 自动扫描注册。
/// </summary>
public sealed class CommitCommand : ICommand
{
    public string Name => "commit";
    public string Description => "激活 commit Skill，按 Conventional Commits 流程提交";
    public CommandType Type => CommandType.System;
    public IReadOnlyList<string> Aliases => Array.Empty<string>();
    public string Usage => "/commit";

    public Task<CommandResult> ExecuteAsync(CommandContext context)
    {
        if (context.SkillExecutor is null)
            return Task.FromResult(CommandResult.WithOutput("[!] Skill 系统未启用（配置 skills.enable: false）"));

        var result = context.SkillExecutor.Activate("commit");
        if (!result.Success)
            return Task.FromResult(CommandResult.WithOutput($"[!] {result.Error}"));

        // 把 SOP 作为 user 消息注入 history（后续每轮 LLM 可见），UI 显示 /commit
        var prompt = $"请按以下流程执行提交：\n\n{result.SopContent}";
        context.Ui.AppendUserMessage("/commit");
        context.History.AddUser(prompt);

        // 更新状态栏 token 估算
        context.Ui.UpdateTokenEstimate(context.History.EstimatedTokens);

        // StartAgent=true 让 TerminalApp 在显示 Output 后调用 StartAgentRound()
        return Task.FromResult(CommandResult.StartAgentRound("[i] 已激活 commit Skill，开始提交流程..."));
    }
}
