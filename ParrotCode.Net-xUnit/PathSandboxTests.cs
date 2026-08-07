using System.IO;
using ParrotCode;

namespace ParrotCode.xUnit;

/// <summary>
/// PathSandbox 单元测试（迭代 8a）。
/// 覆盖验收标准 08a-22 ~ 08a-34。
/// 跨平台：用 Path.Combine 构造路径；Windows 大小写用例条件跳过。
/// </summary>
public class PathSandboxTests
{
    private static readonly string ProjRoot = Path.Combine(Path.GetTempPath(), "parrotcode-8a-proj");

    private static SecurityContext Ctx(
        string? allowPath = null, string? denyPath = null, string? projectRoot = null) =>
        new()
        {
            ProjectRoot = projectRoot ?? ProjRoot,
            AllowPaths = allowPath is null ? Array.Empty<string>() : new[] { allowPath },
            DenyPaths = denyPath is null ? Array.Empty<string>() : new[] { denyPath },
            ExtraBlacklist = Array.Empty<string>()
        };

    private static string ExternalAbs() =>
        OperatingSystem.IsWindows() ? @"C:\parrotcode-external" : "/parrotcode-external";

    private readonly PathSandbox _sut = new(Ctx());

    [Theory]
    [InlineData("../../../etc/passwd")]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Permissive_AlwaysAllowed(string? rawPath)
    {
        // 08a-22 Permissive：所有路径 Allowed
        var result = _sut.Check(rawPath, SecurityLevel.Permissive);

        result.IsAllowed.Should().BeTrue();
    }

    [Fact]
    public void Strict_WithinProjectRoot_Allowed()
    {
        // 08a-23 Strict：项目根子树内 Allowed
        var path = Path.Combine(ProjRoot, "sub", "file.txt");

        var result = _sut.Check(path, SecurityLevel.Strict);

        result.IsAllowed.Should().BeTrue();
    }

    [Fact]
    public void Strict_OutsideProjectRoot_DeniedSandbox()
    {
        // 08a-24 Strict：项目根外绝对路径 DeniedSandbox
        var result = _sut.Check(ExternalAbs(), SecurityLevel.Strict);

        result.Kind.Should().Be(PathCheckResultKind.DeniedSandbox);
        result.Detail.Should().Contain("Strict");
    }

    [Fact]
    public void Strict_AllowPaths_ExtraRoot_Allowed()
    {
        // 08a-25 Strict：AllowPaths 配置的额外路径 Allowed
        var external = ExternalAbs();
        var sut = new PathSandbox(Ctx(allowPath: external));

        var result = sut.Check(external, SecurityLevel.Strict);

        result.IsAllowed.Should().BeTrue();
    }

    [Fact]
    public void Strict_AllowPaths_Subtree_Allowed()
    {
        // AllowPaths 子树也放行
        var external = ExternalAbs();
        var sut = new PathSandbox(Ctx(allowPath: external));
        var subPath = Path.Combine(external, "sub", "file.txt");

        var result = sut.Check(subPath, SecurityLevel.Strict);

        result.IsAllowed.Should().BeTrue();
    }

    [Fact]
    public void Normal_WithinRoot_DotDotNotEscape_Allowed()
    {
        // 08a-26 Normal：项目根内 sub/../file（含 .. 但未越界）Allowed
        var result = _sut.Check("sub/../file", SecurityLevel.Normal);

        result.IsAllowed.Should().BeTrue();
    }

    [Fact]
    public void Normal_DotDotEscape_DeniedTraversal()
    {
        // 08a-27 Normal：../sibling 跳出项目根 DeniedTraversal
        var result = _sut.Check("../sibling", SecurityLevel.Normal);

        result.Kind.Should().Be(PathCheckResultKind.DeniedTraversal);
        result.Detail.Should().Contain("..");
    }

