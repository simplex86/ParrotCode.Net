using System.Text;

namespace ParrotCode;

/// <summary>
/// /help：显示可用命令列表。需依赖 CommandRegistry（手动注册）。
/// </summary>
public sealed class HelpCommand : ICommand
{
    private readonly CommandRegistry _registry;

    public HelpCommand(CommandRegistry registry) => _registry = registry;

    public string Name => "help";
    public string Description => "显示可用命令列表";
    public CommandType Type => CommandType.System;
    public IReadOnlyList<string> Aliases => new[] { "?" };
    public string Usage => "/help";

    public Task<CommandResult> ExecuteAsync(CommandContext context)
    {
        var sb = new StringBuilder();
        sb.AppendLine("可用命令：");
        foreach (var cmd in _registry.GetVisibleCommands().OrderBy(c => c.Name))
            sb.AppendLine($"  {cmd.Usage,-24} {cmd.Description}");
        sb.AppendLine();
        sb.AppendLine("提示：输入消息与 AI 对话；/ 开头走命令。");
        return Task.FromResult(CommandResult.WithOutput(sb.ToString()));
    }
}
