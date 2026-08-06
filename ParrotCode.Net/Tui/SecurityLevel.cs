namespace ParrotCode;

/// <summary>
/// 安全等级枚举（迭代 7a 占位，迭代 8 接入真实拦截）。
/// - Strict: 只允许白名单路径读写（迭代 8 实现）。
/// - Normal: 读放行、写询问（HITL，7b 接入）。
/// - Permissive: 仅黑名单拦截（迭代 8）。
/// 迭代 7a 仅状态栏显示，不做真实拦截。7a 硬编码 Normal，无配置项。
/// </summary>
public enum SecurityLevel
{
    /// <summary>严格模式：只允许白名单路径读写（迭代 8 实现）。</summary>
    Strict,

    /// <summary>普通模式：读放行、写询问（HITL，7b 接入）。7a 默认值。</summary>
    Normal,

    /// <summary>宽松模式：仅黑名单拦截（迭代 8）。</summary>
    Permisive
}
