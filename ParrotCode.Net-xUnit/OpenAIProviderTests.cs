using System.Net;
using System.Text;
using System.Text.Json;
using ParrotCode;

namespace ParrotCode.xUnit;

/// <summary>
/// OpenAIProvider 单元测试。用 FakeHttpHandler mock HTTP 响应，不打真实 API。
/// </summary>
public class OpenAIProviderTests
{
    private static readonly ProviderConfig ValidConfig = new()
    {
        Name = "test",
        Protocol = "openai",
        Model = "test-model",
        BaseUrl = "https://api.test.com/v1",
        ApiKey = "sk-test-key"
    };

    // ---- 构造器校验 ----

    [Fact]
    public void Constructor_WithEmptyModel_ThrowsConfigException()
    {
        var config = ValidConfig with { Model = "" };

        var act = () => new OpenAIProvider(config, new FakeHttpHandler(HttpStatusCode.OK, ""));

        var ex = act.Should().Throw<ConfigException>().Which;
        ex.Message.Should().Contain("model");
    }

    [Fact]
    public void Constructor_WithEmptyBaseUrl_ThrowsConfigException()
    {
        var config = ValidConfig with { BaseUrl = "" };

        var act = () => new OpenAIProvider(config, new FakeHttpHandler(HttpStatusCode.OK, ""));

        var ex = act.Should().Throw<ConfigException>().Which;
        ex.Message.Should().Contain("base_url");
    }

    [Fact]
    public void Constructor_WithEmptyApiKey_ThrowsConfigException()
    {
        var config = ValidConfig with { ApiKey = "" };

        var act = () => new OpenAIProvider(config, new FakeHttpHandler(HttpStatusCode.OK, ""));

        var ex = act.Should().Throw<ConfigException>().Which;
        ex.Message.Should().Contain("api_key");
    }

    // ---- 流式正常输出 ----

    [Fact]
    public async Task ChatStreamAsync_WithMultipleChunks_YieldsEachContentDelta()
    {
        var sse = Sse(
            Chunk(role: "assistant"),
            Chunk(content: "你好"),
            Chunk(content: "世界"),
            Done());
        var provider = CreateProvider(sse);

        var tokens = await CollectTokensAsync(provider.ChatStreamAsync(UserMessages("hi"), CancellationToken.None));

        tokens.Should().HaveCount(2);
        tokens[0].Should().Be("你好");
        tokens[1].Should().Be("世界");
    }

    [Fact]
    public async Task ChatStreamAsync_FirstChunkWithoutContent_DoesNotYield()
    {
        var sse = Sse(
            Chunk(role: "assistant"),  // 无 content
            Chunk(content: "你好"),
            Done());
        var provider = CreateProvider(sse);

        var tokens = await CollectTokensAsync(provider.ChatStreamAsync(UserMessages("hi"), CancellationToken.None));

        tokens.Should().HaveCount(1);
        tokens[0].Should().Be("你好");
    }

    [Fact]
    public async Task ChatStreamAsync_WithEmptyChoicesHeartbeat_SkipsGracefully()
    {
        var sse = Sse(
            """data: {"choices":[]}""",
            "",
            Chunk(content: "你好"),
            "",
            Done());
        var provider = CreateProvider(sse);

        var tokens = await CollectTokensAsync(provider.ChatStreamAsync(UserMessages("hi"), CancellationToken.None));

        tokens.Should().HaveCount(1);
        tokens[0].Should().Be("你好");
    }

    [Fact]
    public async Task ChatStreamAsync_WithDoneMarker_StopsGracefully()
    {
        var sse = Sse(
            Chunk(content: "你好"),
            Done());
        var provider = CreateProvider(sse);

        var tokens = await CollectTokensAsync(provider.ChatStreamAsync(UserMessages("hi"), CancellationToken.None));

        tokens.Should().HaveCount(1);
        tokens[0].Should().Be("你好");
    }

    [Fact]
    public async Task ChatStreamAsync_WithReasoningContent_IgnoresReasoningYieldsContentOnly()
    {
        // DeepSeek-reasoner 格式：delta 含 reasoning_content + content
        var sse = Sse(
            """data: {"choices":[{"delta":{"reasoning_content":"思考中","content":""}}]}""",
            "",
            """data: {"choices":[{"delta":{"reasoning_content":"继续思考","content":""}}]}""",
            "",
            """data: {"choices":[{"delta":{"content":"你好"}}]}""",
            "",
            Done());
        var provider = CreateProvider(sse);

        var tokens = await CollectTokensAsync(provider.ChatStreamAsync(UserMessages("hi"), CancellationToken.None));

        tokens.Should().HaveCount(1);
        tokens[0].Should().Be("你好");
    }

