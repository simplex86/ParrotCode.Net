using System.IO;
using System.Text;
using System.Text.Json;
using ParrotCode;

namespace ParrotCode.xUnit;

/// <summary>
/// SessionStore 单元测试（迭代 10b）。
/// 覆盖保存/加载/损坏行跳过/配对修复/列表/Meta/DTO 往返。
/// 隔离策略：每个用例用独立临时目录，用完清理。
/// </summary>
public class SessionStoreTests
{
    private static readonly ProviderConfig TestProvider = new()
    {
        Name = "test",
        Protocol = "mock",
        Model = "test-model"
    };

    /// <summary>创建独立临时目录，用完自动清理。</summary>
    private sealed class TempDir : IDisposable
    {
        public string Dir { get; } = Directory.CreateTempSubdirectory("parrotcode-session-").FullName;
        public void Dispose() { try { Directory.Delete(Dir, recursive: true); } catch { } }
    }

    private static List<Message> SimpleMessages() => new()
    {
        new(MessageRole.User, "你好"),
        new(MessageRole.Assistant, "你好！有什么可以帮你的？"),
    };

    private static Message MakeToolCallMessage()
    {
        using var doc = JsonDocument.Parse("{\"path\":\"README.md\"}");
        return new Message(MessageRole.Assistant, "")
        {
            ToolCalls = new List<ToolCall>
            {
                new("call_1", "read_file", doc.RootElement.Clone())
            }
        };
    }

    private static Message MakeToolResultMessage() =>
        new(MessageRole.Tool, "# README\n内容")
        {
            ToolCallId = "call_1"
        };

    // ===== SaveAsync =====

    [Fact]
    public async Task SaveAsync_GeneratesJsonlFile()
    {
        using var dir = new TempDir();
        var store = new SessionStore(dir.Dir);

        var meta = await store.SaveAsync(SimpleMessages(), TestProvider, null, CancellationToken.None);

        var jsonlPath = Path.Combine(dir.Dir, $"{meta.Id}.jsonl");
        File.Exists(jsonlPath).Should().BeTrue();
    }

    [Fact]
    public async Task SaveAsync_GeneratesMetaFile()
    {
        using var dir = new TempDir();
        var store = new SessionStore(dir.Dir);

        var meta = await store.SaveAsync(SimpleMessages(), TestProvider, null, CancellationToken.None);

        var metaPath = Path.Combine(dir.Dir, $"{meta.Id}.meta.json");
        File.Exists(metaPath).Should().BeTrue();
    }

    [Fact]
    public async Task SaveAsync_JsonlEachLineIsIndependentMessageJson()
    {
        using var dir = new TempDir();
        var store = new SessionStore(dir.Dir);
        var messages = SimpleMessages();

        var meta = await store.SaveAsync(messages, TestProvider, null, CancellationToken.None);

        var jsonlPath = Path.Combine(dir.Dir, $"{meta.Id}.jsonl");
        var lines = await File.ReadAllLinesAsync(jsonlPath);
        lines.Length.Should().Be(messages.Count);
        // 每行都能独立解析为 JSON 对象
        foreach (var line in lines)
        {
            using var doc = JsonDocument.Parse(line);
            doc.RootElement.ValueKind.Should().Be(JsonValueKind.Object);
            doc.RootElement.TryGetProperty("role", out _).Should().BeTrue();
            doc.RootElement.TryGetProperty("content", out _).Should().BeTrue();
        }
    }

