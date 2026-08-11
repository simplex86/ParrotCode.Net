using ParrotCode;

namespace ParrotCode.xUnit;

/// <summary>
/// SkillExecutor 单元测试（迭代 12）。
/// 覆盖工具白名单交集/并集计算、激活委托。
/// </summary>
public class SkillExecutorTests
{
    private static SkillDefinition MakeSkill(string name, List<string>? allow = null, List<string>? deny = null)
        => new()
        {
            Meta = new SkillMeta
            {
                Name = name,
                Description = "",
                ToolsAllow = allow ?? new List<string>(),
                ToolsDeny = deny ?? new List<string>()
            },
            Body = "",
            Source = SkillSource.Builtin
        };

    private static SkillExecutor MakeExecutor(params SkillDefinition[] skills)
        => new(new SkillRegistry(skills.ToDictionary(s => s.Meta.Name, s => s)));

    [Fact]
    public void GetEffectiveToolFilter_NoActive_ReturnsEmpty()
    {
        var exec = MakeExecutor(MakeSkill("a"));
        var (allowed, denied) = exec.GetEffectiveToolFilter();

        allowed.Should().BeEmpty();
        denied.Should().BeEmpty();
    }

    [Fact]
    public void GetEffectiveToolFilter_SingleSkill_ReturnsItsAllow()
    {
        var exec = MakeExecutor(MakeSkill("a", allow: new List<string> { "read_file", "run_command" }));
        exec.Activate("a");

        var (allowed, denied) = exec.GetEffectiveToolFilter();
        allowed.Should().Contain(new[] { "read_file", "run_command" });
    }

    [Fact]
    public void GetEffectiveToolFilter_TwoSkillsBothAllow_TakesIntersection()
    {
        var exec = MakeExecutor(
            MakeSkill("a", allow: new List<string> { "read_file", "run_command" }),
            MakeSkill("b", allow: new List<string> { "read_file", "write_file" }));
        exec.Activate("a");
        exec.Activate("b");

        var (allowed, _) = exec.GetEffectiveToolFilter();
        allowed.Should().ContainSingle("read_file");
    }

    [Fact]
    public void GetEffectiveToolFilter_OneSkillNoAllow_NotRestricted()
    {
        var exec = MakeExecutor(
            MakeSkill("a", allow: new List<string> { "read_file" }),
            MakeSkill("b"));  // 不声明 tools_allow
        exec.Activate("a");
        exec.Activate("b");

        var (allowed, _) = exec.GetEffectiveToolFilter();
        // 有 Skill 不限制 allow → 整体不限制
        allowed.Should().BeEmpty();
    }

    [Fact]
    public void GetEffectiveToolFilter_DenyIsUnion()
    {
        var exec = MakeExecutor(
            MakeSkill("a", deny: new List<string> { "skill_loader", "delete_file" }),
            MakeSkill("b", deny: new List<string> { "skill_loader", "run_command" }));
        exec.Activate("a");
        exec.Activate("b");

        var (_, denied) = exec.GetEffectiveToolFilter();
        denied.Should().Contain(new[] { "skill_loader", "delete_file", "run_command" });
        denied.Distinct().Count().Should().Be(3);  // 去重
    }

    [Fact]
    public void Activate_Deactivate_DelegateToRegistry()
    {
        var exec = MakeExecutor(MakeSkill("commit"));
        exec.Activate("commit").Success.Should().BeTrue();
        exec.IsActive("commit").Should().BeTrue();

        exec.Deactivate("commit").Should().BeTrue();
        exec.IsActive("commit").Should().BeFalse();
    }

    [Fact]
    public void GetActive_ReturnsActiveSkills()
    {
        var exec = MakeExecutor(MakeSkill("a"), MakeSkill("b"));
        exec.Activate("a");

        exec.GetActive().Should().HaveCount(1);
        exec.GetActive()[0].Meta.Name.Should().Be("a");
    }
}
