# 迭代 10b：JSONL 会话持久化（SessionStore + MessageDto + `/session` 接入）

> **状态**：[设计完成，待实现]
> **前置迭代**：10a [已完成]（命令系统骨架 + `/session` stub）
> **父文档**：[iter-10-design.md](iter-10-design.md)（保留追溯）
> **后续迭代**：10c（项目指令 + 端到端装配）
> **目标**：交付 JSONL 会话持久化——`SessionStore`（追加写 + 逐行读 + 损坏行跳过 + 配对修复 + Meta 文件）+ 协议中性 `MessageDto` + `/session save|load|list|current` 子命令接入真实 Store + `SessionConfig` 配置。本迭代验证"退出后恢复"核心目标。

---

## 一、迭代目标

### 1.1 核心目标

把 10a 的 `/session` stub 升级为**真实持久化**：

1. **`SessionStore`**：JSONL 持久化引擎
   - `SaveAsync`：全量快照写（`FileMode.Create` 覆盖），每行一条 `MessageDto` JSON
   - `LoadAsync`：逐行 `ReadLineAsync` 解析，损坏行跳过并记日志
   - `ListAsync`：扫描 `.meta.json` 文件，按 `UpdatedAt` 倒序
   - 配对修复：未配对 `tool_use`（缺 `tool_result`）截断到最后一个完整状态

2. **`MessageDto` / `ToolCallDto`**：协议中性序列化 DTO
   - 不依赖 OpenAI wire format（`MessageExtensions.ToOpenAiWire`）
   - 保留 `ToolCalls` 和 `ToolCallId` 以支持完整恢复
   - `ToolCall.Input`（`JsonElement`）序列化为字符串，反序列化用 `JsonDocument.Parse` 重建

3. **`/session` 子命令接入**：把 10a stub 替换为真实实现
   - `save [title]`：保存当前会话，返回会话 ID
   - `load <id>`：加载指定会话（清空当前历史 + 渲染到 UI + 时间跨度提醒）
   - `list`：列出最近 10 个会话
   - `current`：显示当前会话状态

4. **`SessionConfig`**：配置项（`storage_dir` / `enable`）

5. **`App.cs` 装配**：构造 `SessionStore` 注入 `TerminalApp`

### 1.2 本迭代要验证的关键问题

| 问题 | 验证方式 | 失败对策 |
|------|---------|---------|
| JSONL 崩溃恢复：损坏行是否跳过不崩溃 | 单测（手动构造损坏 JSONL） | `try-catch JsonException` 逐行处理 |
| 未配对 `tool_use` 截断是否正确 | 单测（构造 assistant+tool_calls 缺 tool_result） | 从后往前找第一个未配对的，截断到此之前 |
| `MessageDto` 往返序列化是否无损（含 ToolCalls） | 单测（save → load 比对） | `JsonElement` 用 `GetRawText` 序列化、`JsonDocument.Parse` 重建 |
| 大历史会话 `SaveAsync` 是否阻塞 UI | 手动（100 条消息） | async 文件写入；本迭代接受（消息数通常 < 100） |
| `/session load` 恢复后 AI 是否记得历史 | 端到端（load 后继续对话） | `History.ReplaceMessages` 正确恢复全部消息含 ToolCalls |
| 恢复 30 分钟前的会话是否显示时间提醒 | 手动（改系统时间或等 30 分钟） | `LoadAsync` 后计算 `DateTime.UtcNow - meta.UpdatedAt` |
| `/session load` 覆盖当前历史是否需提示 | 代码审查 | 本迭代直接覆盖；10c 或后续加 HITL 确认 |

### 1.3 非目标（明确不做）

- ❌ 不做会话自动恢复（启动时加载上次会话）——进阶练习
- ❌ 不做多会话并发 / 会话切换栈——后续迭代
- ❌ 不做会话导出为 Markdown / 纯文本——进阶练习
- ❌ 不做 `AllowPermanent` 跨会话持久化——迭代 12
- ❌ 不做增量追加写（每轮自动保存）——本迭代 `SaveAsync` 用全量快照
- ❌ 不做 `/session load` 前的 HITL 确认——本迭代直接覆盖（简化交互）

### 1.4 与 10a 的衔接策略

