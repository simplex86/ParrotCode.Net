using ParrotCode;

namespace ParrotCode.xUnit;

/// <summary>
/// SkillCommand 单元测试（迭代 13b）。
/// 覆盖 list / info / activate / deactivate 四个子命令 + 边界情况。
/// </summary>
public class SkillCommandTests
{
    // ---- 测试基础设施 ----

    private static CommandContext MakeContext(SkillExecutor? skillExecutor, string rawInput = "/skill")
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
            RawInput = rawInput
        };
    }

    private static SkillExecutor MakeExecutor(params SkillDefinition[] skills)
        => new(new SkillRegistry(skills.ToDictionary(s => s.Meta.Name, s => s)));

    private static SkillDefinition MakeSkill(string name, string desc = "",
        List<string>? allow = null, List<string>? deny = null,
        string? skillDir = null, List<SkillResource>? resources = null,
        string body = "SOP body")
        => new()
        {
            Meta = new SkillMeta
            {
                Name = name,
                Description = desc,
                ToolsAllow = allow ?? new List<string>(),
                ToolsDeny = deny ?? new List<string>()
            },
            Body = body,
            Source = SkillSource.Builtin,
            SourcePath = $"/abs/{name}/SKILL.md",
            SkillDir = skillDir,
            Resources = resources ?? new List<SkillResource>()
        };

    private static SkillResource Res(SkillResourceKind kind, string rel, string abs)
        => new() { Kind = kind, RelativePath = rel, AbsolutePath = abs };

    // ---- 命令元数据 ----

    [Fact]
    public void Command_Metadata_Correct()
    {
        var cmd = new SkillCommand();
        cmd.Name.Should().Be("skill");
        cmd.Type.Should().Be(CommandType.System);
        cmd.Aliases.Should().BeEmpty();
        cmd.Usage.Should().Contain("list");
        cmd.Usage.Should().Contain("info");
        cmd.Usage.Should().Contain("activate");
        cmd.Usage.Should().Contain("deactivate");
    }

    // ---- /skill list ----

    [Fact]
    public async Task List_MultipleSkills_ShowsAll()
    {
        var cmd = new SkillCommand();
        var exec = MakeExecutor(
            MakeSkill("commit", "commit desc"),
            MakeSkill("review", "review desc"));
        var ctx = MakeContext(exec, "/skill list");

        var result = await cmd.ExecuteAsync(ctx);

        result.Handled.Should().BeTrue();
        result.Output.Should().Contain("commit");
        result.Output.Should().Contain("commit desc");
        result.Output.Should().Contain("review");
        result.Output.Should().Contain("review desc");
        result.Output.Should().Contain("已加载 Skill（2）");
    }

    [Fact]
    public async Task List_Empty_ShowsHint()
    {
        var cmd = new SkillCommand();
        var exec = MakeExecutor();
        var ctx = MakeContext(exec, "/skill list");

        var result = await cmd.ExecuteAsync(ctx);

        result.Output.Should().Contain("未加载任何 Skill");
    }

    [Fact]
    public async Task List_ActiveSkill_ShowsStarMarker()
    {
        var cmd = new SkillCommand();
        var exec = MakeExecutor(MakeSkill("commit"), MakeSkill("review"));
        exec.Activate("commit");
        var ctx = MakeContext(exec, "/skill list");

        var result = await cmd.ExecuteAsync(ctx);

        result.Output.Should().Contain("[*] commit");
        result.Output.Should().Contain("[ ] review");
    }

    [Fact]
    public async Task List_WithResources_ShowsResourceCount()
    {
        var cmd = new SkillCommand();
        var exec = MakeExecutor(MakeSkill("tool", resources: new List<SkillResource>
        {
            Res(SkillResourceKind.Script, "scripts/a.sh", "/abs/scripts/a.sh"),
            Res(SkillResourceKind.Reference, "refs/b.md", "/abs/refs/b.md"),
            Res(SkillResourceKind.Asset, "assets/c.json", "/abs/assets/c.json")
        }));
        var ctx = MakeContext(exec, "/skill list");

        var result = await cmd.ExecuteAsync(ctx);

        result.Output.Should().Contain("（3 资源）");
    }

    [Fact]
    public async Task List_NoResources_NoResourceHint()
    {
        var cmd = new SkillCommand();
        var exec = MakeExecutor(MakeSkill("simple"));
        var ctx = MakeContext(exec, "/skill list");

        var result = await cmd.ExecuteAsync(ctx);

        result.Output.Should().NotContain("资源");
    }

    [Fact]
    public async Task List_SortedByName()
    {
        var cmd = new SkillCommand();
        var exec = MakeExecutor(
            MakeSkill("zebra"),
            MakeSkill("alpha"),
            MakeSkill("middle"));
        var ctx = MakeContext(exec, "/skill list");

        var result = await cmd.ExecuteAsync(ctx);

        var lines = result.Output!.Split('\n');
        var skillLines = lines.Where(l => l.StartsWith("[") || l.StartsWith("[*")).ToArray();
        skillLines[0].Should().Contain("alpha");
        skillLines[1].Should().Contain("middle");
        skillLines[2].Should().Contain("zebra");
    }

    // ---- /skill info ----

    [Fact]
    public async Task Info_ValidName_ShowsDetails()
    {
        var cmd = new SkillCommand();
        var exec = MakeExecutor(MakeSkill("commit", "commit desc",
            allow: new List<string> { "read_file", "run_command" },
            deny: new List<string> { "skill_loader" },
            skillDir: "/abs/commit",
            body: "# Commit SOP\n1. git status"));
        var ctx = MakeContext(exec, "/skill info commit");

        var result = await cmd.ExecuteAsync(ctx);

        result.Output.Should().Contain("=== Skill: commit ===");
        result.Output.Should().Contain("描述: commit desc");
        result.Output.Should().Contain("来源: Builtin");
        result.Output.Should().Contain("状态: 未激活");
        result.Output.Should().Contain("目录: /abs/commit");
        result.Output.Should().Contain("可用工具: read_file, run_command");
        result.Output.Should().Contain("禁用工具: skill_loader");
        result.Output.Should().Contain("SOP 预览");
        result.Output.Should().Contain("# Commit SOP");
    }

    [Fact]
    public async Task Info_MissingName_ShowsUsage()
    {
        var cmd = new SkillCommand();
        var exec = MakeExecutor(MakeSkill("commit"));
        var ctx = MakeContext(exec, "/skill info");

        var result = await cmd.ExecuteAsync(ctx);

        result.Output.Should().Contain("用法：/skill info <name>");
    }

    [Fact]
    public async Task Info_NotFound_ShowsError()
    {
        var cmd = new SkillCommand();
        var exec = MakeExecutor(MakeSkill("commit"));
        var ctx = MakeContext(exec, "/skill info nonexistent");

        var result = await cmd.ExecuteAsync(ctx);

        result.Output.Should().Contain("未找到 Skill：nonexistent");
    }

    [Fact]
    public async Task Info_SingleFileSkill_ShowsSingleFileFormat()
    {
        var cmd = new SkillCommand();
        var exec = MakeExecutor(MakeSkill("legacy", skillDir: null));
        var ctx = MakeContext(exec, "/skill info legacy");

        var result = await cmd.ExecuteAsync(ctx);

        result.Output.Should().Contain("单文件格式");
    }

    [Fact]
    public async Task Info_DirectorySkill_ShowsSkillDir()
    {
        var cmd = new SkillCommand();
        var exec = MakeExecutor(MakeSkill("dir", skillDir: "/abs/dir"));
        var ctx = MakeContext(exec, "/skill info dir");

        var result = await cmd.ExecuteAsync(ctx);

        result.Output.Should().Contain("目录: /abs/dir");
    }

    [Fact]
    public async Task Info_WithResources_ShowsResourceList()
    {
        var cmd = new SkillCommand();
        var exec = MakeExecutor(MakeSkill("tool", skillDir: "/abs/tool", resources: new List<SkillResource>
        {
            Res(SkillResourceKind.Script, "scripts/run.sh", "/abs/tool/scripts/run.sh"),
            Res(SkillResourceKind.Reference, "refs/doc.md", "/abs/tool/refs/doc.md")
        }));
        var ctx = MakeContext(exec, "/skill info tool");

        var result = await cmd.ExecuteAsync(ctx);

        result.Output.Should().Contain("资源（2）");
        result.Output.Should().Contain("[Script] scripts/run.sh");
        result.Output.Should().Contain("[Reference] refs/doc.md");
    }

    [Fact]
    public async Task Info_LongBody_TruncatesPreview()
    {
        var longBody = string.Join("\n", Enumerable.Range(1, 20).Select(i => $"Line {i}"));
        var cmd = new SkillCommand();
        var exec = MakeExecutor(MakeSkill("long", body: longBody));
        var ctx = MakeContext(exec, "/skill info long");

        var result = await cmd.ExecuteAsync(ctx);

        result.Output.Should().Contain("Line 1");
        result.Output.Should().Contain("Line 10");
        result.Output.Should().NotContain("Line 11");
        result.Output.Should().Contain("更多内容");
    }

    [Fact]
    public async Task Info_ActiveSkill_ShowsActiveStatus()
    {
        var cmd = new SkillCommand();
        var exec = MakeExecutor(MakeSkill("commit"));
        exec.Activate("commit");
        var ctx = MakeContext(exec, "/skill info commit");

        var result = await cmd.ExecuteAsync(ctx);

        result.Output.Should().Contain("状态: 已激活");
    }

    // ---- /skill activate ----

    [Fact]
    public async Task Activate_ValidName_ActivatesAndStartsAgent()
    {
        var cmd = new SkillCommand();
        var exec = MakeExecutor(MakeSkill("review", body: "# Review SOP\n1. Read code"));
        var ctx = MakeContext(exec, "/skill activate review");

        var result = await cmd.ExecuteAsync(ctx);

        result.Handled.Should().BeTrue();
        result.StartAgent.Should().BeTrue();
        result.Output.Should().Contain("已激活 Skill review");

        // SOP 应作为 user 消息注入 history
        var messages = ctx.History.ToProviderMessages();
        var lastUser = messages.Last(m => m.Role == MessageRole.User);
        lastUser.Content.Should().Contain("请按以下 Skill 流程执行");
        lastUser.Content.Should().Contain("Review SOP");

        // UI 应显示 /skill activate review
        ctx.Ui.As<MockUiControl>().UserMessages.Should().Contain("/skill activate review");
    }

    [Fact]
    public async Task Activate_MissingName_ShowsUsage()
    {
        var cmd = new SkillCommand();
        var exec = MakeExecutor(MakeSkill("review"));
        var ctx = MakeContext(exec, "/skill activate");

        var result = await cmd.ExecuteAsync(ctx);

        result.Output.Should().Contain("用法：/skill activate <name>");
        result.StartAgent.Should().BeFalse();
    }

    [Fact]
    public async Task Activate_NotFound_ShowsError()
    {
        var cmd = new SkillCommand();
        var exec = MakeExecutor(MakeSkill("commit"));
        var ctx = MakeContext(exec, "/skill activate nonexistent");

        var result = await cmd.ExecuteAsync(ctx);

        result.Output.Should().Contain("未找到 Skill：nonexistent");
        result.StartAgent.Should().BeFalse();
    }

    [Fact]
    public async Task Activate_AlreadyActive_Idempotent()
    {
        var cmd = new SkillCommand();
        var exec = MakeExecutor(MakeSkill("review", body: "SOP"));
        exec.Activate("review");
        var ctx = MakeContext(exec, "/skill activate review");

        var result = await cmd.ExecuteAsync(ctx);

        // 幂等：仍然成功 + StartAgent=true + SOP 再次注入
        result.StartAgent.Should().BeTrue();
        result.Output.Should().Contain("已激活 Skill review");
    }

    [Fact]
    public async Task Activate_MaxActiveLimit_ReturnsError()
    {
        var cmd = new SkillCommand();
        // maxActive=1，已激活一个，激活第二个应失败
        var registry = new SkillRegistry(
            new Dictionary<string, SkillDefinition>
            {
                ["a"] = MakeSkill("a"),
                ["b"] = MakeSkill("b")
            },
            maxActive: 1);
        var exec = new SkillExecutor(registry);
        exec.Activate("a");
        var ctx = MakeContext(exec, "/skill activate b");

        var result = await cmd.ExecuteAsync(ctx);

        result.Output.Should().Contain("上限");
        result.StartAgent.Should().BeFalse();
    }

    // ---- /skill deactivate ----

    [Fact]
    public async Task Deactivate_ActiveSkill_Deactivates()
    {
        var cmd = new SkillCommand();
        var exec = MakeExecutor(MakeSkill("review"));
        exec.Activate("review");
        var ctx = MakeContext(exec, "/skill deactivate review");

        var result = await cmd.ExecuteAsync(ctx);

        result.Handled.Should().BeTrue();
        result.Output.Should().Contain("已停用 Skill review");
        result.StartAgent.Should().BeFalse();
        exec.IsActive("review").Should().BeFalse();
    }

    [Fact]
    public async Task Deactivate_NotActive_ShowsHint()
    {
        var cmd = new SkillCommand();
        var exec = MakeExecutor(MakeSkill("review"));
        var ctx = MakeContext(exec, "/skill deactivate review");

        var result = await cmd.ExecuteAsync(ctx);

        result.Output.Should().Contain("未处于激活状态");
    }

    [Fact]
    public async Task Deactivate_MissingName_ShowsUsage()
    {
        var cmd = new SkillCommand();
        var exec = MakeExecutor(MakeSkill("review"));
        var ctx = MakeContext(exec, "/skill deactivate");

        var result = await cmd.ExecuteAsync(ctx);

        result.Output.Should().Contain("用法：/skill deactivate <name>");
    }

    [Fact]
    public async Task Deactivate_NotFound_ShowsNotActive()
    {
        var cmd = new SkillCommand();
        var exec = MakeExecutor(MakeSkill("commit"));
        var ctx = MakeContext(exec, "/skill deactivate nonexistent");

        // 不存在的 Skill → Deactivate 返回 false → "未处于激活状态"
        var result = await cmd.ExecuteAsync(ctx);

        result.Output.Should().Contain("未处于激活状态");
    }

    // ---- 边界情况 ----

    [Fact]
    public async Task NoArgs_DefaultsToList()
    {
        var cmd = new SkillCommand();
        var exec = MakeExecutor(MakeSkill("commit", "desc"));
        var ctx = MakeContext(exec, "/skill");

        var result = await cmd.ExecuteAsync(ctx);

        result.Output.Should().Contain("已加载 Skill（1）");
        result.Output.Should().Contain("commit");
    }

    [Fact]
    public async Task UppercaseSubcommand_CaseInsensitive()
    {
        var cmd = new SkillCommand();
        var exec = MakeExecutor(MakeSkill("commit", "desc"));
        var ctx = MakeContext(exec, "/skill LIST");

        var result = await cmd.ExecuteAsync(ctx);

        result.Output.Should().Contain("已加载 Skill（1）");
    }

    [Fact]
    public async Task UnknownSubcommand_ShowsUsage()
    {
        var cmd = new SkillCommand();
        var exec = MakeExecutor(MakeSkill("commit"));
        var ctx = MakeContext(exec, "/skill unknown");

        var result = await cmd.ExecuteAsync(ctx);

        result.Output.Should().Contain("未知子命令：unknown");
        result.Output.Should().Contain("用法：");
    }

    [Fact]
    public async Task SkillExecutorNull_ShowsNotEnabled()
    {
        var cmd = new SkillCommand();
        var ctx = MakeContext(skillExecutor: null, "/skill list");

        var result = await cmd.ExecuteAsync(ctx);

        result.Output.Should().Contain("未启用");
        result.StartAgent.Should().BeFalse();
    }

    [Fact]
    public async Task Activate_SkillExecutorNull_ShowsNotEnabled()
    {
        var cmd = new SkillCommand();
        var ctx = MakeContext(skillExecutor: null, "/skill activate commit");

        var result = await cmd.ExecuteAsync(ctx);

        result.Output.Should().Contain("未启用");
        result.StartAgent.Should().BeFalse();
    }
}
