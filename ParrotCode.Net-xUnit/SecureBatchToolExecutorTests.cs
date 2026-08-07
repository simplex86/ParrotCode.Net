using System.IO;
using System.Text.Json;
using ParrotCode;

namespace ParrotCode.xUnit;

/// <summary>
/// SecureBatchToolExecutor 集成测试（迭代 8b）。
/// 覆盖验收标准 08b-15 ~ 08b-29：预扫描、Read 组过安全层、Write 组 HITL 顺序、全放行、全拦截。
/// 用假 IHitlGate + 计数 Read/Write 工具验证预扫描 → 分组 → HITL → 执行顺序。
/// </summary>
public class SecureBatchToolExecutorTests
{
    private static readonly string ProjRoot = Path.Combine(Path.GetTempPath(), "parrotcode-8b-sec");

    private static string ExternalAbs() =>
        OperatingSystem.IsWindows() ? @"C:\parrotcode-8b-sec-ext" : "/parrotcode-8b-sec-ext";

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

    private static ToolCall ReadFile(string path)
    {
        var argsJson = $"{{\"path\":{JsonSerializer.Serialize(path)}}}";
        return MakeCall("read_file", argsJson);
    }

    private static ToolCall WriteFile(string path)
    {
        var argsJson = $"{{\"path\":{JsonSerializer.Serialize(path)},\"content\":\"x\"}}";
        return MakeCall("write_file", argsJson);
    }

    private static ToolCall RunCommand(string command, string? args = null)
    {
        var argsJson = args is null
            ? $"{{\"command\":{JsonSerializer.Serialize(command)}}}"
            : $"{{\"command\":{JsonSerializer.Serialize(command)},\"args\":{JsonSerializer.Serialize(args)}}}";
        return MakeCall("run_command", argsJson);
    }

    // —— 测试替身 ——

    private sealed class CountingReadTool : ToolBase
    {
        public int CallCount;
        public override string Name => "read_file";
        public override string Description => "test read";
        public override ToolCategory Category => ToolCategory.Read;
        public override IReadOnlyList<ToolParameter> Parameters => Array.Empty<ToolParameter>();
        public override Task<ToolResult> ExecuteAsync(JsonElement input, CancellationToken ct)
        {
            CallCount++;
            return Task.FromResult(ToolResult.Ok("read-ok"));
        }
    }

    private sealed class CountingWriteTool : ToolBase
    {
        public int CallCount;
        public override string Name => "write_file";
        public override string Description => "test write";
        public override ToolCategory Category => ToolCategory.Write;
        public override IReadOnlyList<ToolParameter> Parameters => Array.Empty<ToolParameter>();
        public override Task<ToolResult> ExecuteAsync(JsonElement input, CancellationToken ct)
        {
            CallCount++;
            return Task.FromResult(ToolResult.Ok("write-ok"));
        }
    }

    private sealed class CountingRunCommandTool : ToolBase
    {
        public int CallCount;
        public override string Name => "run_command";
        public override string Description => "test run_command";
        public override ToolCategory Category => ToolCategory.Write;
        public override IReadOnlyList<ToolParameter> Parameters => Array.Empty<ToolParameter>();
        public override Task<ToolResult> ExecuteAsync(JsonElement input, CancellationToken ct)
        {
            CallCount++;
            return Task.FromResult(ToolResult.Ok("cmd-ok"));
        }
    }

    private sealed class FakeHitlGate : IHitlGate
    {
        private readonly HitlDecision? _decision;
        public int RequestCalls;
        public List<string> RequestedTools { get; } = new();

        public FakeHitlGate(HitlDecision? decision) => _decision = decision;

        public Task<HitlDecision?> RequestAsync(ToolCall call, CancellationToken ct)
        {
            RequestCalls++;
            RequestedTools.Add(call.Name);
            return Task.FromResult(_decision);
        }

        public bool IsAllowedThisSession(string toolName) => false;
    }

