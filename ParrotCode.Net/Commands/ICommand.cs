namespace ParrotCode;

/// <summary>
/// 斜杠命令接口。所有命令实现此接口，由 CommandRegistry 反射自动扫描注册。
/// 命令是同步逻辑（无 LLM 调用），通过 CommandContext 操作 UI/History/Compressor 等。
/// </summary>
public interface ICommand
{
    /// <summary>
    /// 命令名（不含 / 前缀），如 "help" / "clear" / "session"。
    /// </summary>
    string Name { get; }

    /// <summary>
    /// 命令描述（/help 展示用，简短一句话）。
    /// </summary>
    string Description { get; }

    /// <summary>
    /// 命令类型（System 在 /help 可见，Hidden 不可见）。
    /// </summary>
    CommandType Type { get; }

    /// <summary>
    /// 命令别名（不含 / 前缀），如 exit 的别名 ["quit"]。空列表表示无别名。
    /// </summary>
    IReadOnlyList<string> Aliases { get; }

    /// <summary>
    /// 用法示例（/help 展示用，如 "/session save" / "/mode strict"）。
    /// </summary>
    string Usage { get; }

    /// <summary>
    /// 执行命令。返回 CommandResult。
    /// 命令不应抛异常——错误信息通过 CommandResult.Output 返回。
    /// </summary>
    Task<CommandResult> ExecuteAsync(CommandContext context);
}
