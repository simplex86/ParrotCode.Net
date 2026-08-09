namespace ParrotCode;

/// <summary>
/// UI 抽象接口：命令通过此接口操作 UI，不直接依赖 TerminalApp。
/// 仅暴露命令需要的能力，遵循接口隔离原则。
/// </summary>
public interface IUiControl
{
    void AppendStaticMessage(string text);
    void AppendUserMessage(string text);
    void ClearMessages();
    void RefreshStatusBar();
    void UpdateTokenEstimate(int estimatedTokens);
    void UpdateSecurityLevel(SecurityLevel level);
    void RequestExit();
}
