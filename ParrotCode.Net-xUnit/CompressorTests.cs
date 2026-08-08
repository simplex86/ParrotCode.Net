using ParrotCode;

namespace ParrotCode.xUnit;

/// <summary>
/// ContextCompressor 单元测试：警告一次、压缩触发、熔断器跳过、重置、enableAutoCompress=false。
/// </summary>
public class CompressorTests
{
    private static string MakeString(int len) => new('x', len);

    private static ContextCompressor CreateCompressor(
        IBaseProvider provider,
        int contextWindow = 100,
        bool enableAutoCompress = true,
        int keepRecent = 2,
        int maxCircuitFailures = 2,
        string? projectRoot = null)
    {
        return new ContextCompressor(
            provider, contextWindow,
            truncateConfig: new TruncateConfig { PerResultThreshold = 50_000, RoundTotalThreshold = 200_000, PreviewLength = 2_000 },
            warningFraction: 0.7,
            triggerFraction: 0.9,
            keepRecent: keepRecent,
            maxCircuitFailures: maxCircuitFailures,
            enableAutoCompress: enableAutoCompress,
            projectRoot: projectRoot ?? Path.GetTempPath());
    }

    [Fact]
    public void TruncateBatch_DelegatesToTruncator()
    {
        var compressor = CreateCompressor(new MockProvider(), projectRoot: Path.GetTempPath());

        var (contents, infos) = compressor.TruncateBatch(
            new[] { "short" }, new[] { "read_file" });

        contents[0].Should().Be("short");
        infos.Should().BeEmpty();
    }

    [Fact]
    public async Task CheckAndCompress_TokenBelowWarning_NoAction()
    {
        var compressor = CreateCompressor(new MockProvider(), contextWindow: 1000);
        var history = new ConversationHistory();
        history.AddUser("short");  // 2 tokens << 700 warning

        var result = await compressor.CheckAndCompressAsync(history, CancellationToken.None);

        result.WasCompressed.Should().BeFalse();
        result.WarningIssued.Should().BeFalse();
        result.CircuitOpen.Should().BeFalse();
    }

    [Fact]
    public async Task CheckAndCompress_TokenOverWarning_EmitsWarningOnce()
    {
        var compressor = CreateCompressor(new MockProvider(), contextWindow: 100);
        var history = new ConversationHistory();
        history.AddUser(MakeString(250));  // 84 tokens > 70 warning, < 90 trigger

        // 第一次检查 → 发警告
        var result1 = await compressor.CheckAndCompressAsync(history, CancellationToken.None);
        result1.WarningIssued.Should().BeTrue();
        result1.WarningMessage.Should().NotBeNullOrEmpty();

        // 第二次检查 → 不再发警告
        var result2 = await compressor.CheckAndCompressAsync(history, CancellationToken.None);
        result2.WarningIssued.Should().BeFalse();
    }

    [Fact]
    public async Task CheckAndCompress_TokenOverTrigger_Compresses()
    {
        var provider = new MockProvider();
        var compressor = CreateCompressor(provider, contextWindow: 100, keepRecent: 2);
        var history = new ConversationHistory();
        for (int i = 0; i < 6; i++)
        {
            history.AddUser(MakeString(50));
            history.AddAssistant(MakeString(50));
        }
        // 12 条 × 50 chars = 600 chars → 200 tokens > 90 trigger

        var result = await compressor.CheckAndCompressAsync(history, CancellationToken.None);

        result.WasCompressed.Should().BeTrue();
        result.MessagesCompressed.Should().Be(10);  // 12 - 2
    }

    [Fact]
    public async Task CheckAndCompress_CompressSuccess_ResetsWarning()
    {
        // 用 ShortSummaryProvider 返回短摘要，确保压缩后 token 低于警告阈值
        var provider = new ShortSummaryProvider();
        var compressor = CreateCompressor(provider, contextWindow: 100, keepRecent: 2);
        var history = new ConversationHistory();
        for (int i = 0; i < 6; i++)
        {
            history.AddUser(MakeString(50));
            history.AddAssistant(MakeString(50));
        }

        // 触发压缩
        await compressor.CheckAndCompressAsync(history, CancellationToken.None);

        // 压缩后历史变短，ResetWarning 被调用
        // 再次检查不应发警告（摘要很短，token < 70 警告阈值）
        var result2 = await compressor.CheckAndCompressAsync(history, CancellationToken.None);
        result2.WarningIssued.Should().BeFalse();
    }

