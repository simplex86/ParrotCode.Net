using ParrotCode;
using FluentAssertions;
using Xunit;
using Microsoft.Extensions.Logging.Abstractions;
using System.Net;

namespace ParrotCode.xUnit;

public class ActionExecutorTests
{
    private readonly ActionExecutor _executor = new(logger: NullLogger.Instance);

    private static Dictionary<string, object?> Ctx(params (string, object?)[] pairs)
        => pairs.ToDictionary(p => p.Item1, p => p.Item2);

    private static string SlowCommand(int seconds) =>
        OperatingSystem.IsWindows()
            ? $"ping -n {seconds + 1} 127.0.0.1 > nul"
            : $"sleep {seconds}";

    private static string StderrCommand() =>
        OperatingSystem.IsWindows()
            ? "echo err 1>&2"
            : "echo err >&2";

    [Fact]
    public async Task Shell_Executes_Success()
    {
        var action = new HookAction { ActionType = HookActionType.Shell, Command = "echo hello" };
        var result = await _executor.ExecuteAsync(action, Ctx());
        result.Should().NotBeNull();
        result!.Trim().Should().Be("hello");
    }

    [Fact]
    public async Task Shell_Timeout_Returns_TimeoutMessage()
    {
        var action = new HookAction { ActionType = HookActionType.Shell, Command = SlowCommand(5) };
        var result = await _executor.ExecuteAsync(action, Ctx(), timeoutSeconds: 1);
        result.Should().NotBeNull();
        result.Should().Contain("超时");
    }

    [Fact]
    public async Task Shell_Stderr_Captured()
    {
        var action = new HookAction { ActionType = HookActionType.Shell, Command = StderrCommand() };
        var result = await _executor.ExecuteAsync(action, Ctx());
        result.Should().NotBeNull();
        result.Should().Contain("err");
    }

    [Fact]
    public async Task Shell_LongOutput_Truncated()
    {
        // Generate > 2000 chars
        var count = 500;
        var action = new HookAction
        {
            ActionType = HookActionType.Shell,
            Command = OperatingSystem.IsWindows()
                ? $"powershell -Command \"Write-Output ('x' * {count * 5})\""
                : $"python3 -c \"print('x' * {count * 5})\""
        };
        var result = await _executor.ExecuteAsync(action, Ctx());
        result.Should().NotBeNull();
        result.Should().Contain("（截断）");
    }

    [Fact]
    public async Task Shell_EmptyCommand_Returns_Null()
    {
        var action = new HookAction { ActionType = HookActionType.Shell, Command = "" };
        var result = await _executor.ExecuteAsync(action, Ctx());
        result.Should().BeNull();
    }

    [Fact]
    public async Task Shell_CrossPlatform()
    {
        var action = new HookAction { ActionType = HookActionType.Shell, Command = "echo cross" };
        var result = await _executor.ExecuteAsync(action, Ctx());
        result.Should().NotBeNull();
        result!.Trim().Should().Be("cross");
    }

    [Fact]
    public async Task PromptInject_Renders_Template()
    {
        var action = new HookAction
        {
            ActionType = HookActionType.PromptInject,
            Text = "拒绝 {{tool_name}}"
        };
        var result = await _executor.ExecuteAsync(action, Ctx(("tool_name", "write_file")));
        result.Should().Be("拒绝 write_file");
    }

    [Fact]
    public async Task Http_Post_SendsRequest()
    {
        var handler = new MockHttpHandler("ok");
        var executor = new ActionExecutor(handler: handler, logger: NullLogger.Instance);
        var action = new HookAction
        {
            ActionType = HookActionType.Http,
            Url = "http://test.local/api",
            Method = "POST",
            Body = "{\"key\":\"value\"}"
        };
        var result = await executor.ExecuteAsync(action, Ctx());
        result.Should().Be("ok");
        handler.LastRequest.Should().NotBeNull();
        handler.LastRequest!.Method.Method.Should().Be("POST");
    }

    [Fact]
    public async Task Http_Get_SendsRequest()
    {
        var handler = new MockHttpHandler("get-ok");
        var executor = new ActionExecutor(handler: handler, logger: NullLogger.Instance);
        var action = new HookAction
        {
            ActionType = HookActionType.Http,
            Url = "http://test.local/api",
            Method = "GET"
        };
        var result = await executor.ExecuteAsync(action, Ctx());
        result.Should().Be("get-ok");
        handler.LastRequest!.Method.Method.Should().Be("GET");
    }

    [Fact]
    public async Task Http_Headers_Sent()
    {
        var handler = new MockHttpHandler("ok");
        var executor = new ActionExecutor(handler: handler, logger: NullLogger.Instance);
        var action = new HookAction
        {
            ActionType = HookActionType.Http,
            Url = "http://test.local/api",
            Method = "POST",
            Headers = new() { ["X-Api-Key"] = "secret" }
        };
        await executor.ExecuteAsync(action, Ctx());
        handler.LastRequest!.Headers.Contains("X-Api-Key").Should().BeTrue();
    }

    [Fact]
    public async Task Http_Body_TemplateRendered()
    {
        var handler = new MockHttpHandler("ok");
        var executor = new ActionExecutor(handler: handler, logger: NullLogger.Instance);
        var action = new HookAction
        {
            ActionType = HookActionType.Http,
            Url = "http://test.local/api",
            Method = "POST",
            Body = "{\"tool\":\"{{tool_name}}\"}"
        };
        await executor.ExecuteAsync(action, Ctx(("tool_name", "write_file")));
        handler.LastRequestBody.Should().Be("{\"tool\":\"write_file\"}");
    }

    [Fact]
    public async Task Http_Timeout_Returns_TimeoutMessage()
    {
        var handler = new MockHttpHandler("slow", delay: TimeSpan.FromSeconds(5));
        var executor = new ActionExecutor(handler: handler, logger: NullLogger.Instance);
        var action = new HookAction
        {
            ActionType = HookActionType.Http,
            Url = "http://test.local/slow",
            Method = "GET"
        };
        var result = await executor.ExecuteAsync(action, Ctx(), timeoutSeconds: 1);
        result.Should().NotBeNull();
        result.Should().Contain("超时");
    }

    [Fact]
    public async Task Http_LongResponse_Truncated()
    {
        var longBody = new string('x', 3000);
        var handler = new MockHttpHandler(longBody);
        var executor = new ActionExecutor(handler: handler, logger: NullLogger.Instance);
        var action = new HookAction
        {
            ActionType = HookActionType.Http,
            Url = "http://test.local/api",
            Method = "GET"
        };
        var result = await executor.ExecuteAsync(action, Ctx());
        result.Should().Contain("（截断）");
    }

    [Fact]
    public async Task SubAgent_NotInjected_Returns_Null()
    {
        var action = new HookAction
        {
            ActionType = HookActionType.SubAgent,
            Task = "summarize"
        };
        var result = await _executor.ExecuteAsync(action, Ctx());
        result.Should().BeNull();
    }

    [Fact]
    public async Task ActionException_Returns_Null()
    {
        // http with invalid URL format triggers exception
        var action = new HookAction
        {
            ActionType = HookActionType.Http,
            Url = "not-a-valid-url",
            Method = "GET"
        };
        var result = await _executor.ExecuteAsync(action, Ctx());
        result.Should().BeNull();
    }
}
