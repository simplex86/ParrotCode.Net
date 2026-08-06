using Spectre.Console;
using Spectre.Console.Rendering;

namespace ParrotCode;

/// <summary>
/// 状态栏组件。显示 Provider/Model/安全等级/上下文占比/当前轮次/工具数。
/// 作为 Live 渲染目标的一部分，每次刷新调 Render() 返回最新 IRenderable。
/// 迭代 7a 安全等级硬编码 Normal（无配置项）；7b/迭代 8 加配置项后可切换。
/// </summary>
public sealed class StatusBar
{
    /// <summary>Provider 名（来自 ProviderConfig.Name）。</summary>
    public string Provider { get; set; } = string.Empty;

    /// <summary>Model 名（来自 ProviderConfig.Model）。</summary>
    public string Model { get; set; } = string.Empty;

    /// <summary>安全等级（7a 硬编码 Normal）。</summary>
    public SecurityLevel SecurityLevel { get; set; } = SecurityLevel.Normal;

    /// <summary>历史估算 token 数（来自 ConversationHistory.EstimatedTokens）。</summary>
    public int EstimatedTokens { get; set; }

    /// <summary>上下文窗口 token 数（配置的 context_window_tokens，默认 64000）。</summary>
    public int ContextWindowTokens { get; set; } = 64000;

    /// <summary>当前 ReAct 轮次（来自 RoundStartEvent）。</summary>
    public int CurrentRound { get; set; }

    /// <summary>已注册工具数（来自 ToolRegistry.GetAll().Count）。</summary>
    public int ToolCount { get; set; }

    /// <summary>上下文占比（0-1）。超 0.7 黄色，超 0.9 红色。</summary>
    public double ContextRatio =>
        ContextWindowTokens > 0 ? (double)EstimatedTokens / ContextWindowTokens : 0;

    /// <summary>
    /// 渲染状态栏为 IRenderable。
    /// 用灰色边框 Panel 包裹，占比颜色：绿(<70%)/黄(70-90%)/红(>90%)。
    /// Provider/Model 名截断到 20 字符防换行。
    /// </summary>
    public IRenderable Render()
    {
        var ratio = ContextRatio;
        var ratioColor = ratio >= 0.9 ? "red" : ratio >= 0.7 ? "yellow" : "green";
        var pct = (int)(ratio * 100);
        var securityColor = SecurityLevel switch
        {
            SecurityLevel.Strict => "red",
            SecurityLevel.Normal => "yellow",
            SecurityLevel.Permisive => "green",
            _ => "grey"
        };

        var provider = Truncate(Provider, 20);
        var model = Truncate(Model, 20);

        var markup =
            $"[grey]provider=[/][cyan]{Markup.Escape(provider)}[/] " +
            $"[grey]model=[/][cyan]{Markup.Escape(model)}[/] " +
            $"[grey]security=[/][{securityColor}]{SecurityLevel}[/] " +
            $"[grey]ctx=[/][{ratioColor}]{pct}%[/]({EstimatedTokens}/{ContextWindowTokens}) " +
            $"[grey]round=[/][cyan]{CurrentRound}[/] " +
            $"[grey]tools=[/][cyan]{ToolCount}[/]";

        return new Panel(new Markup(markup))
        {
            BorderStyle = new Style(foreground: Color.Grey50),
            Padding = new Padding(1, 0, 1, 0)
        };
    }

    private static string Truncate(string s, int max) =>
        s.Length <= max ? s : s[..(max - 3)] + "...";
}