10a 的 `SessionCommand` stub 已检测 `context.SessionStore is null`：
- 10a：`TerminalApp._sessionStore = null` → stub 返回"未启用"
- 10b：`App.cs` 构造真实 `SessionStore` 注入 `TerminalApp` → `SessionCommand` 自动走真实逻辑
- **`SessionCommand` 代码需扩展**：10a 的 stub 只有 null 检查，10b 需补充 save/load/list/current 子命令分发

---

## 二、文件改动清单

### 2.1 新增文件（4 个）

```
Storage/
├── SessionStore.cs        # JSONL 持久化引擎（含 MessageDto / ToolCallDto 内部类）
├── SessionMeta.cs         # 会话元数据 record
└── SessionSummary.cs      # 会话列表摘要 record（/session list 用）
```

> **注**：`MessageDto` / `ToolCallDto` 作为 `SessionStore.cs` 的 internal 类，不单独文件（它们只在 SessionStore 内使用）。

### 2.2 修改文件（4 个）

| 文件 | 改动 |
|------|------|
| `Commands/Builtin/SessionCommand.cs` | 10a stub 扩展为真实 save/load/list/current 子命令分发 |
| `Tui/TerminalApp.cs` | `_sessionStore` 从 null 改为构造函数注入真实 Store；`_instructionSummary` 仍为 null（10c 填充） |
| `App/App.cs` | 构造 `SessionStore`（读 `SessionConfig`）注入 `TerminalApp` |
| `Config/Models.cs` | 新增 `SessionConfig` record + `AppConfig.Session` 字段 |
| `example.parrotcode.yaml` | 新增 `session:` 配置节示例 |

### 2.3 不变文件

- `Commands/` 骨架（Registry/Parser/Dispatcher）——10a 已完成
- `Instructions/`——10c
- `Agent/AgentLoop.cs`——10c 才改 system prompt

---

## 三、详细设计

### 3.1 JSONL 文件结构

```
.parrotcode/sessions/
├── {sessionId}.jsonl          ← 每行一条消息 JSON（全量快照写）
├── {sessionId}.meta.json      ← 会话元数据
├── {sessionId2}.jsonl
├── {sessionId2}.meta.json
└── ...
```

**JSONL 每行格式**（协议中性的 `MessageDto`）：

```json
{"role":"user","content":"你好","toolCalls":null,"toolCallId":null}
{"role":"assistant","content":"","toolCalls":[{"id":"call_1","name":"read_file","input":"{\"path\":\"README.md\"}"}],"toolCallId":null}
{"role":"tool","content":"# README\n...","toolCalls":null,"toolCallId":"call_1"}
```

**Meta 文件格式**（`{sessionId}.meta.json`）：

```json
{
  "id": "20260809_153000_a1b2c3",
  "createdAt": "2026-08-09T15:30:00Z",
  "updatedAt": "2026-08-09T16:45:12Z",
  "messageCount": 24,
  "providerName": "deepseek",
  "modelName": "deepseek-chat",
  "title": "你好"
}
```

### 3.2 SessionMeta / SessionSummary

```csharp
// Storage/SessionMeta.cs
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
```

```csharp
// Storage/SessionSummary.cs
namespace ParrotCode;

/// <summary>
/// 会话列表摘要（/session list 用，不含消息内容）。
/// </summary>
public sealed record SessionSummary(
    string Id,
    DateTime UpdatedAt,
    int MessageCount,
    string Title);
```

### 3.3 SessionStore（核心引擎）

```csharp
// Storage/SessionStore.cs
using System.IO;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace ParrotCode;

/// <summary>
/// JSONL 会话持久化。
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
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public SessionStore(string? storageDir = null, ILogger? logger = null)
    {
        _storageDir = storageDir ?? ".parrotcode/sessions";
        _logger = logger;
    }

    /// <summary>存储目录（绝对路径或相对项目根）。</summary>
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
            meta = JsonSerializer.Deserialize<SessionMeta>(metaJson) ?? CreateDefaultMeta(sessionId);
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
                var dto = JsonSerializer.Deserialize<MessageDto>(line);
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
                var meta = JsonSerializer.Deserialize<SessionMeta>(json);
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
    private static List<Message> RepairToolCallPairing(List<Message> messages)
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
```

### 3.4 MessageDto / ToolCallDto（协议中性 DTO）

```csharp
// Storage/SessionStore.cs 内部类

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
```

