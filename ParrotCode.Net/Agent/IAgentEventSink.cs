namespace ParrotCode;

/// <summary>
/// 事件消费者抽象。AgentLoop 写入 sink，App/TUI 从 sink 读取。
/// 本迭代只有 ChannelEventSink 一个实现；迭代 7 可加 TuiEventSink 直接渲染（不走 Channel）。
/// </summary>
public interface IAgentEventSink
{
    /// <summary>
    /// 写入事件。不阻塞——Channel 写入通常立即返回（无界通道）。
    /// </summary>
    ValueTask WriteAsync(AgentEvent evt, CancellationToken cancellationToken);

    /// <summary>
    /// 标记事件流结束。AgentLoop 退出前调用，让消费者的 ReadAllAsync 自然结束。
    /// </summary>
    void Complete();
}
