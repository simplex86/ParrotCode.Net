namespace ParrotCode;

/// <summary>
/// 最小聊天 Provider 抽象。本迭代（迭代 1）的临时接口，
/// 迭代 2 将演进为支持流式与工具调用的 IBaseProvider。
/// </summary>
public interface IChatProvider
{
    /// <summary>
    /// 非流式聊天：给定用户输入，返回完整回复。
    /// </summary>
    Task<string> ChatAsync(string userInput, CancellationToken cancellationToken);
}
