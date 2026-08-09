using ParrotCode;

namespace ParrotCode.xUnit;

/// <summary>
/// 内置命令单元测试（迭代 10a）。
/// 覆盖 Help/Clear/Compress/Mode/Status/Session(stub)/Exit 命令。
/// </summary>
public class BuiltinCommandTests
{
    private static CommandContext CreateContext(
        MockUiControl? ui = null,
        ContextCompressor? compressor = null,
        SecurityGuard? guard = null,
        ConversationHistory? history = null,
        SessionStore? sessionStore = null)
    {
        ui ??= new MockUiControl();
        history ??= new ConversationHistory();
        guard ??= new SecurityGuard(
            new SecurityContext { ProjectRoot = Path.GetTempPath() },
            SecurityLevel.Normal);

        return new CommandContext(history, compressor, guard, ui, sessionStore, CancellationToken.None)
        {
            ProviderConfig = new ProviderConfig { Name = "test", Protocol = "mock", Model = "test-model" },
            TuiConfig = new TuiConfig(),
            AgentConfig = new AgentConfig(),
        };
    }

    // ===== HelpCommand =====

    [Fact]
    public async Task HelpCommand_ListsAllVisibleCommands()
    {
        var registry = new CommandRegistry();
        registry.Register(new HelpCommand(registry));
        registry.AutoRegisterFromAssembly();
        var cmd = new HelpCommand(registry);
        var ctx = CreateContext();

        var result = await cmd.ExecuteAsync(ctx);

        result.Handled.Should().BeTrue();
        result.Output.Should().NotBeNull();
        result.Output.Should().Contain("可用命令");
        result.Output.Should().Contain("/clear");
        result.Output.Should().Contain("/mode");
        result.Output.Should().Contain("/exit");
        result.Output.Should().Contain("/help");
    }

    [Fact]
    public async Task HelpCommand_DoesNotListHiddenCommands()
    {
        var registry = new CommandRegistry();
        registry.Register(new HelpCommand(registry));
        registry.AutoRegisterFromAssembly();
        // 注册一个隐藏命令
        registry.Register(new TestHiddenCommand());
        var cmd = new HelpCommand(registry);
        var ctx = CreateContext();

        var result = await cmd.ExecuteAsync(ctx);

        result.Output.Should().NotContain("hidden-cmd");
    }

    // ===== ClearCommand =====

    [Fact]
    public async Task ClearCommand_ClearsHistoryAndUi()
    {
        var history = new ConversationHistory();
        history.AddUser("hello");
        var ui = new MockUiControl();
        var cmd = new ClearCommand();
        var ctx = CreateContext(ui: ui, history: history);

        var result = await cmd.ExecuteAsync(ctx);

        result.Handled.Should().BeTrue();
        ui.MessagesCleared.Should().BeTrue();
        history.Count.Should().Be(0);
        ui.LastTokenEstimate.Should().Be(0);
    }

    // ===== CompressCommand =====

    [Fact]
    public async Task CompressCommand_NullCompressor_ReturnsNotEnabled()
    {
        var cmd = new CompressCommand();
        var ctx = CreateContext(compressor: null);

        var result = await cmd.ExecuteAsync(ctx);

        result.Output.Should().Contain("未启用");
    }

    // ===== ModeCommand =====

    [Fact]
    public async Task ModeCommand_NoArg_ShowsCurrentLevel()
    {
        var cmd = new ModeCommand();
        var ctx = CreateContext();
        ctx = ctx with { RawInput = "/mode" };

        var result = await cmd.ExecuteAsync(ctx);

        result.Handled.Should().BeTrue();
        result.Output.Should().Contain("当前安全等级");
        result.Output.Should().Contain("Normal");
    }

