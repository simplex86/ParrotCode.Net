using System.Runtime.CompilerServices;

namespace ParrotCode;

/// <summary>
/// 固定回显 Provider，用于在接入真实 LLM 前跑通管线。
/// 取最后一条 user 消息的 Content，返回 "{content}（mock）"，便于一眼区分 mock 与真实回复。
/// </summary>
public sealed class MockProvider : IBaseProvider
{
    public Task<string> ChatAsync(IReadOnlyList<Message> messages, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var lastUser = messages.LastOrDefault(m => m.Role == MessageRole.User);
        var content = lastUser?.Content ?? string.Empty;
        return Task.FromResult($"{content}（mock）");
    }

    public async IAsyncEnumerable<string> ChatStreamAsync(IReadOnlyList<Message> messages, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var lastUser = messages.LastOrDefault(m => m.Role == MessageRole.User);
        var content = lastUser?.Content ?? string.Empty;
        // MockProvider 不模拟逐字延迟，一次性产出完整回复。
        // 消费方（App）的 await foreach 逻辑与真实 Provider 一致，验证流式管线正确性。
        yield return $"{content}（mock）";
        await Task.CompletedTask;
    }
}
