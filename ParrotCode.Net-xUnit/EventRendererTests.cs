using System.Text.Json;
using ParrotCode;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace ParrotCode.xUnit;

/// <summary>
/// EventRenderer 单元测试。
/// 验证事件翻译、累积逻辑、BuildActive/BuildCommitted、Reset、SetTransient 扩展点。
/// </summary>
public class EventRendererTests
{
    /// <summary>把 IRenderable 渲染为纯文本（去掉 ANSI/Markup 标记）用于断言。</summary>
    private static string RenderToString(IRenderable? renderable)
    {
        if (renderable is null) return string.Empty;
        using var sw = new StringWriter();
        var console = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Out = new AnsiConsoleOutput(sw),
            ColorSystem = ColorSystemSupport.NoColors,
            Ansi = AnsiSupport.No,
            Interactive = InteractionSupport.No
        });
        console.Write(renderable);
        return sw.ToString();
    }

    private static ToolCall CreateToolCall(string name = "echo", string argsJson = "{}") =>
        new("call_1", name, JsonDocument.Parse(argsJson).RootElement.Clone());

    private static ToolResult CreateOkResult(string content = "ok") =>
        ToolResult.Ok(content);

    private static ToolResult CreateFailResult(string error = "boom") =>
        ToolResult.Fail(error);

    [Fact]
    public void Render_RoundStart_SetsCurrentRound()
    {
        var renderer = new EventRenderer();
        var result = renderer.Render(new AgentEvent.RoundStartEvent(3));

        renderer.CurrentRound.Should().Be(3);
        result.Should().NotBeNull();
        result.Should().BeOfType<Markup>();
    }

    [Fact]
    public void Render_RoundStart_ClearsBufferAndPending()
    {
        var renderer = new EventRenderer();
        renderer.Render(new AgentEvent.TextDeltaEvent("foo"));
        renderer.Render(new AgentEvent.ToolCallStartEvent(CreateToolCall()));

        renderer.CurrentText.Should().Be("foo");
        renderer.PendingCount.Should().Be(1);

        // 新轮开始应清空
        renderer.Render(new AgentEvent.RoundStartEvent(2));

        renderer.CurrentText.Should().BeEmpty();
        renderer.PendingCount.Should().Be(0);
    }

    [Fact]
    public void Render_TextDelta_AccumulatesToBuffer()
    {
        var renderer = new EventRenderer();
        var r1 = renderer.Render(new AgentEvent.TextDeltaEvent("foo"));
        var r2 = renderer.Render(new AgentEvent.TextDeltaEvent("bar"));

        renderer.CurrentText.Should().Be("foobar");
        r1.Should().BeNull();
        r2.Should().BeNull();
    }

    [Fact]
    public void Render_ToolCallStart_AddsToPending()
    {
        var renderer = new EventRenderer();
        var result = renderer.Render(new AgentEvent.ToolCallStartEvent(CreateToolCall()));

        renderer.PendingCount.Should().Be(1);
        result.Should().BeNull();  // 由 BuildActive 统一输出
    }

    [Fact]
    public void Render_ToolResultSuccess_AddsPanel()
    {
        var renderer = new EventRenderer();
        var call = CreateToolCall();
        var result = renderer.Render(new AgentEvent.ToolResultEvent(call, CreateOkResult("success content")));

        renderer.PendingCount.Should().Be(1);
        result.Should().NotBeNull();
        result.Should().BeOfType<Panel>();
    }

    [Fact]
    public void Render_ToolResultFail_AddsPanel()
    {
        var renderer = new EventRenderer();
        var call = CreateToolCall();
        var result = renderer.Render(new AgentEvent.ToolResultEvent(call, CreateFailResult("boom")));

        renderer.PendingCount.Should().Be(1);
        result.Should().BeOfType<Panel>();
    }

    [Fact]
    public void Render_ToolBlocked_AddsRedPanel()
    {
        // 7a 预留分支验证：主路径不触发，但 EventRenderer 已处理此事件类型
        var renderer = new EventRenderer();
        var call = CreateToolCall("write_file");
        var result = renderer.Render(new AgentEvent.ToolBlockedEvent(call, "用户拒绝执行"));

        renderer.PendingCount.Should().Be(1);
        result.Should().BeOfType<Panel>();
    }

    [Fact]
    public void Render_AgentDone_ReturnsMarkup()
    {
        var renderer = new EventRenderer();
        var result = renderer.Render(new AgentEvent.AgentDoneEvent("final"));

        result.Should().NotBeNull();
        result.Should().BeOfType<Markup>();
    }

    [Fact]
    public void Render_MaxRounds_ReturnsMarkup()
    {
        var renderer = new EventRenderer();
        var result = renderer.Render(new AgentEvent.MaxRoundsReachedEvent(10));

        result.Should().NotBeNull();
        result.Should().BeOfType<Markup>();
    }

    [Fact]
    public void Render_Error_ReturnsMarkup()
    {
        var renderer = new EventRenderer();
        var result = renderer.Render(new AgentEvent.ErrorEvent("something wrong", null));

        result.Should().NotBeNull();
        result.Should().BeOfType<Markup>();
    }

    [Fact]
    public void Render_Cancelled_ReturnsMarkup()
    {
        var renderer = new EventRenderer();
        var result = renderer.Render(new AgentEvent.CancelledEvent());

        result.Should().NotBeNull();
        result.Should().BeOfType<Markup>();
    }

    [Fact]
    public void Render_Warning_ReturnsMarkup()
    {
        var renderer = new EventRenderer();
        var result = renderer.Render(new AgentEvent.WarningEvent("warning msg"));

        result.Should().NotBeNull();
        result.Should().BeOfType<Markup>();
    }

    [Fact]
    public void Render_RoundEnd_ReturnsNull()
    {
        var renderer = new EventRenderer();
        var result = renderer.Render(new AgentEvent.RoundEndEvent(1));

        result.Should().BeNull();
    }

    [Fact]
    public void Render_AssistantMessage_ReturnsNull()
    {
        var renderer = new EventRenderer();
        var result = renderer.Render(new AgentEvent.AssistantMessageEvent("text"));

        result.Should().BeNull();
    }

    [Fact]
    public void Reset_ClearsBufferAndPending()
    {
        var renderer = new EventRenderer();
        renderer.Render(new AgentEvent.RoundStartEvent(3));
        renderer.Render(new AgentEvent.TextDeltaEvent("accumulated"));
        renderer.Render(new AgentEvent.ToolCallStartEvent(CreateToolCall()));

        renderer.Reset();

        renderer.CurrentText.Should().BeEmpty();
        renderer.PendingCount.Should().Be(0);
        renderer.CurrentRound.Should().Be(0);
    }

    [Fact]
    public void Reset_ClearsTransient()
    {
        var renderer = new EventRenderer();
        renderer.SetTransient(new Markup("[yellow]transient[/]"));

        renderer.Reset();

        // 验证 transient 被清除——BuildActive 不应含临时项
        var bar = new StatusBar();
        var active = renderer.BuildActive(bar);
        RenderToString(active).Should().NotContain("transient");
    }

    [Fact]
    public void BuildActive_IncludesStatusBarAndText()
    {
        var renderer = new EventRenderer();
        renderer.Render(new AgentEvent.RoundStartEvent(1));
        renderer.Render(new AgentEvent.TextDeltaEvent("hello"));

        var bar = new StatusBar { Provider = "test", Model = "test-model" };
        var active = renderer.BuildActive(bar);

        active.Should().NotBeNull();
        var text = RenderToString(active);
        text.Should().Contain("hello");
        text.Should().Contain("test");
        text.Should().Contain("test-model");
    }

    [Fact]
    public void BuildActive_LimitPendingTo5()
    {
        var renderer = new EventRenderer();
        renderer.Render(new AgentEvent.RoundStartEvent(1));
        // 加 7 个工具调用
        for (var i = 0; i < 7; i++)
        {
            renderer.Render(new AgentEvent.ToolCallStartEvent(CreateToolCall()));
        }

        var bar = new StatusBar();
        var active = renderer.BuildActive(bar);
        var text = RenderToString(active);

        // 应含 "...还有 2 个" 提示（7 - 5 = 2）
        text.Should().Contain("还有 2 个");
    }

    [Fact]
    public void BuildCommitted_ExcludesStatusBar()
    {
        var renderer = new EventRenderer();
        renderer.Render(new AgentEvent.RoundStartEvent(1));
        renderer.Render(new AgentEvent.TextDeltaEvent("committed text"));

        var bar = new StatusBar { Provider = "should_not_appear" };
        var committed = renderer.BuildCommitted();
        var text = RenderToString(committed);

        text.Should().Contain("committed text");
        text.Should().NotContain("should_not_appear");
    }

    [Fact]
    public void SetTransient_Null_BuildActiveExcludesTransient()
    {
        var renderer = new EventRenderer();
        renderer.SetTransient(null);

        var bar = new StatusBar();
        var active = renderer.BuildActive(bar);
        var text = RenderToString(active);

        // 无临时项，无 HITL 相关内容
        text.Should().NotContain("HITL");
    }

    [Fact]
    public void SetTransient_NonNull_BuildActiveIncludesTransient()
    {
        var renderer = new EventRenderer();
        renderer.SetTransient(new Markup("[yellow]HITL 提示[/]"));

        var bar = new StatusBar();
        var active = renderer.BuildActive(bar);
        var text = RenderToString(active);

        // 验证 7b 扩展点工作——临时项加入活跃区
        text.Should().Contain("HITL 提示");
    }
}
