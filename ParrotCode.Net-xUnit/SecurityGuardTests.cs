using System.IO;
using System.Text.Json;
using ParrotCode;

namespace ParrotCode.xUnit;

/// <summary>
/// SecurityGuard 管线编排单元测试（迭代 8b）。
/// 覆盖验收标准 08b-05 ~ 08b-14：三层顺序、短路、原因前缀、工具类型分发、Level 切换。
/// 纯逻辑测试，无 IO 依赖（路径仅字符串处理，沙箱不读盘）。
/// </summary>
public class SecurityGuardTests
{
    private static readonly string ProjRoot = Path.Combine(Path.GetTempPath(), "parrotcode-8b-proj");

    /// <summary>跨平台的项目根外绝对路径。</summary>
    private static string ExternalAbs() =>
        OperatingSystem.IsWindows() ? @"C:\parrotcode-8b-external" : "/parrotcode-8b-external";

    private static SecurityContext NewCtx() => new()
    {
        ProjectRoot = ProjRoot,
        AllowPaths = Array.Empty<string>(),
        DenyPaths = Array.Empty<string>(),
        ExtraBlacklist = Array.Empty<string>()
    };

    private static ToolCall MakeCall(string name, string argsJson = "{}")
    {
        using var doc = JsonDocument.Parse(argsJson);
        return new ToolCall("id", name, doc.RootElement.Clone());
    }

    private static ToolCall RunCommand(string command, string? args = null)
    {
        var argsJson = args is null
            ? $"{{\"command\":{JsonSerializer.Serialize(command)}}}"
            : $"{{\"command\":{JsonSerializer.Serialize(command)},\"args\":{JsonSerializer.Serialize(args)}}}";
        return MakeCall("run_command", argsJson);
    }

    private static ToolCall WithPath(string toolName, string path)
    {
        var argsJson = $"{{\"path\":{JsonSerializer.Serialize(path)}}}";
        return MakeCall(toolName, argsJson);
    }

    private static ToolCall WithCwd(string toolName, string cwd)
    {
        var argsJson = $"{{\"cwd\":{JsonSerializer.Serialize(cwd)}}}";
        return MakeCall(toolName, argsJson);
    }

    // —— 08b-07 全放行返回 null ——

    [Fact]
    public async Task CheckAsync_AllPass_ReturnsNull()
    {
        // run_command 合法命令无 path：黑名单不命中，沙箱无 path 跳过，策略默认放行
        var sut = new SecurityGuard(NewCtx(), SecurityLevel.Strict);

        var result = await sut.CheckAsync(RunCommand("git", "status"), CancellationToken.None);

        result.Should().BeNull();
    }

    // —— 08b-13 未知工具全放行 ——

    [Fact]
    public async Task CheckAsync_UnknownTool_NoCommandNoPath_ReturnsNull()
    {
        var sut = new SecurityGuard(NewCtx(), SecurityLevel.Strict);

        // 无 command 无 path：黑名单跳过，沙箱跳过，策略放行
        var result = await sut.CheckAsync(MakeCall("unknown_tool"), CancellationToken.None);

        result.Should().BeNull();
    }

    // —— 08b-08 黑名单拦截原因含 [黑名单] 前缀 ——

    [Fact]
    public async Task CheckAsync_BlacklistHit_ErrorContainsPrefix()
    {
        var sut = new SecurityGuard(NewCtx(), SecurityLevel.Permissive);

        // Permissive 模式下黑名单仍生效
        var result = await sut.CheckAsync(RunCommand("rm", "-rf /"), CancellationToken.None);

        result.Should().NotBeNull();
        result!.Success.Should().BeFalse();
        result.Error.Should().StartWith("[黑名单]");
        result.Error.Should().Contain("递归删除根目录");
    }

    // —— 08b-09 沙箱拦截原因含 [路径沙箱] 前缀 ——

    [Fact]
    public async Task CheckAsync_SandboxHit_ErrorContainsPrefix()
    {
        var sut = new SecurityGuard(NewCtx(), SecurityLevel.Strict);

        var result = await sut.CheckAsync(WithPath("read_file", ExternalAbs()), CancellationToken.None);

        result.Should().NotBeNull();
        result!.Success.Should().BeFalse();
        result.Error.Should().StartWith("[路径沙箱]");
    }

    // —— 08b-05 黑名单命中时不调沙箱（短路验证）——

    [Fact]
    public async Task CheckAsync_BlacklistHit_ShortCircuitsSandbox()
    {
        // run_command 命中黑名单 → 返回 [黑名单]
        // 即便 run_command 无 path 参数，沙箱本也不会调；本测试验证返回前缀是 [黑名单] 而非 [路径沙箱]
        // 这是短路的最小验证：黑名单命中后结果只可能含 [黑名单]，沙箱逻辑未参与
        var sut = new SecurityGuard(NewCtx(), SecurityLevel.Strict);

        var result = await sut.CheckAsync(RunCommand("rm", "-rf /"), CancellationToken.None);

        result.Should().NotBeNull();
        result!.Error.Should().StartWith("[黑名单]");
        result.Error.Should().NotContain("[路径沙箱]");
    }

    // —— 08b-06 沙箱命中时不调策略（短路验证）——