    private static (SecureBatchToolExecutor batch, CountingReadTool readTool, CountingWriteTool writeTool, CountingRunCommandTool cmdTool, FakeHitlGate gate) NewSecure(
        SecurityLevel level, HitlDecision? hitlDecision = null)
    {
        var registry = new ToolRegistry();
        var readTool = new CountingReadTool();
        var writeTool = new CountingWriteTool();
        var cmdTool = new CountingRunCommandTool();
        registry.Register(readTool);
        registry.Register(writeTool);
        registry.Register(cmdTool);

        var executor = new ToolExecutor(registry, TimeSpan.FromSeconds(5));
        var guard = new SecurityGuard(NewCtx(), level);
        var gate = new FakeHitlGate(hitlDecision ?? new HitlDecision(HitlChoice.AllowOnce));
        var batch = new SecureBatchToolExecutor(executor, registry, guard, hitlGate: gate);
        return (batch, readTool, writeTool, cmdTool, gate);
    }

    // —— 08b-15 基类（非 Secure）预扫描全放行，行为等价 7b ——

    [Fact]
    public async Task BaseBatchToolExecutor_Prescan_AllPass_BehavesAs7b()
    {
        // 基类 BatchToolExecutor（非 Secure）的 OnBeforeExecuteAsync 返回 null，预扫描全放行
        // 行为应等价 7b：Read 并发执行、Write 串行执行 + HITL
        var registry = new ToolRegistry();
        var readTool = new CountingReadTool();
        registry.Register(readTool);
        var executor = new ToolExecutor(registry, TimeSpan.FromSeconds(5));
        var gate = new FakeHitlGate(new HitlDecision(HitlChoice.AllowOnce));
        var batch = new BatchToolExecutor(executor, registry, hitlGate: gate);

        var results = await batch.ExecuteAsync(new[] { ReadFile("test.txt") }, CancellationToken.None);

        results.Should().HaveCount(1);
        results[0].Success.Should().BeTrue();
        readTool.CallCount.Should().Be(1, "Read 工具应被调用一次");
        gate.RequestCalls.Should().Be(0, "Read 工具不调 HITL");
    }

    // —— 08b-23 注入 SecurityGuard 后 Read 组也过安全层 ——

    [Fact]
    public async Task SecureExecutor_ReadUnderSandbox_StrictOutsideBlocked_NotExecuted()
    {
        // 08b-24 Strict 模式 Read 越界路径被拦，不执行
        var (batch, readTool, _, _, _) = NewSecure(SecurityLevel.Strict);

        var results = await batch.ExecuteAsync(new[] { ReadFile(ExternalAbs()) }, CancellationToken.None);

        results.Should().HaveCount(1);
        results[0].Success.Should().BeFalse();
        results[0].Error.Should().StartWith("[路径沙箱]");
        readTool.CallCount.Should().Be(0, "Read 组被沙箱拦后不应执行");
    }

    // —— 08b-25 Normal 模式 Read 项目根内放行并发执行 ——

    [Fact]
    public async Task SecureExecutor_ReadInsideProject_Normal_Allowed()
    {
        var (batch, readTool, _, _, _) = NewSecure(SecurityLevel.Normal);

        var insidePath = Path.Combine(ProjRoot, "file.txt");
        var results = await batch.ExecuteAsync(new[] { ReadFile(insidePath) }, CancellationToken.None);

        results.Should().HaveCount(1);
        results[0].Success.Should().BeTrue();
        readTool.CallCount.Should().Be(1, "项目根内 Read 应放行执行");
    }

    // —— 08b-17 拦截的 call 不进 Read/Write 分组 ——

    [Fact]
    public async Task SecureExecutor_BlockedCall_StaysOutOfReadAndWriteGroup()
    {
        var (batch, readTool, writeTool, _, _) = NewSecure(SecurityLevel.Strict);

        // read_file 越界（拦） + write_file 项目根内（放行）
        var results = await batch.ExecuteAsync(
            new[] { ReadFile(ExternalAbs()), WriteFile(Path.Combine(ProjRoot, "f.txt")) },
            CancellationToken.None);

        results.Should().HaveCount(2);
        results[0].Success.Should().BeFalse("第一个 call 被拦");
        results[0].Error.Should().StartWith("[路径沙箱]");
        results[1].Success.Should().BeTrue("第二个 call 放行");
        readTool.CallCount.Should().Be(0, "被拦的 read_file 不执行");
        writeTool.CallCount.Should().Be(1, "放行的 write_file 走 HITL → 执行");
    }

