using ParrotCode;
using Terminal.Gui;

namespace ParrotCode.xUnit;

/// <summary>
/// InputFieldView 单元测试（迭代 7c-3）。
/// 覆盖 Enter 提交、Esc 退出、Tab 补全、历史导航（Up/Down）。
/// 使用 TerminalGuiFixture 确保 Application 已初始化。
/// </summary>
[Collection("Terminal.Gui")]
public class InputFieldViewTests
{
    private static InputFieldView CreateView()
    {
        var view = new InputFieldView
        {
            X = 0,
            Y = 0,
            Width = 80,
            Height = 1
        };
        view.Text = "";
        return view;
    }

    [Fact]
    public void Enter_SubmitsText_AndClearsField()
    {
        var view = CreateView();
        view.Text = "hello world";

        var handled = view.NewKeyDownEvent(new Key(KeyCode.Enter));

        handled.Should().BeTrue();
        view.Text.Should().BeEmpty("Enter 后应清空输入框");
        view.Submits.TryRead(out var submitted).Should().BeTrue("应提交到 Submits Channel");
        submitted.Should().Be("hello world");
    }

    [Fact]
    public void Enter_EmptyText_DoesNotRecordHistory()
    {
        var view = CreateView();
        view.Text = "";

        view.NewKeyDownEvent(new Key(KeyCode.Enter));

        // 空文本不应记入历史——按 Up 应无历史可导航
        view.NewKeyDownEvent(new Key(KeyCode.CursorUp));
        view.Text.Should().BeEmpty("空提交不应记入历史");
    }

    [Fact]
    public void Esc_TriggersExitRequested()
    {
        var view = CreateView();
        var exitCalled = false;
        view.ExitRequested += () => exitCalled = true;

        var handled = view.NewKeyDownEvent(new Key(KeyCode.Esc));

        handled.Should().BeTrue();
        exitCalled.Should().BeTrue("Esc 应触发 ExitRequested");
    }

    [Fact]
    public void Tab_CompletesSlashCommand()
    {
        var view = CreateView();
        view.Text = "/c";

        var handled = view.NewKeyDownEvent(new Key(KeyCode.Tab));

        handled.Should().BeTrue("Tab 补全应拦截按键");
        view.Text.Should().Be("/clear", "/c 应补全为 /clear");
    }

    [Fact]
    public void Tab_DoesNotComplete_NonCommandText()
    {
        var view = CreateView();
        view.Text = "hello";

        view.NewKeyDownEvent(new Key(KeyCode.Tab));

        // 非 / 开头的文本不应触发补全，Text 不变
        view.Text.Should().Be("hello");
    }

    [Fact]
    public void Tab_CompletesExitCommand()
    {
        var view = CreateView();
        view.Text = "/e";  // 唯一匹配 /exit

        view.NewKeyDownEvent(new Key(KeyCode.Tab));
        view.Text.Should().Be("/exit");
    }

    [Fact]
    public void Up_NavigatesToMostRecentHistory()
    {
        var view = CreateView();
        view.Text = "first message";
        view.NewKeyDownEvent(new Key(KeyCode.Enter));
        view.Text.Should().BeEmpty();

        // 按 Up → 应显示最近一条历史
        view.NewKeyDownEvent(new Key(KeyCode.CursorUp));

        view.Text.Should().Be("first message");
    }

    [Fact]
    public void Up_Twice_NavigatesToOlderHistory()
    {
        var view = CreateView();
        // 提交两条消息
        view.Text = "first";
        view.NewKeyDownEvent(new Key(KeyCode.Enter));
        view.Text = "second";
        view.NewKeyDownEvent(new Key(KeyCode.Enter));

        // 第一次 Up → 最近一条（second）
        view.NewKeyDownEvent(new Key(KeyCode.CursorUp));
        view.Text.Should().Be("second");

        // 第二次 Up → 更早一条（first）
        view.NewKeyDownEvent(new Key(KeyCode.CursorUp));
        view.Text.Should().Be("first");
    }

