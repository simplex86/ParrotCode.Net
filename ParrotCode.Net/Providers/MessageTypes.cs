using System.Text.Json;

namespace ParrotCode;

/// <summary>
/// 
/// </summary>
public enum MessageRole 
{ 
    /// <summary>
    /// 
    /// </summary>
    System,
    /// <summary>
    /// 
    /// </summary>
    User, 
    /// <summary>
    /// 
    /// </summary>
    Assistant, 
    /// <summary>
    /// 
    /// </summary>
    Tool 
}

/// <summary>
/// 协议中性的消息。Content 为文本；ToolCalls 仅 assistant 消息可能非空。
/// 本迭代仅用到 Role=User + Content；ToolCalls 字段为迭代 5/6 预留。
/// </summary>
public sealed record Message(MessageRole Role, string Content)
{
    public IReadOnlyList<ToolCall>? ToolCalls { get; init; }
}

/// <summary>
/// LLM 发起的工具调用。Input 为原始 JSON（保留协议细节，由 Provider 层解释）。
/// 本迭代仅定义，不产生也不执行。
/// </summary>
public sealed record ToolCall(string Id, string Name, JsonElement Input);
