using System.IO;
using ParrotCode;

namespace ParrotCode.xUnit;

/// <summary>
/// SessionCommand 单元测试（迭代 10b）。
/// 覆盖 save/load/list/current/子命令错误/未启用。
/// 隔离策略：每个用例用独立临时目录 + MockUiControl。
/// </summary>
public class SessionCommandTests
{
    private static readonly ProviderConfig TestProvider = new()
    {
        Name = "test",
        Protocol = "mock",
        Model = "test-model"
    };

    private sealed class TempDir : IDisposable
    {
        public string Dir { get; } = Directory.CreateTempSubdirectory("parrotcode-cmd-").FullName;
        public void Dispose() { try { Directory.Delete(Dir, recursive: true); } catch { } }
    }

    private static CommandContext CreateContext(
        SessionStore store,
        MockUiControl? ui = null,
        ConversationHistory? history = null,
        ContextCompressor? compressor = null,
        string rawInput = "/session")
    {
        ui ??= new MockUiControl();
        history ??= new ConversationHistory();
        var guard = new SecurityGuard(
            new SecurityContext { ProjectRoot = Path.GetTempPath() },
            SecurityLevel.Normal);

        return new CommandContext(history, compressor, guard, ui, store, CancellationToken.None)
        {
            ProviderConfig = TestProvider,
            TuiConfig = new TuiConfig(),
            AgentConfig = new AgentConfig(),
            RawInput = rawInput,
        };
    }

    // ===== /session (null store) =====

    [Fact]
    public async Task SessionCommand_NullStore_ReturnsNotEnabled()
    {
        var cmd = new SessionCommand();
        var ctx = CreateContext(null!);

        var result = await cmd.ExecuteAsync(ctx);

        result.Handled.Should().BeTrue();
        result.Output.Should().Contain("未启用");
    }

    // ===== /session (no subcommand) =====

    [Fact]
    public async Task SessionCommand_NoSubcommand_ReturnsUsage()
    {
        using var dir = new TempDir();
        var store = new SessionStore(dir.Dir);
        var cmd = new SessionCommand();
        var ctx = CreateContext(store, rawInput: "/session");

        var result = await cmd.ExecuteAsync(ctx);

        result.Handled.Should().BeTrue();
        result.Output.Should().Contain("用法");
        result.Output.Should().Contain("save");
        result.Output.Should().Contain("load");
        result.Output.Should().Contain("list");
        result.Output.Should().Contain("current");
    }

    // ===== /session foo (unknown subcommand) =====

    [Fact]
    public async Task SessionCommand_UnknownSubcommand_ReturnsError()
    {
        using var dir = new TempDir();
        var store = new SessionStore(dir.Dir);
        var cmd = new SessionCommand();
        var ctx = CreateContext(store, rawInput: "/session foo");

        var result = await cmd.ExecuteAsync(ctx);

        result.Handled.Should().BeTrue();
        result.Output.Should().Contain("未知子命令");
        result.Output.Should().Contain("foo");
    }

    // ===== /session save =====

    [Fact]
    public async Task Save_GeneratesJsonlAndMetaFiles()
    {
        using var dir = new TempDir();
        var store = new SessionStore(dir.Dir);
        var history = new ConversationHistory();
        history.AddUser("你好");
        history.AddAssistant("你好！有什么可以帮你的？");
        var cmd = new SessionCommand();
        var ctx = CreateContext(store, history: history, rawInput: "/session save");

        var result = await cmd.ExecuteAsync(ctx);

        result.Handled.Should().BeTrue();
        result.Output.Should().Contain("会话已保存");
        result.Output.Should().Contain("ID:");
        result.Output.Should().Contain("消息数: 2");
        // 验证文件生成
        var files = Directory.GetFiles(dir.Dir);
        files.Should().Contain(f => f.EndsWith(".jsonl"));
        files.Should().Contain(f => f.EndsWith(".meta.json"));
    }

