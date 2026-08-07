using Terminal.Gui;
using Attribute = Terminal.Gui.Attribute;

namespace ParrotCode;

/// <summary>
/// 顶部状态栏 View（迭代 7c-1：静态内容）。
/// 固定顶部 1 行，显示 provider/model/security/ctx/round/tools。
/// 7c-1 不实时更新（不接 Agent），7c-2 接入 RoundStartEvent 后实时更新 round。
/// </summary>
internal sealed class StatusBarView : View
{
    private ProviderConfig? _providerConfig;
    private SecurityLevel _securityLevel;
    private int _contextWindowTokens = 64000;
    private int _toolCount;
    private int _estimatedTokens;
    private int _currentRound;

    public int EstimatedTokens
    {
        get => _estimatedTokens;
        set { _estimatedTokens = value; SetNeedsDraw(); }
    }

    public int CurrentRound
    {
        get => _currentRound;
        set { _currentRound = value; SetNeedsDraw(); }
    }

    public StatusBarView()
    {
        CanFocus = false;  // 状态栏不获取焦点
    }

    /// <summary>初始化状态栏数据。</summary>
    public void Update(ProviderConfig config, SecurityLevel level, TuiConfig tui, ToolRegistry registry)
    {
        _providerConfig = config;
        _securityLevel = level;
        _contextWindowTokens = tui.ContextWindowTokens ?? 64000;
        _toolCount = registry.GetAll().Count;
        SetNeedsDraw();
    }

    protected override bool OnDrawingContent()
    {
        if (_providerConfig is null) return false;

        var ratio = _contextWindowTokens > 0 ? (double)_estimatedTokens / _contextWindowTokens : 0;
        var pct = (int)(ratio * 100);

        var text = $"provider={_providerConfig.Name} model={_providerConfig.Model} " +
                   $"security={_securityLevel} ctx={pct}%({_estimatedTokens}/{_contextWindowTokens}) " +
                   $"round={_currentRound} tools={_toolCount}";

        // 用 Attribute 设置颜色（白字黑底）
        SetAttribute(new Attribute(Color.White, Color.Black));
        // 绘制文本到 Viewport
        Move(0, 0);
        AddStr(text);
        return true;
    }
}
