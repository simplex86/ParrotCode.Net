namespace ParrotCode;

/// <summary>
/// 三档模式策略评估（迭代 8b）。
/// 决策矩阵：
/// - Strict:  Write 非白名单路径 → 沙箱层已拦；白名单内 Write → 放行交 HITL。
/// - Normal:  Write → 放行交 HITL（BatchToolExecutor 在 Write 组调 IHitlGate）；Read → 放行。
/// - Permissive: 全部放行（仅黑名单拦，黑名单在更早层）。
/// 策略层不直接弹 HITL——HITL 由 BatchToolExecutor 的 Write 组逻辑触发。
/// 本迭代 Evaluate 默认放行，保持管线完整性，为迭代 10 /mode 切换与未来细粒度策略留接口。
/// </summary>
public sealed class SecurityPolicy
{
    private readonly PathSandbox _sandbox;

    public SecurityPolicy(PathSandbox sandbox) => _sandbox = sandbox ?? throw new ArgumentNullException(nameof(sandbox));

    /// <summary>
    /// 评估是否拦截。null=放行；ToolResult.Fail=拦截。
    /// 不返回"需询问"——询问由 BatchToolExecutor 在 Write 组调 IHitlGate 触发。
    /// 当前设计下，沙箱层已覆盖 Strict 的白名单检查，策略层无需重复。
    /// 预留扩展点：Strict 下禁止 run_command、Strict 下 Write 二次确认等。
    /// </summary>
    public ToolResult? Evaluate(ToolCall call, SecurityLevel level) => null;
}
