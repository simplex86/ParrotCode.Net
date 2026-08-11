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

    /// <summary>
    /// 是否在命令处理后启动 Agent round（迭代 12 新增，/commit 用）。
    /// true 时 TerminalApp 在显示 Output 后调用 StartAgentRound()。
    /// </summary>
    public bool StartAgent { get; init; }

    public static CommandResult NotHandled => new() { Handled = false };
    public static CommandResult WithOutput(string output) => new() { Handled = true, Output = output };
    /// <summary>命令已处理并请求启动 Agent round（迭代 12 /commit 用）。</summary>
    public static CommandResult StartAgentRound(string output) => new() { Handled = true, Output = output, StartAgent = true };
    public static CommandResult Ok => new() { Handled = true };
    public static CommandResult Exit => new() { Handled = true, ExitApp = true };
}