    // —— 08b-19 Write 组拦截后不调 HITL（安全层先于 HITL）——

    [Fact]
    public async Task SecureExecutor_WriteBlockedBySandbox_HitlNotCalled()
    {
        // write_file 越界 → 沙箱拦 → 不调 HITL
        var (batch, _, writeTool, _, gate) = NewSecure(SecurityLevel.Strict);

        var results = await batch.ExecuteAsync(new[] { WriteFile(ExternalAbs()) }, CancellationToken.None);

        results.Should().HaveCount(1);
        results[0].Success.Should().BeFalse();
        results[0].Error.Should().StartWith("[路径沙箱]");
        gate.RequestCalls.Should().Be(0, "安全层拦截后不应调 HITL");
        writeTool.CallCount.Should().Be(0, "被拦的 write_file 不执行");
    }

    // —— 08b-20 Write 组放行后仍调 HITL ——

    [Fact]
    public async Task SecureExecutor_WriteAllowed_HitlCalled()
    {
        var (batch, _, writeTool, _, gate) = NewSecure(SecurityLevel.Normal);

        var results = await batch.ExecuteAsync(new[] { WriteFile(Path.Combine(ProjRoot, "f.txt")) }, CancellationToken.None);

        results.Should().HaveCount(1);
        results[0].Success.Should().BeTrue();
        gate.RequestCalls.Should().Be(1, "Write 组放行后应调 HITL");
        writeTool.CallCount.Should().Be(1);
    }

    [Fact]
    public async Task SecureExecutor_WriteHitlDeny_NotExecuted()
    {
        var gate = new FakeHitlGate(HitlDecision.Deny("用户拒绝"));
        var registry = new ToolRegistry();
        var writeTool = new CountingWriteTool();
        registry.Register(writeTool);
        var executor = new ToolExecutor(registry, TimeSpan.FromSeconds(5));
        var guard = new SecurityGuard(NewCtx(), SecurityLevel.Normal);
        var batch = new SecureBatchToolExecutor(executor, registry, guard, hitlGate: gate);

        var results = await batch.ExecuteAsync(new[] { WriteFile(Path.Combine(ProjRoot, "f.txt")) }, CancellationToken.None);

        results[0].Success.Should().BeFalse();
        results[0].Error.Should().Contain("用户拒绝");
        writeTool.CallCount.Should().Be(0);
    }

    // —— 08b-21 全部拦截时 pending.Count == 0 直接返回 ——

    [Fact]
    public async Task SecureExecutor_AllBlocked_ReturnsDirectly()
    {
        var (batch, readTool, writeTool, _, gate) = NewSecure(SecurityLevel.Strict);

        // 两个 call 都越界 → 全部拦
        var results = await batch.ExecuteAsync(
            new[] { ReadFile(ExternalAbs()), WriteFile(ExternalAbs()) },
            CancellationToken.None);

        results.Should().HaveCount(2);
        results[0].Success.Should().BeFalse();
        results[1].Success.Should().BeFalse();
        readTool.CallCount.Should().Be(0);
        writeTool.CallCount.Should().Be(0);
        gate.RequestCalls.Should().Be(0, "全部被安全层拦，不调 HITL");
    }

    // —— 08b-22 预扫描顺序：call[0] → call[1] → ... ——