### 3.5 SessionCommand 扩展（stub → 真实实现）

```csharp
// Commands/Builtin/SessionCommand.cs — 10b 扩展

using System.Text;

namespace ParrotCode.Commands.Builtin;

/// <summary>
/// /session save|load|list|current：会话持久化。
/// 10a：SessionStore 为 null 时返回"未启用"。
/// 10b：注入真实 SessionStore 后走真实逻辑。
/// </summary>
public sealed class SessionCommand : ICommand
{
    public string Name => "session";
    public string Description => "会话持久化（save/load/list/current）";
    public CommandType Type => CommandType.System;
    public IReadOnlyList<string> Aliases => new[] { "sessions" };
    public string Usage => "/session save|load <id>|list|current";

    public async Task<CommandResult> ExecuteAsync(CommandContext context)
    {
        if (context.SessionStore is null)
            return CommandResult.WithOutput("[!] 会话持久化未启用（迭代 10b 接入）");

        var parts = context.RawInput.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
        var subcommand = parts.Length > 1 ? parts[1].ToLowerInvariant() : null;

        return subcommand switch
        {
            null => CommandResult.WithOutput("用法：/session save|load <id>|list|current"),
            "save" => await SaveAsync(context, parts),
            "load" => await LoadAsync(context, parts),
            "list" => await ListAsync(context),
            "current" => Current(context),
            _ => CommandResult.WithOutput($"[!] 未知子命令：{subcommand}（可选：save/load/list/current）")
        };
    }

    private async Task<CommandResult> SaveAsync(CommandContext context, string[] parts)
    {
        var title = parts.Length > 2 ? parts[2] : null;
        var messages = context.History.ToProviderMessages();

        if (messages.Count == 0)
            return CommandResult.WithOutput("[!] 历史为空，无需保存");

        var meta = await context.SessionStore!.SaveAsync(
            messages, context.ProviderConfig, title, context.Ct);

        return CommandResult.WithOutput(
            $"[i] 会话已保存\n  ID: {meta.Id}\n  消息数: {meta.MessageCount}\n  标题: {meta.Title}");
    }

    private async Task<CommandResult> LoadAsync(CommandContext context, string[] parts)
    {
        if (parts.Length < 3)
            return CommandResult.WithOutput("[!] 用法：/session load <id>");

        var sessionId = parts[2];

        // 注：本迭代直接覆盖当前历史，不自动保存。后续可加 HITL 确认。
        var (meta, messages) = await context.SessionStore!.LoadAsync(sessionId, context.Ct);

        if (messages.Count == 0)
            return CommandResult.WithOutput($"[!] 会话 {sessionId} 无消息或不存在");

        // 清空当前历史 + UI
        context.History.Clear();
        context.Ui.ClearMessages();
        context.Compressor?.ResetWarning();

        // 加载消息到历史
        context.History.ReplaceMessages(messages);

        // 时间跨度提醒
        var elapsed = DateTime.UtcNow - meta.UpdatedAt;
        if (elapsed.TotalMinutes > 30)
        {
            context.Ui.AppendStaticMessage(
                $"[i] 这是 {FormatTimeSpan(elapsed)}前的会话（{meta.UpdatedAt:yyyy-MM-dd HH:mm} 保存）");
        }

        // 渲染历史消息到 UI
        foreach (var msg in messages)
            RenderHistoricalMessage(context.Ui, msg);

        context.Ui.UpdateTokenEstimate(context.History.EstimatedTokens);

        return CommandResult.WithOutput(
            $"[i] 已加载会话 {meta.Id}（{messages.Count} 条消息）");
    }

    private async Task<CommandResult> ListAsync(CommandContext context)
    {
        var sessions = await context.SessionStore!.ListAsync(context.Ct);
        if (sessions.Count == 0)
            return CommandResult.WithOutput("[i] 无已保存会话");

        var sb = new StringBuilder();
        sb.AppendLine("最近会话（按更新时间倒序）：");
        foreach (var s in sessions.Take(10))
            sb.AppendLine($"  {s.Id}  {s.UpdatedAt:MM-dd HH:mm}  {s.MessageCount,3}条  {s.Title}");
        return CommandResult.WithOutput(sb.ToString());
    }

    private static CommandResult Current(CommandContext context)
    {
        // 本迭代不跟踪"当前会话 ID"（无自动恢复），始终返回提示
        return CommandResult.WithOutput("[i] 当前会话未持久化（用 /session save 保存）");
    }

    private static string FormatTimeSpan(TimeSpan elapsed)
    {
        if (elapsed.TotalDays >= 1) return $"{(int)elapsed.TotalDays} 天 ";
        if (elapsed.TotalHours >= 1) return $"{(int)elapsed.TotalHours} 小时 ";
        if (elapsed.TotalMinutes >= 1) return $"{(int)elapsed.TotalMinutes} 分钟 ";
        return "刚刚";
    }

    private static void RenderHistoricalMessage(IUiControl ui, Message msg)
    {
        switch (msg.Role)
        {
            case MessageRole.User:
                ui.AppendUserMessage(msg.Content);
                break;
            case MessageRole.Assistant:
                ui.AppendStaticMessage($"⏺ {msg.Content}");
                break;
            case MessageRole.Tool:
                ui.AppendStaticMessage($"  ⎿ [tool] {TruncateForDisplay(msg.Content)}");
                break;
            case MessageRole.System:
                // 压缩摘要等 system 消息不渲染到 UI（避免干扰）
                break;
        }
    }

    private static string TruncateForDisplay(string s, int max = 200) =>
        s.Length <= max ? s : s[..(max - 3)] + "...";
}
```