    [Fact]
    public async Task Save_DerivesTitleFromFirstUserMessage()
    {
        using var dir = new TempDir();
        var store = new SessionStore(dir.Dir);
        var history = new ConversationHistory();
        history.AddUser("帮我读 README");
        history.AddAssistant("好的");
        var cmd = new SessionCommand();
        var ctx = CreateContext(store, history: history, rawInput: "/session save");

        var result = await cmd.ExecuteAsync(ctx);

        result.Output.Should().Contain("标题: 帮我读 README");
    }

    [Fact]
    public async Task Save_CustomTitle()
    {
        using var dir = new TempDir();
        var store = new SessionStore(dir.Dir);
        var history = new ConversationHistory();
        history.AddUser("你好");
        var cmd = new SessionCommand();
        var ctx = CreateContext(store, history: history, rawInput: "/session save 我的自定义标题");

        var result = await cmd.ExecuteAsync(ctx);

        result.Output.Should().Contain("标题: 我的自定义标题");
    }

    [Fact]
    public async Task Save_EmptyHistory_ReturnsNoNeedToSave()
    {
        using var dir = new TempDir();
        var store = new SessionStore(dir.Dir);
        var history = new ConversationHistory();
        var cmd = new SessionCommand();
        var ctx = CreateContext(store, history: history, rawInput: "/session save");

        var result = await cmd.ExecuteAsync(ctx);

        result.Handled.Should().BeTrue();
        result.Output.Should().Contain("历史为空");
        result.Output.Should().Contain("无需保存");
    }

    // ===== /session load =====

    [Fact]
    public async Task Load_LoadsMessagesIntoHistory()
    {
        using var dir = new TempDir();
        var store = new SessionStore(dir.Dir);
        // 先保存一个会话
        var savedMessages = new List<Message>
        {
            new(MessageRole.User, "你好"),
            new(MessageRole.Assistant, "你好！"),
        };
        var savedMeta = await store.SaveAsync(savedMessages, TestProvider, null, CancellationToken.None);

        // 当前历史有其他消息
        var history = new ConversationHistory();
        history.AddUser("临时消息");
        var ui = new MockUiControl();
        var cmd = new SessionCommand();
        var ctx = CreateContext(store, ui: ui, history: history, rawInput: $"/session load {savedMeta.Id}");

        var result = await cmd.ExecuteAsync(ctx);

        result.Handled.Should().BeTrue();
        result.Output.Should().Contain("已加载会话");
        result.Output.Should().Contain(savedMeta.Id);
        result.Output.Should().Contain("2 条消息");
        // 历史被替换为加载的消息
        var historyMessages = history.ToProviderMessages();
        historyMessages.Count.Should().Be(2);
        historyMessages[0].Content.Should().Be("你好");
        historyMessages[1].Content.Should().Be("你好！");
    }

    [Fact]
    public async Task Load_ClearsCurrentHistoryBeforeLoading()
    {
        using var dir = new TempDir();
        var store = new SessionStore(dir.Dir);
        var savedMessages = new List<Message>
        {
            new(MessageRole.User, "加载的消息"),
        };
        var savedMeta = await store.SaveAsync(savedMessages, TestProvider, null, CancellationToken.None);

        var history = new ConversationHistory();
        history.AddUser("旧消息1");
        history.AddUser("旧消息2");
        history.AddUser("旧消息3");
        var ui = new MockUiControl();
        var cmd = new SessionCommand();
        var ctx = CreateContext(store, ui: ui, history: history, rawInput: $"/session load {savedMeta.Id}");

        await cmd.ExecuteAsync(ctx);

        // 旧消息被清除
        var historyMessages = history.ToProviderMessages();
        historyMessages.Count.Should().Be(1);
        historyMessages[0].Content.Should().Be("加载的消息");
        // UI 被清空
        ui.MessagesCleared.Should().BeTrue();
    }