    [Fact]
    public void Normal_DeepDotDotEscape_DeniedTraversal()
    {
        // ../../../sibling 多级跳出
        var result = _sut.Check("../../../sibling", SecurityLevel.Normal);

        result.Kind.Should().Be(PathCheckResultKind.DeniedTraversal);
    }

    [Fact]
    public void Normal_OutsideAbsolute_Allowed()
    {
        // 08a-28 Normal：项目根外绝对路径 Allowed（交 HITL）
        var result = _sut.Check(ExternalAbs(), SecurityLevel.Normal);

        result.IsAllowed.Should().BeTrue();
    }

    [Theory]
    [InlineData(SecurityLevel.Normal)]
    [InlineData(SecurityLevel.Strict)]
    public void DenyPaths_NonPermissive_DeniedExplicit(SecurityLevel level)
    {
        // 08a-29 DenyPaths 在 Normal/Strict 下 DeniedExplicit
        var denyDir = Path.Combine(ProjRoot, "secret");
        var sut = new PathSandbox(Ctx(denyPath: denyDir));
        var path = Path.Combine(denyDir, "file.txt");

        var result = sut.Check(path, level);

        result.Kind.Should().Be(PathCheckResultKind.DeniedExplicit);
        result.Detail.Should().Contain("DenyPaths");
    }

    [Fact]
    public void DenyPaths_Permissive_Allowed()
    {
        // 08a-30 DenyPaths 在 Permissive 下 Allowed（Permissive 不查路径）
        var denyDir = Path.Combine(ProjRoot, "secret");
        var sut = new PathSandbox(Ctx(denyPath: denyDir));
        var path = Path.Combine(denyDir, "file.txt");

        var result = sut.Check(path, SecurityLevel.Permissive);

        result.IsAllowed.Should().BeTrue();
    }

    [Fact]
    public void Windows_CaseInsensitive_SamePath_Allowed()
    {
        // 08a-31 Windows：大小写不敏感，D:\Proj\file 与 d:\proj\FILE 视为同路径
        if (!OperatingSystem.IsWindows()) return;

        var lowerRoot = ProjRoot.ToLowerInvariant();
        var sut = new PathSandbox(Ctx(projectRoot: lowerRoot));
        var upperPath = ProjRoot.ToUpperInvariant() + Path.DirectorySeparatorChar + "FILE";

        var result = sut.Check(upperPath, SecurityLevel.Strict);

        result.IsAllowed.Should().BeTrue();
    }

    [Fact]
    public void SubtreeBoundary_UserEvil_NotUnderUser()
    {
        // 08a-32 子树边界：user-evil 不误判为 user 子树
        var denyDir = Path.Combine(ProjRoot, "user");
        var sut = new PathSandbox(Ctx(denyPath: denyDir));
        var evilPath = Path.Combine(ProjRoot, "user-evil", "file");

        var result = sut.Check(evilPath, SecurityLevel.Normal);

        // user-evil 不在 user 子树内，不应被 DenyPaths 拦截
        result.IsAllowed.Should().BeTrue();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void EmptyPath_Allowed(string rawPath)
    {
        // 08a-33 空路径 Allowed（交工具报错）
        var result = _sut.Check(rawPath, SecurityLevel.Strict);

        result.IsAllowed.Should().BeTrue();
    }

    [Fact]
    public void NullPath_Allowed()
    {
        var result = _sut.Check(null, SecurityLevel.Strict);

        result.IsAllowed.Should().BeTrue();
    }

    [Fact]
    public void InvalidPathChars_Allowed()
    {
        // 08a-34 非法路径字符（含 \0）规范化失败时 Allowed（交工具报错）
        var result = _sut.Check("foo\0bar", SecurityLevel.Strict);

        result.IsAllowed.Should().BeTrue();
    }

    [Fact]
    public void Strict_ExactProjectRoot_Allowed()
    {
        // 精确等于 ProjectRoot 也算在白名单内
        var result = _sut.Check(ProjRoot, SecurityLevel.Strict);

        result.IsAllowed.Should().BeTrue();
    }
}