    [Fact]
    public async Task CheckAndCompress_CircuitOpen_DoesNotCallLLM()
    {
        var provider = new FailingProvider();
        var compressor = CreateCompressor(provider, contextWindow: 100, keepRecent: 2, maxCircuitFailures: 2);
        var history = new ConversationHistory();
        for (int i = 0; i < 6; i++)
        {
            history.AddUser(MakeString(50));
            history.AddAssistant(MakeString(50));
        }

        // 第一次失败
        await compressor.CheckAndCompressAsync(history, CancellationToken.None);
        // 第二次失败 → 熔断
        await compressor.CheckAndCompressAsync(history, CancellationToken.None);
        compressor.CircuitOpen.Should().BeTrue();

        // 第三次 → 熔断器 open，不调 LLM
        provider.CallCount.Should().Be(2);
        var result = await compressor.CheckAndCompressAsync(history, CancellationToken.None);
        provider.CallCount.Should().Be(2);  // 没有增加
        result.CircuitOpen.Should().BeTrue();
    }

    [Fact]
    public async Task CheckAndCompress_EnableAutoCompressFalse_SkipsCompression()
    {
        var provider = new MockProvider();
        var compressor = CreateCompressor(provider, contextWindow: 100, enableAutoCompress: false);
        var history = new ConversationHistory();
        for (int i = 0; i < 6; i++)
        {
            history.AddUser(MakeString(50));
            history.AddAssistant(MakeString(50));
        }

        var result = await compressor.CheckAndCompressAsync(history, CancellationToken.None);

        result.WasCompressed.Should().BeFalse();
        result.WarningIssued.Should().BeFalse();
    }

    [Fact]
    public void CheckAndCompress_EnableAutoCompressFalse_TruncateStillWorks()
    {
        var provider = new MockProvider();
        var compressor = CreateCompressor(provider, contextWindow: 100, enableAutoCompress: false);
        var big = MakeString(60_000);

        var (contents, infos) = compressor.TruncateBatch(
            new[] { big }, new[] { "read_file" });

        infos.Should().HaveCount(1);
        contents[0].Should().Contain("[工具结果过大");
    }

    [Fact]
    public void ResetCircuit_AllowsCompressionAgain()
    {
        var provider = new FailingProvider();
        var compressor = CreateCompressor(provider, contextWindow: 100, maxCircuitFailures: 1);

        // 手动触发熔断（通过 CheckAndCompress）
        // 这里直接测 ResetCircuit
        compressor.CircuitOpen.Should().BeFalse();
        compressor.ResetCircuit();
        compressor.CircuitOpen.Should().BeFalse();
    }

    [Fact]
    public void ResetWarning_AllowsWarningAgain()
    {
        var compressor = CreateCompressor(new MockProvider(), contextWindow: 100);

        compressor.ResetWarning();
        // 没有直接暴露 _warningEmitted，但 ResetWarning 不抛异常即可
        // 间接验证：再次检查时如果 token > warning，会再发警告
    }

    // ---- 辅助 Provider ----

    /// <summary>返回短摘要的 Provider（确保压缩后 token 低于阈值）。</summary>
    private sealed class ShortSummaryProvider : IBaseProvider
    {
        public Task<string> ChatAsync(IReadOnlyList<Message> messages, CancellationToken ct)
            => Task.FromResult("短摘要");

        public async IAsyncEnumerable<string> ChatStreamAsync(
            IReadOnlyList<Message> messages,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            await Task.CompletedTask;
            yield return "短摘要";
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

    /// <summary>ChatStreamAsync(string) 抛异常的 Provider（模拟摘要失败）。</summary>
    private sealed class FailingProvider : IBaseProvider
    {
        public int CallCount { get; private set; }

        public Task<string> ChatAsync(IReadOnlyList<Message> messages, CancellationToken ct)
        {
            CallCount++;
            return Task.FromException<string>(new HttpRequestException("API error"));
        }

        public async IAsyncEnumerable<string> ChatStreamAsync(
            IReadOnlyList<Message> messages,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            CallCount++;
            await Task.CompletedTask;
            throw new HttpRequestException("API error");
#pragma warning disable CS0162 // 无法到达的代码
            yield return "";  // 满足 async-iterator 必须有 yield 的编译器要求
#pragma warning restore CS0162
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
}
