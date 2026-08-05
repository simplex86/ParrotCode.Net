namespace ParrotCode;

/// <summary>
/// 固定回显 Provider，用于在接入真实 LLM 前跑通管线。
/// 返回 "{输入}（mock）"，便于一眼区分 mock 与真实回复。
/// </summary>
public sealed class MockProvider : IChatProvider
{
    public Task<string> ChatAsync(string userInput, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult($"{userInput}（mock）");
    }
}
