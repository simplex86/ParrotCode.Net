using System.Collections;
using System.Collections.Specialized;
using System.Text;
using Terminal.Gui;
using Attribute = Terminal.Gui.Attribute;
using Color = Terminal.Gui.Color;

namespace ParrotCode;

/// <summary>
/// 对话区（迭代 7c-2：继承内置 ListView，接入 AgentEvent 流式渲染）。
/// 消息列表模型 + 流式文本缓冲 + 自动换行 + 原生滚动 + IListDataSource.Render 上色。
/// </summary>
internal sealed class ChatView : ListView
{
    private readonly List<ChatMessage> _messages = new();
    private readonly List<VisualLine> _visualLines = new();  // 预换行后的可视行
    private readonly StringBuilder _currentText = new();  // 当前流式文本缓冲
    private bool _hasStreamingText;
    private int _lastWidth = -1;

    public ChatView()
    {
        CanFocus = false;  // 不抢焦点（焦点给输入框）；鼠标滚轮仍可滚动
        Source = new ChatMessageListSource(_visualLines);
    }

    protected override void OnViewportChanged(DrawEventArgs e)
    {
        base.OnViewportChanged(e);
        // 视口宽度变化时重新换行
        var width = Viewport.Width;
        if (width > 0 && width != _lastWidth)
        {
            _lastWidth = width;
            RebuildVisualLines();
        }
    }

    // ===== 7c-1 保留方法（兼容静态占位）=====

    /// <summary>追加静态消息（7c-1 用，7c-2 保留兼容）。</summary>
    public void AppendStaticMessage(string text)
    {
        _messages.Add(new ChatMessage(MessageType.System, text));
        RebuildVisualLines();
    }

    /// <summary>清空对话区。</summary>
    public void ClearMessages()
    {
        _messages.Clear();
        _currentText.Clear();
        _hasStreamingText = false;
        RebuildVisualLines();
    }

    // ===== 7c-2 新增方法 =====

    /// <summary>追加用户消息。</summary>
    public void AppendUserMessage(string text)
    {
        FlushCurrentText();
        _messages.Add(new ChatMessage(MessageType.User, text));
        RebuildVisualLines();
    }

    /// <summary>
    /// 渲染 Agent 事件（流式）。
    /// 由 TerminalApp 的事件消费循环调用（通过 AddIdle 调度到主线程）。
    /// </summary>
    public void RenderEvent(AgentEvent evt)
    {
        switch (evt)
        {
            case AgentEvent.RoundStartEvent(var round):
                FlushCurrentText();
                _messages.Add(new ChatMessage(MessageType.System, $"── Round {round} ──"));
                RebuildVisualLines();
                break;

            case AgentEvent.TextDeltaEvent(var text):
                // 流式追加：若无 assistant 槽位则创建一个可变槽位
                if (!_hasStreamingText)
                {
                    _messages.Add(new ChatMessage(MessageType.Assistant, ""));
                    _hasStreamingText = true;
                }
                _currentText.Append(text);
                UpdateLastMessage(_currentText.ToString());
                break;

            case AgentEvent.AssistantMessageEvent:
                // 文本已在 TextDelta 实时展示，此处不处理
                break;

            case AgentEvent.ToolCallStartEvent(var call):
                FlushCurrentText();
                _messages.Add(new ChatMessage(MessageType.ToolCall,
                    $"  ⎿ → {call.Name}({Truncate(call.Input.GetRawText(), 80)})"));
                RebuildVisualLines();
                break;

            case AgentEvent.ToolResultEvent(_, var result):
                FlushCurrentText();
                var icon = result.Success ? "✓" : "✗";
                var content = result.Success
                    ? Truncate(result.Content, 200)
                    : (result.Error ?? "未知错误");
                _messages.Add(new ChatMessage(
                    result.Success ? MessageType.ToolResult : MessageType.ToolError,
                    $"  ⎿ {icon} {content}"));
                RebuildVisualLines();
                break;

            case AgentEvent.ToolBlockedEvent(var call, var reason):
                FlushCurrentText();
                _messages.Add(new ChatMessage(MessageType.System,
                    $"  ⎿ ⛔ 拦截 {call.Name}: {reason}"));
                RebuildVisualLines();
                break;

            case AgentEvent.AgentDoneEvent:
                FlushCurrentText();
                break;

            case AgentEvent.MaxRoundsReachedEvent(var rounds):
                FlushCurrentText();
                _messages.Add(new ChatMessage(MessageType.Warning, $"⚠ 已达最大轮次 {rounds}"));
                RebuildVisualLines();
                break;

            case AgentEvent.WarningEvent(var msg):
                FlushCurrentText();
                _messages.Add(new ChatMessage(MessageType.Warning, $"⚠ {msg}"));
                RebuildVisualLines();
                break;

            case AgentEvent.ErrorEvent(var msg, _):
                FlushCurrentText();
                _messages.Add(new ChatMessage(MessageType.Error, $"✗ 错误：{msg}"));
                RebuildVisualLines();
                break;

            case AgentEvent.CancelledEvent:
                FlushCurrentText();
                _messages.Add(new ChatMessage(MessageType.System, "── 已取消 ──"));
                RebuildVisualLines();
                break;

            case AgentEvent.RoundEndEvent:
                // 不渲染
                break;
        }
    }

