using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace ParrotCode;

/// <summary>
/// 固定回显 Provider，用于在接入真实 LLM 前跑通管线。
/// 取最后一条 user 消息的 Content，返回 "{content}（mock）"，便于一眼区分 mock 与真实回复。
/// 迭代 6 扩展：支持脚本队列，注入预设 ChatChunk 序列模拟 LLM 的 tool_call 响应，
/// 让无 LLM 环境下也能测试 AgentLoop 的 ReAct 闭环。
/// 默认行为（无脚本时）保持迭代 3 的回显语义，既有测试不回归。
/// </summary>
public sealed class MockProvider : IBaseProvider
{
    private readonly ConcurrentQueue<IReadOnlyList<ChatChunk>> _scripts = new();

    /// <summary>
    /// 注入一段脚本（对应 AgentLoop 的一轮 LLM 调用）。
    /// AgentLoop 每轮调 ChatStreamAsync 时出队一段脚本并按序产出。
    /// 脚本耗尽后回退到默认回显行为。
    /// </summary>
    public void EnqueueScript(params ChatChunk[] chunks) =>
        _scripts.Enqueue(chunks);

    // —— 旧 ChatAsync / ChatStreamAsync(string) 保留，行为不变 ——

    public Task<string> ChatAsync(IReadOnlyList<Message> messages, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var lastUser = messages.LastOrDefault(m => m.Role == MessageRole.User);
        var content = lastUser?.Content ?? string.Empty;
        return Task.FromResult($"{content}（mock）");
    }

    public async IAsyncEnumerable<string> ChatStreamAsync(
        IReadOnlyList<Message> messages,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var lastUser = messages.LastOrDefault(m => m.Role == MessageRole.User);
        var content = lastUser?.Content ?? string.Empty;
        yield return $"{content}（mock）";
        await Task.CompletedTask;
    }

    // —— 新增：带 tools 的流式 ——

    public async IAsyncEnumerable<ChatChunk> ChatStreamAsync(
        IReadOnlyList<Message> messages,
        JsonElement? tools,
        string toolChoice,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (_scripts.TryDequeue(out var script))
        {
            var hasDone = false;
            foreach (var chunk in script)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return chunk;
                if (chunk is ChatChunk.Done) hasDone = true;
            }
            if (!hasDone) yield return new ChatChunk.Done();
        }
        else
        {
            // 无脚本：回退到回显（与旧 ChatStreamAsync 一致，但包装成 ChatChunk）
            var lastUser = messages.LastOrDefault(m => m.Role == MessageRole.User);
            var content = lastUser?.Content ?? string.Empty;
            yield return new ChatChunk.TextDelta($"{content}（mock）");
            yield return new ChatChunk.Done();
        }
        await Task.CompletedTask;
    }
}
