namespace ParrotCode;

/// <summary>
/// AgentLoop 产出的事件单元。事件流解耦生产（AgentLoop）与展示（控制台/TUI）。
/// 12 种类型覆盖一轮 ReAct 的完整生命周期 + 控制信号。
/// 本迭代消费者是控制台打印；迭代 7 替换为 Spectre TUI 渲染器。
/// </summary>
public abstract record AgentEvent
{
    /// <summary>
    /// 新一轮 ReAct 开始。Round 是 1-based 轮次号。
    /// </summary>
    public sealed record RoundStartEvent(int Round) : AgentEvent;

    /// <summary>
    /// 文本增量（与 ChatChunk.TextDelta 对应，转发给消费者实时展示）。
    /// </summary>
    public sealed record TextDeltaEvent(string Text) : AgentEvent;

    /// <summary>
    /// 本轮 LLM 完整回复（流式结束后产出，便于消费者做"整段刷新"渲染）。
    /// </summary>
    public sealed record AssistantMessageEvent(string Content) : AgentEvent;

    /// <summary>
    /// 工具调用开始（LLM 决定调工具）。Call 含 Id/Name/Input。
    /// </summary>
    public sealed record ToolCallStartEvent(ToolCall Call) : AgentEvent;

    /// <summary>
    /// 工具执行结果。Call + Result 配对，消费者可展示"调用 X → 成功/失败"。
    /// </summary>
    public sealed record ToolResultEvent(ToolCall Call, ToolResult Result) : AgentEvent;

    /// <summary>
    /// 工具被拦截（迭代 8 安全层 / 迭代 7 HITL 拒绝）。
    /// 本迭代主路径不产生——BatchToolExecutor 不做安全检查。
    /// 预留事件类型，迭代 7/8 接入时填充。
    /// </summary>
    public sealed record ToolBlockedEvent(ToolCall Call, string Reason) : AgentEvent;

    /// <summary>
    /// 本轮 ReAct 结束。Round 与 RoundStartEvent 对应。
    /// </summary>
    public sealed record RoundEndEvent(int Round) : AgentEvent;

    /// <summary>
    /// Agent 完成（LLM 不再调工具，输出最终回复）。FinalText 为最终文本（可能为空）。
    /// </summary>
    public sealed record AgentDoneEvent(string? FinalText) : AgentEvent;

    /// <summary>
    /// 达到最大轮次，Agent 被强制停止。Rounds 为实际执行的轮次数。
    /// </summary>
    public sealed record MaxRoundsReachedEvent(int Rounds) : AgentEvent;

    /// <summary>
    /// 非致命警告（如某工具超时但 Agent 继续）。Message 为人类可读原因。
    /// </summary>
    public sealed record WarningEvent(string Message) : AgentEvent;

    /// <summary>
    /// 致命错误（如 Provider 401 / 网络断开）。Agent 终止。
    /// </summary>
    public sealed record ErrorEvent(string Message, Exception? Exception) : AgentEvent;

    /// <summary>
    /// 用户取消（Ctrl+C）。Agent 优雅停止。
    /// </summary>
    public sealed record CancelledEvent : AgentEvent;
}
