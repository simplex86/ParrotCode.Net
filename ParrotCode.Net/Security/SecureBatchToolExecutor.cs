using Microsoft.Extensions.Logging;

namespace ParrotCode;

/// <summary>
/// BatchToolExecutor 子类（迭代 8b）：注入 SecurityGuard，覆写 OnBeforeExecuteAsync。
/// 安全层在 HITL 之前执行（基类入口预扫描已统一对所有 calls 调 OnBeforeExecuteAsync）。
/// 安全层拒绝时不问用户（避免打扰已拦截的操作）；HITL 由基类在 Write 组执行阶段触发。
/// </summary>
public sealed class SecureBatchToolExecutor : BatchToolExecutor
{
    private readonly SecurityGuard _guard;

    public SecureBatchToolExecutor(
        ToolExecutor executor,
        ToolRegistry registry,
        SecurityGuard guard,
        int maxParallelism = 5,
        IHitlGate? hitlGate = null,
        ILogger? logger = null)
        : base(executor, registry, maxParallelism, hitlGate, logger)
    {
        _guard = guard ?? throw new ArgumentNullException(nameof(guard));
    }

    /// <summary>
    /// 委托 SecurityGuard.CheckAsync 做三层安全检查（黑名单 → 沙箱 → 策略）。
    /// 基类入口预扫描会对此方法对每个 call 调一次（含 Read 组）。
    /// </summary>
    protected override async Task<ToolResult?> OnBeforeExecuteAsync(ToolCall call, CancellationToken ct) => await _guard.CheckAsync(call, ct);
}
