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

    /// <summary>创建目录格式 Skill：dir/SKILL.md + 可选子目录文件。</summary>
    private static void WriteDirSkill(string baseDir, string skillName, string skillContent,
        Dictionary<string, string>? subDirFiles = null)
    {
        var skillDir = Path.Combine(baseDir, skillName);
        Directory.CreateDirectory(skillDir);
        File.WriteAllText(Path.Combine(skillDir, "SKILL.md"), skillContent);
        if (subDirFiles is not null)
        {
            foreach (var kv in subDirFiles)
            {
                var fullPath = Path.Combine(skillDir, kv.Key);
                Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
                File.WriteAllText(fullPath, kv.Value);
            }
        }
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

    // ---- 迭代 13a：目录格式 + 子资源扫描 ----

    [Fact]
    public void Load_DirectoryFormat_ParsesSkillAndResources()
    {
        var dir = CreateTempDir();
        try
        {
            WriteDirSkill(dir, "cleaner", ValidSkill.Replace("commit", "cleaner"), new()
            {
                ["scripts/clean.py"] = "# script",
                ["references/spec.md"] = "# spec",
                ["assets/template.json"] = "{}"
            });
            var loader = new SkillLoader(builtinDir: dir, userHome: "/nonexistent", projectRoot: "/nonexistent");
            var skills = loader.Load();

            skills.Should().ContainKey("cleaner");
            var def = skills["cleaner"];
            def.SkillDir.Should().NotBeNullOrEmpty();
            def.Resources.Should().HaveCount(3);
            def.Resources.Should().Contain(r => r.Kind == SkillResourceKind.Script && r.RelativePath == Path.Combine("scripts", "clean.py"));
            def.Resources.Should().Contain(r => r.Kind == SkillResourceKind.Reference && r.RelativePath == Path.Combine("references", "spec.md"));
            def.Resources.Should().Contain(r => r.Kind == SkillResourceKind.Asset && r.RelativePath == Path.Combine("assets", "template.json"));
        }
        finally { Cleanup(dir); }
    }

    [Fact]
    public void Load_SingleFile_HasNullSkillDirAndEmptyResources()
    {
        var dir = CreateTempDir();
        try
        {
            WriteSkill(dir, "commit.md", ValidSkill);
            var loader = new SkillLoader(builtinDir: dir, userHome: "/nonexistent", projectRoot: "/nonexistent");
            var skills = loader.Load();

            var def = skills["commit"];
            def.SkillDir.Should().BeNull();
            def.Resources.Should().BeEmpty();
        }
        finally { Cleanup(dir); }
    }

    [Fact]
    public void Load_DirectoryWithoutSkillMd_SkipsDirectory()
    {
        var dir = CreateTempDir();
        try
        {
            // 目录存在但无 SKILL.md
            Directory.CreateDirectory(Path.Combine(dir, "incomplete"));
            File.WriteAllText(Path.Combine(dir, "incomplete", "notes.md"), "not a skill");
            var loader = new SkillLoader(builtinDir: dir, userHome: "/nonexistent", projectRoot: "/nonexistent");
            var skills = loader.Load();

            skills.Should().BeEmpty();
        }
        finally { Cleanup(dir); }
    }

    [Fact]
    public void Load_DirectoryWithNoSubDirs_HasEmptyResources()
    {
        var dir = CreateTempDir();
        try
        {
            WriteDirSkill(dir, "simple", ValidSkill.Replace("commit", "simple"));
            var loader = new SkillLoader(builtinDir: dir, userHome: "/nonexistent", projectRoot: "/nonexistent");
            var skills = loader.Load();

            var def = skills["simple"];
            def.SkillDir.Should().NotBeNullOrEmpty();
            def.Resources.Should().BeEmpty();
        }
        finally { Cleanup(dir); }
    }

    [Fact]
    public void Load_SameLevelConflict_DirectoryOverridesSingleFile()
    {
        var dir = CreateTempDir();
        try
        {
            WriteSkill(dir, "commit.md", ValidSkill);
            WriteDirSkill(dir, "commit", ValidSkill.Replace("commit skill", "directory version"));
            var loader = new SkillLoader(builtinDir: dir, userHome: "/nonexistent", projectRoot: "/nonexistent");
            var skills = loader.Load();

            var def = skills["commit"];
            def.SkillDir.Should().NotBeNullOrEmpty("目录格式应优先");
            def.Meta.Description.Should().Be("directory version");
        }
        finally { Cleanup(dir); }
    }

    [Fact]
    public void Load_CrossLevel_DirectoryOverridesSingleFile()
    {
        var builtin = CreateTempDir();
        var project = CreateTempDir();
        try
        {
            WriteSkill(builtin, "commit.md", ValidSkill);
            WriteDirSkill(Path.Combine(project, ".parrotcode", "skills"), "commit",
                ValidSkill.Replace("commit skill", "project dir"));
            var loader = new SkillLoader(builtinDir: builtin, userHome: "/nonexistent", projectRoot: project);
            var skills = loader.Load();

            var def = skills["commit"];
            def.Source.Should().Be(SkillSource.Project);
            def.SkillDir.Should().NotBeNullOrEmpty();
            def.Meta.Description.Should().Be("project dir");
        }
        finally { Cleanup(builtin, project); }
    }

    [Fact]
    public void Load_CrossLevel_BothDirectory_ProjectWins()
    {
        var builtin = CreateTempDir();
        var project = CreateTempDir();
        try
        {
            WriteDirSkill(builtin, "commit", ValidSkill);
            WriteDirSkill(Path.Combine(project, ".parrotcode", "skills"), "commit",
                ValidSkill.Replace("commit skill", "project dir"));
            var loader = new SkillLoader(builtinDir: builtin, userHome: "/nonexistent", projectRoot: project);
            var skills = loader.Load();

            var def = skills["commit"];
            def.Source.Should().Be(SkillSource.Project);
            def.Meta.Description.Should().Be("project dir");
        }
        finally { Cleanup(builtin, project); }
    }

    [Fact]
    public void Load_RecursiveScan_NestedSubDirsIncluded()
    {
        var dir = CreateTempDir();
        try
        {
            WriteDirSkill(dir, "tool", ValidSkill.Replace("commit", "tool"), new()
            {
                ["references/api/v1.md"] = "# v1",
                ["references/api/v2.md"] = "# v2"
            });
            var loader = new SkillLoader(builtinDir: dir, userHome: "/nonexistent", projectRoot: "/nonexistent");
            var skills = loader.Load();

            var def = skills["tool"];
            def.Resources.Should().HaveCount(2);
            def.Resources.Should().Contain(r => r.RelativePath == Path.Combine("references", "api", "v1.md"));
            def.Resources.Should().Contain(r => r.RelativePath == Path.Combine("references", "api", "v2.md"));
        }
        finally { Cleanup(dir); }
    }

    [Fact]
    public void Load_HiddenFiles_Skipped()
    {
        var dir = CreateTempDir();
        try
        {
            WriteDirSkill(dir, "tool", ValidSkill.Replace("commit", "tool"), new()
            {
                ["scripts/.DS_Store"] = "junk",
                ["scripts/run.sh"] = "#!/bin/bash"
            });
            var loader = new SkillLoader(builtinDir: dir, userHome: "/nonexistent", projectRoot: "/nonexistent");
            var skills = loader.Load();

            var def = skills["tool"];
            def.Resources.Should().ContainSingle(r => r.Kind == SkillResourceKind.Script);
            def.Resources.Should().NotContain(r => r.RelativePath.Contains(".DS_Store"));
        }
        finally { Cleanup(dir); }
    }

    [Fact]
    public void Load_TopLevelSkillMd_Skipped()
    {
        var dir = CreateTempDir();
        try
        {
            // 顶层 SKILL.md（非子目录内）应被跳过，不当作单文件 Skill 解析
            WriteSkill(dir, "SKILL.md", ValidSkill);
            var loader = new SkillLoader(builtinDir: dir, userHome: "/nonexistent", projectRoot: "/nonexistent");
            var skills = loader.Load();

            skills.Should().BeEmpty();
        }
        finally { Cleanup(dir); }
    }

    [Fact]
    public void Load_ResourceAbsolutePaths_AreFullPath()
    {
        var dir = CreateTempDir();
        try
        {
            WriteDirSkill(dir, "tool", ValidSkill.Replace("commit", "tool"), new()
            {
                ["scripts/run.sh"] = "#!/bin/bash"
            });
            var loader = new SkillLoader(builtinDir: dir, userHome: "/nonexistent", projectRoot: "/nonexistent");
            var skills = loader.Load();

            var res = skills["tool"].Resources.Single();
            res.AbsolutePath.Should().Be(Path.GetFullPath(Path.Combine(dir, "tool", "scripts", "run.sh")));
        }
        finally { Cleanup(dir); }
    }

    [Fact]
    public void Load_DirectoryFormat_EquivalentToSingleFile()
    {
        var dirFile = CreateTempDir();
        var dirDir = CreateTempDir();
        try
        {
            WriteSkill(dirFile, "x.md", ValidSkill.Replace("commit", "x"));
            WriteDirSkill(dirDir, "x", ValidSkill.Replace("commit", "x"));

            var loader1 = new SkillLoader(builtinDir: dirFile, userHome: "/nonexistent", projectRoot: "/nonexistent");
            var loader2 = new SkillLoader(builtinDir: dirDir, userHome: "/nonexistent", projectRoot: "/nonexistent");
            var f1 = loader1.Load()["x"];
            var f2 = loader2.Load()["x"];

            f1.Meta.Name.Should().Be(f2.Meta.Name);
            f1.Meta.Description.Should().Be(f2.Meta.Description);
            f1.Body.Should().Be(f2.Body);
            f1.Resources.Should().BeEmpty();
            f2.Resources.Should().BeEmpty();
            f1.SkillDir.Should().BeNull();
            f2.SkillDir.Should().NotBeNull();
        }
        finally { Cleanup(dirFile, dirDir); }
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
