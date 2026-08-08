using ParrotCode;

namespace ParrotCode.xUnit;

/// <summary>
/// StructuredSummarizer 单元测试：阈值判断、摘要成功/失败/空内容、draft 提取、熔断器、取消。
/// 用 MockProvider 的 ChatStreamAsync(string) 重载注入摘要响应。
/// </summary>
public class SummarizerTests
{
    private static string MakeString(int len) => new('x', len);

    /// <summary>
    /// 辅助：构造一个有足够消息 + token 的历史。
    /// contextWindow=100, warning=0.7→70, trigger=0.9→90。
    /// 每条消息 300 字符 → 100 tokens。
    /// </summary>
    private static (ConversationHistory, StructuredSummarizer, MockProvider) CreateSetup(
        int contextWindow = 100,
        double warning = 0.7,
        double trigger = 0.9,
        int keepRecent = 4,
        int maxCircuitFailures = 2)
    {
        var provider = new MockProvider();
        // internal 类需要通过 Compressor 间接测试，但 ExtractFormalSummary 是 internal static
        // 这里直接用反射或通过 Compressor 测试。
        // StructuredSummarizer 是 internal，但在同一 namespace 下测试项目可访问（InternalsVisibleTo）
        var summarizer = new StructuredSummarizer(
            provider, contextWindow, warning, trigger, keepRecent, maxCircuitFailures);
        var history = new ConversationHistory();
        return (history, summarizer, provider);
    }

    // ---- ExtractFormalSummary（internal static 方法）----

    [Fact]
    public void ExtractFormalSummary_NoDraft_ReturnsWholeText()
    {
        var raw = "## 主要请求\n用户想读文件";

        var result = StructuredSummarizer.ExtractFormalSummary(raw);

        result.Should().Be("## 主要请求\n用户想读文件");
    }

    [Fact]
    public void ExtractFormalSummary_WithDraft_ReturnsAfterDraft()
    {
        var raw = "```draft\n这是草稿\n```\n## 主要请求\n用户想读文件";

        var result = StructuredSummarizer.ExtractFormalSummary(raw);

        result.Should().Be("## 主要请求\n用户想读文件");
    }

    [Fact]
    public void ExtractFormalSummary_DraftNotClosed_ReturnsBeforeDraft()
    {
        var raw = "正式内容\n```draft\n草稿未闭合";

        var result = StructuredSummarizer.ExtractFormalSummary(raw);

        result.Should().Be("正式内容");
    }

    [Fact]
    public void ExtractFormalSummary_EmptyInput_ReturnsEmpty()
    {
        var result = StructuredSummarizer.ExtractFormalSummary("");

        result.Should().Be("");
    }

    [Fact]
    public void ExtractFormalSummary_CaseInsensitiveDraft()
    {
        var raw = "```DRAFT\n草稿\n```\n正式摘要";

        var result = StructuredSummarizer.ExtractFormalSummary(raw);

        result.Should().Be("正式摘要");
    }

    // ---- NeedsWarning / NeedsCompression ----

    [Fact]
    public void NeedsWarning_TokenOverWarningThreshold_ReturnsTrue()
    {
        var (_, summarizer, _) = CreateSetup(contextWindow: 100, warning: 0.7, trigger: 0.9);
        // warningThreshold = (int)(100 * 0.7) = 70
        // 200 chars → ceil(200/3) = 67 tokens < 70
        var messages = new[] { new Message(MessageRole.User, MakeString(200)) };

        summarizer.NeedsWarning(messages).Should().BeFalse();

        // 300 chars → ceil(300/3) = 100 tokens > 70
        var bigMessages = new[] { new Message(MessageRole.User, MakeString(300)) };
        summarizer.NeedsWarning(bigMessages).Should().BeTrue();
    }

    [Fact]
    public void NeedsCompression_TokenOverTriggerThreshold_ReturnsTrue()
    {
        var (_, summarizer, _) = CreateSetup(contextWindow: 100, warning: 0.7, trigger: 0.9);
        // triggerThreshold = (int)(100 * 0.9) = 90
        // 250 chars → ceil(250/3) = 84 tokens < 90
        var smallMessages = new[] { new Message(MessageRole.User, MakeString(250)) };

        summarizer.NeedsCompression(smallMessages).Should().BeFalse();

        // 300 chars → ceil(300/3) = 100 tokens > 90
        var bigMessages = new[] { new Message(MessageRole.User, MakeString(300)) };
        summarizer.NeedsCompression(bigMessages).Should().BeTrue();
    }

    // ---- SummarizeAsync ----

    [Fact]
    public async Task SummarizeAsync_TooFewMessages_ReturnsFailure()
    {
        var (history, summarizer, _) = CreateSetup(keepRecent: 4);
        // 只放 3 条消息，不够 keepRecent+4=8
        history.AddUser("a");
        history.AddAssistant("b");
        history.AddUser("c");

        var result = await summarizer.SummarizeAsync(history, CancellationToken.None);

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("消息太少");
    }