    [Fact]
    public async Task Load_RendersHistoryMessagesToUi()
    {
        using var dir = new TempDir();
        var store = new SessionStore(dir.Dir);
        var savedMessages = new List<Message>
        {
            new(MessageRole.User, "用户消息"),
            new(MessageRole.Assistant, "AI回复"),
        };
        var savedMeta = await store.SaveAsync(savedMessages, TestProvider, null, CancellationToken.None);

        var ui = new MockUiControl();
        var history = new ConversationHistory();
        var cmd = new SessionCommand();
        var ctx = CreateContext(store, ui: ui, history: history, rawInput: $"/session load {savedMeta.Id}");

        await cmd.ExecuteAsync(ctx);

        // UI 被清空后渲染历史消息
        ui.MessagesCleared.Should().BeTrue();
        ui.UserMessages.Should().Contain("用户消息");
        ui.StaticMessages.Should().ContainMatch("*AI回复*");
    }

    [Fact]
    public async Task Load_UpdatesTokenEstimate()
    {
        using var dir = new TempDir();
        var store = new SessionStore(dir.Dir);
        var savedMessages = new List<Message>
        {
            new(MessageRole.User, "你好"),
            new(MessageRole.Assistant, "你好！"),
        };
        var savedMeta = await store.SaveAsync(savedMessages, TestProvider, null, CancellationToken.None);

        var ui = new MockUiControl();
        var history = new ConversationHistory();
        var cmd = new SessionCommand();
        var ctx = CreateContext(store, ui: ui, history: history, rawInput: $"/session load {savedMeta.Id}");

        await cmd.ExecuteAsync(ctx);

        ui.LastTokenEstimate.Should().NotBeNull();
        ui.LastTokenEstimate.Should().Be(history.EstimatedTokens);
    }

    [Fact]
    public async Task Load_NonexistentId_ThrowsFileNotFoundException()
    {
        using var dir = new TempDir();
        var store = new SessionStore(dir.Dir);
        var cmd = new SessionCommand();
        var ctx = CreateContext(store, rawInput: "/session load nonexistent_id");

        var act = async () => await cmd.ExecuteAsync(ctx);

        await act.Should().ThrowAsync<FileNotFoundException>();
    }

    [Fact]
    public async Task Load_NoId_ReturnsUsage()
    {
        using var dir = new TempDir();
        var store = new SessionStore(dir.Dir);
        var cmd = new SessionCommand();
        var ctx = CreateContext(store, rawInput: "/session load");

        var result = await cmd.ExecuteAsync(ctx);

        result.Handled.Should().BeTrue();
        result.Output.Should().Contain("用法");
        result.Output.Should().Contain("/session load <id>");
    }

    [Fact]
    public async Task Load_CallsCompressorResetWarning()
    {
        using var dir = new TempDir();
        var store = new SessionStore(dir.Dir);
        // 保存一个会话（消息长度足够触发警告）
        var longContent = new string('x', 250);  // ~84 tokens
        var savedMessages = new List<Message>
        {
            new(MessageRole.User, longContent),
            new(MessageRole.Assistant, "回复"),
        };
        var savedMeta = await store.SaveAsync(savedMessages, TestProvider, null, CancellationToken.None);

        // 创建低 contextWindow 的 compressor（warning 阈值 = 70 tokens）
        var compressor = new ContextCompressor(
            new MockProvider(), contextWindowTokens: 100,
            truncateConfig: new TruncateConfig { PerResultThreshold = 50_000, RoundTotalThreshold = 200_000, PreviewLength = 2_000 },
            warningFraction: 0.7, triggerFraction: 0.9,
            keepRecent: 4, maxCircuitFailures: 2,
            enableAutoCompress: true,
            projectRoot: Path.GetTempPath());

        var history = new ConversationHistory();
        history.AddUser(longContent);
        // 第一次检查 → 发警告（_warningEmitted = true）
        var result1 = await compressor.CheckAndCompressAsync(history, CancellationToken.None);
        result1.WarningIssued.Should().BeTrue();

        // /session load 会调用 ResetWarning
        var ui = new MockUiControl();
        var cmd = new SessionCommand();
        var ctx = CreateContext(store, ui: ui, history: history, compressor: compressor,
            rawInput: $"/session load {savedMeta.Id}");

        await cmd.ExecuteAsync(ctx);

        // 再次检查 → 应该再次发警告（因为 ResetWarning 被调用了）
        var result2 = await compressor.CheckAndCompressAsync(history, CancellationToken.None);
        result2.WarningIssued.Should().BeTrue("ResetWarning 应在 load 时被调用，使警告可再次触发");
    }

