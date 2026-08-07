using Terminal.Gui;

namespace ParrotCode;

/// <summary>
/// 对话消息类型。参照 Claude Code 的消息类型扩展。
/// </summary>
internal enum MessageType
{
    User,        // 用户消息（❯ 前缀）
    Assistant,   // 助手回复（⏺ 前缀）
    ToolCall,    // 工具调用（⎿ → 前缀）
    ToolResult,  // 工具结果成功（⎿ ✓ 前缀）
    ToolError,   // 工具失败（⎿ ✗ 前缀）
    System,      // 系统提示（── Round N ── 等）
    Warning,     // 警告（⚠ 前缀）
    Error        // 错误（✗ 前缀）
}

/// <summary>
/// 单条对话消息。包含类型 + 内容，提供格式化和颜色映射。
/// 迭代 7c-2 新增。
/// </summary>
internal sealed record ChatMessage(MessageType Type, string Content)
{
    /// <summary>
    /// 格式化为带前缀的显示字符串。
    /// </summary>
    public string Format() => Type switch
    {
        MessageType.User       => $"❯ {Content}",
        MessageType.Assistant  => $"⏺ {Content}",
        MessageType.ToolCall   => Content,   // 已含 ⎿ 前缀
        MessageType.ToolResult => Content,
        MessageType.ToolError  => Content,
        MessageType.System     => Content,
        MessageType.Warning    => Content,
        MessageType.Error      => Content,
        _ => Content
    };

    /// <summary>
    /// 获取该消息类型的前景色。
    /// </summary>
    public Color GetColor() => Type switch
    {
        MessageType.User       => Color.White,
        MessageType.Assistant  => Color.BrightCyan,
        MessageType.ToolCall   => Color.Cyan,
        MessageType.ToolResult => Color.Green,
        MessageType.ToolError  => Color.Red,
        MessageType.System     => Color.DarkGray,
        MessageType.Warning    => Color.Yellow,
        MessageType.Error      => Color.Red,
        _ => Color.White
    };
}
