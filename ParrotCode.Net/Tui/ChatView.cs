using System.Collections.ObjectModel;
using Terminal.Gui;

namespace ParrotCode;

/// <summary>
/// 对话区（迭代 7c-1：继承内置 ListView，静态占位 + 滚动能力验证）。
/// 滚动/滚轮/resize 由 ListView 原生处理，不自实现。
/// 7c-2 将扩展为 RenderEvent(AgentEvent) + IListDataSource.Render 上色。
/// </summary>
internal sealed class ChatView : ListView
{
    private readonly ObservableCollection<string> _lines = new();

    public ChatView()
    {
        CanFocus = false;  // 不抢焦点（焦点给输入框）；鼠标滚轮仍可滚动
        SetSource(_lines);
    }

    /// <summary>追加静态消息（7c-1 用，7c-2 改为 RenderEvent）。</summary>
    public void AppendStaticMessage(string text)
    {
        _lines.Add(text);
        ScrollToBottom();
    }

    /// <summary>清空对话区。</summary>
    public void ClearMessages()
    {
        _lines.Clear();
    }

    /// <summary>滚动到底部（ListView 原生：选中最后一项即滚入可视）。</summary>
    private void ScrollToBottom()
    {
        if (_lines.Count > 0)
            SelectedItem = _lines.Count - 1;
    }
}
