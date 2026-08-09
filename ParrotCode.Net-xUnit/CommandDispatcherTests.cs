using ParrotCode;

namespace ParrotCode.xUnit;

/// <summary>
/// CommandDispatcher 单元测试（迭代 10a）。
/// 覆盖分发/未注册命令/异常处理/取消。
/// </summary>
public class CommandDispatcherTests
{
    private sealed class TestCommand : ICommand
    {
        public string Name => "test";
        public string Description => "test desc";
        public CommandType Type => CommandType.System;
        public IReadOnlyList<string> Aliases => Array.Empty<string>();
        public string Usage => "/test";
        public Func<CommandContext, CommandResult>? Handler { get; set; }

        public Task<CommandResult> ExecuteAsync(CommandContext context)
            => Task.FromResult(Handler?.Invoke(context) ?? CommandResult.Ok);
    }

    private sealed class ThrowingCommand : ICommand
    {
        public string Name => "throw";
        public string Description => "always throws";
        public CommandType Type => CommandType.System;
        public IReadOnlyList<string> Aliases => Array.Empty<string>();
        public string Usage => "/throw";

        public Task<CommandResult> ExecuteAsync(CommandContext context)
            => throw new InvalidOperationException("boom");
    }

    private sealed class CancelAwareCommand : ICommand
    {
        public string Name => "cancel";
        public string Description => "respects cancellation";
        public CommandType Type => CommandType.System;
        public IReadOnlyList<string> Aliases => Array.Empty<string>();
        public string Usage => "/cancel";

        public Task<CommandResult> ExecuteAsync(CommandContext context)
        {
            context.Ct.ThrowIfCancellationRequested();
            return Task.FromResult(CommandResult.Ok);
        }
    }

    private static CommandContext CreateContext()
    {
        var history = new ConversationHistory();
        var secCtx = new SecurityContext { ProjectRoot = Path.GetTempPath() };
        var guard = new SecurityGuard(secCtx, SecurityLevel.Normal);
        return new CommandContext(history, null, guard, new MockUiControl(), CancellationToken.None)
        {
            ProviderConfig = new ProviderConfig { Name = "test", Protocol = "mock", Model = "test-model" },
            TuiConfig = new TuiConfig(),
            AgentConfig = new AgentConfig(),
        };
    }

    [Fact]
    public async Task DispatchAsync_NonSlashPrefix_ReturnsNotHandled()
    {
        var registry = new CommandRegistry();
        var dispatcher = new CommandDispatcher(registry);
        var ctx = CreateContext();

        var result = await dispatcher.DispatchAsync("hello world", ctx, CancellationToken.None);

        result.Handled.Should().BeFalse();
    }

    [Fact]
    public async Task DispatchAsync_RegisteredCommand_ExecutesAndReturnsResult()
    {
        var registry = new CommandRegistry();
        var cmd = new TestCommand { Handler = _ => CommandResult.WithOutput("test output") };
        registry.Register(cmd);
        var dispatcher = new CommandDispatcher(registry);
        var ctx = CreateContext();

        var result = await dispatcher.DispatchAsync("/test", ctx, CancellationToken.None);

        result.Handled.Should().BeTrue();
        result.Output.Should().Be("test output");
    }

    [Fact]
    public async Task DispatchAsync_UnknownCommand_ReturnsErrorMessage()
    {
        var registry = new CommandRegistry();
        var dispatcher = new CommandDispatcher(registry);
        var ctx = CreateContext();

        var result = await dispatcher.DispatchAsync("/nonexistent", ctx, CancellationToken.None);

        result.Handled.Should().BeTrue();
        result.Output.Should().Contain("未知命令");
        result.Output.Should().Contain("/help");
    }

    [Fact]
    public async Task DispatchAsync_CommandThrows_ReturnsErrorMessageWithoutCrashing()
    {
        var registry = new CommandRegistry();
        registry.Register(new ThrowingCommand());
        var dispatcher = new CommandDispatcher(registry);
        var ctx = CreateContext();

        var result = await dispatcher.DispatchAsync("/throw", ctx, CancellationToken.None);

        result.Handled.Should().BeTrue();
        result.Output.Should().Contain("[!]");
        result.Output.Should().Contain("执行命令 /throw 失败");
        result.Output.Should().Contain("boom");
    }

    [Fact]
    public async Task DispatchAsync_CancellationRequested_PropagatesOperationCanceledException()
    {
        var registry = new CommandRegistry();
        registry.Register(new CancelAwareCommand());
        var dispatcher = new CommandDispatcher(registry);
        var ctx = CreateContext();
        var cts = new CancellationTokenSource();
        cts.Cancel();

        var act = async () => await dispatcher.DispatchAsync("/cancel", ctx, cts.Token);
        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task DispatchAsync_SetsRawInputInContext()
    {
        var registry = new CommandRegistry();
        string? capturedRaw = null;
        var cmd = new TestCommand
        {
            Handler = ctx => { capturedRaw = ctx.RawInput; return CommandResult.Ok; }
        };
        registry.Register(cmd);
        var dispatcher = new CommandDispatcher(registry);
        var ctx = CreateContext();

        await dispatcher.DispatchAsync("/test arg1", ctx, CancellationToken.None);

        capturedRaw.Should().Be("/test arg1");
    }
}
