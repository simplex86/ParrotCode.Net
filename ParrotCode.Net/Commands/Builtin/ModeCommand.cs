namespace ParrotCode;

/// <summary>
/// /mode [strict|normal|permissive]：查看或切换安全等级。
/// 无参数 → 显示当前等级；有参数 → 切换。
/// </summary>
public sealed class ModeCommand : ICommand
{
    public string Name => "mode";
    public string Description => "查看或切换安全等级（strict/normal/permissive）";
    public CommandType Type => CommandType.System;
    public IReadOnlyList<string> Aliases => Array.Empty<string>();
    public string Usage => "/mode [strict|normal|permissive]";

    public Task<CommandResult> ExecuteAsync(CommandContext context)
    {
        var parts = context.RawInput.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        var modeArg = parts.Length > 1 ? parts[1].Trim() : null;

        if (string.IsNullOrEmpty(modeArg))
        {
            return Task.FromResult(CommandResult.WithOutput($"当前安全等级：{context.SecurityGuard.Level}（可选：strict / normal / permissive）"));
        }

        var newLevel = SecurityLevelParser.Parse(modeArg);
        context.SecurityGuard.Level = newLevel;
        context.Ui.UpdateSecurityLevel(newLevel);
        context.Ui.RefreshStatusBar();

        return Task.FromResult(CommandResult.WithOutput($"安全等级已切换为：{newLevel}"));
    }
}