### 3.6 SessionConfig 配置

```csharp
// Config/Models.cs — 新增 SessionConfig

/// <summary>
/// 会话持久化配置（迭代 10b 新增）。null 时用默认值。
/// </summary>
public sealed record SessionConfig
{
    /// <summary>会话存储目录。默认 ".parrotcode/sessions"（项目根下）。</summary>
    public string? StorageDir { get; init; }

    /// <summary>是否启用会话持久化。默认 true。false 时 /session 命令不可用。</summary>
    public bool? Enable { get; init; }
}

// AppConfig 扩展
public sealed record AppConfig
{
    // ... 既有字段 ...
    /// <summary>会话持久化配置（迭代 10b 新增）。null 时用默认值。</summary>
    public SessionConfig? Session { get; init; }
}
```

**示例 YAML**（`example.parrotcode.yaml` 追加）：

```yaml
# 迭代 10b 新增：会话持久化配置（全部可选，省略时用默认值）
session:
  enable: true                        # 是否启用会话持久化
  storage_dir: .parrotcode/sessions   # 会话存储目录（相对项目根）
```

### 3.7 App.cs 装配扩展

```csharp
// App/App.cs — 扩展：构造 SessionStore 注入 TerminalApp

public async Task RunAsync()
{
    // ... 既有装配（Provider/SecurityGuard/Compressor）...

    // 【迭代 10b】构造 SessionStore
    var sessionConfig = _config.Session ?? new SessionConfig();
    SessionStore? sessionStore = null;
    if (sessionConfig.Enable ?? true)
    {
        sessionStore = new SessionStore(
            storageDir: sessionConfig.StorageDir ?? ".parrotcode/sessions",
            _logger);
    }

    using var terminalApp = new TerminalApp(/* 既有参数 */,
                                            sessionStore,        // 10b 注入
                                            /* instructions: null,  10c 注入 */
                                            _logger,
                                            _ct);
    await terminalApp.RunAsync();
}
```

> **注**：`TerminalApp` 构造函数在 10a 已接受 `SessionStore?`（10a 传 null）。10b 改为传真实实例。`SessionCommand` 的 `context.SessionStore` 自动非 null，走真实逻辑。

### 3.8 配对修复流程图

```
加载 JSONL → 逐行解析为 List<Message>
                    │
                    ▼
        RepairToolCallPairing(messages)
                    │
                    ▼
    ┌───────────────────────────────────┐
    │ 1. 收集所有 tool 消息的 ToolCallId │
    │    → pairedToolCallIds HashSet     │
    │                                   │
    │ 2. 从后往前找第一个 assistant      │
    │    带 ToolCalls 且未全部配对的     │
    │                                   │
    │ 3. 找到 → 截断到此消息之前（不含）│
    │    未找到 → 保留全部               │
    └───────────────────────────────────┘
                    │
                    ▼
            返回修复后的 messages
```

