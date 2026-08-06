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
/// ToolCallId 仅 tool 角色消息非空（关联到触发它的 assistant tool_call）。
/// 迭代 6 启用 ToolCalls 与 ToolCallId。
/// </summary>
public sealed record Message(MessageRole Role, string Content)
{
    /// <summary>assistant 消息携带的工具调用（仅 Role=Assistant 时可能非空）。</summary>
    public IReadOnlyList<ToolCall>? ToolCalls { get; init; }

    /// <summary>
    /// tool 角色消息关联的 tool_call_id（OpenAI 要求 tool 消息必须带 tool_call_id
    /// 关联到触发它的 assistant tool_call）。
    /// 迭代 6 启用。
    /// </summary>
    public string? ToolCallId { get; init; }
}

/// <summary>
/// LLM 发起的工具调用。Input 为原始 JSON（保留协议细节，由 Provider 层解释）。
/// 本迭代仅定义，不产生也不执行。
/// </summary>
public sealed record ToolCall(string Id, string Name, JsonElement Input);
