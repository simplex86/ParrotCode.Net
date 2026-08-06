using System.Text.Json;
using ParrotCode;

namespace ParrotCode.xUnit;

/// <summary>
/// MockProvider 脚本队列测试：覆盖 EnqueueScript + ChatStreamAsync(tools) 重载。
/// 脚本耗尽回退回显；空脚本自动补 Done；已取消令牌抛 OperationCanceledException。
/// </summary>
public class MockProviderScriptTests
{
    private readonly MockProvider _provider = new();

    private static async Task<List<ChatChunk>> CollectChunksAsync(IAsyncEnumerable<ChatChunk> stream)
    {
        var chunks = new List<ChatChunk>();
        await foreach (var c in stream) chunks.Add(c);
        return chunks;
    }

    private static IReadOnlyList<Message> UserMessages(params string[] contents) =>
        contents.Select(c => new Message(MessageRole.User, c)).ToArray();

    private IAsyncEnumerable<ChatChunk> StreamTools(IReadOnlyList<Message> messages, CancellationToken ct) =>
        _provider.ChatStreamAsync(messages, null, "auto", ct);

    [Fact]
    public async Task ChatStreamAsync_NoScript_FallsBackToEcho()
    {
        var chunks = await CollectChunksAsync(StreamTools(UserMessages("你好"), CancellationToken.None));

        chunks.Should().HaveCount(2);
        chunks[0].Should().BeOfType<ChatChunk.TextDelta>()
            .Which.Text.Should().Be("你好（mock）");
        chunks[1].Should().BeOfType<ChatChunk.Done>();
    }

    [Fact]
    public async Task ChatStreamAsync_WithScript_YieldsScriptChunks()
    {
        _provider.EnqueueScript(new ChatChunk.TextDelta("hi"), new ChatChunk.Done());

        var chunks = await CollectChunksAsync(StreamTools(UserMessages("你好"), CancellationToken.None));

        chunks.Should().HaveCount(2);
        chunks[0].Should().BeOfType<ChatChunk.TextDelta>()
            .Which.Text.Should().Be("hi");
        chunks[1].Should().BeOfType<ChatChunk.Done>();
    }

    [Fact]
    public async Task ChatStreamAsync_MultipleScripts_DequeueInOrder()
    {
        _provider.EnqueueScript(new ChatChunk.TextDelta("first"), new ChatChunk.Done());
        _provider.EnqueueScript(new ChatChunk.TextDelta("second"), new ChatChunk.Done());

        var first = await CollectChunksAsync(StreamTools(UserMessages("x"), CancellationToken.None));
        var second = await CollectChunksAsync(StreamTools(UserMessages("y"), CancellationToken.None));

        first[0].Should().BeOfType<ChatChunk.TextDelta>().Which.Text.Should().Be("first");
        second[0].Should().BeOfType<ChatChunk.TextDelta>().Which.Text.Should().Be("second");
    }

    [Fact]
    public async Task ChatStreamAsync_ScriptWithoutDone_AppendsDone()
    {
        _provider.EnqueueScript(new ChatChunk.TextDelta("no-done"));

        var chunks = await CollectChunksAsync(StreamTools(UserMessages("x"), CancellationToken.None));

        chunks.Should().HaveCount(2);
        chunks[0].Should().BeOfType<ChatChunk.TextDelta>().Which.Text.Should().Be("no-done");
        chunks[1].Should().BeOfType<ChatChunk.Done>();
    }

    [Fact]
    public async Task ChatStreamAsync_ScriptExhausted_FallsBackToEcho()
    {
        _provider.EnqueueScript(new ChatChunk.TextDelta("scripted"), new ChatChunk.Done());

        var first = await CollectChunksAsync(StreamTools(UserMessages("echo"), CancellationToken.None));
        var second = await CollectChunksAsync(StreamTools(UserMessages("echo"), CancellationToken.None));

        first[0].Should().BeOfType<ChatChunk.TextDelta>().Which.Text.Should().Be("scripted");
        second.Should().HaveCount(2);
        second[0].Should().BeOfType<ChatChunk.TextDelta>().Which.Text.Should().Be("echo（mock）");
        second[1].Should().BeOfType<ChatChunk.Done>();
    }

    [Fact]
    public async Task ChatStreamAsync_WithAlreadyCancelledToken_ThrowsOperationCanceledException()
    {
        var cts = new CancellationTokenSource();
        cts.Cancel();

        var act = async () =>
        {
            await foreach (var _ in StreamTools(UserMessages("x"), cts.Token)) { }
        };

        await act.Should().ThrowAsync<OperationCanceledException>();
    }
}
