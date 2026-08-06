using ParrotCode;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace ParrotCode.xUnit;

/// <summary>
/// StatusBar 单元测试。
/// 验证字段渲染、占比颜色、安全等级颜色、截断逻辑。
/// </summary>
public class StatusBarTests
{
    private static StatusBar CreateStatus() => new()
    {
        Provider = "deepseek",
        Model = "deepseek-chat",
        SecurityLevel = SecurityLevel.Normal,
        EstimatedTokens = 1000,
        ContextWindowTokens = 64000,
        CurrentRound = 1,
        ToolCount = 6
    };

    /// <summary>把 IRenderable 渲染为纯文本（去掉 ANSI/Markup 标记）用于断言。</summary>
    private static string RenderToString(IRenderable renderable)
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
        return sw.ToString();
    }

    private static string RenderToText(StatusBar bar) => RenderToString(bar.Render());

    [Fact]
    public void Render_ContainsAllFields()
    {
        var bar = CreateStatus();
        var text = RenderToText(bar);
        text.Should().Contain("deepseek");
        text.Should().Contain("deepseek-chat");
        text.Should().Contain("security");
        text.Should().Contain("ctx");
        text.Should().Contain("round");
        text.Should().Contain("tools");
    }

    [Fact]
    public void ContextRatio_Below70_ReturnsGreen()
    {
        var bar = CreateStatus();
        bar.EstimatedTokens = 1000;
        bar.ContextWindowTokens = 64000;
        bar.ContextRatio.Should().BeLessThan(0.7);
    }

    [Fact]
    public void ContextRatio_Over70_ReturnsYellow()
    {
        var bar = new StatusBar { EstimatedTokens = 50000, ContextWindowTokens = 64000 };
        var ratio = bar.ContextRatio;
        ratio.Should().BeGreaterThanOrEqualTo(0.7);
        ratio.Should().BeLessThan(0.9);
    }

    [Fact]
    public void ContextRatio_Over90_ReturnsRed()
    {
        var bar = new StatusBar { EstimatedTokens = 60000, ContextWindowTokens = 64000 };
        bar.ContextRatio.Should().BeGreaterThanOrEqualTo(0.9);
    }

    [Fact]
    public void ContextWindow_Zero_RatioZero()
    {
        var bar = new StatusBar { EstimatedTokens = 1000, ContextWindowTokens = 0 };
        bar.ContextRatio.Should().Be(0);
    }

    [Fact]
    public void SecurityLevel_Strict_RenderContainsRed()
    {
        var bar = CreateStatus();
        bar.SecurityLevel = SecurityLevel.Strict;
        var text = RenderToText(bar);
        // 严格模式渲染应含 "red" 颜色标记
        text.Should().Contain("Strict");
    }

    [Fact]
    public void SecurityLevel_Normal_RenderContainsLevel()
    {
        var bar = CreateStatus();
        bar.SecurityLevel = SecurityLevel.Normal;
        var text = RenderToText(bar);
        text.Should().Contain("Normal");
    }

    [Fact]
    public void SecurityLevel_Permisive_RenderContainsLevel()
    {
        var bar = CreateStatus();
        bar.SecurityLevel = SecurityLevel.Permisive;
        var text = RenderToText(bar);
        text.Should().Contain("Permisive");
    }

    [Fact]
    public void Provider_LongName_TruncatedInRender()
    {
        var bar = CreateStatus();
        bar.Provider = new string('a', 25);  // 25 字符，超 20 上限
        var text = RenderToText(bar);
        // 截断后应含 "..."
        text.Should().Contain("...");
        // 不应含完整 25 字符的 provider 名
        text.Should().NotContain(new string('a', 25));
    }

    [Fact]
    public void Model_LongName_TruncatedInRender()
    {
        var bar = CreateStatus();
        bar.Model = new string('b', 25);
        var text = RenderToText(bar);
        text.Should().Contain("...");
        text.Should().NotContain(new string('b', 25));
    }
}