    [Fact]
    public async Task ModeCommand_StrictArg_SwitchesLevel()
    {
        var ui = new MockUiControl();
        var guard = new SecurityGuard(
            new SecurityContext { ProjectRoot = Path.GetTempPath() },
            SecurityLevel.Normal);
        var cmd = new ModeCommand();
        var ctx = CreateContext(ui: ui, guard: guard) with { RawInput = "/mode strict" };

        var result = await cmd.ExecuteAsync(ctx);

        result.Handled.Should().BeTrue();
        result.Output.Should().Contain("Strict");
        guard.Level.Should().Be(SecurityLevel.Strict);
        ui.LastSecurityLevel.Should().Be(SecurityLevel.Strict);
        ui.RefreshStatusBarCount.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task ModeCommand_PermissiveArg_SwitchesLevel()
    {
        var guard = new SecurityGuard(
            new SecurityContext { ProjectRoot = Path.GetTempPath() },
            SecurityLevel.Normal);
        var cmd = new ModeCommand();
        var ctx = CreateContext(guard: guard) with { RawInput = "/mode permissive" };

        var result = await cmd.ExecuteAsync(ctx);

        result.Output.Should().Contain("Permissive");
        guard.Level.Should().Be(SecurityLevel.Permissive);
    }

    [Fact]
    public async Task ModeCommand_InvalidArg_FallsBackToNormal()
    {
        var guard = new SecurityGuard(
            new SecurityContext { ProjectRoot = Path.GetTempPath() },
            SecurityLevel.Strict);
        var cmd = new ModeCommand();
        var ctx = CreateContext(guard: guard) with { RawInput = "/mode invalid" };

        var result = await cmd.ExecuteAsync(ctx);

        guard.Level.Should().Be(SecurityLevel.Normal);
        result.Output.Should().Contain("Normal");
    }

    // ===== StatusCommand =====

    [Fact]
    public async Task StatusCommand_ShowsProviderAndModel()
    {
        var cmd = new StatusCommand();
        var ctx = CreateContext();

        var result = await cmd.ExecuteAsync(ctx);

        result.Handled.Should().BeTrue();
        result.Output.Should().NotBeNull();
        result.Output.Should().Contain("test");
        result.Output.Should().Contain("test-model");
    }

    [Fact]
    public async Task StatusCommand_ShowsSecurityLevel()
    {
        var cmd = new StatusCommand();
        var ctx = CreateContext();

        var result = await cmd.ExecuteAsync(ctx);

        result.Output.Should().Contain("安全等级");
        result.Output.Should().Contain("Normal");
    }

    [Fact]
    public async Task StatusCommand_InstructionSummaryNull_ShowsNotLoaded()
    {
        var cmd = new StatusCommand();
        var ctx = CreateContext();

        var result = await cmd.ExecuteAsync(ctx);

        result.Output.Should().Contain("项目指令");
        result.Output.Should().Contain("未加载");
    }

    // ===== SessionCommand =====

    [Fact]
    public async Task SessionCommand_NullStore_ReturnsNotEnabled()
    {
        var cmd = new SessionCommand();
        var ctx = CreateContext();

        var result = await cmd.ExecuteAsync(ctx);

        result.Handled.Should().BeTrue();
        result.Output.Should().Contain("未启用");
    }

    // ===== ExitCommand =====

    [Fact]
    public async Task ExitCommand_ReturnsExitApp()
    {
        var ui = new MockUiControl();
        var cmd = new ExitCommand();
        var ctx = CreateContext(ui: ui);

        var result = await cmd.ExecuteAsync(ctx);

        result.Handled.Should().BeTrue();
        result.ExitApp.Should().BeTrue();
        ui.ExitRequested.Should().BeTrue();
    }

    // 测试辅助
    private sealed class TestHiddenCommand : ICommand
    {
        public string Name => "hidden-cmd";
        public string Description => "hidden";
        public CommandType Type => CommandType.Hidden;
        public IReadOnlyList<string> Aliases => Array.Empty<string>();
        public string Usage => "/hidden-cmd";
        public Task<CommandResult> ExecuteAsync(CommandContext context)
            => Task.FromResult(CommandResult.Ok);
    }
}
