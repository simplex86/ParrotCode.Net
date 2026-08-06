using System.Text.Json;
using ParrotCode;

namespace ParrotCode.xUnit;

/// <summary>
/// AgentLoop ReAct 循环单元测试：用 MockProvider 脚本注入 LLM 响应，
/// 验证事件流、历史更新、工具执行、取消与最大轮次等行为。
/// </summary>
public class AgentLoopTests
{
    private static async Task<List<AgentEvent>> CollectEventsAsync(ChannelEventSink sink, Task agentTask)
    {
        var events = new List<AgentEvent>();
        await foreach (var evt in sink.Reader.ReadAllAsync(CancellationToken.None))
            events.Add(evt);
        await agentTask;
        return events;
    }

    private sealed class EchoTool : ToolBase
    {
        public override string Name => "echo";
        public override string Description => "returns its input";
        public override ToolCategory Category => ToolCategory.Read;
        public override IReadOnlyList<ToolParameter> Parameters => Array.Empty<ToolParameter>();
        public override Task<ToolResult> ExecuteAsync(JsonElement input, CancellationToken ct)
            => Task.FromResult(ToolResult.Ok(input.GetRawText()));
    }

    private sealed class FailingTool : ToolBase
    {
        public override string Name => "fail";
        public override string Description => "always fails";
        public override ToolCategory Category => ToolCategory.Read;
        public override IReadOnlyList<ToolParameter> Parameters => Array.Empty<ToolParameter>();
        public override Task<ToolResult> ExecuteAsync(JsonElement input, CancellationToken ct)
            => Task.FromResult(ToolResult.Fail("boom"));
    }

    private static ChatChunk[] ToolCallScript(string id, string name, string argsJson)
    {
        return new ChatChunk[]
        {
            new ChatChunk.ToolCallDelta(0, id, name, argsJson),
            new ChatChunk.Done()
        };
    }

    private static ChatChunk[] TextScript(string text)
    {
        return new ChatChunk[]
        {
            new ChatChunk.TextDelta(text),
            new ChatChunk.Done()
        };
    }

    private static (AgentLoop, MockProvider, ChannelEventSink) CreateLoop(int maxRounds = 10)
    {
        var provider = new MockProvider();
        var registry = new ToolRegistry();
        registry.Register(new EchoTool());
        var executor = new ToolExecutor(registry, TimeSpan.FromSeconds(5));
        var batch = new BatchToolExecutor(executor, registry);
        var loop = new AgentLoop(provider, registry, batch, maxRounds: maxRounds);
        return (loop, provider, new ChannelEventSink());
    }

    [Fact]
    public async Task RunAsync_TextOnlyNoToolCalls_EmitsAgentDone()
    {
        var (loop, provider, sink) = CreateLoop();
        provider.EnqueueScript(TextScript("hello"));
        var history = new ConversationHistory();
        history.AddUser("hi");

        var events = await CollectEventsAsync(sink, loop.RunAsync(history, sink, CancellationToken.None));

        events.OfType<AgentEvent.TextDeltaEvent>().Should().Contain(td => td.Text == "hello");
        events.Should().Contain(e => e is AgentEvent.AgentDoneEvent);
        events.Should().NotContain(e => e is AgentEvent.ToolCallStartEvent);
    }

    [Fact]
    public async Task RunAsync_SingleToolCallThenDone_EmitsToolCallAndResult()
    {
        var (loop, provider, sink) = CreateLoop();
        provider.EnqueueScript(ToolCallScript("call_1", "echo", "{}"));
        provider.EnqueueScript(TextScript("done"));
        var history = new ConversationHistory();
        history.AddUser("hi");

        var events = await CollectEventsAsync(sink, loop.RunAsync(history, sink, CancellationToken.None));

        events.OfType<AgentEvent.RoundStartEvent>().Should().Contain(r => r.Round == 1);
        events.Should().Contain(e => e is AgentEvent.ToolCallStartEvent);
        events.OfType<AgentEvent.ToolResultEvent>().Single().Result.Success.Should().BeTrue();
        events.OfType<AgentEvent.RoundEndEvent>().Should().Contain(r => r.Round == 1);
        events.OfType<AgentEvent.RoundStartEvent>().Should().Contain(r => r.Round == 2);
        events.OfType<AgentEvent.TextDeltaEvent>().Should().Contain(td => td.Text == "done");
        events.Should().Contain(e => e is AgentEvent.AgentDoneEvent);

        events.Select(e => e.GetType().Name).Should().ContainInOrder(
            nameof(AgentEvent.RoundStartEvent),
            nameof(AgentEvent.ToolCallStartEvent),
            nameof(AgentEvent.ToolResultEvent),
            nameof(AgentEvent.RoundEndEvent),
            nameof(AgentEvent.RoundStartEvent),
            nameof(AgentEvent.TextDeltaEvent),
            nameof(AgentEvent.AgentDoneEvent));
    }

