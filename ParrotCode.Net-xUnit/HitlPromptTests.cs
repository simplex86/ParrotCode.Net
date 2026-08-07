using System.Text.Json;
using ParrotCode;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace ParrotCode.xUnit;

/// <summary>
/// HitlPrompt 单元测试（方案 A：流式渲染，注入 IConsole）。
/// 覆盖 A/S/P/D 四键映射、缓存命中、取消、未知键安全默认、IConsole 渲染调用。
/// 用 FakeConsole 注入预设按键，断言返回决策。
/// </summary>
public class HitlPromptTests
{
    private static ToolCall MakeCall(string name = "write_file", string argsJson = "{\"path\":\"/tmp/a\"}") =>
        new("call_1", name, JsonDocument.Parse(argsJson).RootElement.Clone());

    /// <summary>假 IConsole 实现：预设按键队列 + 收集输出。</summary>
    private sealed class FakeConsole : IConsole
    {
        private readonly Queue<ConsoleKeyInfo> _keys = new();
        public List<IRenderable> Rendered { get; } = new();

        public void EnqueueKey(ConsoleKey key) =>
            _keys.Enqueue(new ConsoleKeyInfo('\0', key, false, false, false));

        public ConsoleKeyInfo ReadKey(bool intercept)
        {
            if (_keys.Count == 0)
                throw new InvalidOperationException("FakeConsole: 无可用按键");
            return _keys.Dequeue();
        }

        public void Write(string text) { }
        public void WriteLine() { }
        public void WriteMarkup(string markup) { }
        public void WriteMarkupLine(string markup) { }
        public void Write(IRenderable renderable) => Rendered.Add(renderable);
    }

    [Fact]
    public async Task RequestAsync_ReadKeyA_ReturnsAllowOnce()
    {
        var console = new FakeConsole();
        console.EnqueueKey(ConsoleKey.A);
        var prompt = new HitlPrompt(console);

        var decision = await prompt.RequestAsync(MakeCall(), CancellationToken.None);

        decision.Should().NotBeNull();
        decision!.Choice.Should().Be(HitlChoice.AllowOnce);
        decision.IsAllowed.Should().BeTrue();
        decision.Reason.Should().BeNull();
    }

    [Fact]
    public async Task RequestAsync_ReadKeyS_ReturnsAllowSession_AndCaches()
    {
        var console = new FakeConsole();
        console.EnqueueKey(ConsoleKey.S);
        var prompt = new HitlPrompt(console);

        var decision = await prompt.RequestAsync(MakeCall("write_file"), CancellationToken.None);

        decision!.Choice.Should().Be(HitlChoice.AllowSession);
        prompt.IsAllowedThisSession("write_file").Should().BeTrue();
    }

    [Fact]
    public async Task RequestAsync_ReadKeyD_ReturnsDenyWithReason()
    {
        var console = new FakeConsole();
        console.EnqueueKey(ConsoleKey.D);
        var prompt = new HitlPrompt(console);

        var decision = await prompt.RequestAsync(MakeCall(), CancellationToken.None);

        decision!.Choice.Should().Be(HitlChoice.Deny);
        decision.IsAllowed.Should().BeFalse();
        decision.Reason.Should().NotBeNull();
        decision.Reason.Should().Contain("拒绝");
    }

    [Fact]
    public async Task RequestAsync_ReadKeyP_CachesAsSession()
    {
        var console = new FakeConsole();
        console.EnqueueKey(ConsoleKey.P);
        var prompt = new HitlPrompt(console);

        var decision = await prompt.RequestAsync(MakeCall("edit_file"), CancellationToken.None);

        decision!.Choice.Should().Be(HitlChoice.AllowPermanent);
        prompt.IsAllowedThisSession("edit_file").Should().BeTrue();
    }

    [Fact]
    public async Task RequestAsync_CacheHit_DoesNotPrompt()
    {
        var console = new FakeConsole();
        console.EnqueueKey(ConsoleKey.S);
        var prompt = new HitlPrompt(console);

        // 第一次：S 键缓存
        await prompt.RequestAsync(MakeCall("write_file"), CancellationToken.None);
        var firstRenderCount = console.Rendered.Count;

        // 第二次：缓存命中，不应调 Write/ReadKey
        var decision = await prompt.RequestAsync(MakeCall("write_file"), CancellationToken.None);

        decision!.Choice.Should().Be(HitlChoice.AllowSession);
        console.Rendered.Count.Should().Be(firstRenderCount, "缓存命中不应调 Write");
    }

