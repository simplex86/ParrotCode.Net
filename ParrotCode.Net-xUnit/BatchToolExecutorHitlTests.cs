using System.Text.Json;
using ParrotCode;

namespace ParrotCode.xUnit;

/// <summary>
/// BatchToolExecutor HITL 接入单元测试（迭代 7b 新增）。
/// 用假 IHitlGate 注入验证：Write 工具问 HITL、Read 不问、Deny 不执行、Allow 执行、缓存命中跳过、
/// hitlGate=null 等价 7a、OnBeforeExecuteAsync hook 拦截、取消时抛 OperationCanceledException。
/// </summary>
public class BatchToolExecutorHitlTests
{
    private static ToolCall MakeCall(string id, string name, string argsJson = "{}") =>
        new(id, name, JsonDocument.Parse(argsJson).RootElement.Clone());

    private sealed class WriteSuccessTool : ToolBase
    {
        private readonly string _content;
        public WriteSuccessTool(string name, string content = "ok")
        {
            Name = name;
            _content = content;
        }
        public override string Name { get; }
        public override string Description => "test write";
        public override ToolCategory Category => ToolCategory.Write;
        public override IReadOnlyList<ToolParameter> Parameters => Array.Empty<ToolParameter>();
        public override Task<ToolResult> ExecuteAsync(JsonElement input, CancellationToken ct)
            => Task.FromResult(ToolResult.Ok(_content));
    }

    private sealed class ReadSuccessTool : ToolBase
    {
        public ReadSuccessTool(string name) { Name = name; }
        public override string Name { get; }
        public override string Description => "test read";
        public override ToolCategory Category => ToolCategory.Read;
        public override IReadOnlyList<ToolParameter> Parameters => Array.Empty<ToolParameter>();
        public override Task<ToolResult> ExecuteAsync(JsonElement input, CancellationToken ct)
            => Task.FromResult(ToolResult.Ok("read-ok"));
    }

    /// <summary>记录调用次数的假 IHitlGate，可预设返回决策。</summary>
    private sealed class FakeHitlGate : IHitlGate
    {
        private readonly HitlDecision? _decision;
        public int RequestCalls { get; private set; }
        public List<ToolCall> RequestedCalls { get; } = new();

        public FakeHitlGate(HitlDecision? decision) => _decision = decision;

        public Task<HitlDecision?> RequestAsync(ToolCall call, CancellationToken ct)
        {
            RequestCalls++;
            RequestedCalls.Add(call);
            return Task.FromResult(_decision);
        }

        public bool IsAllowedThisSession(string toolName) => false;
    }

    /// <summary>记录 OnBeforeExecuteAsync 调用的子类（验证 hook）。</summary>
    private sealed class HookRecordingExecutor : BatchToolExecutor
    {
        public int HookCalls { get; private set; }
        private readonly ToolResult? _hookResult;

        public HookRecordingExecutor(ToolExecutor executor, ToolRegistry registry,
                                       ToolResult? hookResult = null, IHitlGate? gate = null)
            : base(executor, registry, hitlGate: gate)
        {
            _hookResult = hookResult;
        }

        protected override Task<ToolResult?> OnBeforeExecuteAsync(ToolCall call, CancellationToken ct)
        {
            HookCalls++;
            return Task.FromResult(_hookResult);
        }
    }

    private static (BatchToolExecutor batch, WriteSuccessTool tool) NewBatchWithHitl(IHitlGate? gate)
    {
        var registry = new ToolRegistry();
        var tool = new WriteSuccessTool("write_file");
        registry.Register(tool);
        var executor = new ToolExecutor(registry, TimeSpan.FromSeconds(5));
        var batch = new BatchToolExecutor(executor, registry, hitlGate: gate);
        return (batch, tool);
    }

    [Fact]
    public async Task ExecuteAsync_WriteTool_PromptsHitl()
    {
        var gate = new FakeHitlGate(new HitlDecision(HitlChoice.AllowOnce));
        var (batch, _) = NewBatchWithHitl(gate);

        await batch.ExecuteAsync(new[] { MakeCall("1", "write_file") }, CancellationToken.None);

        gate.RequestCalls.Should().Be(1, "Write 工具应触发一次 HITL 询问");
    }