**示例**：
```
[user] 你好
[assistant] (tool_calls: [call_1: read_file])   ← 未配对（缺 tool_result）
→ 截断到此之前，只保留 [user] 你好

[user] 你好
[assistant] (tool_calls: [call_1: read_file])
[tool] (tool_call_id: call_1) "# README..."
[assistant] "这是 README 内容"
→ 全部配对，保留全部
```

---

## 四、验收标准

### 4.1 编译与测试

| 编号 | 标准 | 验证方式 |
|------|------|---------|
| 10b-01 | `dotnet build` 0 错误 0 警告 | 编译 |
| 10b-02 | 全量测试全绿（10a + 10b 新增） | `dotnet test` |
| 10b-03 | `SessionStoreTests` 全绿 | `dotnet test` |
| 10b-04 | `SessionCommandTests` 全绿（真实 Store） | `dotnet test` |
| 10b-05 | `SessionConfigTests` 全绿 | `dotnet test` |
| 10b-06 | 10a 测试不回归 | `dotnet test` |

### 4.2 SessionStore（JSONL 引擎）

| 编号 | 标准 | 验证方式 |
|------|------|---------|
| 10b-10 | `SaveAsync` 生成 `{id}.jsonl` 文件 | 单测（临时目录） |
| 10b-11 | `SaveAsync` 生成 `{id}.meta.json` 文件 | 单测 |
| 10b-12 | JSONL 每行一条消息 JSON，可独立解析 | 单测 |
| 10b-13 | `LoadAsync` 正确恢复所有消息 | 单测 |
| 10b-14 | `LoadAsync` 恢复的消息含 ToolCalls 和 ToolCallId | 单测 |
| 10b-15 | `LoadAsync` 损坏行跳过不抛异常 | 单测（构造损坏 JSONL） |
| 10b-16 | `LoadAsync` 损坏行记日志 | 单测 |
| 10b-17 | `LoadAsync` 未配对 tool_use 截断到最后完整状态 | 单测 |
| 10b-18 | `LoadAsync` 配对完整的 tool_use 不被截断 | 单测 |
| 10b-19 | `LoadAsync` 文件不存在抛 `FileNotFoundException` | 单测 |
| 10b-20 | `LoadAsync` Meta 不存在时用默认 Meta | 单测 |
| 10b-21 | `ListAsync` 按 UpdatedAt 倒序 | 单测 |
| 10b-22 | `ListAsync` 空目录返回空列表 | 单测 |
| 10b-23 | `ListAsync` 损坏 Meta 文件跳过不崩溃 | 单测 |
| 10b-24 | Meta 文件含 Id/CreatedAt/UpdatedAt/MessageCount/ProviderName/ModelName/Title | 单测 |
| 10b-25 | 存储目录不存在时 `SaveAsync` 自动创建 | 单测 |
| 10b-26 | `MessageDto` 正确序列化/反序列化所有 MessageRole | 单测 |
| 10b-27 | `MessageDto` 正确序列化/反序列化 ToolCalls（含 Input JSON） | 单测 |
| 10b-28 | `MessageDto` 往返序列化无损（save→load 比对） | 单测 |
| 10b-29 | `DeriveTitle` 首条用户消息前 50 字符 | 单测 |
| 10b-30 | `GenerateSessionId` 格式 `yyyyMMdd_HHmmss_xxxxxx` | 单测 |

### 4.3 SessionCommand（/session 子命令）

| 编号 | 标准 | 验证方式 |
|------|------|---------|
| 10b-35 | `/session save` 保存消息到 JSONL 文件 | 单测（临时目录 + mock IUiControl） |
| 10b-36 | `/session save` 生成 Meta 文件 | 单测 |
| 10b-37 | `/session save` 标题为首条用户消息前 50 字符 | 单测 |
| 10b-38 | `/session save` 历史为空时返回"无需保存" | 单测 |
| 10b-39 | `/session save <title>` 自定义标题 | 单测 |
| 10b-40 | `/session load <id>` 加载消息到 History | 单测 |
| 10b-41 | `/session load` 清空当前历史再加载 | 单测 |
| 10b-42 | `/session load` 调用 `Compressor.ResetWarning` | 单测 |
| 10b-43 | `/session load` 渲染历史消息到 UI | 单测 |
| 10b-44 | `/session load` 不存在的 ID 抛 `FileNotFoundException`（被 Dispatcher 捕获） | 单测 |
| 10b-45 | `/session load` 无子命令返回用法提示 | 单测 |
| 10b-46 | `/session list` 列出所有会话按时间倒序 | 单测 |
| 10b-47 | `/session list` 空时返回"无已保存会话" | 单测 |
| 10b-48 | `/session current` 返回"未持久化"提示 | 单测 |
| 10b-49 | `/session` 无子命令返回用法 | 单测 |
| 10b-50 | `/session foo` 未知子命令返回错误 | 单测 |
| 10b-51 | `/session`（SessionStore=null）返回"未启用" | 单测（10a stub 行为保持） |

