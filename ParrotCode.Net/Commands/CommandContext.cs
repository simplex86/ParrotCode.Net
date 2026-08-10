namespace ParrotCode;

/// <summary>
/// 命令执行上下文：封装命令执行时需要的所有依赖。
/// 命令通过此上下文操作 UI/History/Compressor/SecurityGuard 等，不直接依赖 TerminalApp。
/// </summary>
public sealed record CommandContext(
    ConversationHistory History,
    ContextCompressor? Compressor,
    SecurityGuard SecurityGuard,
    IUiControl Ui,
    SessionStore? SessionStore,
    CancellationToken Ct)
{
    /// <summary>
    /// 当前 Provider 配置（/status 用）。
    /// 必填。
    /// </summary>
    public ProviderConfig ProviderConfig { get; init; } = null!;

    /// <summary>
    /// 当前 TUI 配置（/status 用）。
    /// 必填。
    /// </summary>
    public TuiConfig TuiConfig { get; init; } = null!;

    /// <summary>
    /// 当前 AgentConfig（/status 用）。
    /// 必填。
    /// </summary>
    public AgentConfig AgentConfig { get; init; } = null!;

    /// <summary>
    /// 项目指令加载概要（/status 显示，10c 填充）。
    /// </summary>
    public string? InstructionSummary { get; init; }

    /// <summary>
    /// MCP 连接状态概要（/status 显示，11c 填充）。
    /// </summary>
    public string? McpSummary { get; init; }

    /// <summary>
    /// 原始输入行（含 / 前缀，便于错误提示引用与参数解析）。
    /// </summary>
    public string RawInput { get; init; } = string.Empty;
}