    [Fact]
    public async Task ExecuteAsync_HitlDeny_ReturnsFail()
    {
        var gate = new FakeHitlGate(HitlDecision.Deny("用户拒绝执行该工具"));
        var (batch, _) = NewBatchWithHitl(gate);

        var results = await batch.ExecuteAsync(new[] { MakeCall("1", "write_file") }, CancellationToken.None);

        results.Should().HaveCount(1);
        results[0].Success.Should().BeFalse();
        results[0].Error.Should().Contain("用户拒绝");
    }

    [Fact]
    public async Task ExecuteAsync_HitlDeny_DoesNotExecute()
    {
        var gate = new FakeHitlGate(HitlDecision.Deny("拒绝"));
        var registry = new ToolRegistry();
        var execCount = 0;
        var countingTool = new CountingWriteTool("write_file", () => execCount++);
        registry.Register(countingTool);
        var executor = new ToolExecutor(registry, TimeSpan.FromSeconds(5));
        var batch = new BatchToolExecutor(executor, registry, hitlGate: gate);

        await batch.ExecuteAsync(new[] { MakeCall("1", "write_file") }, CancellationToken.None);

        execCount.Should().Be(0, "HITL Deny 时不应执行工具");
    }

    [Fact]
    public async Task ExecuteAsync_HitlAllow_Executes()
    {
        var gate = new FakeHitlGate(new HitlDecision(HitlChoice.AllowOnce));
        var (batch, _) = NewBatchWithHitl(gate);

        var results = await batch.ExecuteAsync(new[] { MakeCall("1", "write_file") }, CancellationToken.None);

        results[0].Success.Should().BeTrue();
        results[0].Content.Should().Be("ok");
    }

    [Fact]
    public async Task ExecuteAsync_ReadTool_NoHitl()
    {
        var gate = new FakeHitlGate(new HitlDecision(HitlChoice.AllowOnce));
        var registry = new ToolRegistry();
        registry.Register(new ReadSuccessTool("read_file"));
        var executor = new ToolExecutor(registry, TimeSpan.FromSeconds(5));
        var batch = new BatchToolExecutor(executor, registry, hitlGate: gate);

        await batch.ExecuteAsync(new[] { MakeCall("1", "read_file") }, CancellationToken.None);

        gate.RequestCalls.Should().Be(0, "Read 工具不应触发 HITL");
    }

    [Fact]
    public async Task ExecuteAsync_CacheHit_NoPrompt()
    {
        // 方案 A：用 HitlPrompt 真实实现 + FakeConsole。
        // 第一次 S 缓存，第二次缓存命中不调 ReadKey（不问 HITL）。
        var console = new FakeHitlConsole();
        console.EnqueueKey(ConsoleKey.S);
        var prompt = new HitlPrompt(console);
        var registry = new ToolRegistry();
        registry.Register(new WriteSuccessTool("write_file"));
        var executor = new ToolExecutor(registry, TimeSpan.FromSeconds(5));
        var batch = new BatchToolExecutor(executor, registry, hitlGate: prompt);

        // 第一次：触发 HITL，按 S 缓存
        await batch.ExecuteAsync(new[] { MakeCall("1", "write_file") }, CancellationToken.None);
        var firstReadKeyCalls = console.ReadKeyCalls;

        // 第二次：缓存命中，不应调 ReadKey
        await batch.ExecuteAsync(new[] { MakeCall("2", "write_file") }, CancellationToken.None);

        console.ReadKeyCalls.Should().Be(firstReadKeyCalls, "缓存命中时第二次不应调 ReadKey");
    }

    /// <summary>记录 ReadKey 调用次数的假 IConsole。</summary>
    private sealed class FakeHitlConsole : IConsole
    {
        private readonly Queue<ConsoleKeyInfo> _keys = new();
        public int ReadKeyCalls { get; private set; }

