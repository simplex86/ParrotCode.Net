namespace ParrotCode;

/// <summary>
/// 工具执行结果。无论成功失败都返回 ToolResult，不抛异常（异常由 ToolExecutor 捕获转译）。
/// Success=true 时 Content 含结果文本，Error 为 null。
/// Success=false 时 Error 含人类可读错误原因（会回灌给 LLM），Content 通常为空。
/// </summary>
public sealed record ToolResult(bool Success, string Content, string? Error = null)
{
    /// <summary>
    /// 便捷构造：成功结果。
    /// </summary>
    public static ToolResult Ok(string content) => new(true, content, null);

    /// <summary>
    /// 便捷构造：失败结果。
    /// </summary>
    public static ToolResult Fail(string error) => new(false, string.Empty, error);
}
