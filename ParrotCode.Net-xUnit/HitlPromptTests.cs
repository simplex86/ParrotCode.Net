using System.Text.Json;
using ParrotCode;

namespace ParrotCode.xUnit;

/// <summary>
/// HitlPrompt 单元测试（迭代 7c-3：内联提示，不用模态 Dialog）。
///
/// 仅测无需主线程的分支：
/// - 缓存命中 → 直接返回 AllowSession，不调 UI 回调
/// - 取消 token → 直接返回 Deny，不调 UI 回调
/// - IsAllowedThisSession 默认 false
///
/// UI 回调分支（Button 点击）依赖 Application.Invoke 的主线程调度，
/// 难以在 xUnit 单元测试中模拟，通过 BatchToolExecutorHitlTests
/// 的 FakeHitlGate 间接验证 BatchToolExecutor 与 IHitlGate 的集成行为。
/// </summary>
public class HitlPromptTests
{
    private static ToolCall MakeCall(string name = "write_file", string argsJson = "{\"path\":\"/tmp/a\"}") =>
        new("call_1", name, JsonDocument.Parse(argsJson).RootElement.Clone());

    [Fact]
    public async Task RequestAsync_CancelledToken_ReturnsDeny()
    {
        // 取消时立即返回 Deny，不应调 UI 回调
        var callbackCalled = false;
        var prompt = new HitlPrompt((call, ct) =>
        {
            callbackCalled = true;
            return Task.FromResult(new HitlDecision(HitlChoice.AllowOnce));
        });

        var cts = new CancellationTokenSource();
        cts.Cancel();

        var decision = await prompt.RequestAsync(MakeCall(), cts.Token);

        decision.Should().NotBeNull();
        decision!.Choice.Should().Be(HitlChoice.Deny);
        decision.Reason.Should().Contain("取消");
        callbackCalled.Should().BeFalse("已取消时不应调 UI 回调");
    }

    [Fact]
    public async Task RequestAsync_CacheHit_ReturnsAllowSession_WithoutCallback()
    {
        // 缓存命中分支不调 UI 回调（直接返回 AllowSession）
        var callbackCalled = false;
        var prompt = new HitlPrompt((call, ct) =>
        {
            callbackCalled = true;
            return Task.FromResult(new HitlDecision(HitlChoice.AllowOnce));
        });

        prompt.IsAllowedThisSession("write_file").Should().BeFalse();

        // 用反射注入会话缓存（模拟 S 键已按下的结果）
        var cacheField = typeof(HitlPrompt).GetField("_sessionCache",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        cacheField.Should().NotBeNull();
        var cache = (System.Collections.Concurrent.ConcurrentDictionary<string, byte>)cacheField!.GetValue(prompt)!;
        cache["write_file"] = 0;

        // 缓存命中 → 直接返回 AllowSession，不调 UI 回调
        var decision = await prompt.RequestAsync(MakeCall("write_file"), CancellationToken.None);

        decision.Should().NotBeNull();
        decision!.Choice.Should().Be(HitlChoice.AllowSession);
        decision.IsAllowed.Should().BeTrue();
        prompt.IsAllowedThisSession("write_file").Should().BeTrue();
        callbackCalled.Should().BeFalse("缓存命中时不应调 UI 回调");
    }

    [Fact]
    public async Task RequestAsync_Callback_ReturnsDecision_AndCaches()
    {
        // UI 回调返回 AllowSession → 应缓存
        var prompt = new HitlPrompt((call, ct) =>
            Task.FromResult(new HitlDecision(HitlChoice.AllowSession)));

        var decision = await prompt.RequestAsync(MakeCall("edit_file"), CancellationToken.None);

        decision.Should().NotBeNull();
        decision!.Choice.Should().Be(HitlChoice.AllowSession);
        prompt.IsAllowedThisSession("edit_file").Should().BeTrue("AllowSession 应缓存");
    }

    [Fact]
    public async Task RequestAsync_Callback_Deny_DoesNotCache()
    {
        // UI 回调返回 Deny → 不应缓存
        var prompt = new HitlPrompt((call, ct) =>
            Task.FromResult(HitlDecision.Deny("用户拒绝")));

        var decision = await prompt.RequestAsync(MakeCall("run_command"), CancellationToken.None);

        decision.Should().NotBeNull();
        decision!.Choice.Should().Be(HitlChoice.Deny);
        decision.IsAllowed.Should().BeFalse();
        prompt.IsAllowedThisSession("run_command").Should().BeFalse("Deny 不应缓存");
    }

    [Fact]
    public void IsAllowedThisSession_NotCached_ReturnsFalse()
    {
        var prompt = new HitlPrompt((call, ct) => Task.FromResult(new HitlDecision(HitlChoice.AllowOnce)));

        prompt.IsAllowedThisSession("any_tool").Should().BeFalse();
    }

    [Fact]
    public void Constructor_NullCallback_Throws()
    {
        var act = () => new HitlPrompt(null!);

        act.Should().Throw<ArgumentNullException>();
    }
}
