using System.Text;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace ParrotCode;

/// <summary>
/// 把 AgentEvent 翻译成 Spectre.Console IRenderable。
/// 纯渲染逻辑无副作用——输入事件，更新内部累积状态，输出 IRenderable。
/// 可单测：断言返回的 IRenderable 类型与内容。
///
/// 7b 重构后的渲染策略（修复跨轮顺序混乱 + 状态栏残影）：
/// - _pending：按时间顺序的所有已完成项（文本块 + 工具卡片 + HITL 决策结果）
/// - _textBuf：当前流式文本（未提交到 _pending）
/// - _transient：HITL 提示（临时，ToolResult 后被清除）
///
/// 顺序保证：每次 ToolCallStart/RoundStart/ToolResult 时调 FlushBufferToPending，
/// 把 _textBuf 作为 Text 块加入 _pending 尾部，清空 _textBuf。
/// 这样 _pending 始终按时间顺序包含所有已完成的文本和工具卡片。
///
/// BuildActive 顺序：状态栏 → 轮次 → _pending（历史）→ _textBuf（当前流式）→ _transient（HITL 提示）
/// 历史在前，当前在后，符合时间阅读顺序。
///
/// 行数稳定：RoundStart 不清空 _pending（只 flush buffer），跨轮时行数不骤减，
/// 避免 Spectre.Console Live 清除多余旧行失败导致状态栏残影。
/// _pending 超过 5 个时只显示最近 5 个 + "...还有 N 个"。
/// </summary>
public sealed class EventRenderer
{
    private readonly StringBuilder _textBuf = new();
    private readonly List<IRenderable> _pending = new();  // 按时间顺序的所有已完成项
    private IRenderable? _transient;  // 临时渲染项（HITL 提示/决策结果）
    private int _currentRound;

    /// <summary>当前轮活跃区文本（供状态栏或调试查看）。</summary>
    public string CurrentText => _textBuf.ToString();

    /// <summary>当前轮次号。</summary>
    public int CurrentRound => _currentRound;

    /// <summary>已完成项数（供单测断言）。</summary>
    public int PendingCount => _pending.Count;

    /// <summary>重置渲染器（新对话开始时调）。</summary>
    public void Reset()
    {
        _textBuf.Clear();
        _pending.Clear();
        _transient = null;
        _currentRound = 0;
    }

    /// <summary>设置临时渲染项（HITL 提示用）。传 null 清除。</summary>
    public void SetTransient(IRenderable? renderable) => _transient = renderable;

    /// <summary>
    /// 把 _textBuf 作为 Text 块加入 _pending 尾部，清空 _textBuf。
    /// 在 ToolCallStart/RoundStart/ToolResult 前调，保证文本块按时间顺序在 _pending 中。
    /// </summary>
    private void FlushBufferToPending()
    {
        if (_textBuf.Length > 0)
        {
            _pending.Add(new Text(_textBuf.ToString()));
            _textBuf.Clear();
        }
    }