    [Fact]
    public async Task RequestAsync_CancelledToken_ReturnsDeny()
    {
        var console = new FakeConsole();
        var prompt = new HitlPrompt(console);
        var cts = new CancellationTokenSource();
        cts.Cancel();

        var decision = await prompt.RequestAsync(MakeCall(), cts.Token);

        decision!.Choice.Should().Be(HitlChoice.Deny);
        decision.Reason.Should().Contain("取消");
        console.Rendered.Should().BeEmpty("已取消时不应调 Write");
    }

    [Fact]
    public async Task RequestAsync_RendersPromptThenResult()
    {
        var console = new FakeConsole();
        console.EnqueueKey(ConsoleKey.A);
        var prompt = new HitlPrompt(console);

        await prompt.RequestAsync(MakeCall(), CancellationToken.None);

        // 应调 Write 两次：先 HITL 提示 Panel，后决策结果 Markup
        console.Rendered.Should().HaveCount(2);
        console.Rendered[0].Should().BeOfType<Panel>("第一次渲染 HITL 提示 Panel");
        console.Rendered[1].Should().BeOfType<Markup>("第二次渲染决策结果 Markup");
    }

    [Theory]
    [InlineData(ConsoleKey.X)]
    [InlineData(ConsoleKey.Enter)]
    [InlineData(ConsoleKey.Spacebar)]
    public async Task RequestAsync_UnknownKey_ReturnsDeny(ConsoleKey unknownKey)
    {
        var console = new FakeConsole();
        console.EnqueueKey(unknownKey);
        var prompt = new HitlPrompt(console);

        var decision = await prompt.RequestAsync(MakeCall(), CancellationToken.None);

        decision!.Choice.Should().Be(HitlChoice.Deny, "未知键应安全默认拒绝");
        decision.IsAllowed.Should().BeFalse();
    }

    [Fact]
    public void IsAllowedThisSession_NotCached_ReturnsFalse()
    {
        var prompt = new HitlPrompt();

        prompt.IsAllowedThisSession("any_tool").Should().BeFalse();
    }

    [Fact]
    public void BuildPromptPanel_ReturnsPanelWithCallInfo()
    {
        var call = MakeCall("write_file", "{\"path\":\"/tmp/a\"}");

        var panel = HitlPrompt.BuildPromptPanel(call);

        panel.Should().NotBeNull();
        panel.Should().BeOfType<Panel>();
    }

    [Fact]
    public void BuildResultMarkup_Allow_ReturnsMarkup()
    {
        var markup = HitlPrompt.BuildResultMarkup(HitlChoice.AllowOnce);

        markup.Should().NotBeNull();
        markup.Should().BeOfType<Markup>();
    }

    [Fact]
    public void BuildResultMarkup_Deny_ReturnsMarkup()
    {
        var markup = HitlPrompt.BuildResultMarkup(HitlChoice.Deny);

        markup.Should().NotBeNull();
        markup.Should().BeOfType<Markup>();
    }

    [Theory]
    [InlineData(ConsoleKey.A, HitlChoice.AllowOnce)]
    [InlineData(ConsoleKey.S, HitlChoice.AllowSession)]
    [InlineData(ConsoleKey.P, HitlChoice.AllowPermanent)]
    [InlineData(ConsoleKey.D, HitlChoice.Deny)]
    public void MapKeyToChoice_ASDF_ReturnsCorrectChoice(ConsoleKey key, HitlChoice expected)
    {
        HitlPrompt.MapKeyToChoice(key).Should().Be(expected);
    }

    [Theory]
    [InlineData(ConsoleKey.X)]
    [InlineData(ConsoleKey.Enter)]
    [InlineData(ConsoleKey.Spacebar)]
    public void MapKeyToChoice_UnknownKey_ReturnsDeny(ConsoleKey unknownKey)
    {
        HitlPrompt.MapKeyToChoice(unknownKey).Should().Be(HitlChoice.Deny, "未知键应安全默认拒绝");
    }
}
