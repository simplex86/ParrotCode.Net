namespace ParrotCode;

/// <summary>
/// 协议无关的 Provider 抽象。替代迭代 1 的临时 IChatProvider。
/// 迭代 2a 仅含非流式方法；流式（ChatStreamAsync 返回 IAsyncEnumerable）在迭代 3 加入。
/// </summary>
public interface IBaseProvider
{
    /// <summary>
    /// 非流式聊天：给定消息列表，返回完整回复。
    /// 形参为 IReadOnlyList&lt;Message&gt; 而非单条，让迭代 4（历史）零改动接口。
    /// </summary>
    Task<string> ChatAsync(IReadOnlyList<Message> messages, CancellationToken cancellationToken);
}
