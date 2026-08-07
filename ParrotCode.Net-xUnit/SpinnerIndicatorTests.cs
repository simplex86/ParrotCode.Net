using ParrotCode;
using Terminal.Gui;

namespace ParrotCode.xUnit;

/// <summary>
/// SpinnerIndicator 单元测试（迭代 7c-3）。
/// 覆盖 Start/Stop 动画控制、Verb 属性、Visible 状态。
/// 使用 TerminalGuiFixture 确保 Application 已初始化（AddTimeout 需要）。
/// </summary>
[Collection("Terminal.Gui")]
public class SpinnerIndicatorTests
{
    [Fact]
    public void Constructor_DefaultVerb_IsThinking()
    {
        var spinner = new SpinnerIndicator();

        spinner.Verb.Should().Be("Thinking");
    }

    [Fact]
    public void Constructor_DefaultInvisible()
    {
        var spinner = new SpinnerIndicator();

        spinner.Visible.Should().BeFalse("默认应隐藏");
    }

    [Fact]
    public void Start_SetsVisible_AndShowsText()
    {
        var spinner = new SpinnerIndicator();

        spinner.Start();

        spinner.Visible.Should().BeTrue("Start 后应可见");
        spinner.Text.Should().NotBeNullOrEmpty("Start 后应显示动画文本");
        spinner.Text.ToString().Should().Contain("Thinking", "文本应包含动词");
    }

    [Fact]
    public void Start_FirstFrame_IsBrailleDot0()
    {
        var spinner = new SpinnerIndicator();

        spinner.Start();

        // 第一帧是 ⠋
        spinner.Text.ToString().Should().Contain("⠋", "第一帧应为 ⠋");
    }

    [Fact]
    public void Stop_Hides_AndClearsText()
    {
        var spinner = new SpinnerIndicator();
        spinner.Start();
        spinner.Visible.Should().BeTrue();

        spinner.Stop();

        spinner.Visible.Should().BeFalse("Stop 后应隐藏");
        spinner.Text.ToString().Should().BeEmpty("Stop 后应清空文本");
    }

    [Fact]
    public void Verb_Custom_ShowsInText()
    {
        var spinner = new SpinnerIndicator { Verb = "Working" };

        spinner.Start();

        spinner.Text.ToString().Should().Contain("Working", "自定义 Verb 应显示在文本中");
    }

    [Fact]
    public void Stop_WhenNotStarted_DoesNotThrow()
    {
        var spinner = new SpinnerIndicator();

        var act = () => spinner.Stop();

        act.Should().NotThrow("未 Start 就 Stop 不应抛异常");
    }

    [Fact]
    public void Start_Twice_DoesNotLeakTimeout()
    {
        var spinner = new SpinnerIndicator();

        spinner.Start();
        spinner.Start();  // 二次 Start 应替换旧 timeout，不泄漏

        spinner.Visible.Should().BeTrue();
        spinner.Text.Should().NotBeNullOrEmpty();

        spinner.Stop();
        spinner.Visible.Should().BeFalse();
    }
}
