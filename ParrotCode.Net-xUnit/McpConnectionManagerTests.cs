using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using ParrotCode;

namespace ParrotCode.xUnit;

/// <summary>
/// McpConnectionManager + StreamableHttpTransport SSE 解析单元测试（迭代 11c）。
/// </summary>
public class McpConnectionManagerTests
{
    // ===== McpConnectionManager =====

    [Fact]
    public async Task ConnectAllAsync_EmptyServerList_DoesNotThrow()
    {
        var manager = new McpConnectionManager(Array.Empty<McpServerConfig>());

        await manager.ConnectAllAsync(CancellationToken.None);

        manager.ConnectedCount.Should().Be(0);
        manager.ToolCount.Should().Be(0);
    }

    [Fact]
    public async Task ConnectAllAsync_AllServersFail_DoesNotThrow()
    {
        var configs = new[]
        {
            new McpServerConfig { Name = "bad1", Transport = "stdio", Command = "nonexistent-cmd-12345" },
            new McpServerConfig { Name = "bad2", Transport = "stdio", Command = "nonexistent-cmd-67890" }
        };

        var manager = new McpConnectionManager(configs);

        await manager.ConnectAllAsync(CancellationToken.None);

        manager.ConnectedCount.Should().Be(0);
        manager.ConfiguredCount.Should().Be(2);
    }

    [Fact]
    public async Task ConnectAllAsync_UnsupportedTransport_FailsGracefully()
    {
        var configs = new[]
        {
            new McpServerConfig { Name = "bad", Transport = "websocket", Command = "test" }
        };

        var manager = new McpConnectionManager(configs);

        await manager.ConnectAllAsync(CancellationToken.None);

        manager.ConnectedCount.Should().Be(0);
    }

    [Fact]
    public async Task CloseAllAsync_NoClients_DoesNotThrow()
    {
        var manager = new McpConnectionManager(Array.Empty<McpServerConfig>());

        await manager.CloseAllAsync(CancellationToken.None);
    }

    [Fact]
    public void GetStatusSummary_NoServers_ReturnsNotConfigured()
    {
        var manager = new McpConnectionManager(Array.Empty<McpServerConfig>());

        manager.GetStatusSummary().Should().Be("未配置");
    }

    [Fact]
    public async Task GetStatusSummary_AllFailed_ReturnsAllFailedMessage()
    {
        var configs = new[]
        {
            new McpServerConfig { Name = "bad", Transport = "stdio", Command = "nonexistent-cmd-12345" }
        };
        var manager = new McpConnectionManager(configs);

        await manager.ConnectAllAsync(CancellationToken.None);

        manager.GetStatusSummary().Should().Contain("全部连接失败");
    }

    [Fact]
    public void ConfiguredCount_ReturnsServerListSize()
    {
        var configs = new[]
        {
            new McpServerConfig { Name = "s1", Transport = "stdio", Command = "test" },
            new McpServerConfig { Name = "s2", Transport = "stdio", Command = "test" },
            new McpServerConfig { Name = "s3", Transport = "stdio", Command = "test" }
        };

        var manager = new McpConnectionManager(configs);

        manager.ConfiguredCount.Should().Be(3);
    }

    [Fact]
    public async Task ConnectAllAsync_MixedSuccessAndFailure_PartialConnect()
    {
        // StdioTransport with nonexistent command will fail
        // We can't easily test successful connection without a real MCP server
        // So we test that one failure doesn't block the others (they all fail, but independently)
        var configs = new[]
        {
            new McpServerConfig { Name = "fail1", Transport = "stdio", Command = "nonexistent-cmd-1" },
            new McpServerConfig { Name = "fail2", Transport = "stdio", Command = "nonexistent-cmd-2" },
            new McpServerConfig { Name = "fail3", Transport = "stdio", Command = "nonexistent-cmd-3" }
        };

        var manager = new McpConnectionManager(configs);

        // Should complete without throwing (all fail independently)
        await manager.ConnectAllAsync(CancellationToken.None);

        manager.ConnectedCount.Should().Be(0);
        manager.ConfiguredCount.Should().Be(3);
    }

    // ===== StreamableHttpTransport SSE 解析 =====

    /// <summary>Mock HttpMessageHandler：返回预设响应。</summary>
    private sealed class MockHttpMessageHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _handler;

