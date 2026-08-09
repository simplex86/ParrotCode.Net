namespace ParrotCode;

/// <summary>
/// /exit /quit：退出 ParrotCode。
/// </summary>
public sealed class ExitCommand : ICommand
{
    public string Name => "exit";
    public string Description => "退出 ParrotCode";
    public CommandType Type => CommandType.System;
    public IReadOnlyList<string> Aliases => new[] { "quit" };
    public string Usage => "/exit";

    public Task<CommandResult> ExecuteAsync(CommandContext context)
    {
        context.Ui.RequestExit();
        return Task.FromResult(CommandResult.Exit);
    }
}