    /// <summary>
    /// 把流式缓冲的文本 flush 到消息列表。
    /// 在工具调用/结果/轮次结束等边界点调用。
    /// </summary>
    private void FlushCurrentText()
    {
        if (_hasStreamingText)
        {
            // 流式槽位已在 TextDelta 实时更新；仅复位标志
            _currentText.Clear();
            _hasStreamingText = false;
            ScrollToBottom();
        }
    }

    /// <summary>流式增量更新最后一条消息（不重建全部）。</summary>
    private void UpdateLastMessage(string text)
    {
        if (_messages.Count > 0)
        {
            _messages[^1] = _messages[^1] with { Content = text };
            RebuildVisualLines();
        }
    }

    /// <summary>
    /// 重建可视行列表：遍历所有消息，按当前视口宽度换行。
    /// </summary>
    private void RebuildVisualLines()
    {
        var width = _lastWidth > 0 ? _lastWidth : 80;

        _visualLines.Clear();
        foreach (var msg in _messages)
        {
            var formatted = msg.Format();
            var color = msg.GetColor();
            foreach (var line in WrapText(formatted, width))
                _visualLines.Add(new VisualLine(color, line));
        }

        Source = new ChatMessageListSource(_visualLines);
        ScrollToBottom();
    }

    /// <summary>滚动到底部（ListView 原生：选中最后一项即滚入可视）。</summary>
    private void ScrollToBottom()
    {
        if (_visualLines.Count > 0)
            SelectedItem = _visualLines.Count - 1;
    }

    /// <summary>
    /// 按显示宽度换行。支持 CJK 全角字符（占 2 列）。
    /// 按字符级别换行——CJK 无空格分词，字符换行最自然。
    /// </summary>
    private static IEnumerable<string> WrapText(string text, int maxWidth)
    {
        if (maxWidth <= 0) maxWidth = 1;

        foreach (var segment in text.Split('\n'))
        {
            if (string.IsNullOrEmpty(segment))
            {
                yield return "";
                continue;
            }

            var current = new StringBuilder();
            int currentWidth = 0;

            foreach (var ch in segment)
            {
                int chWidth = IsWide(ch) ? 2 : 1;

                if (currentWidth + chWidth > maxWidth && current.Length > 0)
                {
                    yield return current.ToString();
                    current.Clear();
                    currentWidth = 0;
                }

                current.Append(ch);
                currentWidth += chWidth;
            }

            if (current.Length > 0)
                yield return current.ToString();
        }
    }

    /// <summary>
    /// 判断字符是否为全角（CJK/emoji 等，占 2 列）。
    /// 基于 Unicode East Asian Width 标准。
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

    private static string Truncate(string s, int max) =>
        s.Length <= max ? s : s[..(max - 3)] + "...";
}

/// <summary>预换行后的单行可视数据（颜色 + 文本）。</summary>
internal sealed record VisualLine(Color Color, string Text);

/// <summary>
/// ChatView 的 IListDataSource 实现：渲染预换行的可视行。
/// 每行已按视口宽度截断，Render 只负责设颜色 + 输出文本。
/// </summary>
internal sealed class ChatMessageListSource : IListDataSource
{
    private readonly List<VisualLine> _lines;
    public ChatMessageListSource(List<VisualLine> lines) => _lines = lines;

    public int Count => _lines.Count;
    public int Length => _lines.Count;

    public bool SuspendCollectionChangedEvent { get; set; }

#pragma warning disable CS0067  // 接口要求，当前用 Source 重新赋值触发重绘
    public event NotifyCollectionChangedEventHandler? CollectionChanged;
#pragma warning restore CS0067

    public void Render(ListView listView, bool selected, int item, int col, int line, int width, int start)
    {
        var vl = _lines[item];
        var normalAttr = listView.GetNormalColor();
        listView.SetAttribute(new Attribute(vl.Color, normalAttr.Background));
        listView.Move(col, line);
        listView.AddStr(vl.Text);
    }

    public bool IsMarked(int index) => false;
    public void SetMark(int index, bool value) { }
    public IList ToList() => _lines;
    public void Dispose() { }  // IListDataSource : IDisposable，无资源需释放
}