    [Fact]
    public async Task CheckAsync_SandboxHit_ShortCircuitsPolicy()
    {
        // read_file 越界 → 沙箱命中，返回 [路径沙箱]
        // 策略层默认放行，若被调用也不会改变结果；本测试验证返回前缀是 [路径沙箱]
        // 即沙箱命中后结果只可能含 [路径沙箱]，策略层未参与改变结果
        var sut = new SecurityGuard(NewCtx(), SecurityLevel.Strict);

        var result = await sut.CheckAsync(WithPath("read_file", ExternalAbs()), CancellationToken.None);

        result.Should().NotBeNull();
        result!.Error.Should().StartWith("[路径沙箱]");
        result.Error.Should().NotContain("[策略]");
    }

    // —— 08b-10 run_command 调黑名单 + 沙箱（无 path 跳过沙箱）——

    [Fact]
    public async Task CheckAsync_RunCommand_NoPath_SandboxSkipped()
    {
        // run_command 仅黑名单匹配，无 path 参数 → 沙箱跳过
        var sut = new SecurityGuard(NewCtx(), SecurityLevel.Strict);

        // 合法命令：黑名单不命中，沙箱无 path 跳过，策略放行
        var result = await sut.CheckAsync(RunCommand("dotnet", "build"), CancellationToken.None);

        result.Should().BeNull("run_command 无 path 时沙箱层跳过，全放行");
    }

    [Fact]
    public async Task CheckAsync_RunCommand_BlacklistHit_Blocked()
    {
        var sut = new SecurityGuard(NewCtx(), SecurityLevel.Permissive);

        // 黑名单命令：即便 Permissive 也拦
        var result = await sut.CheckAsync(RunCommand("rm", "-rf /"), CancellationToken.None);

        result.Should().NotBeNull();
        result!.Success.Should().BeFalse();
    }

    // —— 08b-11 read_file 跳过黑名单，调沙箱 ——

    [Fact]
    public async Task CheckAsync_ReadFile_BlacklistSkipped_SandboxInvoked()
    {
        var sut = new SecurityGuard(NewCtx(), SecurityLevel.Strict);

        // read_file 不是 run_command，黑名单层跳过；path 越界 → 沙箱拦
        var result = await sut.CheckAsync(WithPath("read_file", ExternalAbs()), CancellationToken.None);

        result.Should().NotBeNull();
        result!.Error.Should().StartWith("[路径沙箱]");
    }

    [Fact]
    public async Task CheckAsync_ReadFile_InsideProject_Allowed()
    {
        var sut = new SecurityGuard(NewCtx(), SecurityLevel.Strict);

        // 项目根内路径放行
        var insidePath = Path.Combine(ProjRoot, "file.txt");
        var result = await sut.CheckAsync(WithPath("read_file", insidePath), CancellationToken.None);

        result.Should().BeNull();
    }

    // —— 08b-12 glob/grep 提取 cwd 参数过沙箱 ——

    [Fact]
    public async Task CheckAsync_Glob_CwdOutsideProject_Blocked()
    {
        var sut = new SecurityGuard(NewCtx(), SecurityLevel.Strict);

        var result = await sut.CheckAsync(WithCwd("glob", ExternalAbs()), CancellationToken.None);

        result.Should().NotBeNull();
        result!.Error.Should().StartWith("[路径沙箱]");
    }

    [Fact]
    public async Task CheckAsync_Grep_CwdInsideProject_Allowed()
    {
        var sut = new SecurityGuard(NewCtx(), SecurityLevel.Strict);

        var result = await sut.CheckAsync(WithCwd("grep", ProjRoot), CancellationToken.None);

        result.Should().BeNull();
    }

    // —— 08b-14 Level 属性可运行时 set ——

    [Fact]
    public async Task CheckAsync_Level_ChangesRuntimeBehavior()
    {
        // 同一路径，Strict 拦截，Permissive 放行
        var sut = new SecurityGuard(NewCtx(), SecurityLevel.Strict);
        var externalCall = WithPath("read_file", ExternalAbs());

        var strictResult = await sut.CheckAsync(externalCall, CancellationToken.None);
        strictResult.Should().NotBeNull("Strict 模式下越界路径应被拦");

        // 运行时切换到 Permissive
        sut.Level = SecurityLevel.Permissive;

        var permissiveResult = await sut.CheckAsync(externalCall, CancellationToken.None);
        permissiveResult.Should().BeNull("Permissive 模式下路径不检查");
    }

    // —— Permissive 黑名单仍生效（08b-13 兜底，验证黑名单不依赖 Level）——

    [Fact]
    public async Task CheckAsync_Permissive_BlacklistStillActive()
    {
        var sut = new SecurityGuard(NewCtx(), SecurityLevel.Permissive);

        // Permissive 下 rm -rf / 仍被黑名单拦（黑名单不依赖 Level）
        var result = await sut.CheckAsync(RunCommand("rm", "-rf /"), CancellationToken.None);

        result.Should().NotBeNull();
        result!.Error.Should().StartWith("[黑名单]");
    }

    // —— 08b-29 CancellationToken 取消时预扫描中断 ——

    [Fact]
    public async Task CheckAsync_CancelledToken_ThrowsOperationCanceled()
    {
        var sut = new SecurityGuard(NewCtx(), SecurityLevel.Normal);
        var cts = new CancellationTokenSource();
        cts.Cancel();

        var act = async () => await sut.CheckAsync(RunCommand("git"), cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }
}
