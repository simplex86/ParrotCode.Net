using System.Text.RegularExpressions;

namespace ParrotCode;

/// <summary>
/// 安全检查上下文：传给 SecurityGuard 的环境信息（迭代 8a 引入）。
/// 不可变快照（构造时确定）；运行时切换档位通过 SecurityGuard.Level 属性（迭代 8b/8c）。
/// </summary>
public sealed record SecurityContext
{
    /// <summary>
    /// 项目根目录（白名单默认根），规范化绝对路径。
    /// </summary>
    public required string ProjectRoot { get; init; }

    /// <summary>
    /// 额外允许的路径白名单（规范化绝对路径）。
    /// </summary>
    public IReadOnlyList<string> AllowPaths { get; init; } = Array.Empty<string>();

    /// <summary>
    /// 显式拒绝的路径黑名单（规范化绝对路径，优先级最高）。
    /// </summary>
    public IReadOnlyList<string> DenyPaths { get; init; } = Array.Empty<string>();

    /// <summary>
    /// 额外黑名单命令模式（正则字符串，与硬编码黑名单合并）。
    /// </summary>
    public IReadOnlyList<string> ExtraBlacklist { get; init; } = Array.Empty<string>();
}

/// <summary>
/// 路径检查结果类型。
/// </summary>
internal enum PathCheckResultKind
{
    /// <summary>
    /// 放行。
    /// </summary>
    Allowed,

    /// <summary>
    /// 路径越界（跳出白名单根，Strict 模式）。
    /// </summary>
    DeniedSandbox,

    /// <summary>
    /// .. 遍历越界。
    /// </summary>
    DeniedTraversal,

    /// <summary>
    /// 命中 DenyPaths（显式拒绝）。
    /// </summary>
    DeniedExplicit
}

/// <summary>
/// 路径检查结果。Detail 为人类可读的拒绝原因（回灌 LLM）。
/// </summary>
internal sealed record PathCheckResult(PathCheckResultKind Kind, string? Detail = null)
{
    public bool IsAllowed => Kind == PathCheckResultKind.Allowed;
}

/// <summary>
/// 黑名单规则（正则 + 拒绝原因）。
/// </summary>
internal sealed record BlacklistRule(Regex Pattern, string Reason);

/// <summary>
/// 黑名单命中结果。Reason 会回灌给 LLM。
/// </summary>
public sealed record BlacklistHit(string Reason);
