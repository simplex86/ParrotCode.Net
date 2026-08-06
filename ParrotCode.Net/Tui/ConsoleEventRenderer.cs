using System.Threading.Channels;
using Spectre.Console;

namespace ParrotCode;

/// <summary>
/// 降级行模式渲染器（迭代 6 App.RenderEventsAsync 抽取）。
/// 在 tui.mode=console 或终端非交互（重定向/CI）时使用。
/// 不用 Live，纯 Console.Write + AnsiConsole.MarkupLine。
/// 迭代 7a 新增 ToolBlockedEvent 渲染分支（7b 产生此事件时降级路径也支持）。
///
/// 依赖 IConsole 抽象便于单元测试。
/// RenderEvent 可被 TuiApp 降级路径逐事件调用（配合 StatusBar 更新）。
/// </summary>
internal sealed class ConsoleEventRenderer
{
    private readonly IConsole _console;

    public ConsoleEventRenderer(IConsole? console = null)
    {
        _console = console ?? new SystemConsole();
    }

    public async Task RenderAsync(ChannelReader<AgentEvent> reader, CancellationToken ct)
    {
        WritePrefix();
        await foreach (var evt in reader.ReadAllAsync(ct))
        {
            RenderEvent(evt);
        }
    }

    /// <summary>
    /// 写入 "AI：" 前缀（行模式标记）。
    /// </summary>
    internal void WritePrefix() => _console.WriteMarkup("[green]AI：[/]");

    /// <summary>
    /// 渲染单个事件到控制台（行模式）。
    /// </summary>
    internal void RenderEvent(AgentEvent evt)
    {
        switch (evt)
        {
            case AgentEvent.TextDeltaEvent(var text):
                _console.Write(text);
                break;
            case AgentEvent.ToolCallStartEvent(var call):
                _console.WriteLine();
                _console.WriteMarkupLine(
                    $"[cyan]→[/] {Markup.Escape(call.Name)}({Markup.Escape(Truncate(call.Input.GetRawText(), 80))})");
                break;
            case AgentEvent.ToolResultEvent(_, var result):
                if (result.Success)
                    _console.WriteMarkupLine($"[green]✓[/] {Markup.Escape(Truncate(result.Content, 80))}");
                else
                    _console.WriteMarkupLine($"[red]✗[/] {Markup.Escape(result.Error ?? "未知错误")}");
                break;
            case AgentEvent.ToolBlockedEvent(var call, var reason):
                // 7a 预留分支：主路径不触发。7b HITL 拒绝时产生此事件，降级路径也渲染。
                _console.WriteMarkupLine($"[red]⛔ {Markup.Escape(call.Name)} 被拦截：[/]{Markup.Escape(reason)}");
                break;
            case AgentEvent.AgentDoneEvent:
                _console.WriteLine();
                break;
            case AgentEvent.MaxRoundsReachedEvent(var rounds):
                _console.WriteLine();
                _console.WriteMarkupLine($"[yellow]⚠ 已达最大轮次 {rounds}[/]");
                break;
            case AgentEvent.WarningEvent(var msg):
                _console.WriteMarkupLine($"[yellow]⚠[/] {Markup.Escape(msg)}");
                break;
            case AgentEvent.ErrorEvent(var msg, _):
                _console.WriteLine();
                _console.WriteMarkupLine($"[red]✗ 错误：[/]{Markup.Escape(msg)}");
                break;
            case AgentEvent.CancelledEvent:
                _console.WriteMarkupLine("\n[grey]已取消。[/]");
                break;
            case AgentEvent.RoundStartEvent:
            case AgentEvent.RoundEndEvent:
            case AgentEvent.AssistantMessageEvent:
                break;
        }
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..(max - 3)] + "...";
}
