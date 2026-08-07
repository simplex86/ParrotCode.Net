using Terminal.Gui;

namespace ParrotCode;

/// <summary>
/// 盲文点 spinner 动画（迭代 7c-3：继承内置 Label）。
/// 工具执行时显示 Thinking⠋ → Thinking⠙ → ... 循环。
/// 用 Application.AddTimeout 周期性更新 Text，Label 自动重绘。
/// </summary>
internal sealed class SpinnerIndicator : Label
{
    private static readonly string[] Frames = { "⠋", "⠙", "⠹", "⠸", "⠼", "⠴", "⠦", "⠧", "⠇", "⠏" };

    private int _frame;
    private object? _timeoutToken;

    /// <summary>动词（如 "Thinking"、"Working"）。</summary>
    public string Verb { get; set; } = "Thinking";

    public SpinnerIndicator()
    {
        Width = 20;
        Height = 1;
        Visible = false;  // 默认隐藏
    }

    /// <summary>开始动画。</summary>
    public void Start()
    {
        // 先移除旧 timeout，避免重复 Start 导致泄漏
        if (_timeoutToken != null)
            Application.RemoveTimeout(_timeoutToken);

        _frame = 0;
        Visible = true;
        Text = $"{Verb} {Frames[_frame]}";
        _timeoutToken = Application.AddTimeout(TimeSpan.FromMilliseconds(100), () =>
        {
            _frame = (_frame + 1) % Frames.Length;
            Text = $"{Verb} {Frames[_frame]}";  // Label 设 Text 自动重绘
            return true;  // 继续
        });
    }

    /// <summary>停止动画。</summary>
    public void Stop()
    {
        if (_timeoutToken != null)
        {
            Application.RemoveTimeout(_timeoutToken);
            _timeoutToken = null;
        }
        Visible = false;
        Text = "";
    }
}
