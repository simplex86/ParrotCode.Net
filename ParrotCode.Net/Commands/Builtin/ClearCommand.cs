namespace ParrotCode;

/// <summary>
/// /clear：清空对话历史 + UI + 重置压缩器警告。
/// 不重置熔断器（熔断器是跨轮状态，/clear 只清历史）。
/// </summary>
public sealed class ClearCommand : ICommand
{
    public string Name => "clear";
    public string Description => "清空对话历史，重新开始";
    public CommandType Type => CommandType.System;
    public IReadOnlyList<string> Aliases => Array.Empty<string>();
    public string Usage => "/clear";

    public Task<CommandResult> ExecuteAsync(CommandContext context)
    {
        context.History.Clear();
        context.Ui.ClearMessages();
        context.Compressor?.ResetWarning();
        context.Ui.UpdateTokenEstimate(0);
        return Task.FromResult(CommandResult.Ok);
    }
}