    [Fact]
    public async Task Load_ToolCallHistory_PreservedInHistory()
    {
        using var dir = new TempDir();
        var store = new SessionStore(dir.Dir);
        using var doc = System.Text.Json.JsonDocument.Parse("{\"path\":\"README.md\"}");
        var savedMessages = new List<Message>
        {
            new(MessageRole.User, "读 README"),
            new Message(MessageRole.Assistant, "")
            {
                ToolCalls = new List<ToolCall>
                {
                    new("call_1", "read_file", doc.RootElement.Clone())
                }
            },
            new Message(MessageRole.Tool, "内容") { ToolCallId = "call_1" },
        };
        var savedMeta = await store.SaveAsync(savedMessages, TestProvider, null, CancellationToken.None);

        var history = new ConversationHistory();
        var cmd = new SessionCommand();
        var ctx = CreateContext(store, history: history, rawInput: $"/session load {savedMeta.Id}");

        await cmd.ExecuteAsync(ctx);

        var msgs = history.ToProviderMessages();
        msgs.Count.Should().Be(3);
        msgs[1].ToolCalls.Should().NotBeNull();
        msgs[1].ToolCalls!.Count.Should().Be(1);
        msgs[2].ToolCallId.Should().Be("call_1");
    }

    // ===== /session list =====

    [Fact]
    public async Task List_Empty_ReturnsNoSessions()
    {
        using var dir = new TempDir();
        var store = new SessionStore(dir.Dir);
        var cmd = new SessionCommand();
        var ctx = CreateContext(store, rawInput: "/session list");

        var result = await cmd.ExecuteAsync(ctx);

        result.Handled.Should().BeTrue();
        result.Output.Should().Contain("无已保存会话");
    }

    [Fact]
    public async Task List_ShowsSavedSessions()
    {
        using var dir = new TempDir();
        var store = new SessionStore(dir.Dir);
        var meta1 = await store.SaveAsync(
            new List<Message> { new(MessageRole.User, "第一会话") },
            TestProvider, null, CancellationToken.None);
        var meta2 = await store.SaveAsync(
            new List<Message> { new(MessageRole.User, "第二会话") },
            TestProvider, null, CancellationToken.None);

        var cmd = new SessionCommand();
        var ctx = CreateContext(store, rawInput: "/session list");

        var result = await cmd.ExecuteAsync(ctx);

        result.Handled.Should().BeTrue();
        result.Output.Should().Contain(meta1.Id);
        result.Output.Should().Contain(meta2.Id);
        result.Output.Should().Contain("第一会话");
        result.Output.Should().Contain("第二会话");
    }

    [Fact]
    public async Task List_LimitedToTenSessions()
    {
        using var dir = new TempDir();
        var store = new SessionStore(dir.Dir);
        // 保存 12 个会话
        for (int i = 0; i < 12; i++)
        {
            await store.SaveAsync(
                new List<Message> { new(MessageRole.User, $"会话{i}") },
                TestProvider, null, CancellationToken.None);
        }

        var cmd = new SessionCommand();
        var ctx = CreateContext(store, rawInput: "/session list");

        var result = await cmd.ExecuteAsync(ctx);

        result.Handled.Should().BeTrue();
        // 输出行数 = 标题行 + 最多 10 条会话
        var lines = result.Output!.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        lines.Length.Should().BeLessThanOrEqualTo(11);  // 1 标题 + 最多 10 条
    }

    // ===== /session current =====

    [Fact]
    public async Task Current_ReturnsNotPersistedPrompt()
    {
        using var dir = new TempDir();
        var store = new SessionStore(dir.Dir);
        var cmd = new SessionCommand();
        var ctx = CreateContext(store, rawInput: "/session current");

        var result = await cmd.ExecuteAsync(ctx);

        result.Handled.Should().BeTrue();
        result.Output.Should().Contain("未持久化");
        result.Output.Should().Contain("/session save");
    }
}
