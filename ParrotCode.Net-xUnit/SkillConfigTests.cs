using ParrotCode;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace ParrotCode.xUnit;

/// <summary>
/// SkillConfig 单元测试（迭代 12）。
/// 验证默认值与 YAML 反序列化。
/// </summary>
public class SkillConfigTests
{
    [Fact]
    public void Default_Enable_Null_UsesTrue()
    {
        var cfg = new SkillConfig();
        (cfg.Enable ?? true).Should().BeTrue();
    }

    [Fact]
    public void Default_MaxActiveSkills_Null_UsesThree()
    {
        var cfg = new SkillConfig();
        (cfg.MaxActiveSkills ?? 3).Should().Be(3);
    }

    [Fact]
    public void Yaml_Deserialize_EnableFalse()
    {
        var yaml = """
            enable: false
            max_active_skills: 5
            """;
        var deserializer = new DeserializerBuilder()
            .WithNamingConvention(UnderscoredNamingConvention.Instance)
            .Build();
        var cfg = deserializer.Deserialize<SkillConfig>(yaml);

        cfg.Enable.Should().BeFalse();
        cfg.MaxActiveSkills.Should().Be(5);
    }

    [Fact]
    public void Yaml_Deserialize_PartialConfig()
    {
        // 仅设 enable，max_active_skills 缺省
        var yaml = "enable: true";
        var deserializer = new DeserializerBuilder()
            .WithNamingConvention(UnderscoredNamingConvention.Instance)
            .Build();
        var cfg = deserializer.Deserialize<SkillConfig>(yaml);

        cfg.Enable.Should().BeTrue();
        cfg.MaxActiveSkills.Should().BeNull();
    }

    [Fact]
    public void Yaml_AppConfig_SkillsSection()
    {
        var yaml = """
            active_provider: mock
            providers:
              - name: mock
                protocol: mock
                model: mock-1
            skills:
              enable: false
              max_active_skills: 10
            """;
        var deserializer = new DeserializerBuilder()
            .WithNamingConvention(UnderscoredNamingConvention.Instance)
            .Build();
        var cfg = deserializer.Deserialize<AppConfig>(yaml);

        cfg.Skills.Should().NotBeNull();
        cfg.Skills!.Enable.Should().BeFalse();
        cfg.Skills!.MaxActiveSkills.Should().Be(10);
    }
}
