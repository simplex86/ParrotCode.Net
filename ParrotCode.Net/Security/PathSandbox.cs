using System.IO;

namespace ParrotCode;

/// <summary>
/// 路径沙箱（迭代 8a）。
/// 规范化 + 白名单子树检查 + .. 越界检测，按 SecurityLevel 收紧：
/// - Strict: 路径必须在白名单根（ProjectRoot + AllowPaths）子树内，否则拒。
/// - Normal: .. 跳出白名单根的检测；白名单外的绝对路径放行（靠 Write HITL 询问）。
/// - Permissive: 不检查路径。
/// 跨平台：Windows 路径大小写不敏感（OrdinalIgnoreCase），Unix 敏感（Ordinal）。
/// 不解析符号链接（避免 TOCTOU 竞态）。
/// </summary>
public sealed class PathSandbox
{
    private readonly SecurityContext _ctx;
    private readonly StringComparison _pathComparison;

    public PathSandbox(SecurityContext context)
    {
        _ctx = context ?? throw new ArgumentNullException(nameof(context));
        _pathComparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase
                                                      : StringComparison.Ordinal;
    }

    /// <summary>
    /// 检查路径是否允许。
    /// rawPath 是工具参数原始值（可能是相对/绝对/含 ..）。
    /// </summary>
    internal PathCheckResult Check(string? rawPath, SecurityLevel level)
    {
        // Permissive：路径不检查（仅黑名单生效，在 SecurityGuard 层）
        if (level == SecurityLevel.Permissive)
            return new PathCheckResult(PathCheckResultKind.Allowed);

        // 空路径交给工具自身报错
        if (string.IsNullOrWhiteSpace(rawPath))
            return new PathCheckResult(PathCheckResultKind.Allowed);

        var normalized = TryNormalize(rawPath);
        if (normalized is null)
            return new PathCheckResult(PathCheckResultKind.Allowed);  // 非法路径交工具报错

        // 1. DenyPaths（最高优先级，所有非 Permissive 档位生效）
        if (IsInPaths(normalized, _ctx.DenyPaths))
            return new PathCheckResult(PathCheckResultKind.DeniedExplicit, $"路径在 DenyPaths 中：{normalized}");

        // 2. .. 越界检测（Normal + Strict）：rawPath 含 .. 且规范化后跳出所有白名单根
        if (rawPath.Contains("..") && !IsWithinAnyRoot(normalized))
            return new PathCheckResult(PathCheckResultKind.DeniedTraversal, $".. 遍历跳出项目根：{rawPath} → {normalized}");

        // 3. Strict 白名单：路径必须在白名单子树内
        if (level == SecurityLevel.Strict && !IsWithinAnyRoot(normalized))
            return new PathCheckResult(PathCheckResultKind.DeniedSandbox, $"Strict 模式：路径不在白名单内：{normalized}");

        return new PathCheckResult(PathCheckResultKind.Allowed);
    }

    /// <summary>
    /// 规范化为绝对路径（解析 . 和 ..，基于 ProjectRoot）。
    /// 不解析符号链接（避免 TOCTOU）。非法路径返回 null（Check 放行交工具报错）。
    /// </summary>
    private string? TryNormalize(string rawPath)
    {
        try
        {
            return Path.GetFullPath(rawPath, _ctx.ProjectRoot);
        }
        catch (Exception)
        {
            // 非法路径（含非法字符等）交给工具自身报错，沙箱放行
            return null;
        }
    }

    /// <summary>
    /// 路径是否在任一白名单根子树内。
    /// </summary>
    private bool IsWithinAnyRoot(string normalizedPath)
    {
        foreach (var root in GetAllRoots())
        {
            if (IsSameOrUnder(normalizedPath, root))
                return true;
        }
        return false;
    }

    /// <summary>
    /// 路径是否在指定的路径列表任一子树内。
    /// </summary>
    private bool IsInPaths(string normalizedPath, IReadOnlyList<string> paths)
    {
        foreach (var p in paths)
        {
            var normalizedP = TryNormalize(p);
            if (normalizedP is null) continue;
            if (IsSameOrUnder(normalizedPath, normalizedP))
                return true;
        }
        return false;
    }

    private IEnumerable<string> GetAllRoots()
    {
        yield return _ctx.ProjectRoot;
        foreach (var p in _ctx.AllowPaths)
            yield return p;
    }

    /// <summary>
    /// child 是否等于或位于 parent 目录下（按操作系统大小写规则）。
    /// </summary>
    private bool IsSameOrUnder(string child, string parent)
    {
        if (string.Equals(child, parent, _pathComparison))
            return true;

        // 确保是目录子树匹配（parent + 分隔符前缀），防 /home/user-evil 误判为 /home/user 子树
        var prefix = parent.EndsWith(Path.DirectorySeparatorChar) ? parent
                                                                  : parent + Path.DirectorySeparatorChar;
        return child.StartsWith(prefix, _pathComparison);
    }
}
