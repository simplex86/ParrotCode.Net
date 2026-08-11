using ParrotCode;

namespace ParrotCode.xUnit;

/// <summary>
/// SkillRegistry 单元测试（迭代 12）。
/// 覆盖激活/停用、幂等、上限、摘要生成。
/// </summary>
public class SkillRegistryTests
{
    private static SkillDefinition MakeSkill(string name, string desc = "", List<string>? allow = null, List<string>? deny = null)
        => new()
        {
            Meta = new SkillMeta
            {
                Name = name,
                Description = desc,
                ToolsAllow = allow ?? new List<string>(),
                ToolsDeny = deny ?? new List<string>()
            },
            Body = $"# {name} SOP\nbody",
            Source = SkillSource.Builtin
        };

    private static SkillRegistry MakeRegistry(params SkillDefinition[] skills)
        => new(skills.ToDictionary(s => s.Meta.Name, s => s), maxActive: 3);

    [Fact]
    public void Activate_Success_ReturnsSop()
    {
        var registry = MakeRegistry(MakeSkill("commit", "commit desc"));
        var result = registry.Activate("commit");

        result.Success.Should().BeTrue();
        result.SkillName.Should().Be("commit");
        result.SopContent.Should().Contain("# Skill: commit");
        result.SopContent.Should().Contain("body");
    }

    [Fact]
    public void Activate_UnknownSkill_ReturnsError()
    {
        var registry = MakeRegistry(MakeSkill("commit"));
        var result = registry.Activate("nonexistent");

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("未找到");
    }

    [Fact]
    public void Activate_ExceedsMaxActive_ReturnsError()
    {
        var registry = new SkillRegistry(
            new[] { MakeSkill("a"), MakeSkill("b"), MakeSkill("c"), MakeSkill("d") }
                .ToDictionary(s => s.Meta.Name, s => s),
            maxActive: 2);

        registry.Activate("a").Success.Should().BeTrue();
        registry.Activate("b").Success.Should().BeTrue();
        var result = registry.Activate("c");

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("上限");
    }

    [Fact]
    public void Activate_AlreadyActive_IsIdempotent()
    {
        var registry = MakeRegistry(MakeSkill("commit"));
        registry.Activate("commit");

        var second = registry.Activate("commit");
        second.Success.Should().BeTrue();
        second.SopContent.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public void Deactivate_RemovesActive()
    {
        var registry = MakeRegistry(MakeSkill("commit"));
        registry.Activate("commit");
        registry.IsActive("commit").Should().BeTrue();

        var removed = registry.Deactivate("commit");
        removed.Should().BeTrue();
        registry.IsActive("commit").Should().BeFalse();
    }

    [Fact]
    public void Deactivate_NotActive_ReturnsFalse()
    {
        var registry = MakeRegistry(MakeSkill("commit"));
        var removed = registry.Deactivate("commit");
        removed.Should().BeFalse();
    }

    [Fact]
    public void GetSummary_ContainsAllSkillNames()
    {
        var registry = MakeRegistry(
            MakeSkill("commit", "commit desc"),
            MakeSkill("review", "review desc"));

        var summary = registry.GetSummary();

        summary.Should().Contain("commit");
        summary.Should().Contain("commit desc");
        summary.Should().Contain("review");
        summary.Should().Contain("review desc");
        summary.Should().Contain("skill_loader");
    }

    [Fact]
    public void GetSummary_EmptySkills_ReturnsEmpty()
    {
        var registry = MakeRegistry();
        registry.GetSummary().Should().BeEmpty();
    }

    [Fact]
    public void GetActiveSkills_ReturnsOnlyActive()
    {
        var registry = MakeRegistry(MakeSkill("a"), MakeSkill("b"), MakeSkill("c"));
        registry.Activate("a");
        registry.Activate("c");

        var active = registry.GetActiveSkills();
        active.Should().HaveCount(2);
        active.Select(d => d.Meta.Name).Should().Contain(new[] { "a", "c" });
    }

    [Fact]
    public void GetSummary_SopIncludesToolsInfo()
    {
        var registry = MakeRegistry(MakeSkill("commit", allow: new List<string> { "read_file", "run_command" },
                                                      deny: new List<string> { "skill_loader" }));
        var result = registry.Activate("commit");

        result.SopContent.Should().Contain("可用工具");
        result.SopContent.Should().Contain("read_file");
        result.SopContent.Should().Contain("禁用工具");
        result.SopContent.Should().Contain("skill_loader");
    }
}