        public void EnqueueKey(ConsoleKey key) =>
            _keys.Enqueue(new ConsoleKeyInfo('\0', key, false, false, false));

        public ConsoleKeyInfo ReadKey(bool intercept)
        {
            ReadKeyCalls++;
            return _keys.Dequeue();
        }

        public void Write(string text) { }
        public void WriteLine() { }
        public void WriteMarkup(string markup) { }
        public void WriteMarkupLine(string markup) { }
        public void Write(Spectre.Console.Rendering.IRenderable renderable) { }
    }

    [Fact]
    public async Task ExecuteAsync_HitlNull_GateSkipped()
    {
        // hitlGate=null 等价 7a——直接执行
        var registry = new ToolRegistry();
        registry.Register(new WriteSuccessTool("write_file"));
        var executor = new ToolExecutor(registry, TimeSpan.FromSeconds(5));
        var batch = new BatchToolExecutor(executor, registry, hitlGate: null);

        var results = await batch.ExecuteAsync(new[] { MakeCall("1", "write_file") }, CancellationToken.None);

        results[0].Success.Should().BeTrue("hitlGate=null 时应直接执行（等价 7a）");
    }

    [Fact]
    public async Task ExecuteAsync_CancelledToken_ThrowsOperationCanceled()
    {
        // BatchToolExecutor 在 Write 组循环开头调 ThrowIfCancellationRequested
        // HitlPrompt 的取消处理已在 HitlPromptTests 验证（返回 Deny）
        var gate = new FakeHitlGate(new HitlDecision(HitlChoice.AllowOnce));
        var (batch, _) = NewBatchWithHitl(gate);
        var cts = new CancellationTokenSource();
        cts.Cancel();

        var act = async () => await batch.ExecuteAsync(new[] { MakeCall("1", "write_file") }, cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task ExecuteAsync_OnBeforeExecuteAsync_HookCalled()
    {
        var registry = new ToolRegistry();
        registry.Register(new WriteSuccessTool("write_file"));
        var executor = new ToolExecutor(registry, TimeSpan.FromSeconds(5));
        var batch = new HookRecordingExecutor(executor, registry);

        await batch.ExecuteAsync(new[] { MakeCall("1", "write_file") }, CancellationToken.None);

        batch.HookCalls.Should().Be(1, "OnBeforeExecuteAsync 应对 Write 工具被调一次");
    }

    [Fact]
    public async Task ExecuteAsync_OnBeforeExecuteAsync_BlockedSkipsHitl()
    {
        // hook 返回 Fail 时——不应执行工具，也不应调 HITL
        var gate = new FakeHitlGate(new HitlDecision(HitlChoice.AllowOnce));
        var registry = new ToolRegistry();
        registry.Register(new WriteSuccessTool("write_file"));
        var executor = new ToolExecutor(registry, TimeSpan.FromSeconds(5));
        var hookResult = ToolResult.Fail("被安全层拦截");
        var batch = new HookRecordingExecutor(executor, registry, hookResult: hookResult, gate: gate);

        var results = await batch.ExecuteAsync(new[] { MakeCall("1", "write_file") }, CancellationToken.None);

        results[0].Success.Should().BeFalse();
        results[0].Error.Should().Contain("安全层拦截");
        gate.RequestCalls.Should().Be(0, "安全层拦截后不应再问 HITL");
    }

    private sealed class CountingWriteTool : ToolBase
    {
        private readonly Action _onExecute;
        public CountingWriteTool(string name, Action onExecute) { Name = name; _onExecute = onExecute; }
        public override string Name { get; }
        public override string Description => "counting";
        public override ToolCategory Category => ToolCategory.Write;
        public override IReadOnlyList<ToolParameter> Parameters => Array.Empty<ToolParameter>();
        public override Task<ToolResult> ExecuteAsync(JsonElement input, CancellationToken ct)
        {
            _onExecute();
            return Task.FromResult(ToolResult.Ok("ok"));
        }
    }
}
