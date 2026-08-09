using System.IO;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace ParrotCode;

/// <summary>
/// JSONL 会话持久化（迭代 10b）。
/// - 每行一条消息的 JSON（MessageDto），FileMode.Create 全量快照写。
/// - Meta 文件分离：{sessionId}.meta.json 存会话概要。
/// - 崩溃恢复：逐行解析，损坏行跳过并记日志。
/// - 配对修复：未配对的 tool_use（缺 tool_result）截断到最后一个完整状态。
/// </summary>
public sealed class SessionStore
{
    private readonly string _storageDir;
    private readonly ILogger? _logger;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = false,
        PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public SessionStore(string? storageDir = null, ILogger? logger = null)
    {
        _storageDir = storageDir ?? ".parrotcode/sessions";
        _logger = logger;
    }

    /// <summary>存储目录（相对项目根或绝对路径）。</summary>
    public string StorageDir => _storageDir;

    /// <summary>
    /// 保存会话。生成新 ID，全量快照写 JSONL + 写 Meta。
    /// </summary>
    public async Task<SessionMeta> SaveAsync(
        IReadOnlyList<Message> messages,
        ProviderConfig provider,
        string? title,
        CancellationToken ct)
    {
        Directory.CreateDirectory(_storageDir);

        var id = GenerateSessionId();
        var jsonlPath = GetJsonlPath(id);
        var metaPath = GetMetaPath(id);

        // 写 JSONL（覆盖模式，全量快照）
        await using (var fs = new FileStream(jsonlPath, FileMode.Create, FileAccess.Write, FileShare.None))
        await using (var writer = new StreamWriter(fs, Encoding.UTF8))
        {
            foreach (var msg in messages)
            {
                var dto = MessageDto.FromMessage(msg);
                var line = JsonSerializer.Serialize(dto, JsonOpts);
                await writer.WriteLineAsync(line);
            }
        }

        var now = DateTime.UtcNow;
        var meta = new SessionMeta
        {
            Id = id,
            CreatedAt = now,
            UpdatedAt = now,
            MessageCount = messages.Count,
            ProviderName = provider.Name,
            ModelName = provider.Model,
            Title = title ?? DeriveTitle(messages)
        };

        var metaJson = JsonSerializer.Serialize(meta, JsonOpts);
        await File.WriteAllTextAsync(metaPath, metaJson, Encoding.UTF8, ct);

        _logger?.LogInformation("会话已保存：{Id}（{Count} 条消息）", id, messages.Count);
        return meta;
    }

    /// <summary>
    /// 加载会话。逐行解析 JSONL，损坏行跳过，未配对 tool_use 截断。
    /// </summary>
    public async Task<(SessionMeta Meta, IReadOnlyList<Message> Messages)> LoadAsync(
        string sessionId, CancellationToken ct)
    {
        var jsonlPath = GetJsonlPath(sessionId);
        var metaPath = GetMetaPath(sessionId);

        if (!File.Exists(jsonlPath))
            throw new FileNotFoundException($"会话文件不存在：{jsonlPath}");

        // 读 Meta（不存在时构造默认）
        SessionMeta meta;
        if (File.Exists(metaPath))
        {
            var metaJson = await File.ReadAllTextAsync(metaPath, ct);
            meta = JsonSerializer.Deserialize<SessionMeta>(metaJson, JsonOpts) ?? CreateDefaultMeta(sessionId);
        }
        else
        {
            meta = CreateDefaultMeta(sessionId);
        }

        // 读 JSONL
        var messages = new List<Message>();
        var corruptLines = 0;

        await using var fs = new FileStream(jsonlPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var reader = new StreamReader(fs, Encoding.UTF8);
        string? line;
        while ((line = await reader.ReadLineAsync(ct)) is not null)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                var dto = JsonSerializer.Deserialize<MessageDto>(line, JsonOpts);
                if (dto is not null)
                    messages.Add(dto.ToMessage());
            }
            catch (JsonException ex)
            {
                corruptLines++;
                _logger?.LogWarning("会话 {Id} 损坏行已跳过：{Error}", sessionId, ex.Message);
            }
        }

        if (corruptLines > 0)
            _logger?.LogWarning("会话 {Id} 共跳过 {Count} 行损坏数据", sessionId, corruptLines);

        // 配对修复：截断未配对的 tool_use
        var beforeCount = messages.Count;
        messages = RepairToolCallPairing(messages);
        if (messages.Count < beforeCount)
        {
            _logger?.LogWarning("会话 {Id} 配对修复：截断 {Count} 条未配对消息",
                sessionId, beforeCount - messages.Count);
        }

