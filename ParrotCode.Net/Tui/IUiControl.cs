namespace ParrotCode;

/// <summary>
/// UI 抽象接口（迭代 7a 最小定义，迭代 7b 加 RequestHitlAsync 签名，迭代 10 命令系统通过它调用 UI）。
/// 让命令系统不直接耦合 TuiApp，便于替换 UI 实现。
/// 迭代 7a 只含 PrintMessageAsync + SetStatus。
/// 迭代 7b 加 RequestHitlAsync 签名（预留，TuiApp 不实现此接口——7b 无命令系统调用方）。
/// 迭代 10 命令系统通过此方法触发 HITL（如 /approve 命令）。
/// </summary>
public interface IUiControl
{
    /// <summary>
    /// 打印一条消息（用户可见）。
    /// </summary>
    Task PrintMessageAsync(string message, CancellationToken ct);

    /// <summary>
    /// 更新状态栏字段（通用 key-value，如 "security" → "Strict"）。
    /// </summary>
    void SetStatus(string key, string value);

    /// <summary>
    /// 请求 HITL 决策（委托 IHitlGate）。7b 加签名预留，TuiApp 不实现此接口（7b 无命令系统调用方）。
    /// 迭代 10 命令系统通过此方法触发 HITL（如 /approve 命令）。
    /// </summary>
    Task<HitlDecision?> RequestHitlAsync(ToolCall call, CancellationToken ct);
}