    /// <summary>
    /// 渲染单个事件为 IRenderable，并更新内部累积状态。
    /// 返回 null 表示该事件不产生独立 IRenderable（如 TextDelta 累积到 _textBuf）。
    /// </summary>
    public IRenderable? Render(AgentEvent evt)
    {
        switch (evt)
        {
            case AgentEvent.RoundStartEvent(var round):
                _currentRound = round;
                // 把上一轮的文本和 HITL 结果 flush 到 _pending（不清空 _pending）
                // 保持行数稳定，避免 Live 残影
                FlushBufferToPending();
                if (_transient is not null)
                {
                    _pending.Add(_transient);
                    _transient = null;
                }
                return new Markup($"[grey]── Round {round} ──[/]");

            case AgentEvent.TextDeltaEvent(var text):
                _textBuf.Append(text);
                return null;  // 累积，由 BuildActive 统一输出

            case AgentEvent.AssistantMessageEvent:
                return null;  // 文本已在 TextDelta 实时展示

            case AgentEvent.ToolCallStartEvent(var call):
                // 工具调用前先把已累积的文本 flush 到 _pending（保持时间顺序）
                FlushBufferToPending();
                var callPanel = new Panel(new Markup(
                    $"[cyan]{Markup.Escape(call.Name)}[/]([grey]{Markup.Escape(Truncate(call.Input.GetRawText(), 100))}[/])"))
                {
                    Header = new PanelHeader("[cyan]→ 工具调用[/]"),
                    BorderStyle = new Style(foreground: Color.Cyan1),
                    Padding = new Padding(2, 0, 2, 0)
                };
                _pending.Add(callPanel);
                return null;  // 由 BuildActive 统一输出

            case AgentEvent.ToolResultEvent(_, var result):
                // 工具结果到达时清除 HITL 决策结果（"✓ 已允许"被工具结果 Panel 替代）
                _transient = null;
                var (icon, color, content) = result.Success
                    ? ("✓", Color.Green, Truncate(result.Content, 200))
                    : ("✗", Color.Red, Markup.Escape(result.Error ?? "未知错误"));
                var resultPanel = new Panel(new Markup($"[{color}]{icon}[/] {content}"))
                {
                    Header = new PanelHeader(result.Success ? "[green]✓ 结果[/]" : "[red]✗ 失败[/]"),
                    BorderStyle = new Style(foreground: color),
                    Padding = new Padding(2, 0, 2, 0)
                };
                _pending.Add(resultPanel);
                return resultPanel;

            case AgentEvent.ToolBlockedEvent(var call, var reason):
                // HITL 拒绝：清除 _transient，加红色拦截卡片
                _transient = null;
                var blockedPanel = new Panel(new Markup(
                    $"[red]✗ 被拦截[/] [cyan]{Markup.Escape(call.Name)}[/]\n[red]{Markup.Escape(reason)}[/]"))
                {
                    Header = new PanelHeader("[red]⛔ 拦截[/]"),
                    BorderStyle = new Style(foreground: Color.Red),
                    Padding = new Padding(2, 0, 2, 0)
                };
                _pending.Add(blockedPanel);
                return blockedPanel;

            case AgentEvent.RoundEndEvent:
                FlushBufferToPending();
                return null;

            case AgentEvent.AgentDoneEvent:
                FlushBufferToPending();
                return new Markup("[grey]── 完成 ──[/]");

            case AgentEvent.MaxRoundsReachedEvent(var rounds):
                return new Markup($"[yellow]⚠ 已达最大轮次 {rounds}[/]");

            case AgentEvent.WarningEvent(var msg):
                return new Markup($"[yellow]⚠[/] {Markup.Escape(msg)}");

            case AgentEvent.ErrorEvent(var msg, _):
                return new Markup($"[red]✗ 错误：[/]{Markup.Escape(msg)}");

            case AgentEvent.CancelledEvent:
                return new Markup("[grey]── 已取消 ──[/]");

            default:
                return null;
        }
    }

    /// <summary>
    /// 构建当前 Live 活跃区的 IRenderable。
    /// 顺序：状态栏 → 轮次 → _pending（历史）→ _textBuf（当前流式）→ _transient（HITL 提示）
    /// 历史在前，当前在后，符合时间阅读顺序。
    /// _pending 超过 5 个时只显示最近 5 个 + "...还有 N 个"。
    /// </summary>
    public IRenderable BuildActive(StatusBar statusBar)
    {
        var rows = new List<IRenderable>();

        // 1. 状态栏（顶部固定）
        if (statusBar is not null)
            rows.Add(statusBar.Render());

        // 2. 轮次标记
        if (_currentRound > 0)
            rows.Add(new Markup($"[grey]Round {_currentRound}[/]"));

        // 3. 历史项（_pending，按时间顺序）——限制最近 5 个避免撑满屏
        if (_pending.Count > 5)
        {
            rows.Add(new Markup($"[grey]...还有 {_pending.Count - 5} 个更早的内容[/]"));
            rows.AddRange(_pending.Skip(_pending.Count - 5));
        }
        else
        {
            rows.AddRange(_pending);
        }

        // 4. 当前流式文本（如果有）——在历史之后，是最新的内容
        if (_textBuf.Length > 0)
            rows.Add(new Text(_textBuf.ToString()));

        // 5. 临时渲染项（HITL 提示）——在最后，等待用户输入
        if (_transient is not null)
            rows.Add(_transient);

        return new Rows(rows);
    }

    /// <summary>
    /// 提取已完成的内容作为滚动历史提交（AgentDone 后调）。
    /// 返回的 IRenderable 不含状态栏与临时项。
    /// </summary>
    public IRenderable BuildCommitted()
    {
        var rows = new List<IRenderable>();
        if (_currentRound > 0)
            rows.Add(new Markup($"[grey]Round {_currentRound}[/]"));
        rows.AddRange(_pending);
        if (_textBuf.Length > 0)
            rows.Add(new Text(_textBuf.ToString()));
        return new Rows(rows);
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..(max - 3)] + "...";
}
