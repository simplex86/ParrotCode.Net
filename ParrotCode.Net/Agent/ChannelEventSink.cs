using System.Threading.Channels;

namespace ParrotCode;

/// <summary>
/// 基于 System.Threading.Channels 的无界通道实现。
/// AgentLoop 写入 Writer，消费者通过 Reader.ReadAllAsync 读取。
/// 无界：AgentLoop 不会因消费者慢而阻塞；代价是消费者跟不上时事件积压内存。
/// 本迭代工具执行快、打印快，无界足够；后续如需背压改 Channel.CreateBounded。
/// </summary>
public sealed class ChannelEventSink : IAgentEventSink
{
    private readonly Channel<AgentEvent> _channel = Channel.CreateUnbounded<AgentEvent>(new UnboundedChannelOptions {
        SingleReader = true,   // 只有 App 一个消费者
        SingleWriter = true    // 只有 AgentLoop 一个生产者
    });

    /// <summary>
    /// 消费者读取端。
    /// App 用 await foreach (evt in sink.Reader.ReadAllAsync(ct))。
    /// </summary>
    public ChannelReader<AgentEvent> Reader => _channel.Reader;

    public ValueTask WriteAsync(AgentEvent evt, CancellationToken cancellationToken) => _channel.Writer.WriteAsync(evt, cancellationToken);

    public void Complete() => _channel.Writer.Complete();
}