    [Fact]
    public async Task SummarizeAsync_Success_ReplacesHistory()
    {
        var (history, summarizer, provider) = CreateSetup(contextWindow: 100, keepRecent: 2);
        // 放 8 条消息（keepRecent+4=6，够压缩）
        // 每条 50 字符 → 17 tokens × 8 = 136 tokens > 90 trigger
        for (int i = 0; i < 6; i++)
        {
            history.AddUser(MakeString(50));
            history.AddAssistant(MakeString(50));
        }
        // 共 12 条 > 6

        // MockProvider 的 ChatStreamAsync(string) 会回显最后一条 user 消息 + "（mock）"
        // 我们需要让它返回摘要内容
        // MockProvider 默认回显，这里用默认行为即可

        var result = await summarizer.SummarizeAsync(history, CancellationToken.None);

        // MockProvider 默认回显 "{content}（mock）"，非空，应该成功
        result.Success.Should().BeTrue();
        result.MessagesCompressed.Should().Be(10);  // 12 - 2 keepRecent = 10
        history.Count.Should().Be(4);  // 2 system + 2 recent
    }

    [Fact]
    public async Task SummarizeAsync_Success_HistoryContainsSummarySystemMessage()
    {
        var (history, summarizer, provider) = CreateSetup(contextWindow: 100, keepRecent: 2);
        for (int i = 0; i < 6; i++)
        {
            history.AddUser(MakeString(50));
            history.AddAssistant(MakeString(50));
        }

        await summarizer.SummarizeAsync(history, CancellationToken.None);

        var msgs = history.ToProviderMessages();
        msgs[0].Role.Should().Be(MessageRole.System);
        msgs[0].Content.Should().Contain("[结构化摘要]");
        msgs[1].Role.Should().Be(MessageRole.System);
        msgs[1].Content.Should().Contain("[对话上下文已压缩]");
    }

    [Fact]
    public async Task SummarizeAsync_Success_KeepsRecentMessages()
    {
        var (history, summarizer, provider) = CreateSetup(contextWindow: 100, keepRecent: 2);
        for (int i = 0; i < 6; i++)
        {
            history.AddUser($"user_msg_{i}_" + MakeString(40));
            history.AddAssistant($"assistant_msg_{i}_" + MakeString(40));
        }

        await summarizer.SummarizeAsync(history, CancellationToken.None);

        var msgs = history.ToProviderMessages();
        // 最后 2 条应该是 recent（跳过 2 条 system）
        msgs[^2].Content.Should().Contain("user_msg_5");
        msgs[^1].Content.Should().Contain("assistant_msg_5");
    }

    [Fact]
    public async Task SummarizeAsync_Success_ResetsCircuitBreaker()
    {
        var (history, summarizer, provider) = CreateSetup(contextWindow: 100, keepRecent: 2, maxCircuitFailures: 2);
        for (int i = 0; i < 6; i++)
        {
            history.AddUser(MakeString(50));
            history.AddAssistant(MakeString(50));
        }

        var result = await summarizer.SummarizeAsync(history, CancellationToken.None);

        result.Success.Should().BeTrue();
        summarizer.CircuitOpen.Should().BeFalse();
        summarizer.CircuitFailures.Should().Be(0);
    }

    [Fact]
    public async Task SummarizeAsync_EmptySummary_RecordsFailure()
    {
        // 用自定义 Provider 返回空字符串
        var provider = new EmptyResponseProvider();
        var summarizer = new StructuredSummarizer(provider, 100, 0.7, 0.9, 2, 2);
        var history = new ConversationHistory();
        for (int i = 0; i < 6; i++)
        {
            history.AddUser(MakeString(50));
            history.AddAssistant(MakeString(50));
        }

        var result = await summarizer.SummarizeAsync(history, CancellationToken.None);

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("空内容");
        summarizer.CircuitFailures.Should().Be(1);
    }

    [Fact]
    public async Task SummarizeAsync_ConsecutiveFailures_OpensCircuitBreaker()
    {
        var provider = new EmptyResponseProvider();
        var summarizer = new StructuredSummarizer(provider, 100, 0.7, 0.9, 2, 2);
        var history = new ConversationHistory();
        for (int i = 0; i < 6; i++)
        {
            history.AddUser(MakeString(50));
            history.AddAssistant(MakeString(50));
        }

        // 第一次失败
        await summarizer.SummarizeAsync(history, CancellationToken.None);
        summarizer.CircuitOpen.Should().BeFalse();

        // 第二次失败 → 熔断
        // 历史没被修改（失败不改历史），所以还是 12 条
        await summarizer.SummarizeAsync(history, CancellationToken.None);
        summarizer.CircuitOpen.Should().BeTrue();
    }