### 4.4 SessionConfig 配置

| 编号 | 标准 | 验证方式 |
|------|------|---------|
| 10b-55 | `session:` 段正确解析为 `SessionConfig` | 单测 |
| 10b-56 | 无 `session:` 段时用默认值（`.parrotcode/sessions` / enable=true） | 单测 |
| 10b-57 | `session.enable: false` 时 `SessionStore` 为 null | 单测（App 装配） |
| 10b-58 | `session.storage_dir` 自定义路径生效 | 单测 |

### 4.5 端到端

| 编号 | 标准 | 验证方式 |
|------|------|---------|
| 10b-65 | `/session save` 后退出程序，重启后 `/session list` 能看到该会话 | 手动 |
| 10b-66 | `/session load <id>` 恢复历史消息到对话区 | 手动 |
| 10b-67 | 恢复的会话能继续对话（AI 记得历史） | 手动 |
| 10b-68 | 恢复 30 分钟前的会话时显示时间跨度提醒 | 手动 |
| 10b-69 | 恢复的会话含工具调用历史（ToolCalls 完整） | 手动 |
| 10b-70 | `/status` 显示会话存储路径（非"未启用"） | 手动 |
| 10b-71 | 手动在 JSONL 末尾加损坏行，`/session load` 跳过损坏行正常加载 | 手动 |

### 4.6 回归

| 编号 | 标准 | 验证方式 |
|------|------|---------|
| 10b-75 | 10a 命令系统不受影响（/help /clear /mode 等正常） | 手动回归 |
| 10b-76 | 迭代 9 功能不受影响（截断/摘要/熔断） | 手动回归 |
| 10b-77 | 迭代 8c 功能不受影响（安全层/HITL） | 手动回归 |

---

## 五、测试计划

| 测试文件 | 覆盖范围 | 预估用例数 |
|---------|---------|-----------|
| `SessionStoreTests.cs` | 保存/加载/损坏行/配对修复/列表/Meta/DTO 往返 | ~21 |
| `SessionCommandTests.cs` | save/load/list/current/子命令错误/未启用 | ~17 |
| `SessionConfigTests.cs` | YAML 解析/默认值/enable=false | ~4 |

**端到端手动测试清单**（对照 10b-65 ~ 10b-77）：

1. **会话持久化验证**：
   - 启动程序，对话 3 轮（含工具调用）
   - `/session save`
   - 记录会话 ID，`/exit` 退出
   - 重新启动，`/session list` 看到该会话
   - `/session load <id>`，验证历史消息恢复到对话区
   - 继续对话，验证 AI 记得历史

2. **时间跨度提醒验证**：
   - 保存会话后等待 30 分钟以上（或改系统时间）
   - `/session load <id>`，验证显示"这是 X 小时前的会话"

3. **崩溃恢复验证**：
   - 手动在 `.parrotcode/sessions/xxx.jsonl` 末尾加一行损坏 JSON
   - `/session load xxx`，验证损坏行跳过、其他消息正常加载

4. **回归验证**：
   - 10a 命令正常（/help /clear /mode /compress /status /exit）
   - 9 功能：截断/摘要/熔断正常
   - 8c 功能：安全层/HITL 正常

---

## 六、实施步骤

1. 新建 `Storage/SessionStore.cs`（含 `MessageDto` / `ToolCallDto` 内部类）
2. 新建 `Storage/SessionMeta.cs` / `SessionSummary.cs`
3. 新建 `SessionStoreTests.cs`（用临时目录，覆盖保存/加载/损坏/配对修复/列表）
4. 扩展 `Commands/Builtin/SessionCommand.cs`：stub → 真实 save/load/list/current
5. 新建 `SessionCommandTests.cs`（用临时目录 + mock IUiControl）
6. `Config/Models.cs` 加 `SessionConfig` + `AppConfig.Session`
7. `example.parrotcode.yaml` 加 `session:` 段
8. 新建 `SessionConfigTests.cs`
9. `App/App.cs` 构造 `SessionStore` 注入 `TerminalApp`
10. 验证：`dotnet build` 0 警告 + `dotnet test` 全绿
11. 端到端手动验收（10b-65 ~ 10b-77）
12. 标记迭代 10b [已完成]

