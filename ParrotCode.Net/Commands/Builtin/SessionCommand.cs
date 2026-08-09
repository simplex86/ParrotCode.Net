namespace ParrotCode;

/// <summary>
/// /session save|load|list|current：会话持久化。
/// 10a stub：返回"未启用"提示。10b 注入真实 SessionStore 后扩展实现。
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
        // 10a stub：会话持久化未启用
        // 10b 将在此检测 SessionStore 并实现 save/load/list/current 子命令
        return Task.FromResult(CommandResult.WithOutput("[!] 会话持久化未启用（迭代 10b 接入）"));
    }
}