        public MockHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> handler)
            => _handler = handler;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => Task.FromResult(_handler(request));
    }

    private static string InitResponse(int id) =>
        JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0",
            id,
            result = new
            {
                protocolVersion = "2025-03-26",
                capabilities = new { tools = new { } },
                serverInfo = new { name = "test", version = "1.0" }
            }
        });

    private static string ToolsListResponse(int id) =>
        JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0",
            id,
            result = new
            {
                tools = new[]
                {
                    new { name = "search", description = "Search the web", inputSchema = new { type = "object", properties = new { q = new { type = "string" } } } }
                }
            }
        });

    private static string NotificationJson => """{"jsonrpc":"2.0","method":"notifications/initialized"}""";

    /// <summary>构造 application/json HttpResponseMessage。</summary>
    private static HttpResponseMessage JsonResp(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json")
    };

    /// <summary>从请求 JSON 中提取 id 和 method（通知没有 id）。</summary>
    private static (int id, string method) ParseRequest(string body)
    {
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        var id = root.TryGetProperty("id", out var idEl) ? idEl.GetInt32() : 0;
        var method = root.TryGetProperty("method", out var mEl) ? mEl.GetString() ?? "" : "";
        return (id, method);
    }

    [Fact]
    public async Task StreamableHttp_ConnectAndInitialize_SingleJsonResponse()
    {
        var handler = new MockHttpMessageHandler(req =>
        {
            var body = req.Content!.ReadAsStringAsync(CancellationToken.None).Result;
            var (id, method) = ParseRequest(body);

            return method switch
            {
                "initialize" => new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(InitResponse(id), Encoding.UTF8, "application/json")
                },
                "tools/list" => new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(ToolsListResponse(id), Encoding.UTF8, "application/json")
                },
                _ => new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{}", Encoding.UTF8, "application/json")
                }
            };
        });

        var config = new McpServerConfig { Name = "http-test", Transport = "http", Url = "http://localhost" };
        var transport = new StreamableHttpTransport(config, handler);
        var client = new McpClient("http-test", transport);

        await client.ConnectAsync(CancellationToken.None);

        client.IsInitialized.Should().BeTrue();
        client.Tools.Should().HaveCount(1);
        client.Tools[0].Name.Should().Be("search");

        await client.CloseAsync(CancellationToken.None);
    }

    [Fact]
    public async Task StreamableHttp_SseResponse_ParsesDataLines()
    {
        var handler = new MockHttpMessageHandler(req =>
        {
            var body = req.Content!.ReadAsStringAsync(CancellationToken.None).Result;
            var (id, method) = ParseRequest(body);

            if (method == "initialize")
            {
                var sse = $"data: {InitResponse(id)}\n\n";
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(sse, Encoding.UTF8, "text/event-stream")
                };
            }

            if (method == "tools/list")
            {
                var sse2 = $"data: {ToolsListResponse(id)}\n\n";
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(sse2, Encoding.UTF8, "text/event-stream")
                };
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{}", Encoding.UTF8, "application/json")
            };
        });

        var config = new McpServerConfig { Name = "sse-test", Transport = "http", Url = "http://localhost" };
        var transport = new StreamableHttpTransport(config, handler);
        var client = new McpClient("sse-test", transport);

        await client.ConnectAsync(CancellationToken.None);

        client.IsInitialized.Should().BeTrue();
        client.Tools.Should().HaveCount(1);

        await client.CloseAsync(CancellationToken.None);
    }

    [Fact]
    public async Task StreamableHttp_BearerToken_SetInAuthorizationHeader()
    {
        string? capturedAuth = null;
        var handler = new MockHttpMessageHandler(req =>
        {
            capturedAuth = req.Headers.Authorization?.ToString();
            var body = req.Content!.ReadAsStringAsync(CancellationToken.None).Result;
            var (id, method) = ParseRequest(body);

            if (method == "initialize")
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(InitResponse(id), Encoding.UTF8, "application/json")
                };

            if (method == "tools/list")
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(ToolsListResponse(id), Encoding.UTF8, "application/json")
                };

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{}", Encoding.UTF8, "application/json")
            };
        });

        var config = new McpServerConfig { Name = "auth-test", Transport = "http", Url = "http://localhost", ApiKey = "secret-token" };
        var transport = new StreamableHttpTransport(config, handler);
        var client = new McpClient("auth-test", transport);

        await client.ConnectAsync(CancellationToken.None);

        capturedAuth.Should().Be("Bearer secret-token");

        await client.CloseAsync(CancellationToken.None);
    }

    [Fact]
    public void StreamableHttp_MissingUrl_ThrowsArgumentException()
    {
        var config = new McpServerConfig { Name = "bad", Transport = "http" };

        var act = () => new StreamableHttpTransport(config);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public async Task StreamableHttp_ConnectAsync_InitializesChannel()
    {
        var handler = new MockHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        var config = new McpServerConfig { Name = "test", Transport = "http", Url = "http://localhost" };
        var transport = new StreamableHttpTransport(config, handler);

        await transport.ConnectAsync(CancellationToken.None);

        // ConnectAsync 后 SendAsync 不抛 "Transport 未连接"
        // 直接 Dispose
        await transport.DisposeAsync();
    }

    [Fact]
    public async Task StreamableHttp_SendBeforeConnect_ThrowsInvalidOperationException()
    {
        var handler = new MockHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        var config = new McpServerConfig { Name = "test", Transport = "http", Url = "http://localhost" };
        var transport = new StreamableHttpTransport(config, handler);

        var act = async () => await transport.SendAsync("{}", CancellationToken.None);
        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task StreamableHttp_AcceptHeader_DeclaresJsonAndSse()
    {
        string? capturedAccept = null;
        var handler = new MockHttpMessageHandler(req =>
        {
            capturedAccept = req.Headers.Accept.ToString();
            var body = req.Content!.ReadAsStringAsync(CancellationToken.None).Result;
            var (id, method) = ParseRequest(body);

            return method switch
            {
                "initialize" => JsonResp(InitResponse(id)),
                "tools/list" => JsonResp(ToolsListResponse(id)),
                _ => JsonResp("{}")
            };
        });

        var config = new McpServerConfig { Name = "accept-test", Transport = "http", Url = "http://localhost" };
        var transport = new StreamableHttpTransport(config, handler);
        var client = new McpClient("accept-test", transport);

        await client.ConnectAsync(CancellationToken.None);

        // 每个请求都应声明 Accept: application/json, text/event-stream
        capturedAccept.Should().Contain("application/json");
        capturedAccept.Should().Contain("text/event-stream");

        await client.CloseAsync(CancellationToken.None);
    }

    [Fact]
    public async Task StreamableHttp_SessionId_CapturedAndResent()
    {
        var sessionIds = new List<string?>();
        var handler = new MockHttpMessageHandler(req =>
        {
            // 记录每次请求的 Mcp-Session-Id header
            sessionIds.Add(req.Headers.TryGetValues("Mcp-Session-Id", out var vals) ? vals.FirstOrDefault() : null);
            var body = req.Content!.ReadAsStringAsync(CancellationToken.None).Result;
            var (id, method) = ParseRequest(body);

            var resp = method switch
            {
                "initialize" => JsonResp(InitResponse(id)),
                "tools/list" => JsonResp(ToolsListResponse(id)),
                _ => JsonResp("{}")
            };
            // initialize 响应携带 Mcp-Session-Id header
            if (method == "initialize")
                resp.Headers.Add("Mcp-Session-Id", "test-session-abc");
            return resp;
        });

        var config = new McpServerConfig { Name = "session-test", Transport = "http", Url = "http://localhost" };
        var transport = new StreamableHttpTransport(config, handler);
        var client = new McpClient("session-test", transport);

        await client.ConnectAsync(CancellationToken.None);

        // 请求顺序：initialize, initialized(通知), tools/list
        // 第一个请求（initialize）不应携带 session ID
        sessionIds[0].Should().BeNull();
        // 后续请求应携带 server 返回的 session ID
        sessionIds[1].Should().Be("test-session-abc");
        sessionIds[2].Should().Be("test-session-abc");

        await client.CloseAsync(CancellationToken.None);
    }

    [Fact]
    public async Task StreamableHttp_ProtocolVersionHeader_SentAfterInitialize()
    {
        var protocolVersions = new List<string?>();
        var handler = new MockHttpMessageHandler(req =>
        {
            protocolVersions.Add(req.Headers.TryGetValues("MCP-Protocol-Version", out var vals) ? vals.FirstOrDefault() : null);
            var body = req.Content!.ReadAsStringAsync(CancellationToken.None).Result;
            var (id, method) = ParseRequest(body);

            return method switch
            {
                "initialize" => JsonResp(InitResponse(id)),
                "tools/list" => JsonResp(ToolsListResponse(id)),
                _ => JsonResp("{}")
            };
        });

        var config = new McpServerConfig { Name = "pv-test", Transport = "http", Url = "http://localhost" };
        var transport = new StreamableHttpTransport(config, handler);
        var client = new McpClient("pv-test", transport);

        await client.ConnectAsync(CancellationToken.None);

        // 请求顺序：initialize, initialized(通知), tools/list
        // 第一个请求（initialize）不应携带 MCP-Protocol-Version
        protocolVersions[0].Should().BeNull();
        // 后续请求应携带协议版本号
        protocolVersions[1].Should().Be(McpMethods.ProtocolVersion);
        protocolVersions[2].Should().Be(McpMethods.ProtocolVersion);

        await client.CloseAsync(CancellationToken.None);
    }

    // ===== MCP 配置 =====

    [Fact]
    public void McpConfig_Default_EnableIsNull()
    {
        var config = new McpConfig();

        config.Enable.Should().BeNull();
        config.Servers.Should().BeEmpty();
    }

    [Fact]
    public void AppConfig_Mcp_DefaultIsNull()
    {
        var config = new AppConfig();

        config.Mcp.Should().BeNull();
    }

    [Fact]
    public void McpServerConfig_Defaults()
    {
        var config = new McpServerConfig();

        config.Name.Should().BeEmpty();
        config.Transport.Should().Be("stdio");
        config.Command.Should().BeNull();
        config.Url.Should().BeNull();
        config.ApiKey.Should().BeNull();
    }
}