    [Fact]
    public async Task RunAsync_AgentDoneFinalTextMatches()
    {
        var (loop, provider, sink) = CreateLoop();
        provider.EnqueueScript(TextScript("final answer"));
        var history = new ConversationHistory();
        history.AddUser("hi");

        var events = await CollectEventsAsync(sink, loop.RunAsync(history, sink, CancellationToken.None));

        var done = events.OfType<AgentEvent.AgentDoneEvent>().Single();
        done.FinalText.Should().Be("final answer");
    }

    [Fact]
    public async Task RunAsync_MaxRoundsReached_EmitsMaxRoundsReachedEvent()
    {
        var (loop, provider, sink) = CreateLoop(maxRounds: 2);
        provider.EnqueueScript(ToolCallScript("call_1", "echo", "{}"));
        provider.EnqueueScript(ToolCallScript("call_2", "echo", "{}"));
        var history = new ConversationHistory();
        history.AddUser("hi");

        var events = await CollectEventsAsync(sink, loop.RunAsync(history, sink, CancellationToken.None));

        events.OfType<AgentEvent.MaxRoundsReachedEvent>().Should().Contain(mrr => mrr.Rounds == 2);
        events.Should().NotContain(e => e is AgentEvent.AgentDoneEvent);
    }

    [Fact]
    public async Task RunAsync_ToolExecutionFailure_ContinuesLoop()
    {
        var provider = new MockProvider();
        var registry = new ToolRegistry();
        registry.Register(new FailingTool());
        var executor = new ToolExecutor(registry, TimeSpan.FromSeconds(5));
        var batch = new BatchToolExecutor(executor, registry);
        var loop = new AgentLoop(provider, registry, batch, maxRounds: 10);
        var sink = new ChannelEventSink();

        provider.EnqueueScript(ToolCallScript("call_1", "fail", "{}"));
        provider.EnqueueScript(TextScript("done"));
        var history = new ConversationHistory();
        history.AddUser("hi");

        var events = await CollectEventsAsync(sink, loop.RunAsync(history, sink, CancellationToken.None));

        var toolResult = events.OfType<AgentEvent.ToolResultEvent>().Single();
        toolResult.Result.Success.Should().BeFalse();
        toolResult.Result.Error.Should().Be("boom");
        events.Should().Contain(e => e is AgentEvent.AgentDoneEvent);
    }

    [Fact]
    public async Task RunAsync_HistoryUpdatedWithToolCalls()
    {
        var (loop, provider, sink) = CreateLoop();
        provider.EnqueueScript(ToolCallScript("call_1", "echo", "{}"));
        provider.EnqueueScript(TextScript("done"));
        var history = new ConversationHistory();
        history.AddUser("hi");

        await CollectEventsAsync(sink, loop.RunAsync(history, sink, CancellationToken.None));

        var messages = history.ToProviderMessages();
        messages.Should().HaveCount(4);
        messages[0].Role.Should().Be(MessageRole.User);
        messages[1].Role.Should().Be(MessageRole.Assistant);
        messages[1].ToolCalls.Should().NotBeNull();
        messages[1].ToolCalls.Should().HaveCount(1);
        messages[2].Role.Should().Be(MessageRole.Tool);
        messages[2].ToolCallId.Should().Be("call_1");
        messages[3].Role.Should().Be(MessageRole.Assistant);
        messages[3].Content.Should().Be("done");
    }

    [Fact]
    public async Task RunAsync_CancelledToken_EmitsCancelledEvent()
    {
        var (loop, provider, sink) = CreateLoop();
        provider.EnqueueScript(TextScript("hello"));
        var history = new ConversationHistory();
        history.AddUser("hi");

        var cts = new CancellationTokenSource();
        cts.Cancel();

        var events = await CollectEventsAsync(sink, loop.RunAsync(history, sink, cts.Token));

        events.Should().Contain(e => e is AgentEvent.CancelledEvent);
        events.Should().NotContain(e => e is AgentEvent.AgentDoneEvent);
    }

    [Fact]
    public async Task RunAsync_EmptyTextWithToolCall_StillWorks()
    {
        var (loop, provider, sink) = CreateLoop();
        provider.EnqueueScript(ToolCallScript("call_1", "echo", "{}"));
        provider.EnqueueScript(TextScript("done"));
        var history = new ConversationHistory();
        history.AddUser("hi");

        var events = await CollectEventsAsync(sink, loop.RunAsync(history, sink, CancellationToken.None));

        var textDeltas = events.OfType<AgentEvent.TextDeltaEvent>().ToList();
        textDeltas.Should().HaveCount(1);
        textDeltas[0].Text.Should().Be("done");
        events.Should().Contain(e => e is AgentEvent.AgentDoneEvent);
    }
}