        return (meta with { MessageCount = messages.Count }, messages);
    }

    /// <summary>
    /// 列出所有会话摘要（按 UpdatedAt 倒序）。只扫 Meta 文件，不读 JSONL。
    /// </summary>
    public async Task<IReadOnlyList<SessionSummary>> ListAsync(CancellationToken ct)
    {
        if (!Directory.Exists(_storageDir))
            return Array.Empty<SessionSummary>();

        var result = new List<SessionSummary>();
        foreach (var metaFile in Directory.EnumerateFiles(_storageDir, "*.meta.json"))
        {
            try
            {
                var json = await File.ReadAllTextAsync(metaFile, ct);
                var meta = JsonSerializer.Deserialize<SessionMeta>(json, JsonOpts);
                if (meta is not null)
                    result.Add(new SessionSummary(meta.Id, meta.UpdatedAt, meta.MessageCount, meta.Title));
            }
            catch (Exception ex)
            {
                _logger?.LogWarning("读取会话 Meta 失败 {File}：{Error}", metaFile, ex.Message);
            }
        }

        return result.OrderByDescending(s => s.UpdatedAt).ToList();
    }

    /// <summary>
    /// 配对修复：检测未配对的 tool_use（assistant 带 ToolCalls 但后续缺对应 tool_result）。
    /// 截断到最后一个完整状态——删除未配对的 assistant(tool_calls) 消息。
    /// </summary>
    internal static List<Message> RepairToolCallPairing(List<Message> messages)
    {
        var pairedToolCallIds = new HashSet<string>();
        foreach (var msg in messages)
        {
            if (msg.Role == MessageRole.Tool && msg.ToolCallId is not null)
                pairedToolCallIds.Add(msg.ToolCallId);
        }

        for (var i = messages.Count - 1; i >= 0; i--)
        {
            var msg = messages[i];
            if (msg.Role == MessageRole.Assistant && msg.ToolCalls is { Count: > 0 })
            {
                var allPaired = msg.ToolCalls.All(tc => pairedToolCallIds.Contains(tc.Id));
                if (!allPaired)
                    return messages.Take(i).ToList();
            }
        }

        return messages;
    }

    private static string GenerateSessionId()
    {
        var ts = DateTime.Now.ToString("yyyyMMdd_HHmmss", System.Globalization.CultureInfo.InvariantCulture);
        var suffix = Guid.NewGuid().ToString("N")[..6];
        return $"{ts}_{suffix}";
    }

    private static string DeriveTitle(IReadOnlyList<Message> messages)
    {
        var firstUser = messages.FirstOrDefault(m => m.Role == MessageRole.User);
        if (firstUser is null) return "（无标题）";
        var content = firstUser.Content.Replace("\n", " ").Trim();
        return content.Length <= 50 ? content : content[..50] + "...";
    }

    private static SessionMeta CreateDefaultMeta(string id) => new()
    {
        Id = id,
        CreatedAt = DateTime.MinValue,
        UpdatedAt = DateTime.MinValue,
        MessageCount = 0
    };

    private string GetJsonlPath(string id) => Path.Combine(_storageDir, $"{id}.jsonl");
    private string GetMetaPath(string id) => Path.Combine(_storageDir, $"{id}.meta.json");
}

// ===== 协议中性 DTO（内部类） =====

/// <summary>
/// 消息的 JSONL 序列化 DTO（协议中性，不依赖 OpenAI wire format）。
/// 保留 ToolCalls 和 ToolCallId 以支持完整恢复。
/// </summary>
internal sealed class MessageDto
{
    public string Role { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;
    public List<ToolCallDto>? ToolCalls { get; set; }
    public string? ToolCallId { get; set; }

    public static MessageDto FromMessage(Message msg)
    {
        var dto = new MessageDto
        {
            Role = msg.Role.ToString().ToLowerInvariant(),
            Content = msg.Content,
            ToolCallId = msg.ToolCallId
        };
        if (msg.ToolCalls is { Count: > 0 })
        {
            dto.ToolCalls = msg.ToolCalls.Select(tc => new ToolCallDto
            {
                Id = tc.Id,
                Name = tc.Name,
                Input = tc.Input.GetRawText()  // JsonElement → JSON 字符串
            }).ToList();
        }
        return dto;
    }

    public Message ToMessage()
    {
        var role = Role.ToLowerInvariant() switch
        {
            "system" => MessageRole.System,
            "user" => MessageRole.User,
            "assistant" => MessageRole.Assistant,
            "tool" => MessageRole.Tool,
            _ => MessageRole.User
        };

        Message msg = new(role, Content);

        if (ToolCalls is { Count: > 0 })
        {
            var toolCalls = ToolCalls.Select(tc =>
            {
                using var doc = JsonDocument.Parse(tc.Input ?? "{}");
                return new ToolCall(tc.Id, tc.Name, doc.RootElement.Clone());
            }).ToList();
            msg = msg with { ToolCalls = toolCalls };
        }

        if (ToolCallId is not null)
            msg = msg with { ToolCallId = ToolCallId };

        return msg;
    }
}

internal sealed class ToolCallDto
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? Input { get; set; }
}