    [Fact]
    public async Task SummarizeAsync_CircuitOpen_DoesNotCallLLM()
    {
        var provider = new TrackingProvider();
        var summarizer = new StructuredSummarizer(provider, 100, 0.7, 0.9, 2, 1);  // maxFailures=1
        var history = new ConversationHistory();
        for (int i = 0; i < 6; i++)
        {
            history.AddUser(MakeString(50));
            history.AddAssistant(MakeString(50));
        }

        // 第一次调用 → 空 → 失败 → 熔断（maxFailures=1）
        await summarizer.SummarizeAsync(history, CancellationToken.None);
        provider.CallCount.Should().Be(1);

        // 第二次调用 → 熔断器 open，不调 LLM
        await summarizer.SummarizeAsync(history, CancellationToken.None);
        provider.CallCount.Should().Be(1);  // 没有增加
    }

    [Fact]
    public async Task SummarizeAsync_ResetCircuit_AllowsRetry()
    {
        var provider = new EmptyResponseProvider();
        var summarizer = new StructuredSummarizer(provider, 100, 0.7, 0.9, 2, 1);
        var history = new ConversationHistory();
        for (int i = 0; i < 6; i++)
        {
            history.AddUser(MakeString(50));
            history.AddAssistant(MakeString(50));
        }

        await summarizer.SummarizeAsync(history, CancellationToken.None);
        summarizer.CircuitOpen.Should().BeTrue();

        summarizer.ResetCircuit();
        summarizer.CircuitOpen.Should().BeFalse();

        // 可以再次调用（仍会失败，但至少不会被熔断器阻止）
        var result = await summarizer.SummarizeAsync(history, CancellationToken.None);
        result.Success.Should().BeFalse();  // 仍然返回空
    }

    [Fact]
    public async Task SummarizeAsync_Cancellation_DoesNotRecordFailure()
    {
        var provider = new CancelProvider();
        var summarizer = new StructuredSummarizer(provider, 100, 0.7, 0.9, 2, 1);
        var history = new ConversationHistory();
        for (int i = 0; i < 6; i++)
        {
            history.AddUser(MakeString(50));
            history.AddAssistant(MakeString(50));
        }

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var act = async () => await summarizer.SummarizeAsync(history, cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        summarizer.CircuitFailures.Should().Be(0);  // 取消不记失败
    }

    // ---- 辅助 Provider ----

    /// <summary>返回空字符串的 Provider。</summary>
    private sealed class EmptyResponseProvider : IBaseProvider
    {
        public Task<string> ChatAsync(IReadOnlyList<Message> messages, CancellationToken ct)
            => Task.FromResult("");

        public async IAsyncEnumerable<string> ChatStreamAsync(
            IReadOnlyList<Message> messages,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            await Task.CompletedTask;
            yield return "";  // 返回空
        }

        public async IAsyncEnumerable<ChatChunk> ChatStreamAsync(
            IReadOnlyList<Message> messages,
            System.Text.Json.JsonElement? tools,
            string toolChoice,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            await Task.CompletedTask;
            yield return new ChatChunk.Done();
        }
    }

    /// <summary>跟踪调用次数的 Provider（返回非空摘要）。</summary>
    private sealed class TrackingProvider : IBaseProvider
    {
        public int CallCount { get; private set; }

        public Task<string> ChatAsync(IReadOnlyList<Message> messages, CancellationToken ct)
        {
            CallCount++;
            return Task.FromResult("summary");
        }

        public async IAsyncEnumerable<string> ChatStreamAsync(
            IReadOnlyList<Message> messages,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            CallCount++;
            await Task.CompletedTask;
            yield return "summary content";
        }

        public async IAsyncEnumerable<ChatChunk> ChatStreamAsync(
            IReadOnlyList<Message> messages,
            System.Text.Json.JsonElement? tools,
            string toolChoice,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            await Task.CompletedTask;
            yield return new ChatChunk.Done();
        }
    }

    /// <summary>立即取消的 Provider。</summary>
    private sealed class CancelProvider : IBaseProvider
    {
        public Task<string> ChatAsync(IReadOnlyList<Message> messages, CancellationToken ct)
            => Task.FromCanceled<string>(ct);

        public async IAsyncEnumerable<string> ChatStreamAsync(
            IReadOnlyList<Message> messages,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            await Task.Delay(1, ct);
            yield return "";
        }

        public async IAsyncEnumerable<ChatChunk> ChatStreamAsync(
            IReadOnlyList<Message> messages,
            System.Text.Json.JsonElement? tools,
            string toolChoice,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            await Task.Delay(1, ct);
            yield return new ChatChunk.Done();
        }
    }
}
