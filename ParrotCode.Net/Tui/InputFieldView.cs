using System.Threading.Channels;
using Terminal.Gui;
using Color = Terminal.Gui.Color;

namespace ParrotCode;

/// <summary>
/// 底部输入框（迭代 7c-1：继承内置 TextField）。
/// TextField 原生处理：普通字符/Backspace/方向键/IME 组字/光标/鼠标选区。
/// 本迭代只覆写 Enter（提交）+ Esc（退出）。7c-3 加 Tab 补全 + 历史导航。
/// </summary>
internal sealed class InputFieldView : TextField
{
    private readonly Channel<string> _submitChannel = Channel.CreateUnbounded<string>();

    /// <summary>提交事件的 ChannelReader。主循环用 TryRead 轮询。</summary>
    public ChannelReader<string> Submits => _submitChannel.Reader;

    /// <summary>提交事件（兼容旧代码风格）。</summary>
    public event Action<string>? Submit;

    /// <summary>退出请求事件（Esc 按下）。</summary>
    public event Action? ExitRequested;

    public InputFieldView()
    {
        CanFocus = true;
        // 提示符用 Caption（占位），输入有内容时自动隐藏
        Caption = "> ";
        CaptionColor = Color.BrightBlue;
    }

    protected override bool OnKeyDown(Key key)
    {
        // Enter——提交（直接读 TextField.Text）
        if (key.KeyCode == KeyCode.Enter)
        {
            var line = Text?.ToString() ?? "";
            Text = "";  // TextField 原生清空 + 重绘
            _submitChannel.Writer.TryWrite(line);
            Submit?.Invoke(line);
            return true;
        }

        // Esc——退出
        if (key.KeyCode == KeyCode.Esc)
        {
            ExitRequested?.Invoke();
            return true;
        }

        // 其余按键（含中文 IME 组字、Backspace、左右、Home/End）交给 TextField 基类
        return base.OnKeyDown(key);
    }
}
