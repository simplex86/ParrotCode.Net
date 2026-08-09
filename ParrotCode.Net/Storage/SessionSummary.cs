namespace ParrotCode;

/// <summary>
/// 会话列表摘要（/session list 用，不含消息内容）。
/// </summary>
public sealed record SessionSummary(
    string Id,
    DateTime UpdatedAt,
    int MessageCount,
    string Title);
