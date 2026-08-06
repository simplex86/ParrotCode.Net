using System.Text;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace ParrotCode;

/// <summary>
/// 把 AgentEvent 翻译成 Spectre.Console IRenderable。
/// 纯渲染逻辑无副作用——输入事件，更新内部累积状态，输出 IRenderable。
/// 可单测：断言返回的 IRenderable 类型与内容。
///
/// 渲染策略：
/// - TextDeltaEvent: 累积到 _textBuf，由 BuildActive 一起输出
/// - ToolCallStartEvent: 加 Panel 到 _pending
/// - ToolResultEvent: 成功绿 ✓ + 截断内容；失败红 ✗ + 错误，加 Panel 到 _pending
/// - ToolBlockedEvent: 红色 Panel "被拦截"（7a 预留分支，主路径不触发——7b HITL 接入后产生）
/// - RoundStartEvent: 灰色 [Round N]，清空 _textBuf 与 _pending
/// - AgentDoneEvent: 灰色 [完成]
/// - MaxRoundsReachedEvent: 黄色 ⚠
/// - ErrorEvent: 红色 ✗ 错误
/// - CancelledEvent: 灰色 [已取消]
///
/// SetTransient 扩展点（7b 预留）：设置一个临时 IRenderable 加入活跃区。
/// 7a 不使用（始终 null）。7b 的 HitlPrompt 调 SetTransient(prompt) 渲染 HITL 提示。
/// </summary>
public sealed class EventRenderer
{
    private readonly StringBuilder _textBuf = new();
    private readonly List<IRenderable> _pending = new();  // 本轮已完成项（工具卡片等）
    private IRenderable? _transient;  // 7b 预留：临时渲染项（HITL 提示），7a 始终 null
    private int _currentRound;

    /// <summary>
    /// 当前轮活跃区文本（供状态栏或调试查看）。
    /// </summary>
    public string CurrentText => _textBuf.ToString();

    /// <summary>
    /// 当前轮次号。
    /// </summary>
    public int CurrentRound => _currentRound;

    /// <summary>
    /// 本轮回退的 pending 项数（供单测断言）。
    /// </summary>
    public int PendingCount => _pending.Count;

    /// <summary>
    /// 重置渲染器（新一轮开始或提交后调）。
    /// </summary>
    public void Reset()
    {
        _textBuf.Clear();
        _pending.Clear();
        _transient = null;
        _currentRound = 0;
    }

    /// <summary>
    /// 设置临时渲染项（7b 预留扩展点）。
    /// 7a 不调用此方法。7b 的 HitlPrompt 调用此方法把 HITL 提示 Panel 加入活跃区。
    /// 传 null 清除临时项。
    /// </summary>
    public void SetTransient(IRenderable? renderable) => _transient = renderable;

    /// <summary>
    /// 渲染单个事件为 IRenderable，并更新内部累积状态。
    /// 返回 null 表示该事件不产生独立 IRenderable（如 TextDelta 累积到 _textBuf，由 BuildActive 一起输出）。
    /// </summary>
    public IRenderable? Render(AgentEvent evt)
    {
        switch (evt)
        {
            case AgentEvent.RoundStartEvent(var round):
                _currentRound = round;
                _textBuf.Clear();
                _pending.Clear();
                _transient = null;
                return new Markup($"[grey]── Round {round} ──[/]");

            case AgentEvent.TextDeltaEvent(var text):
                _textBuf.Append(text);
                return null;  // 累积，由 BuildActive 统一输出

            case AgentEvent.AssistantMessageEvent:
                return null;  // 文本已在 TextDelta 实时展示

            case AgentEvent.ToolCallStartEvent(var call):
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
                // 7a 预留分支：主路径不触发（BatchToolExecutor 无 HITL）。
                // 7b HITL 拒绝时由 AgentLoop 产生此事件，此处渲染红色拦截卡片。
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
                return null;  // 不渲染（RoundStart 已标记）

            case AgentEvent.AgentDoneEvent:
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
    /// 构建当前 Live 活跃区的 IRenderable（状态栏 + 轮次 + 文本 + 进行中工具卡片 + 临时项）。
    /// 每次 Live 刷新时调此方法得到最新渲染目标。
    /// _transient 非 null 时加入活跃区尾部（7b HITL 提示用）。
    /// _pending 超过 5 个时限制显示最近 5 个 + "...还有 N 个"。
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

        // 3. 当前流式文本（如果有）
        if (_textBuf.Length > 0)
            rows.Add(new Text(_textBuf.ToString()));

        // 4. 进行中/已完成的工具卡片（限制最近 5 个，避免撑满屏）
        if (_pending.Count > 5)
        {
            rows.Add(new Markup($"[grey]...还有 {_pending.Count - 5} 个更早的工具卡片[/]"));
            rows.AddRange(_pending.Skip(_pending.Count - 5));
        }
        else
        {
            rows.AddRange(_pending);
        }

        // 5. 临时渲染项（7b 预留：HITL 提示；7a 始终 null 不渲染）
        if (_transient is not null)
            rows.Add(_transient);

        return new Rows(rows);
    }

    /// <summary>
    /// 提取已完成的内容作为滚动历史提交（AgentDone 后调）。
    /// 返回的 IRenderable 不含状态栏（状态栏是 Live 专属）与临时项（HITL 提示是 Live 内的）。
    /// </summary>
    public IRenderable BuildCommitted()
    {
        var rows = new List<IRenderable>();
        if (_currentRound > 0)
            rows.Add(new Markup($"[grey]Round {_currentRound}[/]"));
        if (_textBuf.Length > 0)
            rows.Add(new Text(_textBuf.ToString()));
        rows.AddRange(_pending);
        return new Rows(rows);
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..(max - 3)] + "...";
}