---

## 七、风险与对策

| 风险 | 概率 | 影响 | 对策 |
|------|------|------|------|
| JSONL 文件并发写入冲突 | 低 | 中 | 本迭代单会话单线程；`SaveAsync` 用 `FileShare.None` 独占写 |
| `MessageDto.Input` 反序列化丢失 `JsonElement.ValueKind` | 低 | 低 | `JsonDocument.Parse` 重建，`Clone()` 确保生命周期独立 |
| 恢复的会话含压缩后的 system 消息 | 中 | 低 | `RenderHistoricalMessage` 跳过 `MessageRole.System`（不显示到 UI）；历史正常传 AgentLoop |
| 恢复会话后 token 立即超阈值触发自动压缩 | 中 | 低 | `LoadAsync` 后 `Compressor.ResetWarning()`；若仍超 90% 下轮自动摘要——预期行为 |
| 大历史会话 `SaveAsync` 阻塞 UI | 中 | 中 | async 文件写入；本迭代接受（消息数 < 100）；后续可加进度提示 |
| `/session load` 覆盖当前历史丢失 | 中 | 中 | 本迭代直接覆盖；文档明确"不自动保存"；后续可加 HITL 确认 |
| 损坏 Meta 文件导致 `ListAsync` 崩溃 | 低 | 低 | `try-catch` 逐文件处理，损坏的跳过记日志 |
| 会话 ID 碰撞（同秒生成 + 6 位随机） | 极低 | 低 | `Guid.NewGuid().ToString("N")[..6]` 提供足够熵 |

---

## 八、关键设计决策

### Q1：为什么 JSONL 每行一条消息而非 JSON 数组？

**JSON 数组**：追加写需重写整个文件；崩溃时可能损坏整个数组（JSON 语法错误 → 全部丢失）。

**JSONL**：每行一条消息 JSON，`FileStream.Append` 追加写 O(1)；崩溃时最多丢最后一行（损坏行跳过，其他行不受影响）；可流式读取（`ReadLineAsync` 逐行处理）。

**取舍**：`SaveAsync` 当前用 `FileMode.Create` 全量写（快照保存）。后续如需增量追加（每轮自动保存），改用 `FileMode.Append` 即可——JSONL 格式天然支持。

### Q2：为什么用独立的 MessageDto 而非 MessageExtensions.ToOpenAiWire？

`ToOpenAiWire` 输出 OpenAI wire format（`role` / `content` / `tool_calls` / `tool_call_id`），与协议耦合。未来支持 Anthropic 协议时 wire format 不同，SessionStore 不应依赖具体协议。

`MessageDto` 协议中性，字段名与 `Message` record 一致。`ToolCall.Input` 序列化为 `Input` 字符串（原始 JSON），反序列化用 `JsonDocument.Parse` 重建 `JsonElement`。

### Q3：为什么配对修复截断而非补全？

**补全方案**：为未配对的 `tool_use` 补一个假的 `tool_result`（如"工具执行被中断"）。

**问题**：LLM 可能基于假结果继续推理，产生错误结论。

**截断方案**：删除未配对的 `assistant(tool_calls)` 消息，保留之前的历史。LLM 看不到"孤儿 tool_call"，不会报错。

**取舍**：截断丢失部分历史，但保证协议一致性。用户可从 `/session list` 看到消息数减少（Meta 的 MessageCount 会更新）。

### Q4：为什么 `/session load` 不自动保存当前历史？

- 用户可能不想保存当前历史（测试/临时对话）
- 自动保存的会话 ID 不返回给用户——用户不知道去哪找
- 增加复杂度（需处理自动保存失败）

**取舍**：简化交互。`/session load` 直接清空当前历史再加载。用户需主动 `/session save`。后续可加 HITL 内联确认。

---

**文档结束**。状态：[设计完成，待实现]
