using System.Collections.Concurrent;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace ParrotCode;

/// <summary>
/// IHitlGate 的 Spectre.Console 实现（方案 C：不暂停 Live）。迭代 7b 引入。
///
/// 收到请求时：
/// 1. 会话缓存命中 → 直接返回 AllowSession（不弹提示）
/// 2. 取消 → 返回 Deny("已取消")
/// 3. 渲染 HITL 提示 Panel 到 Live 活跃区（通过 _render 回调调 SetTransient + ctx.UpdateTarget）
/// 4. 读按键（A/S/P/D），通过 _readKey 回调（Console.ReadKey，与 Live 输出分离）
/// 5. AllowSession/AllowPermanent 记入 _sessionCache
/// 6. 渲染决策结果到 Live 活跃区（"✓ 已允许" / "✗ 已拒绝"）
/// 7. 返回 HitlDecision
///
/// 方案 C 关键（规避 7a 发现的陷阱）：
/// - 全程不调 AnsiConsole.Prompt / AnsiConsole.Write——会与 Live 重绘字节交错（7a 教训）。
/// - 只用 _render 回调（→ ctx.UpdateTarget，Live 内部刷新）+ _readKey 回调（→ Console.ReadKey，输入分离）。
/// - Live 持续运行，无需 Stop/Restart，状态机简单。
///
/// 线程模型：7b 简化为同线程同步——AgentLoop 与 TuiApp 在同一 await 链上
/// （TuiApp await agentTask，AgentLoop 内 await batchExecutor.ExecuteAsync，
/// BatchToolExecutor 内 await hitlGate.RequestAsync）。
/// RequestAsync 内部直接调 _render + _readKey，返回 Task.FromResult。
/// 若未来 Live 独立线程，需用 TaskCompletionSource 跨线程传递请求。
/// </summary>
public sealed class HitlPrompt : IHitlGate
{
    private readonly ConcurrentDictionary<string, byte> _sessionCache = new();
    private readonly Action<IRenderable?> _render;
    private readonly Func<CancellationToken, ConsoleKey> _readKey;

    /// <summary>
    /// 构造 HitlPrompt。
    /// </summary>
    /// <param name="render">渲染回调：把 IRenderable 设为 EventRenderer 的 transient + 触发 Live 刷新。
    /// 传 null 时只读按键不渲染（测试用）。</param>
    /// <param name="readKey">读按键回调：返回 ConsoleKey.A/S/P/D 等。传 null 时默认返回 D（测试用）。</param>
    public HitlPrompt(Action<IRenderable?>? render = null, Func<CancellationToken, ConsoleKey>? readKey = null)
    {
        _render = render ?? (_ => { });
        _readKey = readKey ?? (_ => ConsoleKey.D);
    }

    /// <summary>查询会话缓存。</summary>
    public bool IsAllowedThisSession(string toolName) =>
        _sessionCache.ContainsKey(toolName);

    /// <summary>
    /// 请求用户决策。同线程同步：调 _render + _readKey，返回 Task.FromResult。
    /// </summary>
    public Task<HitlDecision?> RequestAsync(ToolCall call, CancellationToken cancellationToken)
    {
        // 1. 会话缓存命中——直接返回 AllowSession（不弹提示，不调 render/readKey）
        if (_sessionCache.ContainsKey(call.Name))
            return Task.FromResult<HitlDecision?>(new HitlDecision(HitlChoice.AllowSession));

        // 2. 取消时立即返回 Deny（避免 ReadKey 阻塞取消——Spectre.Console 的 ReadKey 不响应 CancellationToken）
        if (cancellationToken.IsCancellationRequested)
            return Task.FromResult<HitlDecision?>(HitlDecision.Deny("已取消"));

        // 3. 渲染 HITL 提示 Panel 到 Live 活跃区
        var promptPanel = BuildPromptPanel(call);
        _render(promptPanel);

        // 4. 读按键（A/S/P/D），与 Live 输出分离
        var key = _readKey(cancellationToken);
        var choice = MapKeyToChoice(key);

        // 5. 会话缓存（AllowSession/AllowPermanent；7b AllowPermanent 退化为会话级）
        if (choice is HitlChoice.AllowSession or HitlChoice.AllowPermanent)
            _sessionCache[call.Name] = 0;

        // 6. 渲染决策结果到 Live 活跃区
        var resultMarkup = BuildResultMarkup(choice);
        _render(resultMarkup);

        // 7. 返回决策
        return Task.FromResult<HitlDecision?>(
            choice == HitlChoice.Deny
                ? HitlDecision.Deny("用户拒绝执行该工具")
                : new HitlDecision(choice));
    }

    /// <summary>
    /// 构造 HITL 确认提示 Panel。所有动态内容用 Markup.Escape 转义避免渲染崩溃。
    /// </summary>
    private static Panel BuildPromptPanel(ToolCall call)
    {
        var argsText = Truncate(call.Input.GetRawText(), 80);
        var promptPanel = new Panel(new Markup(
            $"[yellow]⚠ 即将执行[/] [cyan]{Markup.Escape(call.Name)}[/]" +
            $"([grey]{Markup.Escape(argsText)}[/])\n" +
            $"[grey]按 A=本次 S=会话 P=永久 D=拒绝[/]"))
        {
            Header = new PanelHeader("[yellow]HITL 确认[/]"),
            BorderStyle = new Style(foreground: Color.Yellow),
            Padding = new Padding(2, 0, 2, 0)
        };
        return promptPanel;
    }

    /// <summary>
    /// 构造决策结果 Markup。允许绿色 ✓，拒绝红色 ✗。
    /// </summary>
    private static Markup BuildResultMarkup(HitlChoice choice)
    {
        return new Markup(
            choice == HitlChoice.Deny
                ? "[red]✗ 已拒绝[/]"
                : $"[green]✓ 已允许（{choice}）[/]");
    }

    /// <summary>
    /// 按键映射。A/S/P/D 大小写不敏感（同时支持 a/s/p/d）；非 A/S/P/D 一律 Deny（安全默认）。
    /// </summary>
    private static HitlChoice MapKeyToChoice(ConsoleKey key) => key switch
    {
        ConsoleKey.A => HitlChoice.AllowOnce,
        ConsoleKey.S => HitlChoice.AllowSession,
        ConsoleKey.P => HitlChoice.AllowPermanent,
        _ => HitlChoice.Deny  // 非 A/S/P/D 一律拒绝（安全默认）
    };

    private static string Truncate(string s, int max) =>
        s.Length <= max ? s : s[..(max - 3)] + "...";
}
