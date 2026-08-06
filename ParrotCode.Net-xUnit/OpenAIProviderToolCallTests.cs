using System.Net;
using System.Text;
using System.Text.Json;
using ParrotCode;

namespace ParrotCode.xUnit;

/// <summary>
/// OpenAIProvider 带 tools 流式重载（返回 IAsyncEnumerable&lt;ChatChunk&gt;）的单元测试。
/// 用 FakeHttpHandler mock SSE 响应，不打真实 API。
/// </summary>
public class OpenAIProviderToolCallTests
{
    private static readonly ProviderConfig ValidConfig = new()
    {
        Name = "test",
        Protocol = "openai",
        Model = "test-model",
        BaseUrl = "https://api.test.com/v1",
        ApiKey = "sk-test-key"
    };

    // ---- 文本 + 工具调用分片 ----

    [Fact]
    public async Task ChatStreamAsync_WithToolCalls_YieldsTextAndToolCallDeltas()
    {
        var sse = Sse(
            TextChunk("我来读取"),
            "",
            ToolCallChunk(0, "call_1", "read_file", "{\"path\":"),
            "",
            ToolCallChunk(0, null, null, "\"a.txt\"}"),
            "",
            Done());
        var provider = CreateProvider(sse);

        var chunks = await CollectChunksAsync(
            provider.ChatStreamAsync(UserMessages("hi"), null, "auto", CancellationToken.None));

        chunks.Should().HaveCount(4);

        chunks[0].Should().BeOfType<ChatChunk.TextDelta>().Which.Text.Should().Be("我来读取");

        var tc1 = chunks[1].Should().BeOfType<ChatChunk.ToolCallDelta>().Which;
        tc1.Index.Should().Be(0);
        tc1.Id.Should().Be("call_1");
        tc1.Name.Should().Be("read_file");
        tc1.ArgumentsFragment.Should().Be("{\"path\":");

        var tc2 = chunks[2].Should().BeOfType<ChatChunk.ToolCallDelta>().Which;
        tc2.Index.Should().Be(0);
        tc2.ArgumentsFragment.Should().Be("\"a.txt\"}");

        chunks[3].Should().BeOfType<ChatChunk.Done>();
    }

    [Fact]
    public async Task ChatStreamAsync_WithMultipleToolCalls_YieldsByIndex()
    {
        var sse = Sse(
            ToolCallChunk(0, "call_1", "tool_a", "{}"),
            "",
            ToolCallChunk(1, "call_2", "tool_b", "{}"),
            "",
            Done());
        var provider = CreateProvider(sse);

        var chunks = await CollectChunksAsync(
            provider.ChatStreamAsync(UserMessages("hi"), null, "auto", CancellationToken.None));

        var toolCalls = chunks.OfType<ChatChunk.ToolCallDelta>().ToList();
        toolCalls.Should().HaveCount(2);
        toolCalls[0].Index.Should().Be(0);
        toolCalls[0].Name.Should().Be("tool_a");
        toolCalls[1].Index.Should().Be(1);
        toolCalls[1].Name.Should().Be("tool_b");
        chunks.Last().Should().BeOfType<ChatChunk.Done>();
    }

    [Fact]
    public async Task ChatStreamAsync_WithTextOnly_YieldsTextDeltaAndDone()
    {
        var sse = Sse(
            TextChunk("hello"),
            "",
            Done());
        var provider = CreateProvider(sse);

        var chunks = await CollectChunksAsync(
            provider.ChatStreamAsync(UserMessages("hi"), null, "auto", CancellationToken.None));

        chunks.Should().HaveCount(2);
        chunks[0].Should().BeOfType<ChatChunk.TextDelta>().Which.Text.Should().Be("hello");
        chunks[1].Should().BeOfType<ChatChunk.Done>();
    }

    [Fact]
    public async Task ChatStreamAsync_WithDoneMarker_YieldsDoneChunk()
    {
        var sse = Sse(
            TextChunk("x"),
            "",
            Done());
        var provider = CreateProvider(sse);

        var chunks = await CollectChunksAsync(
            provider.ChatStreamAsync(UserMessages("hi"), null, "auto", CancellationToken.None));

        chunks.Last().Should().BeOfType<ChatChunk.Done>();
    }

    [Fact]
    public async Task ChatStreamAsync_WithEmptyChoices_SkipsGracefully()
    {
        var sse = Sse(
            TextChunk("你好"),
            "",
            """data: {"choices":[]}""",
            "",
            Done());
        var provider = CreateProvider(sse);

        var chunks = await CollectChunksAsync(
            provider.ChatStreamAsync(UserMessages("hi"), null, "auto", CancellationToken.None));

        chunks.Should().HaveCount(2);
        chunks[0].Should().BeOfType<ChatChunk.TextDelta>().Which.Text.Should().Be("你好");
        chunks[1].Should().BeOfType<ChatChunk.Done>();
    }