    [Fact]
    public async Task SaveAsync_CreatesStorageDirIfNotExist()
    {
        var tempBase = Directory.CreateTempSubdirectory("parrotcode-nested-").FullName;
        var nestedDir = Path.Combine(tempBase, "sub", "sessions");
        try
        {
            var store = new SessionStore(nestedDir);

            await store.SaveAsync(SimpleMessages(), TestProvider, null, CancellationToken.None);

            Directory.Exists(nestedDir).Should().BeTrue();
        }
        finally
        {
            try { Directory.Delete(tempBase, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task SaveAsync_MetaContainsAllFields()
    {
        using var dir = new TempDir();
        var store = new SessionStore(dir.Dir);
        var messages = SimpleMessages();

        var meta = await store.SaveAsync(messages, TestProvider, "自定义标题", CancellationToken.None);

        meta.Id.Should().NotBeNullOrEmpty();
        meta.CreatedAt.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromSeconds(5));
        meta.UpdatedAt.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromSeconds(5));
        meta.MessageCount.Should().Be(messages.Count);
        meta.ProviderName.Should().Be("test");
        meta.ModelName.Should().Be("test-model");
        meta.Title.Should().Be("自定义标题");

        // Meta 文件内容也包含所有字段
        var metaPath = Path.Combine(dir.Dir, $"{meta.Id}.meta.json");
        var metaJson = await File.ReadAllTextAsync(metaPath);
        using var doc = JsonDocument.Parse(metaJson);
        doc.RootElement.GetProperty("id").GetString().Should().Be(meta.Id);
        doc.RootElement.GetProperty("messageCount").GetInt32().Should().Be(messages.Count);
        doc.RootElement.GetProperty("providerName").GetString().Should().Be("test");
        doc.RootElement.GetProperty("modelName").GetString().Should().Be("test-model");
        doc.RootElement.GetProperty("title").GetString().Should().Be("自定义标题");
    }

    [Fact]
    public async Task SaveAsync_DerivesTitleFromFirstUserMessage_WhenTitleNull()
    {
        using var dir = new TempDir();
        var store = new SessionStore(dir.Dir);
        var messages = new List<Message>
        {
            new(MessageRole.System, "system prompt"),
            new(MessageRole.User, "这是第一条用户消息，用于生成标题"),
            new(MessageRole.Assistant, "回复"),
        };

        var meta = await store.SaveAsync(messages, TestProvider, null, CancellationToken.None);

        meta.Title.Should().Be("这是第一条用户消息，用于生成标题");
    }

    [Fact]
    public async Task SaveAsync_DerivesTitleTruncatedTo50Chars()
    {
        using var dir = new TempDir();
        var store = new SessionStore(dir.Dir);
        var longContent = new string('A', 100);
        var messages = new List<Message> { new(MessageRole.User, longContent) };

        var meta = await store.SaveAsync(messages, TestProvider, null, CancellationToken.None);

        meta.Title.Should().Be(new string('A', 50) + "...");
        meta.Title.Length.Should().Be(53);  // 50 chars + "..."
    }

    [Fact]
    public async Task SaveAsync_DerivesTitleNoUserMessage_ReturnsDefault()
    {
        using var dir = new TempDir();
        var store = new SessionStore(dir.Dir);
        var messages = new List<Message> { new(MessageRole.System, "system only") };

        var meta = await store.SaveAsync(messages, TestProvider, null, CancellationToken.None);

        meta.Title.Should().Be("（无标题）");
    }

    [Fact]
    public async Task SaveAsync_SessionIdFormat_IsTimestampPlusSuffix()
    {
        using var dir = new TempDir();
        var store = new SessionStore(dir.Dir);

        var meta = await store.SaveAsync(SimpleMessages(), TestProvider, null, CancellationToken.None);

        // 格式：yyyyMMdd_HHmmss_xxxxxx（6 位随机后缀）
        meta.Id.Should().MatchRegex(@"^\d{8}_\d{6}_[a-f0-9]{6}$");
    }

    // ===== LoadAsync =====

    [Fact]
    public async Task LoadAsync_RestoresAllMessages()
    {
        using var dir = new TempDir();
        var store = new SessionStore(dir.Dir);
        var messages = SimpleMessages();
        var savedMeta = await store.SaveAsync(messages, TestProvider, null, CancellationToken.None);

        var (meta, loaded) = await store.LoadAsync(savedMeta.Id, CancellationToken.None);

        loaded.Count.Should().Be(messages.Count);
        loaded[0].Role.Should().Be(MessageRole.User);
        loaded[0].Content.Should().Be("你好");
        loaded[1].Role.Should().Be(MessageRole.Assistant);
        loaded[1].Content.Should().Be("你好！有什么可以帮你的？");
    }

    [Fact]
    public async Task LoadAsync_RestoresToolCallsAndToolCallId()
    {
        using var dir = new TempDir();
        var store = new SessionStore(dir.Dir);
        var messages = new List<Message>
        {
            new(MessageRole.User, "读 README"),
            MakeToolCallMessage(),
            MakeToolResultMessage(),
            new(MessageRole.Assistant, "这是 README 的内容"),
        };
        var savedMeta = await store.SaveAsync(messages, TestProvider, null, CancellationToken.None);

        var (_, loaded) = await store.LoadAsync(savedMeta.Id, CancellationToken.None);

        loaded.Count.Should().Be(4);
        // assistant 消息含 ToolCalls
        var assistantMsg = loaded[1];
        assistantMsg.Role.Should().Be(MessageRole.Assistant);
        assistantMsg.ToolCalls.Should().NotBeNull();
        assistantMsg.ToolCalls!.Count.Should().Be(1);
        assistantMsg.ToolCalls[0].Id.Should().Be("call_1");
        assistantMsg.ToolCalls[0].Name.Should().Be("read_file");
        // tool 消息含 ToolCallId
        var toolMsg = loaded[2];
        toolMsg.Role.Should().Be(MessageRole.Tool);
        toolMsg.ToolCallId.Should().Be("call_1");
        // ToolCall.Input 往返无损
        var inputJson = assistantMsg.ToolCalls[0].Input.GetRawText();
        inputJson.Should().Contain("path");
        inputJson.Should().Contain("README.md");
    }

    [Fact]
    public async Task LoadAsync_SkipsCorruptedLinesWithoutThrowing()
    {
        using var dir = new TempDir();
        var store = new SessionStore(dir.Dir);
        var savedMeta = await store.SaveAsync(SimpleMessages(), TestProvider, null, CancellationToken.None);

        // 在 JSONL 末尾追加损坏行
        var jsonlPath = Path.Combine(dir.Dir, $"{savedMeta.Id}.jsonl");
        await File.AppendAllTextAsync(jsonlPath, "{这不是合法JSON}\n", Encoding.UTF8);

        var (_, loaded) = await store.LoadAsync(savedMeta.Id, CancellationToken.None);

        // 损坏行跳过，2 条正常消息保留
        loaded.Count.Should().Be(2);
    }

    [Fact]
    public async Task LoadAsync_TruncatesUnpairedToolUse()
    {
        using var dir = new TempDir();
        var store = new SessionStore(dir.Dir);
        // assistant 带 tool_calls 但无对应 tool_result
        var messages = new List<Message>
        {
            new(MessageRole.User, "你好"),
            MakeToolCallMessage(),  // call_1 未配对
        };
        var savedMeta = await store.SaveAsync(messages, TestProvider, null, CancellationToken.None);

        var (_, loaded) = await store.LoadAsync(savedMeta.Id, CancellationToken.None);

        // 未配对的 assistant(tool_calls) 被截断，只保留 user 消息
        loaded.Count.Should().Be(1);
        loaded[0].Role.Should().Be(MessageRole.User);
    }

    [Fact]
    public async Task LoadAsync_DoesNotTruncatePairedToolUse()
    {
        using var dir = new TempDir();
        var store = new SessionStore(dir.Dir);
        var messages = new List<Message>
        {
            new(MessageRole.User, "读 README"),
            MakeToolCallMessage(),
            MakeToolResultMessage(),  // call_1 已配对
            new(MessageRole.Assistant, "README 内容如下"),
        };
        var savedMeta = await store.SaveAsync(messages, TestProvider, null, CancellationToken.None);

        var (_, loaded) = await store.LoadAsync(savedMeta.Id, CancellationToken.None);

        loaded.Count.Should().Be(4);
    }

    [Fact]
    public async Task LoadAsync_FileNotExist_ThrowsFileNotFoundException()
    {
        using var dir = new TempDir();
        var store = new SessionStore(dir.Dir);

        var act = async () => await store.LoadAsync("nonexistent_id", CancellationToken.None);

        await act.Should().ThrowAsync<FileNotFoundException>();
    }

    [Fact]
    public async Task LoadAsync_MetaNotExist_UsesDefaultMeta()
    {
        using var dir = new TempDir();
        var store = new SessionStore(dir.Dir);
        var savedMeta = await store.SaveAsync(SimpleMessages(), TestProvider, null, CancellationToken.None);

        // 删除 Meta 文件，只保留 JSONL
        var metaPath = Path.Combine(dir.Dir, $"{savedMeta.Id}.meta.json");
        File.Delete(metaPath);

        var (meta, loaded) = await store.LoadAsync(savedMeta.Id, CancellationToken.None);

        meta.Id.Should().Be(savedMeta.Id);
        meta.MessageCount.Should().Be(loaded.Count);
        loaded.Count.Should().Be(2);
    }

    // ===== ListAsync =====

    [Fact]
    public async Task ListAsync_EmptyDir_ReturnsEmptyList()
    {
        using var dir = new TempDir();
        var store = new SessionStore(dir.Dir);

        var sessions = await store.ListAsync(CancellationToken.None);

        sessions.Should().BeEmpty();
    }

    [Fact]
    public async Task ListAsync_NonexistentDir_ReturnsEmptyList()
    {
        var nonexistent = Path.Combine(Path.GetTempPath(), $"parrotcode-nonexist-{Guid.NewGuid():N}");
        var store = new SessionStore(nonexistent);

        var sessions = await store.ListAsync(CancellationToken.None);

        sessions.Should().BeEmpty();
    }

    [Fact]
    public async Task ListAsync_SortsByUpdatedAtDescending()
    {
        using var dir = new TempDir();
        var store = new SessionStore(dir.Dir);

        // 保存 3 个会话，手动修改 Meta 的 UpdatedAt 确保顺序
        var meta1 = await store.SaveAsync(SimpleMessages(), TestProvider, "第一", CancellationToken.None);
        var meta2 = await store.SaveAsync(SimpleMessages(), TestProvider, "第二", CancellationToken.None);
        var meta3 = await store.SaveAsync(SimpleMessages(), TestProvider, "第三", CancellationToken.None);

        // 手动覆盖 meta1 的 UpdatedAt 为更早时间
        var meta1Path = Path.Combine(dir.Dir, $"{meta1.Id}.meta.json");
        var earlierMeta = meta1 with { UpdatedAt = DateTime.UtcNow.AddDays(-1) };
        var metaJson = JsonSerializer.Serialize(earlierMeta);
        await File.WriteAllTextAsync(meta1Path, metaJson, Encoding.UTF8);

        var sessions = await store.ListAsync(CancellationToken.None);

        sessions.Should().HaveCount(3);
        // 按 UpdatedAt 倒序：meta3/meta2（刚保存，接近同时）在前，meta1（一天前）在最后
        sessions[2].Id.Should().Be(meta1.Id);
    }

    [Fact]
    public async Task ListAsync_SkipsCorruptedMetaFile()
    {
        using var dir = new TempDir();
        var store = new SessionStore(dir.Dir);

        var meta1 = await store.SaveAsync(SimpleMessages(), TestProvider, "正常", CancellationToken.None);
        // 写一个损坏的 Meta 文件
        var corruptPath = Path.Combine(dir.Dir, "corrupt.meta.json");
        await File.WriteAllTextAsync(corruptPath, "{损坏的JSON}", Encoding.UTF8);

        var sessions = await store.ListAsync(CancellationToken.None);

        sessions.Should().HaveCount(1);
        sessions[0].Id.Should().Be(meta1.Id);
    }

    [Fact]
    public async Task ListAsync_ReturnsSessionSummaryFields()
    {
        using var dir = new TempDir();
        var store = new SessionStore(dir.Dir);
        var meta = await store.SaveAsync(SimpleMessages(), TestProvider, "标题测试", CancellationToken.None);

        var sessions = await store.ListAsync(CancellationToken.None);

        sessions.Should().HaveCount(1);
        sessions[0].Id.Should().Be(meta.Id);
        sessions[0].MessageCount.Should().Be(2);
        sessions[0].Title.Should().Be("标题测试");
    }

    // ===== MessageDto 往返（通过 save→load 验证） =====

    [Fact]
    public async Task RoundTrip_AllMessageRoles_Preserved()
    {
        using var dir = new TempDir();
        var store = new SessionStore(dir.Dir);
        var messages = new List<Message>
        {
            new(MessageRole.System, "system prompt"),
            new(MessageRole.User, "user message"),
            new(MessageRole.Assistant, "assistant reply"),
            new(MessageRole.Tool, "tool result"),
        };
        var savedMeta = await store.SaveAsync(messages, TestProvider, null, CancellationToken.None);

        var (_, loaded) = await store.LoadAsync(savedMeta.Id, CancellationToken.None);

        loaded.Count.Should().Be(4);
        loaded[0].Role.Should().Be(MessageRole.System);
        loaded[1].Role.Should().Be(MessageRole.User);
        loaded[2].Role.Should().Be(MessageRole.Assistant);
        loaded[3].Role.Should().Be(MessageRole.Tool);
        loaded[0].Content.Should().Be("system prompt");
        loaded[1].Content.Should().Be("user message");
        loaded[2].Content.Should().Be("assistant reply");
        loaded[3].Content.Should().Be("tool result");
    }

    [Fact]
    public async Task RoundTrip_ToolCallInputJson_Preserved()
    {
        using var dir = new TempDir();
        var store = new SessionStore(dir.Dir);
        var complexInput = "{\"path\":\"/a/b/c.txt\",\"offset\":10,\"limit\":50}";
        using var doc = JsonDocument.Parse(complexInput);
        var messages = new List<Message>
        {
            new(MessageRole.User, "读文件"),
            new Message(MessageRole.Assistant, "")
            {
                ToolCalls = new List<ToolCall>
                {
                    new("call_complex", "read_file", doc.RootElement.Clone())
                }
            },
            new Message(MessageRole.Tool, "文件内容") { ToolCallId = "call_complex" },
        };
        var savedMeta = await store.SaveAsync(messages, TestProvider, null, CancellationToken.None);

        var (_, loaded) = await store.LoadAsync(savedMeta.Id, CancellationToken.None);

        var assistantMsg = loaded[1];
        assistantMsg.ToolCalls![0].Input.GetRawText().Should().Be(complexInput);
    }

    [Fact]
    public async Task RoundTrip_MultipleToolCalls_Preserved()
    {
        using var dir = new TempDir();
        var store = new SessionStore(dir.Dir);
        using var doc1 = JsonDocument.Parse("{\"path\":\"a.txt\"}");
        using var doc2 = JsonDocument.Parse("{\"pattern\":\"foo\",\"glob\":\"*.cs\"}");
        var messages = new List<Message>
        {
            new(MessageRole.User, "并行读"),
            new Message(MessageRole.Assistant, "")
            {
                ToolCalls = new List<ToolCall>
                {
                    new("call_a", "read_file", doc1.RootElement.Clone()),
                    new("call_b", "grep", doc2.RootElement.Clone()),
                }
            },
            new Message(MessageRole.Tool, "result a") { ToolCallId = "call_a" },
            new Message(MessageRole.Tool, "result b") { ToolCallId = "call_b" },
        };
        var savedMeta = await store.SaveAsync(messages, TestProvider, null, CancellationToken.None);

        var (_, loaded) = await store.LoadAsync(savedMeta.Id, CancellationToken.None);

        var toolCalls = loaded[1].ToolCalls!;
        toolCalls.Count.Should().Be(2);
        toolCalls[0].Id.Should().Be("call_a");
        toolCalls[0].Name.Should().Be("read_file");
        toolCalls[1].Id.Should().Be("call_b");
        toolCalls[1].Name.Should().Be("grep");
    }

    [Fact]
    public async Task RoundTrip_ChineseContent_Preserved()
    {
        using var dir = new TempDir();
        var store = new SessionStore(dir.Dir);
        var messages = new List<Message>
        {
            new(MessageRole.User, "你好世界！包含中文和 emoji 🎉"),
            new(MessageRole.Assistant, "你好！我是 AI 助手 🤖"),
        };
        var savedMeta = await store.SaveAsync(messages, TestProvider, null, CancellationToken.None);

        var (_, loaded) = await store.LoadAsync(savedMeta.Id, CancellationToken.None);

        loaded[0].Content.Should().Be("你好世界！包含中文和 emoji 🎉");
        loaded[1].Content.Should().Be("你好！我是 AI 助手 🤖");
    }

    // ===== RepairToolCallPairing（直接测试内部方法） =====

    [Fact]
    public void RepairToolCallPairing_AllPaired_ReturnsAll()
    {
        var messages = new List<Message>
        {
            new(MessageRole.User, "q"),
            MakeToolCallMessage(),
            MakeToolResultMessage(),
        };

        var result = SessionStore.RepairToolCallPairing(messages);

        result.Count.Should().Be(3);
    }

    [Fact]
    public void RepairToolCallPairing_UnpairedTruncates()
    {
        var messages = new List<Message>
        {
            new(MessageRole.User, "q"),
            MakeToolCallMessage(),  // 无配对 tool_result
            new(MessageRole.Assistant, "回复"),
        };

        var result = SessionStore.RepairToolCallPairing(messages);

        result.Count.Should().Be(1);
        result[0].Role.Should().Be(MessageRole.User);
    }

    [Fact]
    public void RepairToolCallPairing_PartiallyPairedTruncatesAtFirstUnpaired()
    {
        using var doc = JsonDocument.Parse("{\"path\":\"a\"}");
        var messages = new List<Message>
        {
            new(MessageRole.User, "q"),
            new Message(MessageRole.Assistant, "")
            {
                ToolCalls = new List<ToolCall> { new("call_1", "read_file", doc.RootElement.Clone()) }
            },
            new Message(MessageRole.Tool, "result 1") { ToolCallId = "call_1" },
            // 第二个 assistant 带 tool_calls 但未配对
            new Message(MessageRole.Assistant, "")
            {
                ToolCalls = new List<ToolCall> { new("call_2", "grep", doc.RootElement.Clone()) }
            },
            // 这条不应该出现——前面的 call_2 未配对，应截断
            new(MessageRole.Assistant, "不该出现"),
        };

        var result = SessionStore.RepairToolCallPairing(messages);

        result.Count.Should().Be(3);
        result.Last().Role.Should().Be(MessageRole.Tool);
    }

    [Fact]
    public void RepairToolCallPairing_NoToolCalls_ReturnsAll()
    {
        var messages = new List<Message>
        {
            new(MessageRole.User, "q"),
            new(MessageRole.Assistant, "a"),
            new(MessageRole.User, "q2"),
        };

        var result = SessionStore.RepairToolCallPairing(messages);

        result.Count.Should().Be(3);
    }
}
