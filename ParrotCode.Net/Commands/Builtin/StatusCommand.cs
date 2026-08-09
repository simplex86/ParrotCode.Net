using System.Text;

namespace ParrotCode;

/// <summary>
/// /status：显示当前配置概要。
/// 指令字段在 10c 填充（10a 中 InstructionSummary 为 null，显示"未加载"）。
/// </summary>
public sealed class StatusCommand : ICommand
{
    public string Name => "status";
    public string Description => "显示当前配置概要";
    public CommandType Type => CommandType.System;
    public IReadOnlyList<string> Aliases => Array.Empty<string>();
    public string Usage => "/status";

    public Task<CommandResult> ExecuteAsync(CommandContext context)
    {
        var sb = new StringBuilder();
        sb.AppendLine("=== 当前配置 ===");
        sb.AppendLine($"Provider: {context.ProviderConfig.Name} ({context.ProviderConfig.Protocol})");
        sb.AppendLine($"Model: {context.ProviderConfig.Model}");
        sb.AppendLine($"安全等级: {context.SecurityGuard.Level}");
        sb.AppendLine($"最大轮次: {context.AgentConfig.MaxRounds ?? 10}");
        sb.AppendLine($"工具并发: {context.AgentConfig.MaxParallelism ?? 5}");
        sb.AppendLine($"上下文窗口: {context.TuiConfig.ContextWindowTokens ?? 64000}");

        if (context.Compressor is not null)
        {
            sb.AppendLine($"历史消息数: {context.History.Count}");
            sb.AppendLine($"估算 tokens: {context.History.EstimatedTokens}");
            sb.AppendLine($"压缩熔断器: {(context.Compressor.CircuitOpen ? "打开（已禁用自动压缩）" : "正常")}");
        }
        else
        {
            sb.AppendLine("上下文压缩: 未启用");
        }

        // 10b：会话存储状态
        if (context.SessionStore is not null)
            sb.AppendLine($"会话存储: {context.SessionStore.StorageDir}");
        else
            sb.AppendLine("会话存储: 未启用");

        // 10c 填充；10a/10b 中为 null
        sb.AppendLine($"项目指令: {context.InstructionSummary ?? "未加载"}");

        return Task.FromResult(CommandResult.WithOutput(sb.ToString()));
    }
}
