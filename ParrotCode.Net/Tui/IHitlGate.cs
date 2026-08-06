namespace ParrotCode;

/// <summary>
/// HITL（人在回路）双向通道抽象。迭代 7b 引入。
/// BatchToolExecutor 在 Write 组工具执行前调用 RequestAsync 请求用户决策；
/// TUI 实现（HitlPrompt）弹框收集用户选择并完成返回的 Task。
///
/// 与 IAgentEventSink 的区别：
/// - IAgentEventSink 是单向 fire-and-forget（WriteAsync 返回 ValueTask，不携带返回值）。
/// - IHitlGate 是双向请求/响应（RequestAsync 返回 Task&lt;HitlDecision?&gt;）。
/// 把需要返回值的交互独立成接口，保持事件流纯展示通知语义。
///
/// 返回 nullable HitlDecision?：
/// - null 表示"无需 HITL"（Read 工具或缓存命中），调用方直接执行。
/// - 非 null 表示用户已决策（AllowOnce/AllowSession/AllowPermanent/Deny）。
/// 用 nullable 区分"未询问"与"询问结果"，避免 Deny 被误判为"未询问"。
///
/// 7b 只有一个实现（HitlPrompt，方案 C）。
/// 测试用 NullHitlGate（直接 null）与假 IHitlGate（返回预设 Decision）。
/// 迭代 8 SecurityGuard 可作为前置拦截器（在 IHitlGate 之前，通过 OnBeforeExecuteAsync hook）。
/// </summary>
public interface IHitlGate
{
    /// <summary>
    /// 请求用户对某次工具调用的决策。
    /// 实现应阻塞（await）直到用户响应；null 表示无需询问（如 Read 工具或缓存命中）。
    /// 调用方（BatchToolExecutor）await 此方法，期间 AgentLoop 暂停。
    /// </summary>
    /// <param name="call">待执行的工具调用。</param>
    /// <param name="cancellationToken">取消令牌（用户 Ctrl+C 时取消等待）。</param>
    /// <returns>用户决策；null 表示无需 HITL（直接执行）。</returns>
    Task<HitlDecision?> RequestAsync(ToolCall call, CancellationToken cancellationToken);

    /// <summary>
    /// 查询某工具是否已被会话级允许（避免重复弹框）。
    /// BatchToolExecutor 在调 RequestAsync 前可先查缓存（RequestAsync 内部也查，双重保险）。
    /// </summary>
    bool IsAllowedThisSession(string toolName);
}

/// <summary>
/// 默认放行（无 HITL）。用于配置 enable_hitl: false 或终端非交互时。
/// 等价于迭代 7a 的行为——所有工具直接执行。
/// </summary>
public sealed class NullHitlGate : IHitlGate
{
    /// <summary>默认无 HITL：返回 null 表示"无需询问，直接执行"。</summary>
    public Task<HitlDecision?> RequestAsync(ToolCall call, CancellationToken cancellationToken) =>
        Task.FromResult<HitlDecision?>(null);

    /// <summary>无缓存：恒 false。</summary>
    public bool IsAllowedThisSession(string toolName) => false;
}
