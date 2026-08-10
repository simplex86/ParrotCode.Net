using System.Text.Json;
using FluentAssertions;
using ParrotCode;

namespace ParrotCode.xUnit;

/// <summary>
/// JsonRpc 编解码 + id 匹配单元测试（迭代 11a）。
/// 覆盖 CreateRequest/CreateNotification/HandleMessage/并发/CancelAllPending/JsonRpcException。
/// </summary>
public class McpProtocolTests
{
    /// <summary>创建带辅助方法的测试基类。</summary>
    private static JsonRpc CreateRpc() => new();

    private static string MakeResponse(int id, object? result = null, object? error = null)
    {
        if (error is not null)
        {
            return $$"""{"jsonrpc":"2.0","id":{{id}},"error":{{JsonSerializer.Serialize(error)}}}""";
        }
        var resultJson = result is null ? "null" : JsonSerializer.Serialize(result);
        return $$"""{"jsonrpc":"2.0","id":{{id}},"result":{{resultJson}}}""";
    }

    // ===== CreateRequest =====

    [Fact]
    public void CreateRequest_GeneratesValidJsonRpcRequest()
    {
        var rpc = CreateRpc();
        var (json, _) = rpc.CreateRequest("initialize", new { protocolVersion = "2025-03-26" });

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        root.GetProperty("jsonrpc").GetString().Should().Be("2.0");
        root.GetProperty("method").GetString().Should().Be("initialize");
        root.GetProperty("id").GetInt32().Should().BeGreaterThan(0);
        root.TryGetProperty("params", out _).Should().BeTrue();
    }

    [Fact]
    public void CreateRequest_IdAutoIncrementsAndIsUnique()
    {
        var rpc = CreateRpc();
        var (_, task1) = rpc.CreateRequest("method1");
        var (_, task2) = rpc.CreateRequest("method2");
        var (_, task3) = rpc.CreateRequest("method3");

        // 三个请求的 id 应递增（通过响应匹配验证唯一性）
        rpc.HandleMessage(MakeResponse(1));
        rpc.HandleMessage(MakeResponse(2));
        rpc.HandleMessage(MakeResponse(3));

        task1.IsCompleted.Should().BeTrue();
        task2.IsCompleted.Should().BeTrue();
        task3.IsCompleted.Should().BeTrue();
    }

