using System.Text.Json;
using ParrotCode;

namespace ParrotCode.xUnit;

/// <summary>
/// BatchToolExecutor 单元测试：覆盖空输入、Read 并发、Write 串行、混合保序、
/// 未注册工具归串行组、失败不传播异常、外部取消与构造参数校验。
/// 用测试替身（SuccessTool / WriteSuccessTool / ThrowingTool）模拟工具行为，不依赖真实文件系统。
/// </summary>
public class BatchToolExecutorTests
{
    // ---- 空输入 ----

    [Fact]
    public async Task ExecuteAsync_EmptyCalls_ReturnsEmpty()
    {
        var batch = NewBatch(new ToolRegistry());

        var results = await batch.ExecuteAsync(Array.Empty<ToolCall>(), CancellationToken.None);

        results.Should().BeEmpty();
    }

    // ---- 单个 Read 工具 ----

    [Fact]
    public async Task ExecuteAsync_SingleReadTool_ReturnsSuccess()
    {
        var registry = new ToolRegistry();
        registry.Register(new SuccessTool("r1", "hello"));
        var batch = NewBatch(registry);

        var results = await batch.ExecuteAsync(new[] { MakeCall("1", "r1") }, CancellationToken.None);

        results.Should().HaveCount(1);
        results[0].Success.Should().BeTrue();
        results[0].Content.Should().Be("hello");
    }

    // ---- 多个 Read 工具：并发且保序 ----

    [Fact]
    public async Task ExecuteAsync_MultipleReadTools_AllExecuteAndReturnInOrder()
    {
        var registry = new ToolRegistry();
        registry.Register(new SuccessTool("r1", "first"));
        registry.Register(new SuccessTool("r2", "second"));
        var batch = NewBatch(registry);

        var results = await batch.ExecuteAsync(
            new[] { MakeCall("1", "r1"), MakeCall("2", "r2") },
            CancellationToken.None);

        results.Should().HaveCount(2);
        results[0].Success.Should().BeTrue();
        results[0].Content.Should().Be("first");
        results[1].Success.Should().BeTrue();
        results[1].Content.Should().Be("second");
    }

    // ---- 单个 Write 工具：串行执行 ----

    [Fact]
    public async Task ExecuteAsync_WriteTool_ExecutesSequentially()
    {
        var registry = new ToolRegistry();
        registry.Register(new WriteSuccessTool("w1", "written"));
        var batch = NewBatch(registry);

        var results = await batch.ExecuteAsync(new[] { MakeCall("1", "w1") }, CancellationToken.None);

        results.Should().HaveCount(1);
        results[0].Success.Should().BeTrue();
        results[0].Content.Should().Be("written");
    }

    // ---- 混合 Read + Write：保序（两种顺序）----

    [Fact]
    public async Task ExecuteAsync_MixedReadAndWrite_PreservesOrder()
    {
        var registry = new ToolRegistry();
        registry.Register(new SuccessTool("r1", "read-result"));
        registry.Register(new WriteSuccessTool("w1", "write-result"));
        var batch = NewBatch(registry);

        // Read 在前，Write 在后
        var readFirst = await batch.ExecuteAsync(
            new[] { MakeCall("1", "r1"), MakeCall("2", "w1") },
            CancellationToken.None);
        readFirst.Should().HaveCount(2);
        readFirst[0].Success.Should().BeTrue();
        readFirst[0].Content.Should().Be("read-result");
        readFirst[1].Success.Should().BeTrue();
        readFirst[1].Content.Should().Be("write-result");

        // Write 在前，Read 在后
        var writeFirst = await batch.ExecuteAsync(
            new[] { MakeCall("1", "w1"), MakeCall("2", "r1") },
            CancellationToken.None);
        writeFirst.Should().HaveCount(2);
        writeFirst[0].Success.Should().BeTrue();
        writeFirst[0].Content.Should().Be("write-result");
        writeFirst[1].Success.Should().BeTrue();
        writeFirst[1].Content.Should().Be("read-result");
    }

    // ---- 未注册工具归串行组，居中位置返回 Fail ----

    [Fact]
    public async Task ExecuteAsync_UnregisteredTool_ReturnsFailInCorrectPosition()
    {
        var registry = new ToolRegistry();
        registry.Register(new SuccessTool("r1", "ok1"));
        registry.Register(new SuccessTool("r2", "ok2"));
        var batch = NewBatch(registry);

        var results = await batch.ExecuteAsync(
            new[] { MakeCall("1", "r1"), MakeCall("2", "missing"), MakeCall("3", "r2") },
            CancellationToken.None);

        results.Should().HaveCount(3);
        results[0].Success.Should().BeTrue();
        results[0].Content.Should().Be("ok1");
        results[1].Success.Should().BeFalse();
        results[1].Error.Should().Contain("missing");
        results[2].Success.Should().BeTrue();
        results[2].Content.Should().Be("ok2");
    }

