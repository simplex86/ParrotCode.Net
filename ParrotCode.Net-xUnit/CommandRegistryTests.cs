using ParrotCode;

namespace ParrotCode.xUnit;

/// <summary>
/// CommandRegistry 单元测试（迭代 10a）。
/// 覆盖注册/查找/别名/冲突检测/反射自动扫描/可见性。
/// </summary>
public class CommandRegistryTests
{
    // 测试用命令
    private sealed class StubCommand : ICommand
    {
        public string Name { get; set; } = "";
        public string Description { get; set; } = "";
        public CommandType Type { get; set; } = CommandType.System;
        public IReadOnlyList<string> Aliases { get; set; } = Array.Empty<string>();
        public string Usage { get; set; } = "";
        public Task<CommandResult> ExecuteAsync(CommandContext context)
            => Task.FromResult(CommandResult.Ok);
    }

    private static StubCommand Cmd(string name, string[]? aliases = null, CommandType type = CommandType.System)
        => new()
        {
            Name = name,
            Description = $"{name} desc",
            Type = type,
            Aliases = aliases ?? Array.Empty<string>(),
            Usage = $"/{name}"
        };

    [Fact]
    public void Register_FindByName_ReturnsCommand()
    {
        var registry = new CommandRegistry();
        var cmd = Cmd("test");
        registry.Register(cmd);

        registry.Find("test").Should().BeSameAs(cmd);
    }

    [Fact]
    public void Register_FindByAlias_ReturnsSameCommand()
    {
        var registry = new CommandRegistry();
        var cmd = Cmd("exit", new[] { "quit" });
        registry.Register(cmd);

        registry.Find("quit").Should().BeSameAs(cmd);
    }

    [Fact]
    public void Register_DuplicateName_Throws()
    {
        var registry = new CommandRegistry();
        registry.Register(Cmd("test"));

        var act = () => registry.Register(Cmd("test"));
        act.Should().Throw<InvalidOperationException>()
           .WithMessage("*命令名 'test' 冲突*");
    }

    [Fact]
    public void Register_AliasConflict_Throws()
    {
        var registry = new CommandRegistry();
        registry.Register(Cmd("exit", new[] { "quit" }));

        var act = () => registry.Register(Cmd("bye", new[] { "quit" }));
        act.Should().Throw<InvalidOperationException>()
           .WithMessage("*别名 'quit' 冲突*");
    }

    [Fact]
    public void Find_NotFound_ReturnsNull()
    {
        var registry = new CommandRegistry();
        registry.Find("nonexistent").Should().BeNull();
    }

    [Fact]
    public void Find_CaseInsensitive_ReturnsCommand()
    {
        var registry = new CommandRegistry();
        var cmd = Cmd("help");
        registry.Register(cmd);

        registry.Find("HELP").Should().BeSameAs(cmd);
        registry.Find("Help").Should().BeSameAs(cmd);
    }

    [Fact]
    public void AutoRegisterFromAssembly_ScansAndRegistersBuiltinCommands()
    {
        var registry = new CommandRegistry();
        // 先手动注册 HelpCommand（需依赖注入）
        registry.Register(new HelpCommand(registry));
        // 自动扫描其余
        registry.AutoRegisterFromAssembly();

        // 验证内置命令已注册
        registry.Find("clear").Should().NotBeNull();
        registry.Find("compress").Should().NotBeNull();
        registry.Find("mode").Should().NotBeNull();
        registry.Find("status").Should().NotBeNull();
        registry.Find("session").Should().NotBeNull();
        registry.Find("exit").Should().NotBeNull();
        registry.Find("help").Should().NotBeNull();
    }

    [Fact]
    public void AutoRegisterFromAssembly_SkipsAlreadyRegistered()
    {
        var registry = new CommandRegistry();
        registry.Register(new HelpCommand(registry));
        registry.AutoRegisterFromAssembly();

        // HelpCommand 只注册一次（手动注册的实例）
        var help = registry.Find("help");
        help.Should().NotBeNull();
        // 再次 AutoRegister 不会抛异常
        registry.AutoRegisterFromAssembly();
        registry.Find("help").Should().BeSameAs(help);
    }

    [Fact]
    public void AutoRegisterFromAssembly_RegistersExitCommandWithAlias()
    {
        var registry = new CommandRegistry();
        registry.Register(new HelpCommand(registry));
        registry.AutoRegisterFromAssembly();

        registry.Find("exit").Should().NotBeNull();
        registry.Find("quit").Should().BeSameAs(registry.Find("exit"));
    }

    [Fact]
    public void GetVisibleCommands_FiltersHidden()
    {
        var registry = new CommandRegistry();
        registry.Register(Cmd("visible", type: CommandType.System));
        registry.Register(Cmd("hidden", type: CommandType.Hidden));

        var visible = registry.GetVisibleCommands();
        visible.Should().HaveCount(1);
        visible[0].Name.Should().Be("visible");
    }

    [Fact]
    public void GetAllNamesWithAliases_IncludesNamesAndAliases()
    {
        var registry = new CommandRegistry();
        registry.Register(Cmd("exit", new[] { "quit" }));
        registry.Register(Cmd("help", new[] { "?" }));

        var names = registry.GetAllNamesWithAliases();
        names.Should().Contain("exit");
        names.Should().Contain("quit");
        names.Should().Contain("help");
        names.Should().Contain("?");
    }

    [Fact]
    public void Count_ReturnsDistinctCommandCount()
    {
        var registry = new CommandRegistry();
        registry.Register(Cmd("exit", new[] { "quit", "bye" }));
        registry.Register(Cmd("help"));

        // 2 个命令（exit 有 2 个别名，但不重复计算）
        registry.Count.Should().Be(2);
    }
}
