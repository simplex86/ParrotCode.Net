using System.IO;
using ParrotCode;

namespace ParrotCode.xUnit;

/// <summary>
/// InstructionLoader 单元测试（迭代 10c）。
/// 覆盖三级扫描、@include 嵌套、嵌套深度限制、路径解析、Sources 去重、GetSummary。
/// </summary>
public class InstructionLoaderTests
{
    private sealed class TempDir : IDisposable
    {
        public string Dir { get; } = Directory.CreateTempSubdirectory("parrotcode-instr-").FullName;
        public string WriteFile(string relativePath, string content)
        {
            var path = Path.Combine(Dir, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, content);
            return path;
        }
        public string FullPath(string relativePath) => Path.GetFullPath(Path.Combine(Dir, relativePath));
        public void Dispose() { try { Directory.Delete(Dir, recursive: true); } catch { } }
    }

    [Fact]
    public void Load_NoInstructionFiles_ReturnsEmpty()
    {
        using var dir = new TempDir();
        var loader = new InstructionLoader(projectRoot: dir.Dir, userHome: dir.FullPath("fakehome"));

        var result = loader.Load();

        result.HasInstructions.Should().BeFalse();
        result.Content.Should().BeEmpty();
        result.Sources.Should().BeEmpty();
    }

    [Fact]
    public void Load_ProjectRootFile_Loaded()
    {
        using var dir = new TempDir();
        dir.WriteFile("PARROTCODE.md", "# 约定\n- 用中文回复");

        var loader = new InstructionLoader(projectRoot: dir.Dir, userHome: dir.FullPath("fakehome"));
        var result = loader.Load();

        result.HasInstructions.Should().BeTrue();
        result.Content.Should().Contain("用中文回复");
        result.Content.Should().Contain("## 项目指令");
        result.Sources.Should().Contain(dir.FullPath("PARROTCODE.md"));
    }

    [Fact]
    public void Load_GlobalFile_Loaded()
    {
        using var dir = new TempDir();
        var homeDir = dir.FullPath("home");
        Directory.CreateDirectory(Path.Combine(homeDir, ".parrotcode"));
        File.WriteAllText(Path.Combine(homeDir, ".parrotcode", "instructions.md"), "全局约定");

        var loader = new InstructionLoader(projectRoot: dir.Dir, userHome: homeDir);
        var result = loader.Load();

        result.HasInstructions.Should().BeTrue();
        result.Content.Should().Contain("全局约定");
        result.Content.Should().Contain("## 全局指令");
    }

    [Fact]
    public void Load_LocalFile_Loaded()
    {
        using var dir = new TempDir();
        dir.WriteFile(".parrotcode/instructions.md", "本地覆盖");

        var loader = new InstructionLoader(projectRoot: dir.Dir, userHome: dir.FullPath("fakehome"));
        var result = loader.Load();

        result.HasInstructions.Should().BeTrue();
        result.Content.Should().Contain("本地覆盖");
        result.Content.Should().Contain("## 本地指令");
    }

    [Fact]
    public void Load_ThreeLevelsAllPresent_AllSectionsMerged()
    {
        using var dir = new TempDir();
        var homeDir = dir.FullPath("home");
        Directory.CreateDirectory(Path.Combine(homeDir, ".parrotcode"));
        File.WriteAllText(Path.Combine(homeDir, ".parrotcode", "instructions.md"), "全局内容");
        dir.WriteFile("PARROTCODE.md", "项目内容");
        dir.WriteFile(".parrotcode/instructions.md", "本地内容");

        var loader = new InstructionLoader(projectRoot: dir.Dir, userHome: homeDir);
        var result = loader.Load();

        result.HasInstructions.Should().BeTrue();
        result.Content.Should().Contain("全局内容");
        result.Content.Should().Contain("项目内容");
        result.Content.Should().Contain("本地内容");
    }

