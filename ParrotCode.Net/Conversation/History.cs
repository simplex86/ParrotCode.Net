namespace ParrotCode;

/// <summary>
/// 对话历史管理：维护有序消息列表，支持多轮上下文。
/// 不含 system prompt（由后续迭代的 PromptBuilder 在调用前拼装）。
/// 本迭代为内存版，持久化在迭代 10。
/// </summary>
public sealed class ConversationHistory
{
    private readonly List<Message> _messages = new();

    /// <summary>
    /// 当前历史消息数（不含 system prompt）。
    /// </summary>
    public int Count => _messages.Count;

    /// <summary>
    /// 估算的全部历史 token 数（字符数 / 3 近似）。
    /// </summary>
    public int EstimatedTokens => TokenEstimator.Estimate(_messages);

    /// <summary>
    /// 追加 user 消息。
    /// </summary>
    public void AddUser(string content)
    {
        ArgumentNullException.ThrowIfNull(content);
        _messages.Add(new Message(MessageRole.User, content));
    }

    /// <summary>
    /// 追加 assistant 消息（AI 的完整回复）。
    /// </summary>
    public void AddAssistant(string content)
    {
        ArgumentNullException.ThrowIfNull(content);
        _messages.Add(new Message(MessageRole.Assistant, content));
    }

    /// <summary>
    /// 追加携带工具调用的 assistant 消息（ReAct 循环中 LLM 决定调工具时用）。
    /// Content 可能为空（LLM 只调工具不输出文本），ToolCalls 不能为空。
    /// </summary>
    public void AddAssistant(string content, IReadOnlyList<ToolCall> toolCalls)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(toolCalls);
        if (toolCalls.Count == 0)
            throw new ArgumentException("toolCalls 不能为空（无工具调用请用 AddAssistant(content))", nameof(toolCalls));
        _messages.Add(new Message(MessageRole.Assistant, content) { ToolCalls = toolCalls });
    }

    /// <summary>
    /// 追加 tool 消息（工具执行结果）。
    /// 本迭代定义但不使用；迭代 5/6 接入工具后启用。
    /// </summary>
    public void AddTool(string content)
    {
        ArgumentNullException.ThrowIfNull(content);
        _messages.Add(new Message(MessageRole.Tool, content));
    }

    /// <summary>
    /// 追加 tool 消息（工具执行结果）并关联 tool_call_id。
    /// tool_call_id 必须与触发它的 assistant 消息的 ToolCalls[i].Id 一致——
    /// OpenAI 要求 tool 消息按 tool_call_id 关联，否则报错。
    /// </summary>
    public void AddTool(string content, string toolCallId)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(toolCallId);
        _messages.Add(new Message(MessageRole.Tool, content) { ToolCallId = toolCallId });
    }

    /// <summary>
    /// 追加 system 消息（迭代 9 新增：压缩摘要/边界提示用）。
    /// </summary>
    public void AddSystem(string content)
    {
        ArgumentNullException.ThrowIfNull(content);
        _messages.Add(new Message(MessageRole.System, content));
    }

    /// <summary>
    /// 替换全部消息（迭代 9 新增：压缩后用）。
    /// 供 StructuredSummarizer.SummarizeAsync 调用。
    /// </summary>
    public void ReplaceMessages(IReadOnlyList<Message> messages)
    {
        ArgumentNullException.ThrowIfNull(messages);
        _messages.Clear();
        _messages.AddRange(messages);
    }

    /// <summary>
    /// 返回当前历史的快照，供 Provider 调用使用。
    /// 返回数组快照而非 live view——避免异步 Provider 调用期间历史被修改导致不一致。
    /// </summary>
    public IReadOnlyList<Message> ToProviderMessages()
    {
        return _messages.ToArray();
    }

    /// <summary>
    /// 清空全部历史，重新开始对话。
    /// </summary>
    public void Clear()
    {
        _messages.Clear();
    }
}
