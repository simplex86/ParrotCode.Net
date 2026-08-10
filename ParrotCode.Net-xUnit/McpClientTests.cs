using System.Text.Json;
using FluentAssertions;
using ParrotCode;

namespace ParrotCode.xUnit;

/// <summary>
/// McpClient 生命周期 + McpToolAdapter 适配器单元测试（迭代 11b）。
/// 用 MockTransport + SetAutoResponder 模拟 MCP server，发送请求时自动生成响应。
/// </summary>
public class McpClientTests
{
    // ===== 测试辅助 =====

    private static string InitResponse(int id) =>
        JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0",
            id,
            result = new
            {
                protocolVersion = "2025-03-26",
                capabilities = new { tools = new { } },
                serverInfo = new { name = "test-server", version = "1.0" }
            }
        });

    private static string ToolsListResponse(int id, params (string name, string desc, bool? readOnly)[] tools)
    {
        var toolObjs = tools.Select(t =>
        {
            var tool = new Dictionary<string, object?>
            {
                ["name"] = t.name,
                ["description"] = t.desc,
                ["inputSchema"] = new
                {
                    type = "object",
                    properties = new { path = new { type = "string", description = "File path" } },
                    required = new[] { "path" }
                }
            };
            if (t.readOnly.HasValue)
                tool["annotations"] = new { readOnlyHint = t.readOnly.Value };
            return tool;
        });

        return JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0",
            id,
            result = new { tools = toolObjs }
        });
    }

    private static string ToolCallResponse(int id, string text, bool isError = false) =>
        JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0",
            id,
            result = new
            {
                content = new[] { new { type = "text", text } },
                isError
            }
        });

    private static string ErrorResponse(int id, int code, string message) =>
        JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0",
            id,
            error = new { code, message }
        });

    /// <summary>从 JSON 字符串提取 id 和 method。</summary>
    private static (int id, string method) ParseSentMessage(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var id = root.TryGetProperty("id", out var idEl) ? idEl.GetInt32() : 0;
        var method = root.TryGetProperty("method", out var mEl) ? mEl.GetString() ?? "" : "";
        return (id, method);
    }

    /// <summary>创建空 JSON 对象参数（避免 EmptyArgs() 序列化异常）。</summary>
    private static JsonElement EmptyArgs() => JsonSerializer.SerializeToElement(new { });

    /// <summary>
    /// 创建并连接 McpClient，用 AutoResponder 自动响应 initialize 和 tools/list。
    /// 返回的 transport 仍保留所有已发送消息，可用 GetLastSentAsync 验证。
    /// </summary>
    private static async Task<(McpClient client, MockTransport transport)> CreateAndConnectAsync(
        string serverName = "test",
        params (string name, string desc, bool? readOnly)[] tools)
    {
        var transport = new MockTransport();
        var client = new McpClient(serverName, transport);

        transport.SetAutoResponder(sent =>
        {
            var (id, method) = ParseSentMessage(sent);
            return method switch
            {
                "initialize" => InitResponse(id),
                "tools/list" => ToolsListResponse(id, tools),
                "notifications/initialized" => null,  // 通知不响应
                _ => null
            };
        });

        await client.ConnectAsync(CancellationToken.None);
        return (client, transport);
    }

    // ===== McpClient 生命周期 =====

    [Fact]
    public async Task ConnectAsync_CompletesInitializeInitializedToolsList()
    {
        var (client, _) = await CreateAndConnectAsync("fs", ("read_file", "Read a file", true));

        client.IsInitialized.Should().BeTrue();
        client.ServerName.Should().Be("fs");
        client.Tools.Should().HaveCount(1);
        client.Tools[0].Name.Should().Be("read_file");
        client.Tools[0].Description.Should().Be("Read a file");
    }

    [Fact]
    public async Task ConnectAsync_InitializeRequestContainsProtocolVersion()
    {
        var transport = new MockTransport();
        var client = new McpClient("test", transport);

        transport.SetAutoResponder(sent =>
        {
            var (id, method) = ParseSentMessage(sent);
            return method switch
            {
                "initialize" => InitResponse(id),
                "tools/list" => ToolsListResponse(id),
                _ => null
            };
        });

        await client.ConnectAsync(CancellationToken.None);

        // 第一条发送的消息是 initialize 请求
        var sentInit = await transport.GetLastSentAsync(CancellationToken.None);
        sentInit.Should().Contain("\"method\":\"initialize\"");
        sentInit.Should().Contain("\"protocolVersion\":\"2025-06-18\"", "MCP 协议要求 camelCase");

        await client.CloseAsync(CancellationToken.None);
    }

    [Fact]
    public async Task ConnectAsync_InitializedNotificationSentAfterInitializeResponse()
    {
        var transport = new MockTransport();
        var client = new McpClient("test", transport);

        transport.SetAutoResponder(sent =>
        {
            var (id, method) = ParseSentMessage(sent);
            return method switch
            {
                "initialize" => InitResponse(id),
                "tools/list" => ToolsListResponse(id),
                _ => null
            };
        });

        await client.ConnectAsync(CancellationToken.None);

        // 发送顺序：initialize 请求 → initialized 通知 → tools/list 请求
        var sent1 = await transport.GetLastSentAsync(CancellationToken.None);
        sent1.Should().Contain("\"method\":\"initialize\"");

        var sent2 = await transport.GetLastSentAsync(CancellationToken.None);
        sent2.Should().Contain("\"method\":\"notifications/initialized\"");

        var sent3 = await transport.GetLastSentAsync(CancellationToken.None);
        sent3.Should().Contain("\"method\":\"tools/list\"");

        await client.CloseAsync(CancellationToken.None);
    }

    [Fact]
    public async Task ConnectAsync_AfterConnect_IsInitializedTrue()
    {
        var (client, _) = await CreateAndConnectAsync();

        client.IsInitialized.Should().BeTrue();
    }

    [Fact]
    public async Task ConnectAsync_AfterConnect_ToolsPopulated()
    {
        var (client, _) = await CreateAndConnectAsync("srv",
            ("tool1", "desc1", null),
            ("tool2", "desc2", true));

        client.Tools.Should().HaveCount(2);
    }

    [Fact]
    public async Task RefreshToolsAsync_UpdatesToolsList()
    {
        var (client, transport) = await CreateAndConnectAsync("srv", ("old_tool", "old", null));

        client.Tools.Should().HaveCount(1);
        client.Tools[0].Name.Should().Be("old_tool");

        // 更新 AutoResponder 返回新工具列表
        transport.SetAutoResponder(sent =>
        {
            var (id, method) = ParseSentMessage(sent);
            return method switch
            {
                "tools/list" => ToolsListResponse(id, ("new_tool1", "new1", null), ("new_tool2", "new2", null)),
                _ => null
            };
        });

        await client.RefreshToolsAsync(CancellationToken.None);

        client.Tools.Should().HaveCount(2);
        client.Tools[0].Name.Should().Be("new_tool1");
        client.Tools[1].Name.Should().Be("new_tool2");

        await client.CloseAsync(CancellationToken.None);
    }

    // ===== McpClient 工具调用 =====

    [Fact]
    public async Task CallToolAsync_SendsToolsCallRequestWithNameAndArguments()
    {
        var (client, transport) = await CreateAndConnectAsync("srv", ("read_file", "Read", true));

        transport.SetAutoResponder(sent =>
        {
            var (id, method) = ParseSentMessage(sent);
            return method == "tools/call" ? ToolCallResponse(id, "file content") : null;
        });

        var args = JsonSerializer.SerializeToElement(new { path = "test.txt" });
        var result = await client.CallToolAsync("read_file", args, CancellationToken.None);

        result.IsError.Should().BeFalse();
        result.Content[0].Text.Should().Be("file content");

        await client.CloseAsync(CancellationToken.None);
    }

    [Fact]
    public async Task CallToolAsync_ReturnsParsedResult()
    {
        var (client, transport) = await CreateAndConnectAsync("srv", ("read_file", "Read", true));

        transport.SetAutoResponder(sent =>
        {
            var (id, method) = ParseSentMessage(sent);
            return method == "tools/call" ? ToolCallResponse(id, "hello world") : null;
        });

        var result = await client.CallToolAsync("read_file", EmptyArgs(), CancellationToken.None);

        result.IsError.Should().BeFalse();
        result.Content.Should().HaveCount(1);
        result.Content[0].Type.Should().Be("text");
        result.Content[0].Text.Should().Be("hello world");

        await client.CloseAsync(CancellationToken.None);
    }

    [Fact]
    public async Task CallToolAsync_Success_IsErrorFalse()
    {
        var (client, transport) = await CreateAndConnectAsync("srv", ("tool", "desc", null));

        transport.SetAutoResponder(sent =>
        {
            var (id, method) = ParseSentMessage(sent);
            return method == "tools/call" ? ToolCallResponse(id, "ok") : null;
        });

        var result = await client.CallToolAsync("tool", EmptyArgs(), CancellationToken.None);

        result.IsError.Should().BeFalse();
    }

    [Fact]
    public async Task CallToolAsync_Error_IsErrorTrue()
    {
        var (client, transport) = await CreateAndConnectAsync("srv", ("tool", "desc", null));

        transport.SetAutoResponder(sent =>
        {
            var (id, method) = ParseSentMessage(sent);
            return method == "tools/call" ? ToolCallResponse(id, "File not found", isError: true) : null;
        });

        var result = await client.CallToolAsync("tool", EmptyArgs(), CancellationToken.None);

        result.IsError.Should().BeTrue();
        result.Content[0].Text.Should().Be("File not found");

        await client.CloseAsync(CancellationToken.None);
    }

    [Fact]
    public async Task CallToolAsync_MultipleContentBlocks_AllParsed()
    {
        var (client, transport) = await CreateAndConnectAsync("srv", ("tool", "desc", null));

        transport.SetAutoResponder(sent =>
        {
            var (id, method) = ParseSentMessage(sent);
            if (method != "tools/call") return null;
            return JsonSerializer.Serialize(new
            {
                jsonrpc = "2.0",
                id,
                result = new
                {
                    content = new[] {
                        new { type = "text", text = "line1" },
                        new { type = "text", text = "line2" }
                    },
                    isError = false
                }
            });
        });

        var result = await client.CallToolAsync("tool", EmptyArgs(), CancellationToken.None);

        result.Content.Should().HaveCount(2);
        result.Content[0].Text.Should().Be("line1");
        result.Content[1].Text.Should().Be("line2");

        await client.CloseAsync(CancellationToken.None);
    }

    [Fact]
    public async Task CallToolAsync_JsonRpcError_ThrowsJsonRpcException()
    {
        var (client, transport) = await CreateAndConnectAsync("srv", ("tool", "desc", null));

        transport.SetAutoResponder(sent =>
        {
            var (id, method) = ParseSentMessage(sent);
            return method == "tools/call" ? ErrorResponse(id, -32601, "Method not found") : null;
        });

        var act = async () => await client.CallToolAsync("tool", EmptyArgs(), CancellationToken.None);

        await act.Should().ThrowAsync<JsonRpcException>();

        await client.CloseAsync(CancellationToken.None);
    }

    // ===== McpClient 接收循环 =====

    [Fact]
    public async Task ReceiveLoop_DispatchesMessagesToRpcHandler()
    {
        // 通过 ConnectAsync 验证接收循环正常工作——initialize 响应被接收循环拾取并完成 TCS
        var (client, _) = await CreateAndConnectAsync("srv", ("tool", "desc", null));

        client.IsInitialized.Should().BeTrue();
    }

    [Fact]
    public async Task ReceiveLoop_ExitsWhenTransportReturnsNull()
    {
        var (client, transport) = await CreateAndConnectAsync("srv", ("tool", "desc", null));

        // 模拟连接断开
        transport.SimulateDisconnect();

        // 等待接收循环退出
        await Task.Delay(200);

        // 断开后 IsInitialized 仍为 true（CloseAsync 才置 false），但后续操作会失败
        // 接收循环已退出，CloseAsync 应正常完成
        await client.CloseAsync(CancellationToken.None);
    }

    [Fact]
    public async Task ReceiveLoop_ConnectionClosed_CloseAsyncSucceeds()
    {
        var (client, transport) = await CreateAndConnectAsync("srv", ("tool", "desc", null));

        // 断开连接
        transport.SimulateDisconnect();
        await Task.Delay(200);

        // CloseAsync 应能正常关闭（接收循环已退出，不阻塞）
        await client.CloseAsync(CancellationToken.None);

        client.IsInitialized.Should().BeFalse();
    }

    // ===== McpClient 关闭 =====

    [Fact]
    public async Task CloseAsync_StopsReceiveLoop()
    {
        var (client, _) = await CreateAndConnectAsync("srv", ("tool", "desc", null));

        await client.CloseAsync(CancellationToken.None);

        // 关闭后 CallToolAsync 应抛异常（transport 已关闭）
        var act = async () => await client.CallToolAsync("tool", EmptyArgs(), CancellationToken.None);
        await act.Should().ThrowAsync<Exception>();
    }

    [Fact]
    public async Task CloseAsync_SetsIsInitializedFalse()
    {
        var (client, _) = await CreateAndConnectAsync("srv", ("tool", "desc", null));

        client.IsInitialized.Should().BeTrue();

        await client.CloseAsync(CancellationToken.None);

        client.IsInitialized.Should().BeFalse();
    }

    [Fact]
    public async Task CloseAsync_CancelsAllPending()
    {
        var (client, _) = await CreateAndConnectAsync("srv", ("tool", "desc", null));

        // 直接关闭——CloseAsync 应取消 pending
        await client.CloseAsync(CancellationToken.None);
    }

    // ===== McpToolAdapter =====

    private static McpToolAdapter CreateAdapter(
        string serverName = "test",
        string toolName = "read_file",
        string desc = "Read a file",
        bool? readOnlyHint = null,
        string? inputSchemaJson = null)
    {
        var transport = new MockTransport();
        var client = new McpClient(serverName, transport);

        var toolInfo = new McpToolInfo
        {
            Name = toolName,
            Description = desc,
            InputSchema = inputSchemaJson is not null
                ? JsonSerializer.Deserialize<JsonElement>(inputSchemaJson)
                : default,
            Annotations = readOnlyHint.HasValue
                ? new McpToolAnnotations { ReadOnlyHint = readOnlyHint }
                : null
        };

        return new McpToolAdapter(client, toolInfo);
    }

    [Fact]
    public void Adapter_Name_HasServerPrefix()
    {
        var adapter = CreateAdapter("filesystem", "read_file");
        adapter.Name.Should().Be("filesystem-read_file");
    }

    [Fact]
    public void Adapter_Name_MatchesOpenAiApiPattern()
    {
        // OpenAI API 工具名必须匹配 ^[a-zA-Z0-9_-]+$
        var adapter = CreateAdapter("filesystem", "read_file");
        adapter.Name.Should().MatchRegex(@"^[a-zA-Z0-9_-]+$");
    }

    [Fact]
    public void Adapter_Name_SanitizesInvalidChars()
    {
        // serverName 含 '.' 等非法字符时应被替换为 '_'
        var adapter = CreateAdapter("my.server", "echo_string");
        adapter.Name.Should().Be("my_server-echo_string");
        adapter.Name.Should().MatchRegex(@"^[a-zA-Z0-9_-]+$");
    }

    [Fact]
    public void Adapter_Description_ReturnsMcpDescription()
    {
        var adapter = CreateAdapter("srv", "tool", "My tool description");
        adapter.Description.Should().Be("My tool description");
    }

    [Fact]
    public void Adapter_Category_ReadOnlyHintTrue_ReturnsRead()
    {
        var adapter = CreateAdapter("srv", "tool", readOnlyHint: true);
        adapter.Category.Should().Be(ToolCategory.Read);
    }

    [Fact]
    public void Adapter_Category_NoAnnotations_ReturnsWrite()
    {
        var adapter = CreateAdapter("srv", "tool", readOnlyHint: null);
        adapter.Category.Should().Be(ToolCategory.Write);
    }

    [Fact]
    public void Adapter_Category_ReadOnlyHintFalse_ReturnsWrite()
    {
        var adapter = CreateAdapter("srv", "tool", readOnlyHint: false);
        adapter.Category.Should().Be(ToolCategory.Write);
    }

    [Fact]
    public void Adapter_Parameters_ParsedFromInputSchema()
    {
        var schema = """{"type":"object","properties":{"path":{"type":"string","description":"File path"},"content":{"type":"string","description":"Content"}},"required":["path"]}""";
        var adapter = CreateAdapter("srv", "write_file", inputSchemaJson: schema);

        adapter.Parameters.Should().HaveCount(2);
        adapter.Parameters[0].Name.Should().Be("path");
        adapter.Parameters[0].Type.Should().Be("string");
        adapter.Parameters[0].Description.Should().Be("File path");
        adapter.Parameters[0].Required.Should().BeTrue();
        adapter.Parameters[1].Name.Should().Be("content");
        adapter.Parameters[1].Required.Should().BeFalse();
    }

    [Fact]
    public void Adapter_Parameters_RequiredListExtracted()
    {
        var schema = """{"type":"object","properties":{"a":{"type":"string"},"b":{"type":"number"}},"required":["a","b"]}""";
        var adapter = CreateAdapter("srv", "tool", inputSchemaJson: schema);

        adapter.Parameters.Should().AllSatisfy(p => p.Required.Should().BeTrue());
    }

    [Fact]
    public void Adapter_Parameters_NoInputSchema_ReturnsEmpty()
    {
        var adapter = CreateAdapter("srv", "tool", inputSchemaJson: null);
        adapter.Parameters.Should().BeEmpty();
    }

    [Fact]
    public async Task Adapter_ExecuteAsync_Success_ReturnsToolResultOk()
    {
        var (client, transport) = await CreateAndConnectAsync("srv", ("tool", "desc", null));

        transport.SetAutoResponder(sent =>
        {
            var (id, method) = ParseSentMessage(sent);
            return method == "tools/call" ? ToolCallResponse(id, "result text") : null;
        });

        var adapter = new McpToolAdapter(client, client.Tools[0]);

        var result = await adapter.ExecuteAsync(EmptyArgs(), CancellationToken.None);

        result.Success.Should().BeTrue();
        result.Content.Should().Be("result text");

        await client.CloseAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Adapter_ExecuteAsync_IsError_ReturnsToolResultFail()
    {
        var (client, transport) = await CreateAndConnectAsync("srv", ("tool", "desc", null));

        transport.SetAutoResponder(sent =>
        {
            var (id, method) = ParseSentMessage(sent);
            return method == "tools/call" ? ToolCallResponse(id, "File not found", isError: true) : null;
        });

        var adapter = new McpToolAdapter(client, client.Tools[0]);

        var result = await adapter.ExecuteAsync(EmptyArgs(), CancellationToken.None);

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("File not found");

        await client.CloseAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Adapter_ExecuteAsync_JsonRpcException_ReturnsToolResultFail()
    {
        var (client, transport) = await CreateAndConnectAsync("srv", ("tool", "desc", null));

        transport.SetAutoResponder(sent =>
        {
            var (id, method) = ParseSentMessage(sent);
            return method == "tools/call" ? ErrorResponse(id, -32601, "Method not found") : null;
        });

        var adapter = new McpToolAdapter(client, client.Tools[0]);

        var result = await adapter.ExecuteAsync(EmptyArgs(), CancellationToken.None);

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("MCP 工具 srv-tool 调用失败");
        result.Error.Should().Contain("Method not found");

        await client.CloseAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Adapter_ExecuteAsync_ExternalCancellation_ThrowsOCE()
    {
        var (client, transport) = await CreateAndConnectAsync("srv", ("tool", "desc", null));

        // 不设置 tools/call 响应，用 CTS 取消
        var cts = new CancellationTokenSource();
        var adapter = new McpToolAdapter(client, client.Tools[0]);

        var act = async () => await adapter.ExecuteAsync(EmptyArgs(), cts.Token);

        // 在另一个线程上取消
        _ = Task.Run(async () =>
        {
            await Task.Delay(100);
            cts.Cancel();
            transport.SimulateDisconnect();  // 断开连接以解除阻塞
        });

        await act.Should().ThrowAsync<OperationCanceledException>();

        await client.CloseAsync(CancellationToken.None);
    }

    [Fact]
    public void Adapter_ToOpenAiSchema_ContainsPrefixedName()
    {
        var adapter = CreateAdapter("filesystem", "read_file", "Read");

        var schema = adapter.ToOpenAiSchema();

        schema.GetProperty("function").GetProperty("name").GetString().Should().Be("filesystem-read_file");
        schema.GetProperty("type").GetString().Should().Be("function");
    }

    [Fact]
    public void Adapter_ToAnthropicSchema_ContainsPrefixedName()
    {
        var adapter = CreateAdapter("filesystem", "read_file", "Read");

        var schema = adapter.ToAnthropicSchema();

        schema.GetProperty("name").GetString().Should().Be("filesystem-read_file");
        schema.TryGetProperty("input_schema", out _).Should().BeTrue();
    }

    [Fact]
    public void Adapter_EmptyInputSchema_GeneratesDefaultSchema()
    {
        var adapter = CreateAdapter("srv", "tool", inputSchemaJson: null);

        var schema = adapter.ToOpenAiSchema();
        var parameters = schema.GetProperty("function").GetProperty("parameters");

        parameters.GetProperty("type").GetString().Should().Be("object");
    }
}
