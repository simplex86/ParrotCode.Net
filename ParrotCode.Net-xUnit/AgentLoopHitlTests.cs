using System.Text.Json;
using ParrotCode;

namespace ParrotCode.xUnit;

/// <summary>
/// AgentLoop HITL 拒绝转发单元测试（迭代 7b 新增）。
/// 用 MockProvider 脚本触发 write_file + 假 IHitlGate 验证：
/// - HITL Deny → emit ToolBlockedEvent + 拒绝原因回灌历史
/// - 工具自身失败 → emit ToolResultEvent（非 ToolBlockedEvent）
/// - HITL Allow → emit ToolResultEvent
/// - hitlGate=null → 无 ToolBlockedEvent（等价 7a）
/// </summary>
public class AgentLoopHitlTests
{
    private sealed class WriteSuccessTool : ToolBase
    {
        public override string Name => "write_file";
        public override string Description => "test write";
        public override ToolCategory Category => ToolCategory.Write;
        public override IReadOnlyList<ToolParameter> Parameters => Array.Empty<ToolParameter>();
        public override Task<ToolResult> ExecuteAsync(JsonElement input, CancellationToken ct)
            => Task.FromResult(ToolResult.Ok("已写入"));
    }

    private sealed class FailingWriteTool : ToolBase
    {
        public override string Name => "write_file";
        public override string Description => "always fails";
        public override ToolCategory Category => ToolCategory.Write;
        public override IReadOnlyList<ToolParameter> Parameters => Array.Empty<ToolParameter>();
        public override Task<ToolResult> ExecuteAsync(JsonElement input, CancellationToken ct)
            => Task.FromResult(ToolResult.Fail("磁盘满"));
    }

    private sealed class FakeHitlGate : IHitlGate
    {
        private readonly HitlDecision? _decision;
        public FakeHitlGate(HitlDecision? decision) => _decision = decision;
        public Task<HitlDecision?> RequestAsync(ToolCall call, CancellationToken ct)
            => Task.FromResult(_decision);
        public bool IsAllowedThisSession(string toolName) => false;
    }

    private static ChatChunk[] ToolCallScript(string id, string name, string argsJson = "{}") =>
        new ChatChunk[] { new ChatChunk.ToolCallDelta(0, id, name, argsJson), new ChatChunk.Done() };

    private static ChatChunk[] TextScript(string text) =>
        new ChatChunk[] { new ChatChunk.TextDelta(text), new ChatChunk.Done() };

    private static async Task<List<AgentEvent>> CollectEventsAsync(ChannelEventSink sink, Task agentTask)
    {
        var events = new List<AgentEvent>();
        await foreach (var evt in sink.Reader.ReadAllAsync(CancellationToken.None))
            events.Add(evt);
        await agentTask;
        return events;
    }

    private static (AgentLoop loop, MockProvider provider, ChannelEventSink sink) CreateLoopWithHitl(
        IHitlGate? gate, IBaseTool? writeTool = null, int maxRounds = 10)
    {
        var provider = new MockProvider();
        var registry = new ToolRegistry();
        registry.Register(writeTool ?? new WriteSuccessTool());
        var executor = new ToolExecutor(registry, TimeSpan.FromSeconds(5));
        var batch = new BatchToolExecutor(executor, registry, hitlGate: gate);
        var loop = new AgentLoop(provider, registry, batch, maxRounds: maxRounds);
        return (loop, provider, new ChannelEventSink());
    }

    [Fact]
    public async Task HitlDeny_EmitsToolBlockedEvent()
    {
        var gate = new FakeHitlGate(HitlDecision.Deny("用户拒绝执行该工具"));
        var (loop, provider, sink) = CreateLoopWithHitl(gate);
        provider.EnqueueScript(ToolCallScript("call_1", "write_file", "{}"));
        provider.EnqueueScript(TextScript("好的，我不执行"));
        var history = new ConversationHistory();
        history.AddUser("写文件");

        var events = await CollectEventsAsync(sink, loop.RunAsync(history, sink, CancellationToken.None));

        events.OfType<AgentEvent.ToolBlockedEvent>().Should().ContainSingle();
        var blocked = events.OfType<AgentEvent.ToolBlockedEvent>().Single();
        blocked.Call.Name.Should().Be("write_file");
        blocked.Reason.Should().Contain("用户拒绝");
    }

