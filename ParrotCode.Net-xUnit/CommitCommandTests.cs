using ParrotCode;

namespace ParrotCode.xUnit;

/// <summary>
/// CommitCommand 单元测试（迭代 12）。
/// 覆盖 SkillExecutor 未启用、Skill 不存在、成功激活并触发 Agent。
/// </summary>
public class CommitCommandTests
{
    private static CommandContext MakeContext(SkillExecutor? skillExecutor)
    {
        var ui = new MockUiControl();
        var history = new ConversationHistory();
        var guard = new SecurityGuard(
            new SecurityContext { ProjectRoot = Path.GetTempPath() },
            SecurityLevel.Normal);

        return new CommandContext(history, null, guard, ui, null, CancellationToken.None)
        {
            ProviderConfig = new ProviderConfig { Name = "test", Protocol = "mock", Model = "m" },
            TuiConfig = new TuiConfig(),
            AgentConfig = new AgentConfig(),
            SkillExecutor = skillExecutor,
            RawInput = "/commit"
        };
    }

    private static SkillExecutor MakeExecutor(params SkillDefinition[] skills)
        => new(new SkillRegistry(skills.ToDictionary(s => s.Meta.Name, s => s)));

    private static SkillDefinition CommitSkill => new()
    {
        Meta = new SkillMeta { Name = "commit", Description = "commit skill" },
        Body = "# Commit SOP\n1. git status",
        Source = SkillSource.Builtin
    };

    [Fact]
    public async Task ExecuteAsync_SkillExecutorNull_ReturnsNotEnabled()
    {
        var cmd = new CommitCommand();
        var context = MakeContext(skillExecutor: null);

        var result = await cmd.ExecuteAsync(context);

        result.Handled.Should().BeTrue();
        result.Output.Should().Contain("未启用");
        result.StartAgent.Should().BeFalse();
    }

    [Fact]
    public async Task ExecuteAsync_CommitSkillNotFound_ReturnsError()
    {
        var cmd = new CommitCommand();
        var exec = MakeExecutor();  // 空 Registry，无 commit Skill
        var context = MakeContext(exec);

        var result = await cmd.ExecuteAsync(context);

        result.Handled.Should().BeTrue();
        result.Output.Should().Contain("未找到");
        result.StartAgent.Should().BeFalse();
    }

    [Fact]
    public async Task ExecuteAsync_Success_ActivatesAndStartsAgent()
    {
        var cmd = new CommitCommand();
        var exec = MakeExecutor(CommitSkill);
        var context = MakeContext(exec);

        var result = await cmd.ExecuteAsync(context);

        result.Handled.Should().BeTrue();
        result.StartAgent.Should().BeTrue();
        result.Output.Should().Contain("已激活");

        // SOP 应作为 user 消息注入 history
        var messages = context.History.ToProviderMessages();
        messages.Should().NotBeEmpty();
        var lastUser = messages.Last(m => m.Role == MessageRole.User);
        lastUser.Content.Should().Contain("请按以下流程执行提交");
        lastUser.Content.Should().Contain("git status");

        // UI 应显示 /commit
        context.Ui.As<MockUiControl>().UserMessages.Should().Contain("/commit");
    }

    [Fact]
    public void Command_Metadata_Correct()
    {
        var cmd = new CommitCommand();
        cmd.Name.Should().Be("commit");
        cmd.Type.Should().Be(CommandType.System);
        cmd.Aliases.Should().BeEmpty();
    }
}