    [Fact]
    public void CreateRequest_ParamsNull_ContainsNullParamsField()
    {
        var rpc = CreateRpc();
        var (json, _) = rpc.CreateRequest("ping", null);

        using var doc = JsonDocument.Parse(json);
        doc.RootElement.TryGetProperty("params", out var paramsEl).Should().BeTrue();
        paramsEl.ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public void CreateRequest_ReturnsTaskAwaitingResponse()
    {
        var rpc = CreateRpc();
        var (_, task) = rpc.CreateRequest("test");

        task.IsCompleted.Should().BeFalse();
    }

    // ===== CreateNotification =====

    [Fact]
    public void CreateNotification_GeneratesRequestWithoutId()
    {
        var rpc = CreateRpc();
        var json = rpc.CreateNotification("notifications/initialized");

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        root.GetProperty("jsonrpc").GetString().Should().Be("2.0");
        root.GetProperty("method").GetString().Should().Be("notifications/initialized");
        root.TryGetProperty("id", out _).Should().BeFalse();
    }

    [Fact]
    public void CreateNotification_WithParams_ContainsParamsField()
    {
        var rpc = CreateRpc();
        var json = rpc.CreateNotification("progress", new { percent = 50 });

        using var doc = JsonDocument.Parse(json);
        doc.RootElement.TryGetProperty("params", out _).Should().BeTrue();
    }

    // ===== HandleMessage 响应匹配 =====

    [Fact]
    public async Task HandleMessage_MatchesResponseByIdAndSetsResult()
    {
        var rpc = CreateRpc();
        var (_, task) = rpc.CreateRequest("initialize");

        rpc.HandleMessage(MakeResponse(1, new { protocolVersion = "2025-03-26" }));

        task.IsCompleted.Should().BeTrue();
        var result = await task;
        result.GetProperty("protocolVersion").GetString().Should().Be("2025-03-26");
    }

    [Fact]
    public async Task HandleMessage_ErrorResponse_ThrowsJsonRpcException()
    {
        var rpc = CreateRpc();
        var (_, task) = rpc.CreateRequest("unknown_method");

        rpc.HandleMessage(MakeResponse(1, error: new { code = -32601, message = "Method not found" }));

        task.IsFaulted.Should().BeTrue();
        var ex = await FluentActions.Awaiting(() => task).Should().ThrowAsync<JsonRpcException>();
        ex.Which.Code.Should().Be(-32601);
        ex.Which.Message.Should().Contain("Method not found");
    }

    [Fact]
    public void HandleMessage_ResponseMissingResultAndError_ThrowsException()
    {
        var rpc = CreateRpc();
        var (_, task) = rpc.CreateRequest("test");

        // 响应只有 id，没有 result 也没有 error
        rpc.HandleMessage("""{"jsonrpc":"2.0","id":1}""");

        task.IsFaulted.Should().BeTrue();
        task.Exception!.InnerException.Should().BeOfType<JsonRpcException>();
    }

    [Fact]
    public void HandleMessage_UnknownId_LogsWarningDoesNotCrash()
    {
        var rpc = CreateRpc();
        // 没有待匹配的请求，收到 id=999 的响应
        var act = () => rpc.HandleMessage(MakeResponse(999, new { ok = true }));

        act.Should().NotThrow();
    }

    [Fact]
    public async Task HandleMessage_ConcurrentRequests_MatchCorrectResponses()
    {
        var rpc = CreateRpc();
        var tasks = new List<Task<JsonElement>>();
        for (var i = 0; i < 5; i++)
        {
            var (_, task) = rpc.CreateRequest($"method_{i}");
            tasks.Add(task);
        }

        // 乱序响应
        rpc.HandleMessage(MakeResponse(3, new { order = 3 }));
        rpc.HandleMessage(MakeResponse(1, new { order = 1 }));
        rpc.HandleMessage(MakeResponse(5, new { order = 5 }));
        rpc.HandleMessage(MakeResponse(2, new { order = 2 }));
        rpc.HandleMessage(MakeResponse(4, new { order = 4 }));

        for (var i = 0; i < 5; i++)
        {
            tasks[i].IsCompleted.Should().BeTrue();
            (await tasks[i]).GetProperty("order").GetInt32().Should().Be(i + 1);
        }
    }

    // ===== HandleMessage 通知处理 =====

    [Fact]
    public void HandleMessage_NotificationDoesNotTriggerAnyTcs()
    {
        var rpc = CreateRpc();
        var (_, task) = rpc.CreateRequest("test");

        // 通知（有 method 无 id）不应触发任何 TCS
        rpc.HandleMessage("""{"jsonrpc":"2.0","method":"notifications/initialized"}""");

        task.IsCompleted.Should().BeFalse();
    }

    [Fact]
    public void HandleMessage_NotificationDoesNotCrash()
    {
        var rpc = CreateRpc();
        var act = () => rpc.HandleMessage("""{"jsonrpc":"2.0","method":"tools/list_changed"}""");

        act.Should().NotThrow();
    }

    // ===== CancelAllPending =====

    [Fact]
    public void CancelAllPending_AllPendingTasksThrowException()
    {
        var rpc = CreateRpc();
        var (_, task1) = rpc.CreateRequest("m1");
        var (_, task2) = rpc.CreateRequest("m2");
        var (_, task3) = rpc.CreateRequest("m3");

        rpc.CancelAllPending();

        task1.IsFaulted.Should().BeTrue();
        task2.IsFaulted.Should().BeTrue();
        task3.IsFaulted.Should().BeTrue();
    }

    [Fact]
    public void CancelAllPending_ExceptionMessageContainsConnectionClosed()
    {
        var rpc = CreateRpc();
        var (_, task) = rpc.CreateRequest("test");

        rpc.CancelAllPending();

        task.Exception!.InnerException!.Message.Should().Contain("连接已关闭");
    }

    [Fact]
    public async Task CancelAllPending_AfterCancel_NewResponseDoesNotMatch()
    {
        var rpc = CreateRpc();
        var (_, task) = rpc.CreateRequest("test");

        rpc.CancelAllPending();

        // CancelAllPending 后再收到对应 id 的响应，不应崩溃（TryRemove 返回 false）
        var act = () => rpc.HandleMessage(MakeResponse(1, new { ok = true }));
        act.Should().NotThrow();

        await Task.CompletedTask;
        task.IsFaulted.Should().BeTrue();
    }

    [Fact]
    public void CancelAllPending_WhenNoPending_DoesNotCrash()
    {
        var rpc = CreateRpc();
        var act = () => rpc.CancelAllPending();
        act.Should().NotThrow();
    }

    // ===== JsonRpcException =====

    [Fact]
    public void JsonRpcException_ContainsCodeAndMessage()
    {
        var ex = new JsonRpcException(-32601, "Method not found");

        ex.Code.Should().Be(-32601);
        ex.Message.Should().Contain("-32601");
        ex.Message.Should().Contain("Method not found");
    }
}
