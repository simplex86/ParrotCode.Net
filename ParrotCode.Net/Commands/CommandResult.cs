namespace ParrotCode;

/// <summary>
/// 命令执行结果。
/// </summary>
public sealed record CommandResult
{
    /// <summary>
    /// 命令是否被处理（true=已处理，false=未识别/未处理，回退到 AI）。
    /// </summary>
    public bool Handled { get; init; }

    /// <summary>
    /// 命令输出文本（显示到 ChatView，null 表示无输出）。
    /// </summary>
    public string? Output { get; init; }

    /// <summary>
    /// 是否请求退出应用（/exit /quit 设置）。
    /// </summary>
    public bool ExitApp { get; init; }

    public static CommandResult NotHandled => new() { Handled = false };
    public static CommandResult Ok => new() { Handled = true };
    public static CommandResult WithOutput(string output) => new() { Handled = true, Output = output };
    public static CommandResult Exit => new() { Handled = true, ExitApp = true };
}
