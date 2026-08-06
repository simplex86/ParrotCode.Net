using System.Text.Json;
using ParrotCode;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace ParrotCode.xUnit;

/// <summary>
/// HitlPrompt 单元测试（方案 C：用假 render/readKey 回调断言行为）。
/// 覆盖 A/S/P/D 四键映射、缓存命中、取消、未知键安全默认、render 调用次数。
/// </summary>
public class HitlPromptTests
{
    private static ToolCall MakeCall(string name = "write_file", string argsJson = "{\"path\":\"/tmp/a\"}") =>
        new("call_1", name, JsonDocument.Parse(argsJson).RootElement.Clone());

    /// <summary>收集 _render 回调的所有 IRenderable，便于断言渲染顺序。</summary>
    private sealed class RenderCollector
    {
        public List<IRenderable?> Rendered { get; } = new();

        public void Collect(IRenderable? r) => Rendered.Add(r);
    }

    [Fact]
    public async Task RequestAsync_ReadKeyA_ReturnsAllowOnce()
    {
        var prompt = new HitlPrompt(readKey: _ => ConsoleKey.A);

        var decision = await prompt.RequestAsync(MakeCall(), CancellationToken.None);

        decision.Should().NotBeNull();
        decision!.Choice.Should().Be(HitlChoice.AllowOnce);
        decision.IsAllowed.Should().BeTrue();
        decision.Reason.Should().BeNull();
    }

    [Fact]
    public async Task RequestAsync_ReadKeyS_ReturnsAllowSession_AndCaches()
    {
        var prompt = new HitlPrompt(readKey: _ => ConsoleKey.S);

        var decision = await prompt.RequestAsync(MakeCall("write_file"), CancellationToken.None);

        decision!.Choice.Should().Be(HitlChoice.AllowSession);
        prompt.IsAllowedThisSession("write_file").Should().BeTrue();
    }

    [Fact]
    public async Task RequestAsync_ReadKeyD_ReturnsDenyWithReason()
    {
        var prompt = new HitlPrompt(readKey: _ => ConsoleKey.D);

        var decision = await prompt.RequestAsync(MakeCall(), CancellationToken.None);

        decision!.Choice.Should().Be(HitlChoice.Deny);
        decision.IsAllowed.Should().BeFalse();
        decision.Reason.Should().NotBeNull();
        decision.Reason.Should().Contain("拒绝");
    }

    [Fact]
    public async Task RequestAsync_ReadKeyP_CachesAsSession()
    {
        var prompt = new HitlPrompt(readKey: _ => ConsoleKey.P);

        var decision = await prompt.RequestAsync(MakeCall("edit_file"), CancellationToken.None);

        decision!.Choice.Should().Be(HitlChoice.AllowPermanent);
        // 7b AllowPermanent 退化为会话级——应缓存
        prompt.IsAllowedThisSession("edit_file").Should().BeTrue();
    }

    [Fact]
    public async Task RequestAsync_CacheHit_DoesNotPrompt()
    {
        var renderCalls = 0;
        var readKeyCalls = 0;
        var prompt = new HitlPrompt(
            render: _ => renderCalls++,
            readKey: _ => { readKeyCalls++; return ConsoleKey.S; });

        // 第一次：S 键缓存
        await prompt.RequestAsync(MakeCall("write_file"), CancellationToken.None);
        var firstRenderCalls = renderCalls;
        var firstReadKeyCalls = readKeyCalls;

        // 第二次：缓存命中，不应调 render/readKey
        var decision = await prompt.RequestAsync(MakeCall("write_file"), CancellationToken.None);

        decision!.Choice.Should().Be(HitlChoice.AllowSession);
        renderCalls.Should().Be(firstRenderCalls, "缓存命中不应调 render");
        readKeyCalls.Should().Be(firstReadKeyCalls, "缓存命中不应调 readKey");
    }

    [Fact]
    public async Task RequestAsync_CancelledToken_ReturnsDeny()
    {
        var renderCalls = 0;
        var readKeyCalls = 0;
        var prompt = new HitlPrompt(
            render: _ => renderCalls++,
            readKey: _ => { readKeyCalls++; return ConsoleKey.A; });
        var cts = new CancellationTokenSource();
        cts.Cancel();

        var decision = await prompt.RequestAsync(MakeCall(), cts.Token);

        decision!.Choice.Should().Be(HitlChoice.Deny);
        decision.Reason.Should().Contain("取消");
        renderCalls.Should().Be(0, "已取消时不应调 render");
        readKeyCalls.Should().Be(0, "已取消时不应调 readKey");
    }

    [Fact]
    public async Task RequestAsync_RendersPromptThenResult()
    {
        var collector = new RenderCollector();
        var prompt = new HitlPrompt(
            render: collector.Collect,
            readKey: _ => ConsoleKey.A);

        await prompt.RequestAsync(MakeCall(), CancellationToken.None);

        // 应调 render 两次：先提示 Panel，后结果 Markup
        collector.Rendered.Should().HaveCount(2);
        collector.Rendered[0].Should().BeOfType<Panel>("第一次渲染 HITL 提示 Panel");
        collector.Rendered[1].Should().BeOfType<Markup>("第二次渲染决策结果 Markup");
    }

    [Theory]
    [InlineData(ConsoleKey.X)]
    [InlineData(ConsoleKey.Enter)]
    [InlineData(ConsoleKey.Spacebar)]
    public async Task RequestAsync_UnknownKey_ReturnsDeny(ConsoleKey unknownKey)
    {
        var prompt = new HitlPrompt(readKey: _ => unknownKey);

        var decision = await prompt.RequestAsync(MakeCall(), CancellationToken.None);

        decision!.Choice.Should().Be(HitlChoice.Deny, "未知键应安全默认拒绝");
        decision.IsAllowed.Should().BeFalse();
    }

    [Fact]
    public void IsAllowedThisSession_NotCached_ReturnsFalse()
    {
        var prompt = new HitlPrompt(readKey: _ => ConsoleKey.A);

        prompt.IsAllowedThisSession("any_tool").Should().BeFalse();
    }

    [Fact]
    public async Task RequestAsync_NoRenderCallback_DoesNotThrow()
    {
        // render=null 时使用默认空回调，应不抛异常
        var prompt = new HitlPrompt(readKey: _ => ConsoleKey.A);

        var act = async () => await prompt.RequestAsync(MakeCall(), CancellationToken.None);

        await act.Should().NotThrowAsync();
    }
}