    [Fact]
    public void Load_ThreeLevels_OrderIsGlobalProjectLocal()
    {
        using var dir = new TempDir();
        var homeDir = dir.FullPath("home");
        Directory.CreateDirectory(Path.Combine(homeDir, ".parrotcode"));
        File.WriteAllText(Path.Combine(homeDir, ".parrotcode", "instructions.md"), "AAA");
        dir.WriteFile("PARROTCODE.md", "BBB");
        dir.WriteFile(".parrotcode/instructions.md", "CCC");

        var loader = new InstructionLoader(projectRoot: dir.Dir, userHome: homeDir);
        var result = loader.Load();

        var globalIdx = result.Content.IndexOf("AAA");
        var projectIdx = result.Content.IndexOf("BBB");
        var localIdx = result.Content.IndexOf("CCC");

        globalIdx.Should().BeLessThan(projectIdx);
        projectIdx.Should().BeLessThan(localIdx);
    }

    [Fact]
    public void Load_IncludeBasic_ExpandsSubFile()
    {
        using var dir = new TempDir();
        dir.WriteFile("docs/coding-standards.md", "# 编码规范\n- 使用 var");
        dir.WriteFile("PARROTCODE.md", "# 约定\n@include docs/coding-standards.md");

        var loader = new InstructionLoader(projectRoot: dir.Dir, userHome: dir.FullPath("fakehome"));
        var result = loader.Load();

        result.Content.Should().Contain("编码规范");
        result.Content.Should().Contain("使用 var");
        result.Sources.Should().Contain(dir.FullPath("docs/coding-standards.md"));
    }

    [Fact]
    public void Load_IncludeWithSpaces_SupportsQuotedPath()
    {
        using var dir = new TempDir();
        dir.WriteFile("docs/my standards.md", "带空格的文件");
        dir.WriteFile("PARROTCODE.md", "@include \"docs/my standards.md\"");

        var loader = new InstructionLoader(projectRoot: dir.Dir, userHome: dir.FullPath("fakehome"));
        var result = loader.Load();

        result.Content.Should().Contain("带空格的文件");
    }

    [Fact]
    public void Load_IncludeNested_DeepNestingExpands()
    {
        using var dir = new TempDir();
        dir.WriteFile("level3.md", "第三层内容");
        dir.WriteFile("level2.md", "第二层\n@include level3.md");
        dir.WriteFile("PARROTCODE.md", "@include level2.md");

        var loader = new InstructionLoader(projectRoot: dir.Dir, userHome: dir.FullPath("fakehome"));
        var result = loader.Load();

        result.Content.Should().Contain("第二层");
        result.Content.Should().Contain("第三层内容");
    }

    [Fact]
    public void Load_IncludeExceedsMaxDepth_SkipsAndDoesNotCrash()
    {
        using var dir = new TempDir();
        // 构造 5 层嵌套，maxDepth=2 → 第 3 层后跳过
        dir.WriteFile("level5.md", "L5");
        dir.WriteFile("level4.md", "L4\n@include level5.md");
        dir.WriteFile("level3.md", "L3\n@include level4.md");
        dir.WriteFile("level2.md", "L2\n@include level3.md");
        dir.WriteFile("PARROTCODE.md", "L1\n@include level2.md");

        var loader = new InstructionLoader(
            projectRoot: dir.Dir,
            userHome: dir.FullPath("fakehome"),
            maxIncludeDepth: 2);
        var result = loader.Load();

        // 不崩溃，L1/L2/L3 可出现，L5 被跳过
        result.HasInstructions.Should().BeTrue();
        result.Content.Should().Contain("L1");
    }

    [Fact]
    public void Load_IncludeCircularReference_DoesNotStackOverflow()
    {
        using var dir = new TempDir();
        // A 引用 B，B 引用 A → 循环引用，靠深度限制兜底
        dir.WriteFile("a.md", "A\n@include b.md");
        dir.WriteFile("b.md", "B\n@include a.md");
        dir.WriteFile("PARROTCODE.md", "@include a.md");

        var loader = new InstructionLoader(
            projectRoot: dir.Dir,
            userHome: dir.FullPath("fakehome"),
            maxIncludeDepth: 3);

        // 不应抛出 StackOverflowException
        var result = loader.Load();
        result.HasInstructions.Should().BeTrue();
    }

    [Fact]
    public void Load_IncludeNonExistentFile_ReplacedWithWarning()
    {
        using var dir = new TempDir();
        dir.WriteFile("PARROTCODE.md", "内容\n@include nonexistent.md\n后续");

        var loader = new InstructionLoader(projectRoot: dir.Dir, userHome: dir.FullPath("fakehome"));
        var result = loader.Load();

        result.Content.Should().Contain("指令引用失败");
        result.Content.Should().Contain("后续");  // 替换后后续内容保留
    }

