using System.Text.Json;
using ParrotCode;

namespace ParrotCode.xUnit;

/// <summary>
/// HitlPrompt 单元测试（迭代 7c-3：委托 HitlDialog 模态对话框）。
///
/// 仅测无需主线程的分支：
/// - 缓存命中 → 直接返回 AllowSession，不弹框
/// - 取消 token → 直接返回 Deny，不弹框
/// - IsAllowedThisSession 默认 false
///
/// 弹框分支（A/S/P/D/Esc）依赖 Application.Invoke + Application.Run 的嵌套事件循环，
/// 难以在 xUnit 单元测试中模拟（需 Terminal.Gui 主循环），通过 BatchToolExecutorHitlTests
/// 的 FakeHitlGate 间接验证 BatchToolExecutor 与 IHitlGate 的集成行为。
/// </summary>
public class HitlPromptTests
{
    private static ToolCall MakeCall(string name = "write_file", string argsJson = "{\"path\":\"/tmp/a\"}") =>
        new("call_1", name, JsonDocument.Parse(argsJson).RootElement.Clone());

    [Fact]
    public async Task RequestAsync_CancelledToken_ReturnsDeny()
    {
        // 取消时立即返回 Deny，不应调 dialogFactory（不弹框）
        var factoryCalled = false;
        var prompt = new HitlPrompt(_ => { factoryCalled = true; return null!; });

        var cts = new CancellationTokenSource();
        cts.Cancel();

        var decision = await prompt.RequestAsync(MakeCall(), cts.Token);

        decision.Should().NotBeNull();
        decision!.Choice.Should().Be(HitlChoice.Deny);
        decision.Reason.Should().Contain("取消");
        factoryCalled.Should().BeFalse("已取消时不应调 dialogFactory");
    }

    [Fact]
    public async Task RequestAsync_CacheHit_ReturnsAllowSession_WithoutDialog()
    {
        // 缓存命中分支不调 dialogFactory（直接返回 AllowSession），
        // 所以工厂返回 null 也不会被触发——避免依赖 Terminal.Gui 主线程
        var factoryCalled = false;
        var prompt = new HitlPrompt(_ => { factoryCalled = true; return null!; });

        // 通过 IsAllowedThisSession 验证初始状态
        prompt.IsAllowedThisSession("write_file").Should().BeFalse();

        // 用反射注入会话缓存（模拟 S 键已按下的结果）
        var cacheField = typeof(HitlPrompt).GetField("_sessionCache",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        cacheField.Should().NotBeNull();
        var cache = (System.Collections.Concurrent.ConcurrentDictionary<string, byte>)cacheField!.GetValue(prompt)!;
        cache["write_file"] = 0;

        // 缓存命中 → 直接返回 AllowSession，不弹框（dialogFactory 不会被调）
        var decision = await prompt.RequestAsync(MakeCall("write_file"), CancellationToken.None);

        decision.Should().NotBeNull();
        decision!.Choice.Should().Be(HitlChoice.AllowSession);
        decision.IsAllowed.Should().BeTrue();
        prompt.IsAllowedThisSession("write_file").Should().BeTrue();
        factoryCalled.Should().BeFalse("缓存命中时不应调 dialogFactory");
    }

    [Fact]
    public void IsAllowedThisSession_NotCached_ReturnsFalse()
    {
        var prompt = new HitlPrompt(_ => null!);

        prompt.IsAllowedThisSession("any_tool").Should().BeFalse();
    }

    [Fact]
    public void Constructor_NullFactory_Throws()
    {
        var act = () => new HitlPrompt(null!);

        act.Should().Throw<ArgumentNullException>();
    }
}