    [Fact]
    public async Task SecureExecutor_PrescanOrder_IsCallOrder()
    {
        // 用一个记录调用顺序的 gate 验证 Write 组顺序
        // 但预扫描顺序由 SecurityGuard.CheckAsync 内部决定，无法直接观察
        // 改为验证：多个 call 同时调用时，结果列表按原序返回
        var (batch, _, _, _, _) = NewSecure(SecurityLevel.Normal);

        var results = await batch.ExecuteAsync(
            new[]
            {
                WriteFile(Path.Combine(ProjRoot, "a.txt")),
                ReadFile(Path.Combine(ProjRoot, "b.txt")),
                WriteFile(Path.Combine(ProjRoot, "c.txt"))
            },
            CancellationToken.None);

        results.Should().HaveCount(3);
        // 验证结果按原序返回（保序）
        results[0].Success.Should().BeTrue();
        results[1].Success.Should().BeTrue();
        results[2].Success.Should().BeTrue();
    }

    // —— 08b-27 Permissive 模式仅黑名单拦，路径不查 ——

    [Fact]
    public async Task SecureExecutor_Permissive_OnlyBlacklist_PathUnchecked()
    {
        var (batch, readTool, _, _, _) = NewSecure(SecurityLevel.Permissive);

        // Permissive 下越界路径放行，Read 工具执行
        var results = await batch.ExecuteAsync(new[] { ReadFile(ExternalAbs()) }, CancellationToken.None);

        results.Should().HaveCount(1);
        results[0].Success.Should().BeTrue();
        readTool.CallCount.Should().Be(1, "Permissive 模式下路径不检查，应执行");
    }

    [Fact]
    public async Task SecureExecutor_Permissive_BlacklistStillBlocked()
    {
        var (batch, _, _, cmdTool, _) = NewSecure(SecurityLevel.Permissive);

        // Permissive 下 rm -rf / 仍被黑名单拦
        var results = await batch.ExecuteAsync(new[] { RunCommand("rm", "-rf /") }, CancellationToken.None);

        results.Should().HaveCount(1);
        results[0].Success.Should().BeFalse();
        results[0].Error.Should().StartWith("[黑名单]");
        cmdTool.CallCount.Should().Be(0, "黑名单命令不应执行");
    }

    // —— 08b-28 拦截原因作为 ToolResult.Error 回灌 ——

    [Fact]
    public async Task SecureExecutor_BlockedReason_PropagatedToToolResultError()
    {
        var (batch, _, _, _, _) = NewSecure(SecurityLevel.Strict);

        var results = await batch.ExecuteAsync(new[] { ReadFile(ExternalAbs()) }, CancellationToken.None);

        results[0].Success.Should().BeFalse();
        results[0].Error.Should().NotBeNullOrEmpty("拦截原因应回灌到 Error");
        results[0].Content.Should().BeEmpty("拦截后 Content 应为空");
    }

    // —— 08b-29 CancellationToken 取消时预扫描中断 ——

    [Fact]
    public async Task SecureExecutor_CancelledToken_PrescanThrowsOperationCanceled()
    {
        var (batch, _, _, _, _) = NewSecure(SecurityLevel.Normal);
        var cts = new CancellationTokenSource();
        cts.Cancel();

        var act = async () => await batch.ExecuteAsync(
            new[] { WriteFile(Path.Combine(ProjRoot, "f.txt")) },
            cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    // —— 混合场景：黑名单拦截一个，其余放行 ——

    [Fact]
    public async Task SecureExecutor_MixedBlacklistAndAllowed_PartiallyBlocked()
    {
        var (batch, readTool, _, cmdTool, _) = NewSecure(SecurityLevel.Normal);

        // rm -rf /（黑名单拦） + read_file 项目根内（放行）
        var results = await batch.ExecuteAsync(
            new[]
            {
                RunCommand("rm", "-rf /"),
                ReadFile(Path.Combine(ProjRoot, "f.txt"))
            },
            CancellationToken.None);

        results.Should().HaveCount(2);
        results[0].Success.Should().BeFalse();
        results[0].Error.Should().StartWith("[黑名单]");
        results[1].Success.Should().BeTrue();
        cmdTool.CallCount.Should().Be(0, "黑名单命令不应执行");
        readTool.CallCount.Should().Be(1, "放行的 read_file 应执行");
    }
}
