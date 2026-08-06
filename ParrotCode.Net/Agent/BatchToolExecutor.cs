using Microsoft.Extensions.Logging;

namespace ParrotCode;

/// <summary>
/// 分批工具执行器：按 ToolCategory 分组调度。
/// Read 组（幂等、无副作用）用 Task.WhenAll 并发，限流到 maxParallelism 防止 OOM；
/// Write 组（有副作用）顺序 await 避免竞态，迭代 7b 注入 IHitlGate? 在执行前询问用户。
/// 委托迭代 5 ToolExecutor 做单次执行（超时 + 异常捕获）。
/// 迭代 8 SecurityGuard 作为 OnBeforeExecuteAsync hook 接入（7b 预留默认返回 null）。
///
/// 7b 改造点：
/// - 构造加可选参数 IHitlGate? hitlGate = null（null 时等价 7a）。
/// - Write 组执行前调 hitlGate.RequestAsync，Deny 时返回 ToolResult.Fail 不执行。
/// - 预留 OnBeforeExecuteAsync 虚方法给迭代 8 SecurityGuard。
/// </summary>
public class BatchToolExecutor
{
    private readonly ToolExecutor _executor;
    private readonly ToolRegistry _registry;
    private readonly int _maxParallelism;
    private readonly IHitlGate? _hitlGate;  // 7b 新增（null 时等价 7a）
    private readonly ILogger? _logger;

    public BatchToolExecutor(
        ToolExecutor executor,
        ToolRegistry registry,
        int maxParallelism = 5,
        IHitlGate? hitlGate = null,  // 7b 新增（可选，null 时不问）
        ILogger? logger = null)
    {
        _executor = executor ?? throw new ArgumentNullException(nameof(executor));
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        if (maxParallelism < 1) throw new ArgumentOutOfRangeException(nameof(maxParallelism));
        _maxParallelism = maxParallelism;
        _hitlGate = hitlGate;
        _logger = logger;
    }

    /// <summary>
    /// 分批执行工具调用列表，返回与输入同序的结果列表。
    /// 流程：按 Category 分组 → Read 并发（分批限流）→ Write 串行（含 HITL 询问）→ 按原序合并。
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

        // Read 组并发（分批限流）——幂等无副作用，不问 HITL（与 7a 一致）
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

        // Write 组串行 + HITL（7b 新增）
        foreach (var i in writeIndices)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var call = calls[i];

            // 1. OnBeforeExecuteAsync hook（迭代 8 SecurityGuard 接入点，7b 默认返回 null）
            var blocked = await OnBeforeExecuteAsync(call, cancellationToken);
            if (blocked is not null)
            {
                results[i] = blocked;
                _logger?.LogInformation("工具 {Name} 被安全层拦截", call.Name);
                continue;
            }

            // 2. HITL 请求（7b 新增，hitlGate 非 null 时）
            if (_hitlGate is not null)
            {
                var decision = await _hitlGate.RequestAsync(call, cancellationToken);
                // null 表示无需询问（理论上 HitlPrompt 不会返回 null，但接口允许）
                // Deny → 返回 Fail 不执行；AllowOnce/AllowSession/AllowPermanent → 继续执行
                if (decision is { IsAllowed: false })
                {
                    results[i] = ToolResult.Fail(decision.Reason ?? "用户拒绝执行");
                    _logger?.LogInformation("HITL 拒绝工具 {Name}", call.Name);
                    continue;
                }
            }

            // 3. 执行工具
            results[i] = await _executor.ExecuteAsync(call, cancellationToken);
        }

        return results;
    }

    /// <summary>
    /// 迭代 8 接入点：SecurityGuard 覆写此方法返回 ToolResult.Fail 拦截。
    /// 7b 默认实现返回 null（不拦截）。预留虚方法供迭代 8 子类化或委托。
    /// 顺序：OnBeforeExecuteAsync（安全层）→ HITL（用户决策）→ 执行。
    /// 安全层拒绝时不问用户（避免打扰已拦截的操作）。
    /// </summary>
    protected virtual Task<ToolResult?> OnBeforeExecuteAsync(ToolCall call, CancellationToken ct) =>
        Task.FromResult<ToolResult?>(null);
}
