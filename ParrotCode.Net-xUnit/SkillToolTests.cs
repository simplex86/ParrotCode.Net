using System.Text.Json;
using ParrotCode;

namespace ParrotCode.xUnit;

/// <summary>
/// SkillTool（skill_loader 工具）单元测试（迭代 12）。
/// 覆盖执行成功/失败、参数校验、schema 生成。
/// </summary>
public class SkillToolTests
{
    private static SkillRegistry MakeRegistry()
    {
        var def = new SkillDefinition
        {
            Meta = new SkillMeta { Name = "commit", Description = "commit skill" },
            Body = "# Commit SOP\nbody",
            Source = SkillSource.Builtin
        };
        return new SkillRegistry(new[] { (KeyValuePair<string, SkillDefinition>)new("commit", def) }
                                    .ToDictionary(x => x.Key, x => x.Value));
    }

    private static JsonElement Json(string s) => JsonDocument.Parse(s).RootElement.Clone();

    [Fact]
    public async Task ExecuteAsync_ValidName_ReturnsSop()
    {
        var tool = new SkillTool(MakeRegistry());
        var result = await tool.ExecuteAsync(Json("""{"name":"commit"}"""), CancellationToken.None);

        result.Success.Should().BeTrue();
        result.Content.Should().Contain("# Skill: commit");
        result.Content.Should().Contain("body");
    }

    [Fact]
    public async Task ExecuteAsync_MissingName_ReturnsFail()
    {
        var tool = new SkillTool(MakeRegistry());
        var result = await tool.ExecuteAsync(Json("""{}"""), CancellationToken.None);

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("name");
    }

    [Fact]
    public async Task ExecuteAsync_EmptyName_ReturnsFail()
    {
        var tool = new SkillTool(MakeRegistry());
        var result = await tool.ExecuteAsync(Json("""{"name":""}"""), CancellationToken.None);

        result.Success.Should().BeFalse();
    }

    [Fact]
    public async Task ExecuteAsync_UnknownSkill_ReturnsFail()
    {
        var tool = new SkillTool(MakeRegistry());
        var result = await tool.ExecuteAsync(Json("""{"name":"nonexistent"}"""), CancellationToken.None);

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("未找到");
    }

    [Fact]
    public void Name_IsSkillLoader()
    {
        var tool = new SkillTool(MakeRegistry());
        tool.Name.Should().Be("skill_loader");
    }

    [Fact]
    public void Category_IsRead()
    {
        var tool = new SkillTool(MakeRegistry());
        tool.Category.Should().Be(ToolCategory.Read);
    }

    [Fact]
    public void ToOpenAiSchema_ContainsNameAndParameters()
    {
        var tool = new SkillTool(MakeRegistry());
        var schema = tool.ToOpenAiSchema();

        schema.GetProperty("function").GetProperty("name").GetString().Should().Be("skill_loader");
        var required = schema.GetProperty("function").GetProperty("parameters").GetProperty("required");
        required.GetArrayLength().Should().Be(1);
        required[0].GetString().Should().Be("name");
    }
}

internal static class JsonElementExtensions
{
    public static JsonElement GetProperty(this JsonElement el, string name)
        => el.GetProperty(name);
}
