using Terminal.Gui;

namespace ParrotCode;

/// <summary>
/// 顶部状态栏（迭代 7c-1：继承内置 Label，静态内容）。
/// 固定顶部 1 行，显示 provider/model/security/ctx/round/tools。
/// 设 Text 即自动重绘，不需要 OnDrawingContent。
/// </summary>
internal sealed class StatusBarView : Label
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
        set { _estimatedTokens = value; RefreshText(); }
    }

    public int CurrentRound
    {
        get => _currentRound;
        set { _currentRound = value; RefreshText(); }
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
        RefreshText();
    }

    /// <summary>格式化并刷新 Text（Label 设 Text 即自动重绘，无需 OnDrawingContent）。</summary>
    private void RefreshText()
    {
        if (_providerConfig is null) return;
        var pct = _contextWindowTokens > 0 ? (int)((double)_estimatedTokens / _contextWindowTokens * 100) : 0;
        Text = $"provider={_providerConfig.Name} model={_providerConfig.Model} " +
               $"security={_securityLevel} ctx={pct}%({_estimatedTokens}/{_contextWindowTokens}) " +
               $"round={_currentRound} tools={_toolCount}";
    }
}