    [Fact]
    public void Load_IncludeRelativePath_BasedOnIncludingFileDirectory()
    {
        using var dir = new TempDir();
        dir.WriteFile("docs/sub/file.md", "子目录文件内容");
        dir.WriteFile("docs/main.md", "@include sub/file.md");
        dir.WriteFile("PARROTCODE.md", "@include docs/main.md");

        var loader = new InstructionLoader(projectRoot: dir.Dir, userHome: dir.FullPath("fakehome"));
        var result = loader.Load();

        result.Content.Should().Contain("子目录文件内容");
    }

    [Fact]
    public void Load_IncludeAbsolutePath_UsedDirectly()
    {
        using var dir = new TempDir();
        var absFile = dir.FullPath("external/abs.md");
        Directory.CreateDirectory(Path.GetDirectoryName(absFile)!);
        File.WriteAllText(absFile, "绝对路径文件");
        dir.WriteFile("PARROTCODE.md", $"@include {absFile}");

        var loader = new InstructionLoader(projectRoot: dir.Dir, userHome: dir.FullPath("fakehome"));
        var result = loader.Load();

        result.Content.Should().Contain("绝对路径文件");
    }

    [Fact]
    public void Load_SourcesContainsAllIncludedFiles()
    {
        using var dir = new TempDir();
        dir.WriteFile("docs/a.md", "A内容");
        dir.WriteFile("docs/b.md", "B内容");
        dir.WriteFile("PARROTCODE.md", "@include docs/a.md\n@include docs/b.md");

        var loader = new InstructionLoader(projectRoot: dir.Dir, userHome: dir.FullPath("fakehome"));
        var result = loader.Load();

        result.Sources.Should().Contain(dir.FullPath("PARROTCODE.md"));
        result.Sources.Should().Contain(dir.FullPath("docs/a.md"));
        result.Sources.Should().Contain(dir.FullPath("docs/b.md"));
    }

    [Fact]
    public void Load_SourcesDeduplicated()
    {
        using var dir = new TempDir();
        dir.WriteFile("shared.md", "共享内容");
        dir.WriteFile("PARROTCODE.md", "@include shared.md\n@include shared.md");

        var loader = new InstructionLoader(projectRoot: dir.Dir, userHome: dir.FullPath("fakehome"));
        var result = loader.Load();

        // shared.md 被引用两次，但 Sources 去重后只出现一次
        var sharedCount = result.Sources.Count(s => s == dir.FullPath("shared.md"));
        sharedCount.Should().Be(1);
    }

    [Fact]
    public void Load_CustomProjectInstructionsPath_Used()
    {
        using var dir = new TempDir();
        dir.WriteFile("MY_RULES.md", "自定义路径指令");

        var loader = new InstructionLoader(
            projectRoot: dir.Dir,
            userHome: dir.FullPath("fakehome"),
            projectInstructionsPath: "MY_RULES.md");
        var result = loader.Load();

        result.Content.Should().Contain("自定义路径指令");
        result.Sources.Should().Contain(dir.FullPath("MY_RULES.md"));
    }

    [Fact]
    public void GetSummary_NoInstructions_ReturnsNotLoaded()
    {
        var result = new InstructionResult();
        InstructionLoader.GetSummary(result).Should().Be("未加载");
    }

    [Fact]
    public void GetSummary_WithInstructions_ReturnsFileCount()
    {
        var result = new InstructionResult
        {
            Content = "有内容",
            Sources = new[] { "/path/to/PARROTCODE.md", "/path/to/docs/rules.md" }
        };
        var summary = InstructionLoader.GetSummary(result);
        summary.Should().Contain("2 个文件");
        summary.Should().Contain("PARROTCODE.md");
        summary.Should().Contain("rules.md");
    }

    [Fact]
    public void InstructionResult_HasInstructions_FalseWhenEmpty()
    {
        new InstructionResult().HasInstructions.Should().BeFalse();
        new InstructionResult { Content = "   " }.HasInstructions.Should().BeFalse();
    }

    [Fact]
    public void InstructionResult_HasInstructions_TrueWhenNonEmpty()
    {
        new InstructionResult { Content = "指令内容" }.HasInstructions.Should().BeTrue();
    }
}
