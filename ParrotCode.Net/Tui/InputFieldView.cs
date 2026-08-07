using System.Text;
using System.Threading.Channels;
using Terminal.Gui;
using Attribute = Terminal.Gui.Attribute;

namespace ParrotCode;

/// <summary>
/// 底部输入框 View（迭代 7c-1：基础输入 + 回显 + Enter 提交）。
/// 固定底部 1 行，通过 Channel 通知主循环有输入提交。
/// 7c-3 将扩展 Tab 补全 + 历史导航。
/// </summary>
internal sealed class InputFieldView : View
{
    private readonly StringBuilder _buffer = new();
    private readonly Channel<string> _submitChannel = Channel.CreateUnbounded<string>();

    /// <summary>提交事件的 ChannelReader。主循环用 ReadAllAsync 等待。</summary>
    public ChannelReader<string> Submits => _submitChannel.Reader;

    /// <summary>提交事件（兼容旧代码风格）。</summary>
    public event Action<string>? Submit;

    /// <summary>退出请求事件（Esc 按下）。</summary>
    public event Action? ExitRequested;

    public InputFieldView()
    {
        CanFocus = true;
        // 7c-1：简单按键处理，不用 KeyBindings（7c-3 完善）
    }

    /// <summary>等待用户提交输入。</summary>
    public async Task<string?> WaitForSubmitAsync(CancellationToken ct)
    {
        try
        {
            return await _submitChannel.Reader.ReadAsync(ct);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
    }

    protected override bool OnKeyDown(Key key)
    {
        // Enter——提交
        if (key.KeyCode == KeyCode.Enter)
        {
            var line = _buffer.ToString();
            _buffer.Clear();
            _submitChannel.Writer.TryWrite(line);
            Submit?.Invoke(line);
            SetNeedsDraw();
            return true;
        }

        // Esc——退出
        if (key.KeyCode == KeyCode.Esc)
        {
            ExitRequested?.Invoke();
            return true;
        }

        // Backspace——删除
        if (key.KeyCode == KeyCode.Backspace && _buffer.Length > 0)
        {
            _buffer.Remove(_buffer.Length - 1, 1);
            SetNeedsDraw();
            return true;
        }

        // 普通字符——追加
        var rune = key.AsRune;
        var ch = (char)rune.Value;
        if (!char.IsControl(ch))
        {
            _buffer.Append(rune.ToString());
            SetNeedsDraw();
            return true;
        }

        return false;
    }

    protected override bool OnDrawingContent()
    {
        // 绘制提示符 "> "
        SetAttribute(new Attribute(Color.BrightBlue, Color.Black));
        Move(0, 0);
        AddStr("> ");

        // 绘制输入缓冲
        var color = _buffer.Length > 0 && _buffer[0] == '/' ? Color.Cyan : Color.White;
        SetAttribute(new Attribute(color, Color.Black));
        Move(2, 0);
        AddStr(_buffer.ToString());
        return true;
    }
}
