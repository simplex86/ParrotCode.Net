using System.Text;

namespace ParrotCode;

/// <summary>
/// /session save|load|list|current：会话持久化。
/// 10b：注入真实 SessionStore 后实现完整子命令。
/// SessionStore 为 null 时（enable: false）返回"未启用"。
/// </summary>
public sealed class SessionCommand : ICommand
{
    public string Name => "session";
    public string Description => "会话持久化（save/load/list/current）";
    public CommandType Type => CommandType.System;
    public IReadOnlyList<string> Aliases => new[] { "sessions" };
    public string Usage => "/session save|load <id>|list|current";

    public async Task<CommandResult> ExecuteAsync(CommandContext context)
    {
        if (context.SessionStore is null)
            return CommandResult.WithOutput("[!] 会话持久化未启用（配置 session.enable: false）");

        var parts = context.RawInput.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
        var subcommand = parts.Length > 1 ? parts[1].ToLowerInvariant() : null;

        return subcommand switch
        {
            null => CommandResult.WithOutput("用法：/session save|load <id>|list|current"),
            "save" => await SaveAsync(context, parts),
            "load" => await LoadAsync(context, parts),
            "list" => await ListAsync(context),
            "current" => Current(context),
            _ => CommandResult.WithOutput($"[!] 未知子命令：{subcommand}（可选：save/load/list/current）")
        };
    }

    private async Task<CommandResult> SaveAsync(CommandContext context, string[] parts)
    {
        var title = parts.Length > 2 ? parts[2] : null;
        var messages = context.History.ToProviderMessages();

        if (messages.Count == 0)
            return CommandResult.WithOutput("[!] 历史为空，无需保存");

        var meta = await context.SessionStore!.SaveAsync(messages, context.ProviderConfig, title, context.Ct);

        return CommandResult.WithOutput($"[i] 会话已保存\n  ID: {meta.Id}\n  消息数: {meta.MessageCount}\n  标题: {meta.Title}");
    }

    private async Task<CommandResult> LoadAsync(CommandContext context, string[] parts)
    {
        if (parts.Length < 3)
            return CommandResult.WithOutput("[!] 用法：/session load <id>");

        var sessionId = parts[2];

        var (meta, messages) = await context.SessionStore!.LoadAsync(sessionId, context.Ct);

        if (messages.Count == 0)
            return CommandResult.WithOutput($"[!] 会话 {sessionId} 无消息或不存在");

        // 清空当前历史 + UI
        context.History.Clear();
        context.Ui.ClearMessages();
        context.Compressor?.ResetWarning();

        // 加载消息到历史
        context.History.ReplaceMessages(messages);

        // 时间跨度提醒
        var elapsed = DateTime.UtcNow - meta.UpdatedAt;
        if (elapsed.TotalMinutes > 30)
        {
            context.Ui.AppendStaticMessage($"[i] 这是 {FormatTimeSpan(elapsed)}前的会话（{meta.UpdatedAt:yyyy-MM-dd HH:mm} UTC 保存）");
        }

        // 渲染历史消息到 UI
        foreach (var msg in messages)
            RenderHistoricalMessage(context.Ui, msg);

        context.Ui.UpdateTokenEstimate(context.History.EstimatedTokens);

        return CommandResult.WithOutput($"[i] 已加载会话 {meta.Id}（{messages.Count} 条消息）");
    }

    private async Task<CommandResult> ListAsync(CommandContext context)
    {
        var sessions = await context.SessionStore!.ListAsync(context.Ct);
        if (sessions.Count == 0)
            return CommandResult.WithOutput("[i] 无已保存会话");

        var sb = new StringBuilder();
        sb.AppendLine("最近会话（按更新时间倒序）：");
        foreach (var s in sessions.Take(10))
            sb.AppendLine($"  {s.Id}  {s.UpdatedAt:MM-dd HH:mm}  {s.MessageCount,3}条  {s.Title}");
        return CommandResult.WithOutput(sb.ToString());
    }

    private static CommandResult Current(CommandContext context)
    {
        // 本迭代不跟踪"当前会话 ID"（无自动恢复），始终返回提示
        return CommandResult.WithOutput("[i] 当前会话未持久化（用 /session save 保存）");
    }

    private static string FormatTimeSpan(TimeSpan elapsed)
    {
        if (elapsed.TotalDays >= 1) return $"{(int)elapsed.TotalDays} 天 ";
        if (elapsed.TotalHours >= 1) return $"{(int)elapsed.TotalHours} 小时 ";
        if (elapsed.TotalMinutes >= 1) return $"{(int)elapsed.TotalMinutes} 分钟 ";
        return "刚刚";
    }

    private static void RenderHistoricalMessage(IUiControl ui, Message msg)
    {
        switch (msg.Role)
        {
            case MessageRole.User:
                ui.AppendUserMessage(msg.Content);
                break;
            case MessageRole.Assistant:
                ui.AppendStaticMessage($"⏺ {msg.Content}");
                break;
            case MessageRole.Tool:
                ui.AppendStaticMessage($"  ⎿ [tool] {TruncateForDisplay(msg.Content)}");
                break;
            case MessageRole.System:
                // 压缩摘要等 system 消息不渲染到 UI（避免干扰）
                break;
        }
    }

    private static string TruncateForDisplay(string s, int max = 200) => s.Length <= max ? s : s[..(max - 3)] + "...";
}
