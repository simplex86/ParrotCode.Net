namespace ParrotCode;

/// <summary>
/// 会话元数据。与消息内容分离存储（{id}.meta.json）。
/// </summary>
public sealed record SessionMeta
{
    public string Id { get; init; } = string.Empty;
    public DateTime CreatedAt { get; init; }
    public DateTime UpdatedAt { get; init; }
    public int MessageCount { get; init; }
    public string ProviderName { get; init; } = string.Empty;
    public string ModelName { get; init; } = string.Empty;
    public string Title { get; init; } = string.Empty;
}
