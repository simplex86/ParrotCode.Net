namespace ParrotCode;

/// <summary>
/// 消息相关扩展方法：Provider 角色映射 + token 估算便捷方法。
/// 将 OpenAIProvider 中内联的角色映射提取为可复用方法，为后续 AnthropicProvider 建立模式。
/// </summary>
public static class MessageExtensions
{
    /// <summary>
    /// 将 MessageRole 映射为 OpenAI 协议的角色字符串。
    /// OpenAI / DeepSeek 等 OpenAI 兼容服务通用。
    /// </summary>
    public static string ToOpenAiRoleString(this MessageRole role) => role switch
    {
        MessageRole.System => "system",
        MessageRole.User => "user",
        MessageRole.Assistant => "assistant",
        MessageRole.Tool => "tool",
        _ => "user"  // 未知角色兜底为 user，不抛异常（容错优先）
    };

    /// <summary>
    /// 估算单条消息的 token 数（便捷扩展，委托 TokenEstimator）。
    /// </summary>
    public static int EstimateTokens(this Message message) => TokenEstimator.Estimate(message);

    /// <summary>
    /// 估算消息列表的总 token 数（便捷扩展，委托 TokenEstimator）。
    /// </summary>
    public static int EstimateTokens(this IReadOnlyList<Message> messages) => TokenEstimator.Estimate(messages);
}
