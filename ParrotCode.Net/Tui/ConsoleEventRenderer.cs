using System.Threading.Channels;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace ParrotCode;

/// <summary>
/// 流式渲染器（迭代 7b 增强版，替代 Live 模式）。
/// 用 AnsiConsole.Write(IRenderable) 直接输出 Panel/Markup，视觉效果与之前 Live 模式一致。
/// 不用 Live——避免 Live 的 off-by-one、行数跳变、跨轮残影、滚屏失效等固有限制。
///
/// 渲染策略：
/// - TextDelta: 流式 Console.Write（逐字输出，不换行）
/// - ToolCallStart: Panel（青色边框，Header "→ 工具调用"）
/// - ToolResult: Panel（成功绿色/失败红色边框，Header "✓ 结果"/"✗ 失败"）
/// - ToolBlocked: Panel（红色边框，Header "⛔ 拦截"）
/// - RoundStart: Markup 分隔线 "── Round N ──"
/// - AgentDone: 换行
///
/// 依赖 IConsole 抽象便于单元测试。
/// </summary>
internal sealed class ConsoleEventRenderer
{
    private readonly IConsole _console;
    private bool _hasTextOnLine;  // 当前是否已有流式文本在行上（用于 ToolCall 前换行）

    public ConsoleEventRenderer(IConsole? console = null)
    {
        _console = console ?? new SystemConsole();
    }

    public async Task RenderAsync(ChannelReader<AgentEvent> reader, CancellationToken ct)
    {
        await foreach (var evt in reader.ReadAllAsync(ct))
        {
            RenderEvent(evt);
        }
    }

    /// <summary>
    /// 渲染单个事件到控制台（流式模式，用 Panel 输出）。
    /// </summary>
    internal void RenderEvent(AgentEvent evt)
    {
        switch (evt)
        {
            case AgentEvent.RoundStartEvent(var round):
                FlushLine();
                _console.WriteMarkupLine($"[grey]── Round {round} ──[/]");
                break;

            case AgentEvent.TextDeltaEvent(var text):
                _console.Write(text);
                _hasTextOnLine = true;
                break;

            case AgentEvent.AssistantMessageEvent:
                break;  // 文本已在 TextDelta 实时展示

            case AgentEvent.ToolCallStartEvent(var call):
                FlushLine();
                _console.Write(BuildToolCallPanel(call));
                break;

            case AgentEvent.ToolResultEvent(_, var result):
                _console.Write(BuildToolResultPanel(result));
                break;

            case AgentEvent.ToolBlockedEvent(var call, var reason):
                _console.Write(BuildBlockedPanel(call, reason));
                break;

            case AgentEvent.AgentDoneEvent:
                FlushLine();
                break;

            case AgentEvent.MaxRoundsReachedEvent(var rounds):
                FlushLine();
                _console.WriteMarkupLine($"[yellow]⚠ 已达最大轮次 {rounds}[/]");
                break;

            case AgentEvent.WarningEvent(var msg):
                FlushLine();
                _console.WriteMarkupLine($"[yellow]⚠[/] {Markup.Escape(msg)}");
                break;

            case AgentEvent.ErrorEvent(var msg, _):
                FlushLine();
                _console.WriteMarkupLine($"[red]✗ 错误：[/]{Markup.Escape(msg)}");
                break;

            case AgentEvent.CancelledEvent:
                FlushLine();
                _console.WriteMarkupLine("[grey]── 已取消 ──[/]");
                break;

            case AgentEvent.RoundEndEvent:
                break;
        }
    }

    /// <summary>
    /// 如果当前行有流式文本，先换行。
    /// 确保后续 Panel/Markup 从新行开始。
    /// </summary>
    private void FlushLine()
    {
        if (_hasTextOnLine)
        {
            _console.WriteLine();
            _hasTextOnLine = false;
        }
    }

    /// <summary>构造工具调用 Panel（青色边框）。</summary>
    private static Panel BuildToolCallPanel(ToolCall call)
    {
        return new Panel(new Markup(
            $"[cyan]{Markup.Escape(call.Name)}[/]([grey]{Markup.Escape(Truncate(call.Input.GetRawText(), 100))}[/])"))
        {
            Header = new PanelHeader("[cyan]→ 工具调用[/]"),
            BorderStyle = new Style(foreground: Color.Cyan1),
            Padding = new Padding(2, 0, 2, 0)
        };
    }

    /// <summary>构造工具结果 Panel（成功绿色/失败红色边框）。</summary>
    private static Panel BuildToolResultPanel(ToolResult result)
    {
        var (icon, color, content) = result.Success
            ? ("✓", Color.Green, Truncate(result.Content, 200))
            : ("✗", Color.Red, Markup.Escape(result.Error ?? "未知错误"));
        return new Panel(new Markup($"[{color}]{icon}[/] {content}"))
        {
            Header = new PanelHeader(result.Success ? "[green]✓ 结果[/]" : "[red]✗ 失败[/]"),
            BorderStyle = new Style(foreground: color),
            Padding = new Padding(2, 0, 2, 0)
        };
    }

    /// <summary>构造拦截 Panel（红色边框，HITL 拒绝时）。</summary>
    private static Panel BuildBlockedPanel(ToolCall call, string reason)
    {
        return new Panel(new Markup(
            $"[red]✗ 被拦截[/] [cyan]{Markup.Escape(call.Name)}[/]\n[red]{Markup.Escape(reason)}[/]"))
        {
            Header = new PanelHeader("[red]⛔ 拦截[/]"),
            BorderStyle = new Style(foreground: Color.Red),
            Padding = new Padding(2, 0, 2, 0)
        };
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..(max - 3)] + "...";
}
