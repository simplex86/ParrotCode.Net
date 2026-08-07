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
///
/// 8b 改造点（入口预扫描，回归风险核心）：
/// - ExecuteAsync 入口对所有 calls 顺序调 OnBeforeExecuteAsync（安全层）。
/// - 拒绝的填入 results 不进分组；放行的进 pending。
/// - Read/Write 分组从 pending 而非 calls 全量；Read 组也过安全层。
/// - Write 组执行阶段不再重复调 OnBeforeExecuteAsync（预扫描已覆盖），直接走 HITL → 执行。
/// - 基类默认 OnBeforeExecuteAsync 返回 null，预扫描全放行，行为等价 7b（7b 测试不受影响）。
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
    /// 流程：入口预扫描（所有 calls 过 OnBeforeExecuteAsync）→ 按 Category 分组（仅 pending）
    ///       → Read 并发（分批限流，无 HITL）→ Write 串行（含 HITL 询问）→ 按原序合并。
    /// 任何工具失败不中断同批其他工具——失败原因作为 ToolResult.Fail 回灌给 LLM 自我修正。
    /// </summary>
    public async Task<IReadOnlyList<ToolResult>> ExecuteAsync(IReadOnlyList<ToolCall> calls, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(calls);
        if (calls.Count == 0) return Array.Empty<ToolResult>();

        cancellationToken.ThrowIfCancellationRequested();

        var results = new ToolResult[calls.Count];
        var pending = new List<int>(calls.Count);

        // 【迭代 8b 改造】入口预扫描：对所有 calls 调 OnBeforeExecuteAsync（安全层）。
        // 拒绝的填入 results 不进入分组；放行的加入 pending。
        // 基类默认 OnBeforeExecuteAsync 返回 null，预扫描全放行，行为等价 7b。
        for (var i = 0; i < calls.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var blocked = await OnBeforeExecuteAsync(calls[i], cancellationToken);
            if (blocked is not null)
            {
                results[i] = blocked;
                _logger?.LogInformation("工具 {Name} 被安全层拦截", calls[i].Name);
            }
            else
            {
                pending.Add(i);
            }
        }

        if (pending.Count == 0)
            return results;

        // 按 Category 分组（只对 pending，保留原始索引以便最后按原序合并）
        var readIndices = new List<int>();
        var writeIndices = new List<int>();
        foreach (var i in pending)
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

        // Read 组并发（分批限流）——无 HITL（安全层已在预扫描跑过）
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

        // Write 组串行 + HITL（迭代 8b：OnBeforeExecuteAsync 已在预扫描跑过，此处不再调）
        foreach (var i in writeIndices)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var call = calls[i];

            // HITL 请求（hitlGate 非 null 时）
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

            // 执行工具
            results[i] = await _executor.ExecuteAsync(call, cancellationToken);
        }

        return results;
    }

    /// <summary>
    /// 迭代 8 接入点：SecurityGuard 覆写此方法返回 ToolResult.Fail 拦截。
    /// 7b 默认实现返回 null（不拦截）。预留虚方法供迭代 8 子类化或委托。
    /// 顺序：OnBeforeExecuteAsync（安全层）→ HITL（用户决策）→ 执行。
    /// 安全层拒绝时不问用户（避免打扰已拦截的操作）。
    /// 8b 改造：此方法在入口预扫描阶段对每个 call 调一次（含 Read 组）；Write 组执行阶段不再调。
    /// </summary>
    protected virtual Task<ToolResult?> OnBeforeExecuteAsync(ToolCall call, CancellationToken ct) =>
        Task.FromResult<ToolResult?>(null);
}
