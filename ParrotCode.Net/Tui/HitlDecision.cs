namespace ParrotCode;

/// <summary>
/// HITL（人在回路）决策选项。四键对应 A/S/P/D。
/// 迭代 7b 引入。
/// </summary>
public enum HitlChoice
{
    /// <summary>
    /// 允许本次（A）。下次同工具再问。
    /// </summary>
    AllowOnce,

    /// <summary>
    /// 允许本会话（S）。进程内同工具不再问。
    /// </summary>
    AllowSession,

    /// <summary>
    /// 允许永久（P）。跨会话不再问（7b 退化为会话级，迭代 10 持久化）。
    /// </summary>
    AllowPermanent,

    /// <summary>
    /// 拒绝（D）。ToolResult.Fail 回灌 LLM。
    /// </summary>
    Deny
}

/// <summary>
/// HITL 决策结果。Reason 仅 Deny 时填充（回灌给 LLM 的拒绝原因）。
/// 派生属性 IsAllowed / ShouldCache 让调用方无需重复 switch。
/// </summary>
public sealed record HitlDecision(HitlChoice Choice, string? Reason = null)
{
    /// <summary>
    /// 是否允许执行（非 Deny 即允许）。
    /// </summary>
    public bool IsAllowed => Choice != HitlChoice.Deny;

    /// <summary>
    /// 是否应缓存（会话或持久级）。
    /// </summary>
    public bool ShouldCache => Choice is HitlChoice.AllowSession or HitlChoice.AllowPermanent;

    /// <summary>
    /// 拒绝静态工厂：强制带原因（回灌 LLM）。
    /// </summary>
    public static HitlDecision Deny(string reason) => new(HitlChoice.Deny, reason);

    /// <summary>
    /// 允许本次静态工厂：无 Reason。
    /// </summary>
    public static HitlDecision AllowOnce => new(HitlChoice.AllowOnce);
}