    [Fact]
    public async Task ChatStreamAsync_WithMixedReasoningAndContent_YieldsOnlyContent()
    {
        var sse = Sse(
            """data: {"choices":[{"delta":{"reasoning_content":"思考"}}]}""",
            "",
            """data: {"choices":[{"delta":{"content":"你好"}}]}""",
            "",
            """data: {"choices":[{"delta":{"content":"世界"}}]}""",
            "",
            Done());
        var provider = CreateProvider(sse);

        var tokens = await CollectTokensAsync(provider.ChatStreamAsync(UserMessages("hi"), CancellationToken.None));

        tokens.Should().HaveCount(2);
        tokens[0].Should().Be("你好");
        tokens[1].Should().Be("世界");
    }

    [Fact]
    public async Task ChatStreamAsync_WithNonDataLines_SkipsGracefully()
    {
        var sse = Sse(
            ": keepalive",
            "",
            "event: ping",
            "",
            """data: {"choices":[{"delta":{"content":"你好"}}]}""",
            "",
            Done());
        var provider = CreateProvider(sse);

        var tokens = await CollectTokensAsync(provider.ChatStreamAsync(UserMessages("hi"), CancellationToken.None));

        tokens.Should().HaveCount(1);
        tokens[0].Should().Be("你好");
    }

    // ---- 非流式 ----

    [Fact]
    public async Task ChatAsync_WithValidResponse_ReturnsContent()
    {
        var json = """{"choices":[{"message":{"content":"你好"}}]}""";
        var provider = CreateProvider(json, "application/json");

        var reply = await provider.ChatAsync(UserMessages("hi"), CancellationToken.None);

        reply.Should().Be("你好");
    }

    // ---- HTTP 错误转译 ----

    [Fact]
    public async Task ChatStreamAsync_WithHttp401_ThrowsProviderAuthException()
    {
        var errorBody = """{"error":{"message":"Invalid API Key"}}""";
        var provider = CreateProvider(errorBody, statusCode: HttpStatusCode.Unauthorized);

        var act = async () =>
        {
            await foreach (var _ in provider.ChatStreamAsync(UserMessages("hi"), CancellationToken.None)) { }
        };

        var thrown = await act.Should().ThrowAsync<ProviderAuthException>();
        thrown.Which.Message.Should().Contain("Invalid API Key");
        thrown.Which.StatusCode.Should().Be(401);
    }

    [Fact]
    public async Task ChatStreamAsync_WithHttp429_ThrowsProviderRateLimitException()
    {
        var errorBody = """{"error":{"message":"Rate limit exceeded"}}""";
        var provider = CreateProvider(errorBody, statusCode: HttpStatusCode.TooManyRequests);

        var act = async () =>
        {
            await foreach (var _ in provider.ChatStreamAsync(UserMessages("hi"), CancellationToken.None)) { }
        };

        var thrown = await act.Should().ThrowAsync<ProviderRateLimitException>();
        thrown.Which.Message.Should().Contain("Rate limit exceeded");
        thrown.Which.StatusCode.Should().Be(429);
    }

    [Fact]
    public async Task ChatStreamAsync_WithHttp500_ThrowsProviderServerException()
    {
        var errorBody = """{"error":{"message":"Internal error"}}""";
        var provider = CreateProvider(errorBody, statusCode: HttpStatusCode.InternalServerError);

        var act = async () =>
        {
            await foreach (var _ in provider.ChatStreamAsync(UserMessages("hi"), CancellationToken.None)) { }
        };

        var thrown = await act.Should().ThrowAsync<ProviderServerException>();
        thrown.Which.Message.Should().Contain("Internal error");
        thrown.Which.StatusCode.Should().Be(500);
    }

