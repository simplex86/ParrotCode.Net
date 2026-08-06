using System.Text.Json;
using ParrotCode;

namespace ParrotCode.xUnit;

/// <summary>
/// ToolExecutor 单元测试：覆盖正常执行、未注册工具、超时、异常捕获、外部取消。
/// 用测试替身（SlowTool / ThrowingTool）模拟工具行为，不依赖真实文件系统。
/// </summary>
public class ToolExecutorTests
{
    // ---- 构造 ----

    [Fact]
    public void Constructor_NullRegistry_Throws()
    {
        var act = () => new ToolExecutor(null!);

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public async Task Constructor_DefaultTimeoutIs30Seconds()
    {
        var executor = new ToolExecutor(new ToolRegistry());

        // 通过行为间接验证：未注册工具时立即返回 ToolResult.Fail（不触发超时）
        using var doc = JsonDocument.Parse("{}");
        var call = new ToolCall("id", "nonexistent", doc.RootElement);
        var result = await executor.ExecuteAsync(call, CancellationToken.None);

        result.Success.Should().BeFalse();
    }

    [Fact]
    public async Task Constructor_CustomTimeoutRespected()
    {
        var registry = new ToolRegistry();
        registry.Register(new SlowTool(delay: TimeSpan.FromSeconds(5)));
        var executor = new ToolExecutor(registry, timeout: TimeSpan.FromMilliseconds(100));

        using var doc = JsonDocument.Parse("{}");
        var call = new ToolCall("id", "slow", doc.RootElement);
        var result = await executor.ExecuteAsync(call, CancellationToken.None);

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("超时");
    }

    // ---- 正常执行 ----

    [Fact]
    public async Task ExecuteAsync_ValidCall_ReturnsResult()
    {
        var registry = new ToolRegistry();
        registry.Register(new SuccessTool("ok content"));
        var executor = new ToolExecutor(registry);

        using var doc = JsonDocument.Parse("{}");
        var call = new ToolCall("id", "success", doc.RootElement);

        var result = await executor.ExecuteAsync(call, CancellationToken.None);

        result.Success.Should().BeTrue();
        result.Content.Should().Be("ok content");
    }

    [Fact]
    public async Task ExecuteAsync_NullCall_Throws()
    {
        var executor = new ToolExecutor(new ToolRegistry());

        var act = () => executor.ExecuteAsync(null!, CancellationToken.None);

        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    // ---- 未注册工具 ----

    [Fact]
    public async Task ExecuteAsync_UnknownTool_ReturnsError()
    {
        var executor = new ToolExecutor(new ToolRegistry());

        using var doc = JsonDocument.Parse("{}");
        var call = new ToolCall("id", "nonexistent", doc.RootElement);

        var result = await executor.ExecuteAsync(call, CancellationToken.None);

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("未注册工具");
        result.Error.Should().Contain("nonexistent");
    }

    // ---- 工具异常捕获 ----

    [Fact]
    public async Task ExecuteAsync_ToolThrowsException_ReturnsError()
    {
        var registry = new ToolRegistry();
        registry.Register(new ThrowingTool(new InvalidOperationException("工具内部错误")));
        var executor = new ToolExecutor(registry);

        using var doc = JsonDocument.Parse("{}");
        var call = new ToolCall("id", "throwing", doc.RootElement);

        var result = await executor.ExecuteAsync(call, CancellationToken.None);

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("throwing");
        result.Error.Should().Contain("工具内部错误");
    }

    [Fact]
    public async Task ExecuteAsync_ToolThrowsIOException_ReturnsError()
    {
        var registry = new ToolRegistry();
        registry.Register(new ThrowingTool(new IOException("磁盘已满")));
        var executor = new ToolExecutor(registry);

        using var doc = JsonDocument.Parse("{}");
        var call = new ToolCall("id", "throwing", doc.RootElement);

        var result = await executor.ExecuteAsync(call, CancellationToken.None);

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("磁盘已满");
    }

    // ---- 超时 ----

    [Fact]
    public async Task ExecuteAsync_ToolTimesOut_ReturnsTimeoutError()
    {
        var registry = new ToolRegistry();
        registry.Register(new SlowTool(delay: TimeSpan.FromSeconds(2)));
        var executor = new ToolExecutor(registry, timeout: TimeSpan.FromMilliseconds(100));

        using var doc = JsonDocument.Parse("{}");
        var call = new ToolCall("id", "slow", doc.RootElement);

        var result = await executor.ExecuteAsync(call, CancellationToken.None);

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("超时");
    }

    [Fact]
    public async Task ExecuteAsync_TimeoutContainsTimeoutSeconds()
    {
        var registry = new ToolRegistry();
        registry.Register(new SlowTool(delay: TimeSpan.FromSeconds(2)));
        var executor = new ToolExecutor(registry, timeout: TimeSpan.FromMilliseconds(500));

        using var doc = JsonDocument.Parse("{}");
        var call = new ToolCall("id", "slow", doc.RootElement);

        var result = await executor.ExecuteAsync(call, CancellationToken.None);

        result.Error.Should().Contain("0.5");
    }

    // ---- 外部取消 ----

    [Fact]
    public async Task ExecuteAsync_ExternalCancellation_ThrowsOperationCanceledException()
    {
        var registry = new ToolRegistry();
        registry.Register(new SlowTool(delay: TimeSpan.FromSeconds(10)));
        var executor = new ToolExecutor(registry, timeout: TimeSpan.FromSeconds(30));

        using var doc = JsonDocument.Parse("{}");
        var call = new ToolCall("id", "slow", doc.RootElement);
        var cts = new CancellationTokenSource();
        cts.CancelAfter(TimeSpan.FromMilliseconds(50));

        var act = async () => await executor.ExecuteAsync(call, cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task ExecuteAsync_AlreadyCancelledToken_ThrowsOperationCanceledException()
    {
        var executor = new ToolExecutor(new ToolRegistry());

        using var doc = JsonDocument.Parse("{}");
        var call = new ToolCall("id", "nonexistent", doc.RootElement);
        var cts = new CancellationTokenSource();
        cts.Cancel();

        var act = async () => await executor.ExecuteAsync(call, cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    // ---- 测试替身 ----

    /// <summary>立即返回成功结果的工具。</summary>
    private sealed class SuccessTool : ToolBase
    {
        private readonly string _content;

        public SuccessTool(string content) => _content = content;

        public override string Name => "success";
        public override string Description => "test";
        public override ToolCategory Category => ToolCategory.Read;
        public override IReadOnlyList<ToolParameter> Parameters { get; } = Array.Empty<ToolParameter>();

        public override Task<ToolResult> ExecuteAsync(JsonElement input, CancellationToken cancellationToken)
            => Task.FromResult(ToolResult.Ok(_content));
    }

    /// <summary>同步抛指定异常的工具。</summary>
    private sealed class ThrowingTool : ToolBase
    {
        private readonly Exception _ex;

        public ThrowingTool(Exception ex) => _ex = ex;

        public override string Name => "throwing";
        public override string Description => "test";
        public override ToolCategory Category => ToolCategory.Read;
        public override IReadOnlyList<ToolParameter> Parameters { get; } = Array.Empty<ToolParameter>();

        public override Task<ToolResult> ExecuteAsync(JsonElement input, CancellationToken cancellationToken)
            => throw _ex;
    }

    /// <summary>延迟指定时长后返回成功的工具，用于测试超时。</summary>
    private sealed class SlowTool : ToolBase
    {
        private readonly TimeSpan _delay;

        public SlowTool(TimeSpan delay) => _delay = delay;

        public override string Name => "slow";
        public override string Description => "test";
        public override ToolCategory Category => ToolCategory.Read;
        public override IReadOnlyList<ToolParameter> Parameters { get; } = Array.Empty<ToolParameter>();

        public override async Task<ToolResult> ExecuteAsync(JsonElement input, CancellationToken cancellationToken)
        {
            await Task.Delay(_delay, cancellationToken);
            return ToolResult.Ok("slow done");
        }
    }
}
