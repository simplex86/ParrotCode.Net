namespace ParrotCode;

/// <summary>
/// LLM 流式响应的协议中性单元。Provider 层把 OpenAI / Anthropic wire format
/// 翻译成 ChatChunk，AgentLoop 只消费 ChatChunk，不感知协议细节。
/// 迭代 3 的 IAsyncEnumerable&lt;string&gt; 只能承载文本，无法承载 tool_calls——
/// ChatChunk 是其演进：用 union 区分文本增量与工具调用增量。
/// </summary>
public abstract record ChatChunk
{
    /// <summary>
    /// 文本增量（与迭代 3 的 string token 等价）。
    /// LLM 产出的回复文本按片段到达，消费方拼接得到完整回复。
    /// </summary>
    public sealed record TextDelta(string Text) : ChatChunk;

    /// <summary>
    /// 工具调用增量。OpenAI 流式中 tool_calls 按 index 分片到达：
    /// - 首片通常含 Id + Name（arguments 可能空或开始片段）
    /// - 后续片只含 ArgumentsFragment（arguments JSON 字符串的片段）
    /// - 同 index 的多片需累积：Id/Name 取首个非空，Arguments 拼接所有片段
    /// AgentLoop 按 Index 累积，流结束后 Build 成完整 ToolCall。
    /// </summary>
    public sealed record ToolCallDelta(int Index, string? Id, string? Name, string? ArgumentsFragment) : ChatChunk;

    /// <summary>
    /// 流终止标记（OpenAI 的 data: [DONE]）。
    /// 收到此 chunk 后 AgentLoop 停止本轮流式消费，进入 tool_calls 构建阶段。
    /// </summary>
    public sealed record Done : ChatChunk;
}