    [Fact]
    public async Task HitlDeny_ReasonReflowsToHistory()
    {
        var gate = new FakeHitlGate(HitlDecision.Deny("用户拒绝执行该工具"));
        var (loop, provider, sink) = CreateLoopWithHitl(gate);
        provider.EnqueueScript(ToolCallScript("call_1", "write_file", "{}"));
        provider.EnqueueScript(TextScript("好的"));
        var history = new ConversationHistory();
        history.AddUser("写文件");

        await CollectEventsAsync(sink, loop.RunAsync(history, sink, CancellationToken.None));

        // history 中应含 tool 消息，内容含"错误：用户拒绝"
        var messages = history.ToProviderMessages();
        var toolMessage = messages.FirstOrDefault(m => m.Role == MessageRole.Tool);
        toolMessage.Should().NotBeNull();
        toolMessage!.Content.Should().Contain("错误：");
        toolMessage.Content.Should().Contain("用户拒绝");
    }

    [Fact]
    public async Task ToolExecuteFail_EmitsToolResultEvent_NotBlocked()
    {
        // HITL Allow，但工具自身失败 → 应 emit ToolResultEvent（非 ToolBlockedEvent）
        var gate = new FakeHitlGate(new HitlDecision(HitlChoice.AllowOnce));
        var (loop, provider, sink) = CreateLoopWithHitl(gate, writeTool: new FailingWriteTool());
        provider.EnqueueScript(ToolCallScript("call_1", "write_file", "{}"));
        provider.EnqueueScript(TextScript("好的"));
        var history = new ConversationHistory();
        history.AddUser("写文件");

        var events = await CollectEventsAsync(sink, loop.RunAsync(history, sink, CancellationToken.None));

        // 工具自身失败（"磁盘满"）——应 emit ToolResultEvent，不 emit ToolBlockedEvent
        events.OfType<AgentEvent.ToolResultEvent>().Should().ContainSingle();
        events.OfType<AgentEvent.ToolBlockedEvent>().Should().BeEmpty("工具自身失败非 HITL 拒绝");
        var result = events.OfType<AgentEvent.ToolResultEvent>().Single();
        result.Result.Success.Should().BeFalse();
        result.Result.Error.Should().Be("磁盘满");
    }

    [Fact]
    public async Task HitlAllow_EmitsToolResultEvent()
    {
        var gate = new FakeHitlGate(new HitlDecision(HitlChoice.AllowOnce));
        var (loop, provider, sink) = CreateLoopWithHitl(gate);
        provider.EnqueueScript(ToolCallScript("call_1", "write_file", "{}"));
        provider.EnqueueScript(TextScript("已创建"));
        var history = new ConversationHistory();
        history.AddUser("写文件");

        var events = await CollectEventsAsync(sink, loop.RunAsync(history, sink, CancellationToken.None));

        events.OfType<AgentEvent.ToolResultEvent>().Should().ContainSingle();
        events.OfType<AgentEvent.ToolBlockedEvent>().Should().BeEmpty();
        var result = events.OfType<AgentEvent.ToolResultEvent>().Single();
        result.Result.Success.Should().BeTrue();
    }

    [Fact]
    public async Task NoHitlGate_NoToolBlockedEvent()
    {
        // hitlGate=null 等价 7a——Write 工具直接执行，无 ToolBlockedEvent
        var (loop, provider, sink) = CreateLoopWithHitl(gate: null);
        provider.EnqueueScript(ToolCallScript("call_1", "write_file", "{}"));
        provider.EnqueueScript(TextScript("已创建"));
        var history = new ConversationHistory();
        history.AddUser("写文件");

        var events = await CollectEventsAsync(sink, loop.RunAsync(history, sink, CancellationToken.None));

        events.OfType<AgentEvent.ToolBlockedEvent>().Should().BeEmpty("无 HITL 时不产生 ToolBlockedEvent");
        events.OfType<AgentEvent.ToolResultEvent>().Should().ContainSingle();
    }
}
