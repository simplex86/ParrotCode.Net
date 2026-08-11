using System.IO;
using ParrotCode;

namespace ParrotCode.xUnit;

/// <summary>
/// SkillLoader 单元测试（迭代 12）。
/// 覆盖三级扫描、同名覆盖、frontmatter 解析、格式错误降级。
/// 跨平台：用 Path.GetTempPath + Path.Combine 构造临时目录，不硬编码路径。
/// </summary>
public class SkillLoaderTests
{
    /// <summary>创建临时目录并返回其路径，测试后由调用方清理。</summary>
    private static string CreateTempDir()
        => Path.Combine(Path.GetTempPath(), "skilltest_" + Guid.NewGuid().ToString("N"));

    private static void WriteSkill(string dir, string fileName, string content)
    {
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, fileName), content);
    }

    private const string ValidSkill = """
        ---
        name: commit
        description: commit skill
        tools_allow:
          - read_file
          - run_command
        tools_deny:
          - skill_loader
        ---
        # Commit SOP
        do commit
        """;

    [Fact]
    public void Load_BuiltinOnly_LoadsSkill()
    {
        var builtin = CreateTempDir();
        try
        {
            WriteSkill(builtin, "commit.md", ValidSkill);
            var loader = new SkillLoader(builtinDir: builtin, userHome: "/nonexistent", projectRoot: "/nonexistent");
            var skills = loader.Load();

            skills.Should().HaveCount(1);
            skills.Should().ContainKey("commit");
            var def = skills["commit"];
            def.Meta.Description.Should().Be("commit skill");
            def.Meta.ToolsAllow.Should().Contain(new[] { "read_file", "run_command" });
            def.Meta.ToolsDeny.Should().ContainSingle("skill_loader");
            def.Body.Should().Contain("# Commit SOP");
            def.Source.Should().Be(SkillSource.Builtin);
        }
        finally { Cleanup(builtin); }
    }

    [Fact]
    public void Load_ProjectOverridesBuiltin()
    {
        var builtin = CreateTempDir();
        var project = CreateTempDir();
        try
        {
            WriteSkill(builtin, "commit.md", ValidSkill);
            WriteSkill(Path.Combine(project, ".parrotcode", "skills"), "commit.md", """
                ---
                name: commit
                description: project overridden
                ---
                project body
                """);
            var loader = new SkillLoader(builtinDir: builtin, userHome: "/nonexistent", projectRoot: project);
            var skills = loader.Load();

            skills["commit"].Meta.Description.Should().Be("project overridden");
            skills["commit"].Source.Should().Be(SkillSource.Project);
        }
        finally { Cleanup(builtin, project); }
    }

    [Fact]
    public void Load_GlobalOverridesBuiltin()
    {
        var builtin = CreateTempDir();
        var global = CreateTempDir();
        try
        {
            WriteSkill(builtin, "commit.md", ValidSkill);
            var globalDir = Path.Combine(global, ".parrotcode", "skills");
            WriteSkill(globalDir, "commit.md", """
                ---
                name: commit
                description: global override
                ---
                global body
                """);
            var loader = new SkillLoader(builtinDir: builtin, userHome: global, projectRoot: "/nonexistent");
            var skills = loader.Load();

            skills["commit"].Meta.Description.Should().Be("global override");
            skills["commit"].Source.Should().Be(SkillSource.Global);
        }
        finally { Cleanup(builtin, global); }
    }

    [Fact]
    public void Load_MissingFrontmatter_SkipsFile()
    {
        var dir = CreateTempDir();
        try
        {
            WriteSkill(dir, "bad.md", "# just markdown, no frontmatter");
            var loader = new SkillLoader(builtinDir: dir, userHome: "/nonexistent", projectRoot: "/nonexistent");
            var skills = loader.Load();

            skills.Should().BeEmpty();
        }
        finally { Cleanup(dir); }
    }

    [Fact]
    public void Load_EmptyName_SkipsFile()
    {
        var dir = CreateTempDir();
        try
        {
            WriteSkill(dir, "bad.md", """
                ---
                name: ""
                description: no name
                ---
                body
                """);
            var loader = new SkillLoader(builtinDir: dir, userHome: "/nonexistent", projectRoot: "/nonexistent");
            var skills = loader.Load();

            skills.Should().BeEmpty();
        }
        finally { Cleanup(dir); }
    }

    [Fact]
    public void Load_MalformedYaml_SkipsFile()
    {
        var dir = CreateTempDir();
        try
        {
            // YAML 语法错：tools_allow 是字符串而非列表
            WriteSkill(dir, "bad.md", """
                ---
                name: bad
                description: malformed
                tools_allow: not_a_list
                ---
                body
                """);
            var loader = new SkillLoader(builtinDir: dir, userHome: "/nonexistent", projectRoot: "/nonexistent");
            // 解析失败应跳过，不抛异常
            var act = () => loader.Load();
            act.Should().NotThrow();
        }
        finally { Cleanup(dir); }
    }

    [Fact]
    public void Load_NonexistentDir_ReturnsEmpty()
    {
        var loader = new SkillLoader(builtinDir: "/nonexistent", userHome: "/nonexistent", projectRoot: "/nonexistent");
        var skills = loader.Load();
        skills.Should().BeEmpty();
    }

    [Fact]
    public void Load_MultipleSkills_AllLoaded()
    {
        var dir = CreateTempDir();
        try
        {
            WriteSkill(dir, "commit.md", ValidSkill);
            WriteSkill(dir, "review.md", """
                ---
                name: review
                description: review skill
                ---
                review body
                """);
            var loader = new SkillLoader(builtinDir: dir, userHome: "/nonexistent", projectRoot: "/nonexistent");
            var skills = loader.Load();

            skills.Should().HaveCount(2);
            skills.Should().ContainKeys("commit", "review");
        }
        finally { Cleanup(dir); }
    }

    private static void Cleanup(params string[] dirs)
    {
        foreach (var d in dirs)
        {
            try { if (Directory.Exists(d)) Directory.Delete(d, recursive: true); }
            catch { /* 测试清理忽略 */ }
        }
    }
}
