namespace ParrotCode;

/// <summary>
/// 命令类型。决定命令在 /help 中的可见性。
/// </summary>
public enum CommandType
{
    /// <summary>
    /// 系统命令：在 /help 中可见，用户可直接调用。
    /// </summary>
    System,

    /// <summary>
    /// 隐藏命令：不在 /help 中显示，但仍可调用（如内部调试命令）。
    /// </summary>
    Hidden
}
