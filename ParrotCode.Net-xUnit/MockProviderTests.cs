using ParrotCode;

namespace ParrotCode.Net_xUnit;

/// <summary>
/// MockProvider 单元测试：覆盖正常回显、边界输入与取消语义。
/// App 主循环依赖 Console / Spectre.Console 静态 I/O，不在本迭代做单元测试，
/// 由迭代 1 验收标准（dotnet run 手测 + 日志分离重定向验证）覆盖。
/// </summary>
public class MockProviderTests
{
    private readonly MockProvider _provider = new();

    [Fact]
    public async Task ChatAsync_WithChineseInput_ReturnsInputWithMockSuffix()
    {
        // 对齐 plan.md 验收标准：输入「你好」→ 输出「你好（mock）」
        var reply = await _provider.ChatAsync("你好", CancellationToken.None);
        reply.Should().Be("你好（mock）");
    }

    [Theory]
    [InlineData("hello")]
    [InlineData("Hello, World")]
    [InlineData("今天天气如何")]
    [InlineData("含标点的输入！？。，")]
    public async Task ChatAsync_WithVariousInputs_ReturnsInputPlusMockSuffix(string input)
    {
        var reply = await _provider.ChatAsync(input, CancellationToken.None);
        reply.Should().Be($"{input}（mock）");
    }

    [Fact]
    public async Task ChatAsync_WithEmptyInput_ReturnsOnlyMockSuffix()
    {
        var reply = await _provider.ChatAsync(string.Empty, CancellationToken.None);
        reply.Should().Be("（mock）");
    }

    [Fact]
    public async Task ChatAsync_WithWhitespaceInput_PreservesWhitespaceInEcho()
    {
        // MockProvider 不做空白裁剪（裁剪由 App 主循环负责），原样回显
        var reply = await _provider.ChatAsync("   ", CancellationToken.None);
        reply.Should().Be("   （mock）");
    }

    [Fact]
    public async Task ChatAsync_WithMarkupLikeInput_DoesNotInterpretMarkup()
    {
        // 含方括号的输入原样回显；Markup 转义是调用方（App）的职责
        var reply = await _provider.ChatAsync("[red]danger[/]", CancellationToken.None);
        reply.Should().Be("[red]danger[/]（mock）");
    }

    [Fact]
    public async Task ChatAsync_ReturnsSuccessfullyCompletedTask()
    {
        var task = _provider.ChatAsync("你好", CancellationToken.None);

        // MockProvider 同步完成（Task.FromResult），任务应已完成
        task.IsCompleted.Should().BeTrue();
        (await task).Should().Be("你好（mock）");
    }

    [Fact]
    public async Task ChatAsync_WithAlreadyCancelledToken_ThrowsOperationCanceledException()
    {
        var cts = new CancellationTokenSource();
        cts.Cancel();

        var act = async () => await _provider.ChatAsync("你好", cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public void ChatAsync_WithCancelledToken_ThrowsSynchronously()
    {
        var cts = new CancellationTokenSource();
        cts.Cancel();

        // 取消令牌应在返回结果前抛出，确保不会产生部分回复
        var act = () => { _ = _provider.ChatAsync("你好", cts.Token); };

        act.Should().Throw<OperationCanceledException>();
    }

    [Fact]
    public async Task ChatAsync_DoesNotRetainStateAcrossCalls()
    {
        var first = await _provider.ChatAsync("第一次", CancellationToken.None);
        var second = await _provider.ChatAsync("第二次", CancellationToken.None);

        first.Should().Be("第一次（mock）");
        second.Should().Be("第二次（mock）");
    }
}
