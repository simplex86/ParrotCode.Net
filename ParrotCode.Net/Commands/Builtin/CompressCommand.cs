namespace ParrotCode;

/// <summary>
/// /compress：手动触发上下文压缩。
/// 即使熔断器 open 也允许手动触发（与自动触发不同）。
/// 手动触发成功后熔断器保持关闭（先 Reset 再触发）。
/// </summary>
public sealed class CompressCommand : ICommand
{
    public string Name => "compress";
    public string Description => "手动触发上下文压缩（摘要历史）";
    public CommandType Type => CommandType.System;
    public IReadOnlyList<string> Aliases => Array.Empty<string>();
    public string Usage => "/compress";

    public async Task<CommandResult> ExecuteAsync(CommandContext context)
    {
        if (context.Compressor is null)
            return CommandResult.WithOutput("[!] 上下文压缩未启用");

        if (context.Compressor.CircuitOpen)
            context.Compressor.ResetCircuit();

        var result = await context.Compressor.CheckAndCompressAsync(context.History, context.Ct);

        if (result.WasCompressed)
        {
            context.Ui.UpdateTokenEstimate(context.History.EstimatedTokens);
            return CommandResult.WithOutput(
                $"[压缩] 已压缩 {result.MessagesCompressed} 条消息，节省约 {result.EstimatedTokensSaved} tokens");
        }

        if (result.CircuitOpen)
            return CommandResult.WithOutput("[!] 压缩失败，熔断器已打开（摘要连续失败）");

        return CommandResult.WithOutput("[i] 当前无需压缩（token 未达阈值）");
    }
}
