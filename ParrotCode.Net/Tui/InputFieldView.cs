using System.Drawing;
using System.Threading.Channels;
using Terminal.Gui;

namespace ParrotCode;

/// <summary>
/// 底部输入框（迭代 7c-3：继承内置 TextField + Tab 补全 + 历史导航）。
/// TextField 原生处理：普通字符/Backspace/方向键/IME 组字/光标/鼠标选区。
/// ">" 提示符由 TerminalApp 中的独立 Label 提供（始终可见）。
/// </summary>
internal sealed class InputFieldView : TextField
{
    private readonly Channel<string> _submitChannel = Channel.CreateUnbounded<string>();
    private readonly string[] _commands = { "/clear", "/exit", "/quit", "/help", "/status" };
    private readonly List<string> _history = new();
    private int _historyIndex = -1;
    private string? _savedBuffer;  // 历史导航时保存当前输入

    /// <summary>提交事件的 ChannelReader。主循环用 TryRead 轮询。</summary>
    public ChannelReader<string> Submits => _submitChannel.Reader;

    /// <summary>提交事件（兼容旧代码风格）。</summary>
    public event Action<string>? Submit;

    /// <summary>退出请求事件（Esc 按下）。</summary>
    public event Action? ExitRequested;

    public InputFieldView()
    {
        CanFocus = true;
    }

