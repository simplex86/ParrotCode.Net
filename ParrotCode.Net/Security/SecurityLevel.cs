namespace ParrotCode;

/// <summary>
/// 安全等级枚举。迭代 8a 从 Tui/ 迁移到 Security/，修正拼写 Permisive → Permissive。
/// - Strict: 只允许白名单路径（项目根子树）的读写；白名单外拒绝。
/// - Normal: 读放行、写询问（HITL）；.. 越界拦截。
/// - Permissive: 仅黑名单拦截；路径不检查。
/// 黑名单始终生效（不依赖档位）。
/// </summary>
public enum SecurityLevel
{
    /// <summary>严格：只允许白名单路径读写。</summary>
    Strict,

    /// <summary>普通：读放行、写询问。默认值。</summary>
    Normal,

    /// <summary>宽松：仅黑名单拦截。</summary>
    Permissive
}

/// <summary>
/// SecurityLevel 解析器（迭代 8a）。
/// 大小写不敏感；兼容 7a/7b 旧拼法 "permisive"；未配置或无效值默认 Normal。
/// </summary>
public static class SecurityLevelParser
{
    /// <summary>解析安全等级字符串。</summary>
    public static SecurityLevel Parse(string? level) => level?.ToLowerInvariant() switch
    {
        "strict" => SecurityLevel.Strict,
        "permissive" or "permisive" => SecurityLevel.Permissive,  // 兼容 7a/7b 旧拼法
        _ => SecurityLevel.Normal
    };
}