    [Fact]
    public void Down_NavigatesToNewerHistory()
    {
        var view = CreateView();
        view.Text = "first";
        view.NewKeyDownEvent(new Key(KeyCode.Enter));
        view.Text = "second";
        view.NewKeyDownEvent(new Key(KeyCode.Enter));

        // Up 两次到最早
        view.NewKeyDownEvent(new Key(KeyCode.CursorUp));  // → second
        view.NewKeyDownEvent(new Key(KeyCode.CursorUp));  // → first
        view.Text.Should().Be("first");

        // Down 一次 → 更新一条（second）
        view.NewKeyDownEvent(new Key(KeyCode.CursorDown));
        view.Text.Should().Be("second");
    }

    [Fact]
    public void Down_PastNewest_RestoresSavedBuffer()
    {
        var view = CreateView();
        view.Text = "first";
        view.NewKeyDownEvent(new Key(KeyCode.Enter));

        // 输入新内容但未提交
        view.Text = "draft text";

        // Up → first
        view.NewKeyDownEvent(new Key(KeyCode.CursorUp));
        view.Text.Should().Be("first");

        // Down → 超出最新，恢复 draft text
        view.NewKeyDownEvent(new Key(KeyCode.CursorDown));
        view.Text.Should().Be("draft text");
    }

    [Fact]
    public void Up_PastOldest_RestoresSavedBuffer()
    {
        var view = CreateView();
        view.Text = "first";
        view.NewKeyDownEvent(new Key(KeyCode.Enter));

        view.Text = "draft";
        // Up → first
        view.NewKeyDownEvent(new Key(KeyCode.CursorUp));
        view.Text.Should().Be("first");

        // Up 再次 → 超出最早，恢复 draft
        view.NewKeyDownEvent(new Key(KeyCode.CursorUp));
        view.Text.Should().Be("draft");
    }

    [Fact]
    public void Submit_Event_Fires()
    {
        var view = CreateView();
        var submittedText = "";
        view.Submit += text => submittedText = text;

        view.Text = "test message";
        view.NewKeyDownEvent(new Key(KeyCode.Enter));

        submittedText.Should().Be("test message");
    }

    // ===== 迭代 10a：SetCommands 动态 Tab 补全 =====

    [Fact]
    public void SetCommands_UpdatesCommandList()
    {
        var view = CreateView();
        view.SetCommands(new[] { "custom", "special" });

        // /c 唯一匹配 /custom
        view.Text = "/c";
        view.NewKeyDownEvent(new Key(KeyCode.Tab));
        view.Text.Should().Be("/custom");
    }

    [Fact]
    public void SetCommands_TabCompletesFromDynamicList()
    {
        var view = CreateView();
        view.SetCommands(new[] { "alpha", "beta", "gamma" });

        view.Text = "/be";
        view.NewKeyDownEvent(new Key(KeyCode.Tab));
        view.Text.Should().Be("/beta");
    }

    [Fact]
    public void SetCommands_TabUniqueMatch_AutoFills()
    {
        var view = CreateView();
        view.SetCommands(new[] { "help", "history" });

        // /he 唯一匹配 /help
        view.Text = "/he";
        view.NewKeyDownEvent(new Key(KeyCode.Tab));
        view.Text.Should().Be("/help");
    }

    [Fact]
    public void SetCommands_TabMultipleMatches_DoesNotFill()
    {
        var view = CreateView();
        view.SetCommands(new[] { "help", "history" });

        // /h 匹配 /help 和 /history，不填充
        view.Text = "/h";
        view.NewKeyDownEvent(new Key(KeyCode.Tab));
        view.Text.Should().Be("/h");
    }

    [Fact]
    public void SetCommands_IncludesAliases()
    {
        var view = CreateView();
        view.SetCommands(new[] { "exit", "quit" });

        // /q 唯一匹配 /quit
        view.Text = "/q";
        view.NewKeyDownEvent(new Key(KeyCode.Tab));
        view.Text.Should().Be("/quit");
    }
}