    [Fact]
    public async Task ChatStreamAsync_PassesToolsInRequestBody()
    {
        var handler = new CapturingHandler();
        var provider = new OpenAIProvider(ValidConfig, handler);
        using var toolsDoc = JsonDocument.Parse("[{\"type\":\"function\",\"function\":{\"name\":\"test\"}}]");
        JsonElement? tools = toolsDoc.RootElement;

        _ = await CollectChunksAsync(
            provider.ChatStreamAsync(UserMessages("hi"), tools, "auto", CancellationToken.None));

        handler.RequestBody.Should().NotBeNull();
        using var bodyDoc = JsonDocument.Parse(handler.RequestBody!);
        bodyDoc.RootElement.TryGetProperty("tools", out _).Should().BeTrue();
        bodyDoc.RootElement.TryGetProperty("tool_choice", out _).Should().BeTrue();
    }

    [Fact]
    public async Task ChatStreamAsync_WithOmittedToolFields_HandlesNulls()
    {
        var sse = Sse(
            ToolCallChunk(0),
            "",
            Done());
        var provider = CreateProvider(sse);

        var chunks = await CollectChunksAsync(
            provider.ChatStreamAsync(UserMessages("hi"), null, "auto", CancellationToken.None));

        var tc = chunks[0].Should().BeOfType<ChatChunk.ToolCallDelta>().Which;
        tc.Index.Should().Be(0);
        tc.Id.Should().BeNull();
        tc.Name.Should().BeNull();
        tc.ArgumentsFragment.Should().BeNull();
    }

    // ---- 辅助 ----

    private static OpenAIProvider CreateProvider(
        string responseBody, string mediaType = "text/event-stream", HttpStatusCode statusCode = HttpStatusCode.OK)
        => new(ValidConfig, new FakeHttpHandler(statusCode, responseBody, mediaType));

    private static Message[] UserMessages(params string[] contents)
        => contents.Select(c => new Message(MessageRole.User, c)).ToArray();

    private static async Task<List<ChatChunk>> CollectChunksAsync(IAsyncEnumerable<ChatChunk> stream)
    {
        var chunks = new List<ChatChunk>();
        await foreach (var c in stream) chunks.Add(c);
        return chunks;
    }

    private static string Sse(params string[] lines) => string.Join("\n", lines) + "\n";

    private static string Done() => "data: [DONE]";

    private static string TextChunk(string text) =>
        $"data: {{\"choices\":[{{\"delta\":{{\"content\":{JsonSerializer.Serialize(text)}}}}}]}}";

    private static string ToolCallChunk(int index, string? id = null, string? name = null, string? args = null)
    {
        var parts = new List<string> { $"\"index\":{index}" };
        if (id != null) parts.Add($"\"id\":{JsonSerializer.Serialize(id)}");
        var fnParts = new List<string>();
        if (name != null) fnParts.Add($"\"name\":{JsonSerializer.Serialize(name)}");
        if (args != null) fnParts.Add($"\"arguments\":{JsonSerializer.Serialize(args)}");
        if (fnParts.Count > 0) parts.Add($"\"function\":{{{string.Join(",", fnParts)}}}");
        return $"data: {{\"choices\":[{{\"delta\":{{\"tool_calls\":[{{{string.Join(",", parts)}}}]}}}}]}}";
    }

    // ---- Mock Handlers ----

    private sealed class FakeHttpHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _statusCode;
        private readonly string _responseBody;
        private readonly string _mediaType;

        public FakeHttpHandler(HttpStatusCode statusCode, string responseBody, string mediaType = "text/event-stream")
        {
            _statusCode = statusCode;
            _responseBody = responseBody;
            _mediaType = mediaType;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var content = new StringContent(_responseBody, Encoding.UTF8, _mediaType);
            var response = new HttpResponseMessage(_statusCode) { Content = content };
            return Task.FromResult(response);
        }
    }

    /// <summary>捕获请求体并返回 [DONE] 响应的 Handler，用于校验请求构造。</summary>
    private sealed class CapturingHandler : HttpMessageHandler
    {
        public string? RequestBody { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            RequestBody = request.Content?.ReadAsStringAsync(ct).GetAwaiter().GetResult();
            var content = new StringContent("data: [DONE]\n", Encoding.UTF8, "text/event-stream");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        }
    }
}
