using System.Collections.Concurrent;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace ParrotCode;

/// <summary>
/// IHitlGate 的实现（方案 A：流式渲染，不用 Live）。
/// 迭代 7b 引入，方案 D 改进版后最终回归简单实现。
///
/// 工作流程（同步，无 Channel）：
/// 1. RequestAsync 检查缓存命中 → 直接返回 AllowSession
/// 2. 用 _console.Write 显示 HITL 确认 Panel
/// 3. 用 _console.ReadKey(true) 读按键（intercept=true 不回显）
/// 4. 用 _console.Write 显示决策结果 Markup
/// 5. 返回决策
///
/// 不依赖 Live——不在 Live 期间调 AnsiConsole.Write/Prompt，
/// 不需要 Channel/TaskCompletionSource 跨线程通信，
/// 不需要 ctx.UpdateTarget。
/// </summary>
public sealed class HitlPrompt : IHitlGate
{
    private readonly ConcurrentDictionary<string, byte> _sessionCache = new();
    private readonly IConsole _console;

    public HitlPrompt(IConsole? console = null)
    {
        _console = console ?? new SystemConsole();
    }

    /// <summary>
    /// 请求用户决策。
    /// 缓存命中时直接返回；否则显示 HITL 提示 Panel + ReadKey + 显示结果 Markup。
    /// </summary>
    public async Task<HitlDecision?> RequestAsync(ToolCall call, CancellationToken cancellationToken)
    {
        // 1. 会话缓存命中——直接返回 AllowSession（不弹提示）
        if (_sessionCache.ContainsKey(call.Name))
            return new HitlDecision(HitlChoice.AllowSession);

        // 2. 取消时立即返回 Deny
        if (cancellationToken.IsCancellationRequested)
            return HitlDecision.Deny("已取消");

        // 3. 显示 HITL 确认提示 Panel
        _console.Write(BuildPromptPanel(call));

        // 4. 读按键（intercept=true 不回显不移动光标）
        var key = _console.ReadKey(true).Key;
        var choice = MapKeyToChoice(key);
        var decision = choice == HitlChoice.Deny
            ? HitlDecision.Deny("用户拒绝执行")
            : new HitlDecision(choice);

        // 5. 显示决策结果 Markup（替换 HITL 提示）
        _console.Write(BuildResultMarkup(choice));

        // 6. 缓存会话级允许
        if (decision.ShouldCache)
            _sessionCache[call.Name] = 0;

        return await Task.FromResult(decision);
    }

    /// <summary>查询会话缓存。</summary>
    public bool IsAllowedThisSession(string toolName) =>
        _sessionCache.ContainsKey(toolName);

    /// <summary>
    /// 构造 HITL 确认提示 Panel。所有动态内容用 Markup.Escape 转义避免渲染崩溃。
    /// </summary>
    public static Panel BuildPromptPanel(ToolCall call)
    {
        var argsText = Truncate(call.Input.GetRawText(), 80);
        return new Panel(new Markup(
            $"[yellow]⚠ 即将执行[/] [cyan]{Markup.Escape(call.Name)}[/]" +
            $"([grey]{Markup.Escape(argsText)}[/])\n" +
            $"[grey]按 A=本次 S=会话 P=永久 D=拒绝[/]"))
        {
            Header = new PanelHeader("[yellow]HITL 确认[/]"),
            BorderStyle = new Style(foreground: Color.Yellow),
            Padding = new Padding(2, 0, 2, 0)
        };
    }

    /// <summary>
    /// 构造决策结果 Markup。允许绿色 ✓，拒绝红色 ✗。
    /// </summary>
    public static Markup BuildResultMarkup(HitlChoice choice)
    {
        return new Markup(
            choice == HitlChoice.Deny
                ? "[red]✗ 已拒绝[/]\n"
                : $"[green]✓ 已允许（{choice}）[/]\n");
    }

    /// <summary>
    /// 按键映射。A/S/P/D 大小写不敏感；非 A/S/P/D 一律 Deny（安全默认）。
    /// </summary>
    public static HitlChoice MapKeyToChoice(ConsoleKey key) => key switch
    {
        ConsoleKey.A => HitlChoice.AllowOnce,
        ConsoleKey.S => HitlChoice.AllowSession,
        ConsoleKey.P => HitlChoice.AllowPermanent,
        _ => HitlChoice.Deny  // 非 A/S/P/D 一律拒绝（安全默认）
    };

    private static string Truncate(string s, int max) =>
        s.Length <= max ? s : s[..(max - 3)] + "...";
}