    // ---- 全部失败工具：返回全 Fail（不抛异常）----

    [Fact]
    public async Task ExecuteAsync_AllFailingTools_ReturnsAllFails()
    {
        var registry = new ToolRegistry();
        registry.Register(new ThrowingTool());
        var batch = NewBatch(registry);

        var results = await batch.ExecuteAsync(
            new[] { MakeCall("1", "throwing"), MakeCall("2", "throwing") },
            CancellationToken.None);

        results.Should().HaveCount(2);
        results[0].Success.Should().BeFalse();
        results[1].Success.Should().BeFalse();
    }

    // ---- null calls ----

    [Fact]
    public async Task ExecuteAsync_NullCalls_ThrowsArgumentNullException()
    {
        var batch = NewBatch(new ToolRegistry());

        var act = async () => await batch.ExecuteAsync(null!, CancellationToken.None);

        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    // ---- 已取消 token ----

    [Fact]
    public async Task ExecuteAsync_AlreadyCancelledToken_ThrowsOperationCanceledException()
    {
        var registry = new ToolRegistry();
        registry.Register(new SuccessTool("r1", "ok"));
        var batch = NewBatch(registry);

        var cts = new CancellationTokenSource();
        cts.Cancel();

        var act = async () => await batch.ExecuteAsync(new[] { MakeCall("1", "r1") }, cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    // ---- 构造参数校验 ----

    [Fact]
    public void Constructor_NullExecutor_ThrowsArgumentNullException()
    {
        var act = () => new BatchToolExecutor(null!, new ToolRegistry());

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Constructor_NullRegistry_ThrowsArgumentNullException()
    {
        var act = () => new BatchToolExecutor(new ToolExecutor(new ToolRegistry()), null!);

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Constructor_ZeroMaxParallelism_ThrowsArgumentOutOfRangeException()
    {
        var act = () => new BatchToolExecutor(
            new ToolExecutor(new ToolRegistry()), new ToolRegistry(), maxParallelism: 0);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    // ---- 辅助 ----

    private static BatchToolExecutor NewBatch(ToolRegistry registry)
        => new(new ToolExecutor(registry, TimeSpan.FromSeconds(5)), registry);

    private static ToolCall MakeCall(string id, string name, string argsJson = "{}")
    {
        using var doc = JsonDocument.Parse(argsJson);
        return new ToolCall(id, name, doc.RootElement.Clone());
    }

    // ---- 测试替身 ----

    /// <summary>立即返回成功结果的 Read 工具。</summary>
    private sealed class SuccessTool : ToolBase
    {
        private readonly string _content;

        public SuccessTool(string name, string content)
        {
            Name = name;
            _content = content;
        }

        public override string Name { get; }
        public override string Description => "test";
        public override ToolCategory Category => ToolCategory.Read;
        public override IReadOnlyList<ToolParameter> Parameters => Array.Empty<ToolParameter>();

        public override Task<ToolResult> ExecuteAsync(JsonElement input, CancellationToken cancellationToken)
            => Task.FromResult(ToolResult.Ok(_content));
    }

    /// <summary>立即返回成功结果的 Write 工具。</summary>
    private sealed class WriteSuccessTool : ToolBase
    {
        private readonly string _content;

        public WriteSuccessTool(string name, string content)
        {
            Name = name;
            _content = content;
        }

        public override string Name { get; }
        public override string Description => "test";
        public override ToolCategory Category => ToolCategory.Write;
        public override IReadOnlyList<ToolParameter> Parameters => Array.Empty<ToolParameter>();

        public override Task<ToolResult> ExecuteAsync(JsonElement input, CancellationToken cancellationToken)
            => Task.FromResult(ToolResult.Ok(_content));
    }

    /// <summary>同步抛 InvalidOperationException 的 Read 工具。</summary>
    private sealed class ThrowingTool : ToolBase
    {
        public override string Name => "throwing";
        public override string Description => "test";
        public override ToolCategory Category => ToolCategory.Read;
        public override IReadOnlyList<ToolParameter> Parameters => Array.Empty<ToolParameter>();

        public override Task<ToolResult> ExecuteAsync(JsonElement input, CancellationToken cancellationToken)
            => throw new InvalidOperationException("boom");
    }
}