    [Fact]
    public async Task ChatStreamAsync_WithHttp400_ThrowsProviderRequestException()
    {
        var errorBody = """{"error":{"message":"Bad request"}}""";
        var provider = CreateProvider(errorBody, statusCode: HttpStatusCode.BadRequest);

        var act = async () =>
        {
            await foreach (var _ in provider.ChatStreamAsync(UserMessages("hi"), CancellationToken.None)) { }
        };

        var thrown = await act.Should().ThrowAsync<ProviderRequestException>();
        thrown.Which.Message.Should().Contain("Bad request");
        thrown.Which.StatusCode.Should().Be(400);
    }

    [Fact]
    public async Task ChatStreamAsync_WithNetworkFailure_ThrowsProviderRequestException()
    {
        var provider = new OpenAIProvider(ValidConfig, new ThrowingHandler());

        var act = async () =>
        {
            await foreach (var _ in provider.ChatStreamAsync(UserMessages("hi"), CancellationToken.None)) { }
        };

        var thrown = await act.Should().ThrowAsync<ProviderRequestException>();
        thrown.Which.Message.Should().Contain("无法连接");
    }

    [Fact]
    public async Task ChatStreamAsync_WithAlreadyCancelledToken_ThrowsOperationCanceledException()
    {
        var sse = Sse(Chunk(content: "你好"), Done());
        var provider = CreateProvider(sse);
        var cts = new CancellationTokenSource();
        cts.Cancel();

        var act = async () =>
        {
            await foreach (var _ in provider.ChatStreamAsync(UserMessages("hi"), cts.Token)) { }
        };

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task ChatAsync_WithHttp401_ThrowsProviderAuthException()
    {
        var errorBody = """{"error":{"message":"Invalid API Key"}}""";
        var provider = CreateProvider(errorBody, statusCode: HttpStatusCode.Unauthorized, mediaType: "application/json");

        var act = async () => await provider.ChatAsync(UserMessages("hi"), CancellationToken.None);

        var thrown = await act.Should().ThrowAsync<ProviderAuthException>();
        thrown.Which.Message.Should().Contain("Invalid API Key");
    }

    [Fact]
    public async Task ChatStreamAsync_WithNonJsonErrorBody_ContainsRawBody()
    {
        var errorBody = "Service Unavailable";
        var provider = CreateProvider(errorBody, statusCode: HttpStatusCode.InternalServerError);

        var act = async () =>
        {
            await foreach (var _ in provider.ChatStreamAsync(UserMessages("hi"), CancellationToken.None)) { }
        };

        var thrown = await act.Should().ThrowAsync<ProviderServerException>();
        thrown.Which.Message.Should().Contain("Service Unavailable");
    }

    // ---- 辅助 ----

    private static OpenAIProvider CreateProvider(
        string responseBody, string mediaType = "text/event-stream", HttpStatusCode statusCode = HttpStatusCode.OK)
        => new(ValidConfig, new FakeHttpHandler(statusCode, responseBody, mediaType));

    private static Message[] UserMessages(params string[] contents)
        => contents.Select(c => new Message(MessageRole.User, c)).ToArray();

    private static async Task<List<string>> CollectTokensAsync(IAsyncEnumerable<string> stream)
    {
        var tokens = new List<string>();
        await foreach (var token in stream)
        {
            tokens.Add(token);
        }
        return tokens;
    }

    /// <summary>构造 SSE 多行文本。每行后跟 \n。</summary>
    private static string Sse(params string[] lines) => string.Join("\n", lines) + "\n";

    /// <summary>构造 data: chunk 行。</summary>
    private static string Chunk(string? content = null, string? role = null)
    {
        var delta = new Dictionary<string, string>();
        if (role != null) delta["role"] = role;
        if (content != null) delta["content"] = content;
        var deltaJson = string.Join(",", delta.Select(kv => $"\"{kv.Key}\":{JsonSerializer.Serialize(kv.Value)}"));
        return $"data: {{\"choices\":[{{\"delta\":{{{deltaJson}}}}}]}}";
    }

    private static string Done() => "data: [DONE]";

    // ---- Mock Handlers ----

    /// <summary>返回预设 HTTP 响应的 HttpMessageHandler。</summary>
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

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct)
        {
            var content = new StringContent(_responseBody, Encoding.UTF8, _mediaType);
            var response = new HttpResponseMessage(_statusCode) { Content = content };
            return Task.FromResult(response);
        }
    }

    /// <summary>始终抛 HttpRequestException 的 Handler，模拟网络故障。</summary>
    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct)
            => throw new HttpRequestException("Connection refused");
    }
}
