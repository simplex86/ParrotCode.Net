using System.Drawing;
using Terminal.Gui;

namespace ParrotCode;

/// <summary>
/// 对话区 View（迭代 7c-1：静态占位 + 滚动能力验证）。
/// 7c-2 将扩展为流式渲染 AgentEvent。
/// </summary>
internal sealed class ChatView : View
{
    private readonly List<string> _lines = new();

    public ChatView()
    {
        CanFocus = true;
        ViewportSettings = ViewportSettings.AllowXGreaterThanContentWidth;
        SetContentSize(new Size(0, 0));
    }

    /// <summary>追加静态消息（7c-1 用，7c-2 改为 RenderEvent）。</summary>
    public void AppendStaticMessage(string text)
    {
        _lines.Add(text);
        RebuildContent();
    }

    /// <summary>清空对话区。</summary>
    public void ClearMessages()
    {
        _lines.Clear();
        RebuildContent();
    }

    /// <summary>重建内容并自动滚到底部。</summary>
    private void RebuildContent()
    {
        Text = string.Join(Environment.NewLine, _lines);
        SetContentSize(new Size(Viewport.Width, _lines.Count));
        ScrollToBottom();
        SetNeedsDraw();
    }

    /// <summary>自动滚动到底部。</summary>
    private void ScrollToBottom()
    {
        var contentHeight = GetContentSize().Height;
        Viewport = Viewport with
        {
            Location = new Point(0, Math.Max(0, contentHeight - Viewport.Height))
        };
    }
}
