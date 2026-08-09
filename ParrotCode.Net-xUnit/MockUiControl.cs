using ParrotCode;

namespace ParrotCode.xUnit;

/// <summary>
/// IUiControl 的 mock 实现，用于命令单元测试。
/// 记录所有调用，便于断言验证。
/// </summary>
public sealed class MockUiControl : IUiControl
{
    public List<string> StaticMessages { get; } = new();
    public List<string> UserMessages { get; } = new();
    public bool MessagesCleared { get; private set; }
    public int RefreshStatusBarCount { get; private set; }
    public int? LastTokenEstimate { get; private set; }
    public SecurityLevel? LastSecurityLevel { get; private set; }
    public bool ExitRequested { get; private set; }

    public void AppendStaticMessage(string text) => StaticMessages.Add(text);
    public void AppendUserMessage(string text) => UserMessages.Add(text);
    public void ClearMessages() => MessagesCleared = true;
    public void RefreshStatusBar() => RefreshStatusBarCount++;
    public void UpdateTokenEstimate(int estimatedTokens) => LastTokenEstimate = estimatedTokens;
    public void UpdateSecurityLevel(SecurityLevel level) => LastSecurityLevel = level;
    public void RequestExit() => ExitRequested = true;
}
