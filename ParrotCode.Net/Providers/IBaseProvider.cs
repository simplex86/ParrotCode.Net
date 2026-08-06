namespace ParrotCode;

/// <summary>
/// 协议无关的 Provider 抽象。替代迭代 1 的临时 IChatProvider。
/// </summary>
public interface IBaseProvider
{
    /// <summary>
    /// 非流式聊天：给定消息列表，返回完整回复。
    /// 用于不需要实时反馈的场景（如迭代 9 摘要）。
    /// </summary>
    Task<string> ChatAsync(IReadOnlyList<Message> messages, CancellationToken cancellationToken);

    /// <summary>
    /// 流式聊天：逐个产出 token（文本片段）。
    /// token 可能是单个字符、词或一段文本——由 Provider/LLM 决定粒度，消费方不应假设。
    /// 迭代 3 仅产出文本 token；迭代 5/6 可能演进返回类型以承载 ToolCall。
    /// </summary>
    IAsyncEnumerable<string> ChatStreamAsync(IReadOnlyList<Message> messages, CancellationToken cancellationToken);
}