    protected override bool OnKeyDown(Key key)
    {
        // Enter——提交（直接读 TextField.Text）
        if (key.KeyCode == KeyCode.Enter)
        {
            var line = Text?.ToString() ?? "";
            // 清空 Text 后必须显式重置 CursorPosition 和触发重绘
            // TextField.Text 设置器不保证重置 CursorPosition，导致发送后光标错位
            Text = "";
            CursorPosition = 0;
            SetNeedsDraw();

            // 记录历史（非空才记）
            if (!string.IsNullOrEmpty(line))
            {
                _history.Add(line);
                _historyIndex = -1;
                _savedBuffer = null;
            }

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

        // Tab 补全 / 开头的命令
        if (key.KeyCode == KeyCode.Tab)
        {
            var buf = Text?.ToString() ?? "";
            if (buf.Length > 0 && buf[0] == '/')
            {
                CompleteCommand(buf);
                return true;  // 吞掉 Tab，避免焦点跳转
            }
        }

        // 历史导航
        if (key.KeyCode == KeyCode.CursorUp && _history.Count > 0)
        {
            NavigateHistory(direction: -1);  // 向上（更早的历史，索引减小）
            return true;
        }
        if (key.KeyCode == KeyCode.CursorDown && _historyIndex >= 0)
        {
            NavigateHistory(direction: 1);  // 向下（更新的历史，索引增大）
            return true;
        }

        // 其余按键（含 Ctrl+V 粘贴、中文 IME 组字、Backspace、左右、Home/End）交给 TextField 基类
        var result = base.OnKeyDown(key);

        // 粘贴后同步——无论 Ctrl+V 还是右键粘贴，都检测换行符并同步光标。
        // 右键粘贴逐字符写入 stdin，不触发 Ctrl+V 的 KeyCode，所以不能只在 Ctrl+V 时同步。
        // 检测换行符：多行粘贴时 TextField（单行控件）可能残留换行符导致渲染异常。
        SyncIfPasted();

        return result;
    }

    /// <summary>
    /// 检测是否发生了粘贴（Text 包含换行符），如果是则清理并同步光标。
    /// 右键粘贴多行内容时，TextField 可能残留 \n \r，导致渲染和光标异常。
    /// </summary>
    private void SyncIfPasted()
    {
        var text = Text?.ToString() ?? "";
        if (text.Contains('\n') || text.Contains('\r'))
        {
            // 单行控件：移除换行符（保留内容，用空格替代）
            text = text.Replace("\r\n", " ").Replace('\r', ' ').Replace('\n', ' ');
            Text = text;
            CursorPosition = Text.Length;
            SetNeedsDraw();
        }
    }

    /// <summary>
    /// 覆写 PositionCursor：用显示宽度计算光标位置。
    ///
    /// base.PositionCursor 用字符索引作为列坐标：
    ///   Move(CursorPosition - ScrollOffset, 0)
    /// 但 CJK 全角字符占 2 列，字符索引 ≠ 列坐标。
    /// 例如 "你好世界"（4 字符 = 8 列），CursorPosition=4 时 base 放光标在列 4（中间），
    /// 实际应在列 8（末尾）。
    ///
    /// 本方法计算光标前所有字符的显示宽度之和，作为正确的列坐标。
    /// </summary>
    public override Point? PositionCursor()
    {
        var textLen = Text?.Length ?? 0;
        if (CursorPosition < 0) CursorPosition = 0;
        if (CursorPosition > textLen) CursorPosition = textLen;

        var viewportWidth = Viewport.Width;
        if (viewportWidth <= 0) return null;

        var text = Text?.ToString() ?? "";

        // 计算光标前所有字符的显示宽度（CJK 全角=2 列，ASCII=1 列）
        int cursorCol = 0;
        for (int i = 0; i < CursorPosition && i < text.Length; i++)
            cursorCol += IsWide(text[i]) ? 2 : 1;

        // 计算 ScrollOffset 对应的列偏移（ScrollOffset 是字符索引，需转为列）
        int scrollCol = 0;
        for (int i = 0; i < ScrollOffset && i < text.Length; i++)
            scrollCol += IsWide(text[i]) ? 2 : 1;

        int cursorX = cursorCol - scrollCol;

        // clamp 到视口范围
        if (cursorX < 0)
            cursorX = 0;
        if (cursorX >= viewportWidth)
            cursorX = viewportWidth - 1;

        Move(cursorX, 0);
        return new Point(cursorX, 0);
    }

    /// <summary>
    /// 判断字符是否为全角（CJK/emoji 等，占 2 列）。
    /// 基于 Unicode East Asian Width 标准（与 ChatView.IsWide 一致）。
    /// </summary>
    private static bool IsWide(char ch)
    {
        return ch >= 0x1100 && (
            ch <= 0x115F ||                              // Hangul Jamo
            ch == 0x2329 || ch == 0x232A ||
            (ch >= 0x2E80 && ch <= 0xA4CF && ch != 0x303F) ||  // CJK Radicals
            (ch >= 0xAC00 && ch <= 0xD7A3) ||            // Hangul Syllables
            (ch >= 0xF900 && ch <= 0xFAFF) ||            // CJK Compatibility Ideographs
            (ch >= 0xFE30 && ch <= 0xFE4F) ||            // CJK Compatibility Forms
            (ch >= 0xFF00 && ch <= 0xFF60) ||            // Fullwidth Forms
            (ch >= 0xFFE0 && ch <= 0xFFE6) ||
            (ch >= 0x1F300 && ch <= 0x1F64F) ||          // Emoji
            (ch >= 0x20000 && ch <= 0x2FFFD) ||
            (ch >= 0x30000 && ch <= 0x3FFFD)
        );
    }

    /// <summary>
    /// Tab 补全 / 开头的命令。
    /// 唯一匹配→填充；多匹配→不填充（7c-3 简化，不显示候选列表）。
    /// </summary>
    private void CompleteCommand(string prefix)
    {
        var matches = _commands
            .Where(c => c.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (matches.Length == 1)
        {
            Text = matches[0];             // TextField 原生重绘
            CursorPosition = Text.Length;  // 光标移到末尾
            SetNeedsDraw();
        }
        // 多匹配或无匹配——不做任何事（7c-3 简化）
    }

    /// <summary>
    /// 历史导航。
    /// direction=-1 向上（更早的历史，索引减小），direction=1 向下（更新的历史，索引增大）。
    /// </summary>
    private void NavigateHistory(int direction)
    {
        if (_history.Count == 0) return;

        // 第一次按 Up——保存当前输入，跳到最新一条历史
        if (_historyIndex == -1)
        {
            _savedBuffer = Text?.ToString();
            _historyIndex = _history.Count - 1;
        }
        else
        {
            _historyIndex += direction;
            if (_historyIndex < 0)
            {
                // 超出最早历史——恢复保存的输入
                Text = _savedBuffer ?? "";
                _historyIndex = -1;
                CursorPosition = Text.Length;
                SetNeedsDraw();
                return;
            }
            if (_historyIndex >= _history.Count)
            {
                // 超出最新历史——恢复保存的输入
                Text = _savedBuffer ?? "";
                _historyIndex = -1;
                CursorPosition = Text.Length;
                SetNeedsDraw();
                return;
            }
        }

        Text = _history[_historyIndex];           // TextField 原生重绘
        CursorPosition = Text.Length;             // 光标移到末尾
        SetNeedsDraw();
    }
}
