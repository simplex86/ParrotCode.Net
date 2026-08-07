using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using ParrotCode;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace ParrotCode.xUnit;

/// <summary>
/// ConsoleEventRenderer 单元测试（流式渲染）。
/// 验证各事件类型的 Panel/Markup 渲染输出。
/// </summary>
public class ConsoleEventRendererTests
{
    /// <summary>假控制台：捕获所有输出（含 IRenderable 渲染为纯文本）。</summary>
    private sealed class CaptureConsole : IConsole
    {
        private readonly StringBuilder _output = new();

        public string Output => _output.ToString();

        public ConsoleKeyInfo ReadKey(bool intercept) =>
            throw new NotSupportedException("ConsoleEventRenderer 不需要读按键");

        public void Write(string text) => _output.Append(text);
        public void WriteLine() => _output.AppendLine();
        public void WriteMarkup(string markup) => _output.Append(markup);
        public void WriteMarkupLine(string markup) => _output.AppendLine(markup);

        public void Write(IRenderable renderable)
        {
            using var sw = new StringWriter();
            var console = AnsiConsole.Create(new AnsiConsoleSettings
            {
                Out = new AnsiConsoleOutput(sw),
                ColorSystem = ColorSystemSupport.NoColors,
                Ansi = AnsiSupport.No,
                Interactive = InteractionSupport.No
            });
            console.Write(renderable);
            _output.Append(sw.ToString());
        }
    }

    private static ToolCall CreateToolCall(string name = "echo", string argsJson = "{}") =>
        new("call_1", name, JsonDocument.Parse(argsJson).RootElement.Clone());

    private static async Task<string> RenderEventsAsync(params AgentEvent[] events)
    {
        var console = new CaptureConsole();
        var renderer = new ConsoleEventRenderer(console);
        var channel = Channel.CreateUnbounded<AgentEvent>();
        foreach (var evt in events)
            await channel.Writer.WriteAsync(evt);
        channel.Writer.Complete();

        await renderer.RenderAsync(channel.Reader, CancellationToken.None);
        return console.Output;
    }

    [Fact]
    public async Task Render_TextDelta_WritesToConsole()
    {
        var output = await RenderEventsAsync(
            new AgentEvent.TextDeltaEvent("hello world"),
            new AgentEvent.AgentDoneEvent(null));

        output.Should().Contain("hello world");
    }

    [Fact]
    public async Task Render_ToolBlocked_WritesRedLine()
    {
        // 7a 预留分支验证
        var output = await RenderEventsAsync(
            new AgentEvent.ToolBlockedEvent(CreateToolCall("write_file"), "用户拒绝执行"),
            new AgentEvent.AgentDoneEvent(null));

        output.Should().Contain("被拦截");
        output.Should().Contain("write_file");
        output.Should().Contain("用户拒绝执行");
    }

    [Fact]
    public async Task Render_AgentDone_WritesNewline()
    {
        var output = await RenderEventsAsync(
            new AgentEvent.TextDeltaEvent("text"),
            new AgentEvent.AgentDoneEvent(null));

        // AgentDone 后应换行
        output.Should().Contain("text");
        output.Should().EndWith("\n");
    }

    [Fact]
    public async Task Render_Error_WritesRedError()
    {
        var output = await RenderEventsAsync(
            new AgentEvent.ErrorEvent("something broke", null));

        output.Should().Contain("错误");
        output.Should().Contain("something broke");
    }

    [Fact]
    public async Task Render_ToolResultSuccess_WritesGreenCheck()
    {
        var output = await RenderEventsAsync(
            new AgentEvent.ToolResultEvent(CreateToolCall(), ToolResult.Ok("success")),
            new AgentEvent.AgentDoneEvent(null));

        output.Should().Contain("✓");
        output.Should().Contain("success");
    }

    [Fact]
    public async Task Render_ToolResultFail_WritesRedCross()
    {
        var output = await RenderEventsAsync(
            new AgentEvent.ToolResultEvent(CreateToolCall(), ToolResult.Fail("failure")),
            new AgentEvent.AgentDoneEvent(null));

        output.Should().Contain("✗");
        output.Should().Contain("failure");
    }

    [Fact]
    public async Task Render_ToolCallStart_WritesToolName()
    {
        var output = await RenderEventsAsync(
            new AgentEvent.ToolCallStartEvent(CreateToolCall("read_file", "{\"path\":\"test.txt\"}")),
            new AgentEvent.AgentDoneEvent(null));

        output.Should().Contain("read_file");
        output.Should().Contain("→");
    }

    [Fact]
    public async Task Render_MaxRounds_WritesWarning()
    {
        var output = await RenderEventsAsync(
            new AgentEvent.MaxRoundsReachedEvent(10));

        output.Should().Contain("最大轮次");
        output.Should().Contain("10");
    }

    [Fact]
    public async Task Render_Cancelled_WritesCancelled()
    {
        var output = await RenderEventsAsync(
            new AgentEvent.CancelledEvent());

        output.Should().Contain("已取消");
    }

    [Fact]
    public async Task Render_Warning_WritesWarningMessage()
    {
        var output = await RenderEventsAsync(
            new AgentEvent.WarningEvent("tool timeout"),
            new AgentEvent.AgentDoneEvent(null));

        output.Should().Contain("⚠");
        output.Should().Contain("tool timeout");
    }

    [Fact]
    public async Task Render_RoundStart_WritesRoundSeparator()
    {
        var output = await RenderEventsAsync(
            new AgentEvent.RoundStartEvent(1),
            new AgentEvent.RoundEndEvent(1),
            new AgentEvent.AgentDoneEvent(null));

        // RoundStart 现在渲染 "── Round N ──" 分隔线
        output.Should().Contain("Round");
        output.Should().Contain("1");
    }

    [Fact]
    public async Task Render_AssistantMessage_NoOutput()
    {
        var output = await RenderEventsAsync(
            new AgentEvent.AssistantMessageEvent("full text"),
            new AgentEvent.AgentDoneEvent(null));

        // AssistantMessage 在降级模式不渲染（文本已在 TextDelta 中输出）
        output.Should().NotContain("full text");
    }
}
