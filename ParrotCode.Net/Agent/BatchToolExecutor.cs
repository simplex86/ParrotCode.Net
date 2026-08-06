using Microsoft.Extensions.Logging;

namespace ParrotCode;

/// <summary>
/// 分批工具执行器：按 ToolCategory 分组调度。
/// Read 组（幂等、无副作用）用 Task.WhenAll 并发，限流到 maxParallelism 防止 OOM；
/// Write 组（有副作用）顺序 await 避免竞态。
/// 委托迭代 5 ToolExecutor 做单次执行（超时 + 异常捕获）。
/// 本迭代不接安全层——迭代 8 SecurityGuard 作为 OnBeforeExecuteAsync hook 接入。
/// </summary>
public sealed class BatchToolExecutor
{
    private readonly ToolExecutor _executor;
    private readonly ToolRegistry _registry;
    private readonly int _maxParallelism;
    private readonly ILogger? _logger;

    public BatchToolExecutor(ToolExecutor executor, ToolRegistry registry, int maxParallelism = 5, ILogger? logger = null)
    {
        _executor = executor ?? throw new ArgumentNullException(nameof(executor));
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        if (maxParallelism < 1) throw new ArgumentOutOfRangeException(nameof(maxParallelism));
        _maxParallelism = maxParallelism;
        _logger = logger;
    }

    /// <summary>
    /// 分批执行工具调用列表，返回与输入同序的结果列表。
    /// 流程：按 Category 分组 → Read 并发（分批限流）→ Write 串行 → 按原序合并。
    /// 任何工具失败不中断同批其他工具——失败原因作为 ToolResult.Fail 回灌给 LLM 自我修正。
    /// </summary>
    public async Task<IReadOnlyList<ToolResult>> ExecuteAsync(IReadOnlyList<ToolCall> calls, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(calls);
        if (calls.Count == 0) return Array.Empty<ToolResult>();

        cancellationToken.ThrowIfCancellationRequested();

        // 按 Category 分组，保留原始索引以便最后按原序合并
        var readIndices = new List<int>();
        var writeIndices = new List<int>();
        for (var i = 0; i < calls.Count; i++)
        {
            var tool = _registry.Get(calls[i].Name);
            if (tool is null || tool.Category != ToolCategory.Read)
            {
                // 未注册工具或 Write 工具——归到串行组（ToolExecutor 会返回 Fail）
                writeIndices.Add(i);
            }
            else
            {
                readIndices.Add(i);
            }
        }

        var results = new ToolResult[calls.Count];

        // Read 组并发（分批限流）
        foreach (var batch in readIndices.Chunk(_maxParallelism))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var tasks = batch.Select(i => _executor.ExecuteAsync(calls[i], cancellationToken)).ToArray();
            var batchResults = await Task.WhenAll(tasks);
            for (var j = 0; j < batch.Length; j++)
            {
                results[batch[j]] = batchResults[j];
            }
        }

        // Write 组串行
        foreach (var i in writeIndices)
        {
            cancellationToken.ThrowIfCancellationRequested();
            results[i] = await _executor.ExecuteAsync(calls[i], cancellationToken);
        }

        return results;
    }
}
