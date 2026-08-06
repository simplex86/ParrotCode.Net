using System.Text.Json;

namespace ParrotCode;

/// <summary>
/// 协议无关的 Provider 抽象。替代迭代 1 的临时 IChatProvider。
/// 迭代 6：新增带 tools 的流式重载，返回 IAsyncEnumerable&lt;ChatChunk&gt;。
/// 旧重载（ChatAsync + ChatStreamAsync 返回 string）保留，迭代 3/4 既有代码与测试不回归。
/// </summary>
public interface IBaseProvider
{
    /// <summary>
    /// 非流式聊天（迭代 3 保留）。用于不需要工具调用与实时反馈的场景。
    /// </summary>
    Task<string> ChatAsync(IReadOnlyList<Message> messages, CancellationToken cancellationToken);

    /// <summary>
    /// 纯文本流式（迭代 3 保留）。返回 IAsyncEnumerable&lt;string&gt;。
    /// 不传 tools，LLM 不会产出 tool_calls。
    /// </summary>
    IAsyncEnumerable<string> ChatStreamAsync(IReadOnlyList<Message> messages, CancellationToken cancellationToken);

    /// <summary>
    /// 带 tools 的流式（迭代 6 新增）。返回 IAsyncEnumerable&lt;ChatChunk&gt;。
    /// AgentLoop 用此重载：tools 来自 ToolRegistry.ToOpenAiSchemas()，
    /// toolChoice 控制 LLM 是否强制调用工具（auto/none/required）。
    /// Provider 把协议 wire format 翻译成 ChatChunk，AgentLoop 不感知协议细节。
    /// </summary>
    IAsyncEnumerable<ChatChunk> ChatStreamAsync(
        IReadOnlyList<Message> messages,
        JsonElement? tools,
        string toolChoice,
        CancellationToken cancellationToken);
}
