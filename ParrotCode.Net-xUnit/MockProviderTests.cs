using ParrotCode;

namespace ParrotCode.xUnit;

/// <summary>
/// MockProvider 单元测试：覆盖正常回显、边界输入与取消语义。
/// 迭代 2a：入参由 string 迁移为 IReadOnlyList&lt;Message&gt;，取最后一条 user 回显。
/// App 主循环依赖 Console / Spectre.Console 静态 I/O，不在本迭代做单元测试，
/// 由验收标准（dotnet run 手测 + 日志分离重定向验证）覆盖。
/// </summary>
public class MockProviderTests
{
    private readonly MockProvider _provider = new();

    /// <summary>构造仅含 user 消息的列表，按传入顺序排列。</summary>
    private static IReadOnlyList<Message> UserMessages(params string[] contents) =>
        contents.Select(c => new Message(MessageRole.User, c)).ToArray();

    [Fact]
    public async Task ChatAsync_WithChineseInput_ReturnsInputWithMockSuffix()
    {
        // 对齐 plan.md 验收标准：输入「你好」→ 输出「你好（mock）」
        var reply = await _provider.ChatAsync(UserMessages("你好"), CancellationToken.None);
        reply.Should().Be("你好（mock）");
    }

    [Theory]
    [InlineData("hello")]
    [InlineData("Hello, World")]
    [InlineData("今天天气如何")]
    [InlineData("含标点的输入！？。，")]
    public async Task ChatAsync_WithVariousInputs_ReturnsInputPlusMockSuffix(string input)
    {
        var reply = await _provider.ChatAsync(UserMessages(input), CancellationToken.None);
        reply.Should().Be($"{input}（mock）");
    }

    [Fact]
    public async Task ChatAsync_WithEmptyContent_ReturnsOnlyMockSuffix()
    {
        var reply = await _provider.ChatAsync(UserMessages(string.Empty), CancellationToken.None);
        reply.Should().Be("（mock）");
    }

    [Fact]
    public async Task ChatAsync_WithWhitespaceContent_PreservesWhitespaceInEcho()
    {
        // MockProvider 不做空白裁剪（裁剪由 App 主循环负责），原样回显
        var reply = await _provider.ChatAsync(UserMessages("   "), CancellationToken.None);
        reply.Should().Be("   （mock）");
    }

    [Fact]
    public async Task ChatAsync_WithMarkupLikeContent_DoesNotInterpretMarkup()
    {
        // 含方括号的输入原样回显；Markup 转义是调用方（App）的职责
        var reply = await _provider.ChatAsync(UserMessages("[red]danger[/]"), CancellationToken.None);
        reply.Should().Be("[red]danger[/]（mock）");
    }

    [Fact]
    public async Task ChatAsync_ReturnsSuccessfullyCompletedTask()
    {
        var task = _provider.ChatAsync(UserMessages("你好"), CancellationToken.None);

        // MockProvider 同步完成（Task.FromResult），任务应已完成
        task.IsCompleted.Should().BeTrue();
        (await task).Should().Be("你好（mock）");
    }

    [Fact]
    public async Task ChatAsync_WithAlreadyCancelledToken_ThrowsOperationCanceledException()
    {
        var cts = new CancellationTokenSource();
        cts.Cancel();

        var act = async () => await _provider.ChatAsync(UserMessages("你好"), cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public void ChatAsync_WithCancelledToken_ThrowsSynchronously()
    {
        var cts = new CancellationTokenSource();
        cts.Cancel();

        // 取消令牌应在返回结果前抛出，确保不会产生部分回复
        var act = () => { _ = _provider.ChatAsync(UserMessages("你好"), cts.Token); };

        act.Should().Throw<OperationCanceledException>();
    }

    [Fact]
    public async Task ChatAsync_DoesNotRetainStateAcrossCalls()
    {
        var first = await _provider.ChatAsync(UserMessages("第一次"), CancellationToken.None);
        var second = await _provider.ChatAsync(UserMessages("第二次"), CancellationToken.None);

        first.Should().Be("第一次（mock）");
        second.Should().Be("第二次（mock）");
    }

    // ---- 迭代 2a 补充用例（新签名相关边界）----

    [Fact]
    public async Task ChatAsync_WithEmptyMessageList_ReturnsOnlyMockSuffix()
    {
        var reply = await _provider.ChatAsync(Array.Empty<Message>(), CancellationToken.None);
        reply.Should().Be("（mock）");
    }

    [Fact]
    public async Task ChatAsync_WithOnlyAssistantMessage_ReturnsOnlyMockSuffix()
    {
        // 列表只有 assistant 消息（无 user），无内容可回显
        var messages = new[] { new Message(MessageRole.Assistant, "我是 AI") };
        var reply = await _provider.ChatAsync(messages, CancellationToken.None);
        reply.Should().Be("（mock）");
    }

    [Fact]
    public async Task ChatAsync_WithMultipleUserMessages_EchoesLastUserContent()
    {
        // 多条 user 消息时回显最后一条 user 的 Content
        var reply = await _provider.ChatAsync(UserMessages("第一", "第二", "第三"), CancellationToken.None);
        reply.Should().Be("第三（mock）");
    }

    [Fact]
    public async Task ChatAsync_WithSystemAndUserMessage_EchoesUserContent()
    {
        // system 消息不影响回显；回显最后一条 user
        var messages = new Message[]
        {
            new(MessageRole.System, "你是助手"),
            new(MessageRole.User, "你好")
        };
        var reply = await _provider.ChatAsync(messages, CancellationToken.None);
        reply.Should().Be("你好（mock）");
    }
}
