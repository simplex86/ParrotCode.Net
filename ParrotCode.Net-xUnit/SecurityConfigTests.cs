using System.IO;
using ParrotCode;

namespace ParrotCode.xUnit;

/// <summary>
/// SecurityConfig 与装配单元测试（迭代 8c）。
/// 覆盖验收标准 08c-04 ~ 08c-10：YAML 解析、相对路径规范化、非法路径忽略、默认值、兼容拼法。
/// 通过 ConfigLoader 加载完整 YAML 验证 SecurityConfig 字段；通过 App.NormalizePaths 验证规范化行为。
/// </summary>
public class SecurityConfigTests
{
    /// <summary>创建临时目录并在其中写文件，用完自动清理。</summary>
    private sealed class TestDir : IDisposable
    {
        public string Dir { get; } = Directory.CreateTempSubdirectory("parrotcode-8c-").FullName;

        public string WriteFile(string name, string content)
        {
            var path = Path.Combine(Dir, name);
            File.WriteAllText(path, content);
            return path;
        }

        public void Dispose()
        {
            try { Directory.Delete(Dir, recursive: true); } catch { }
        }
    }

    private const string MockYamlHeader = """
        active_provider: mock
        providers:
          - name: mock
            protocol: mock
            model: mock-1
        """;

    private static AppConfig LoadYaml(string yaml)
    {
        using var dir = new TestDir();
        var path = dir.WriteFile(".parrotcode.yaml", yaml);
        return ConfigLoader.Load(path);
    }

    // —— 08c-04 security.level: strict 解析为 SecurityLevel.Strict ——

    [Fact]
    public void SecurityConfig_Level_Strict_Parsed()
    {
        var yaml = MockYamlHeader + "\nsecurity:\n  level: strict\n";
        var config = LoadYaml(yaml);

        config.Security.Should().NotBeNull();
        config.Security!.Level.Should().Be("strict");
        // 进一步验证 SecurityLevelParser 正确映射
        SecurityLevelParser.Parse(config.Security.Level).Should().Be(SecurityLevel.Strict);
    }

    // —— 08c-09 旧拼法 level: permisive 解析为 Permissive（兼容）——

    [Fact]
    public void SecurityConfig_Level_PermisiveLegacy_ParsedAsPermissive()
    {
        var yaml = MockYamlHeader + "\nsecurity:\n  level: permisive\n";
        var config = LoadYaml(yaml);

        config.Security!.Level.Should().Be("permisive");
        SecurityLevelParser.Parse(config.Security.Level).Should().Be(SecurityLevel.Permissive,
            "旧拼法 'permisive' 应兼容映射到 Permissive");
    }

    [Fact]
    public void SecurityConfig_Level_Permissive_Parsed()
    {
        var yaml = MockYamlHeader + "\nsecurity:\n  level: permissive\n";
        var config = LoadYaml(yaml);

        SecurityLevelParser.Parse(config.Security!.Level).Should().Be(SecurityLevel.Permissive);
    }

    // —— 08c-06 deny_paths 解析为 SecurityConfig.DenyPaths ——

    [Fact]
    public void SecurityConfig_DenyPaths_Parsed()
    {
        var yaml = MockYamlHeader + """
            
            security:
              level: normal
              deny_paths:
                - /etc/secrets
                - /tmp/blocked
            """;
        var config = LoadYaml(yaml);

        config.Security!.DenyPaths.Should().HaveCount(2);
        config.Security.DenyPaths.Should().Contain("/etc/secrets");
        config.Security.DenyPaths.Should().Contain("/tmp/blocked");
    }

    // —— 08c-07 extra_blacklist 正则数组解析 ——

    [Fact]
    public void SecurityConfig_ExtraBlacklist_Parsed()
    {
        // YAML 中反斜杠需转义：\\b 在 YAML 中是 \b（实际正则模式 \b）
        var yaml = MockYamlHeader + """
            
            security:
              level: normal
              extra_blacklist:
                - "\\bkubectl\\s+delete\\b"
                - "\\bdocker\\s+rm\\s+-f"
            """;
        var config = LoadYaml(yaml);

        config.Security!.ExtraBlacklist.Should().HaveCount(2);
        // YAML 解析后 \\b 变为 \b（正则边界）
        config.Security.ExtraBlacklist.Should().Contain(@"\bkubectl\s+delete\b");
        config.Security.ExtraBlacklist.Should().Contain(@"\bdocker\s+rm\s+-f");
    }

    // —— 08c-08 无 security 段时默认 null（App 层回退 Normal + 空白名单）——

    [Fact]
    public void SecurityConfig_MissingSection_DefaultsNull()
    {
        var config = LoadYaml(MockYamlHeader);

        config.Security.Should().BeNull("无 security 段时 SecurityConfig 为 null，App 层默认 Normal + 空白名单");
    }

    // —— SecurityConfig 部分字段缺失时默认空数组 ——

    [Fact]
    public void SecurityConfig_OnlyLevel_DefaultsEmptyLists()
    {
        var yaml = MockYamlHeader + "\nsecurity:\n  level: strict\n";
        var config = LoadYaml(yaml);

        config.Security!.AllowPaths.Should().NotBeNull().And.BeEmpty("未配置 allow_paths 时为空数组");
        config.Security.DenyPaths.Should().NotBeNull().And.BeEmpty();
        config.Security.ExtraBlacklist.Should().NotBeNull().And.BeEmpty();
    }

    // —— 08c-05 allow_paths 相对路径规范化为绝对 ——

    [Fact]
    public void NormalizePaths_Relative_ResolvedToAbsolute()
    {
        var projectRoot = Path.Combine(Path.GetTempPath(), "parrotcode-8c-proj");

        var result = App.NormalizePaths(new[] { "../sibling", "sub/dir" }, projectRoot);

        result.Should().HaveCount(2);
        Path.IsPathFullyQualified(result[0]).Should().BeTrue("相对路径应被规范化为绝对");
        result[0].Should().Be(Path.GetFullPath("../sibling", projectRoot));
        result[1].Should().Be(Path.GetFullPath("sub/dir", projectRoot));
    }

    [Fact]
    public void NormalizePaths_Absolute_Preserved()
    {
        var projectRoot = Path.Combine(Path.GetTempPath(), "parrotcode-8c-proj");
        var absPath = OperatingSystem.IsWindows() ? @"C:\external\libs" : "/external/libs";

        var result = App.NormalizePaths(new[] { absPath }, projectRoot);

        result.Should().HaveCount(1);
        result[0].Should().Be(absPath, "绝对路径应原样保留（已规范化）");
    }

    // —— 08c-10 非法路径在 allow_paths 中被忽略不抛异常 ——

    [Fact]
    public void NormalizePaths_InvalidPath_IgnoredNoException()
    {
        var projectRoot = Path.Combine(Path.GetTempPath(), "parrotcode-8c-proj");
        // 含 null 字符的路径在所有平台都被 Path.GetFullPath 视为非法字符
        var invalidPath = "foo\0bar";

        var act = () => App.NormalizePaths(new[] { invalidPath, "../valid" }, projectRoot);

        act.Should().NotThrow("非法路径应被忽略，不抛异常");
        var result = act();
        result.Should().HaveCount(1, "非法路径被忽略，仅保留有效路径");
        result[0].Should().Be(Path.GetFullPath("../valid", projectRoot));
    }

    [Fact]
    public void NormalizePaths_EmptyAndWhitespace_Ignored()
    {
        var projectRoot = Path.Combine(Path.GetTempPath(), "parrotcode-8c-proj");

        var result = App.NormalizePaths(new[] { "", "   ", null!, "../sibling" }, projectRoot);

        result.Should().HaveCount(1, "空/空白/null 路径被忽略");
        result[0].Should().Be(Path.GetFullPath("../sibling", projectRoot));
    }

    [Fact]
    public void NormalizePaths_NullList_ReturnsEmpty()
    {
        var projectRoot = Path.Combine(Path.GetTempPath(), "parrotcode-8c-proj");

        var result = App.NormalizePaths(null, projectRoot);

        result.Should().BeEmpty();
    }

    // —— 装配集成：SecurityContext 构造（验证字段传递）——

    [Fact]
    public void SecurityContext_Constructed_WithAllFields()
    {
        var projectRoot = Path.Combine(Path.GetTempPath(), "parrotcode-8c-proj");
        var allowPath = Path.GetFullPath("../sibling", projectRoot);
        var denyPath = "/etc/secrets";
        var extraBlacklist = new[] { @"\bkubectl\s+delete\b" };

        var ctx = new SecurityContext
        {
            ProjectRoot = projectRoot,
            AllowPaths = new[] { allowPath },
            DenyPaths = new[] { denyPath },
            ExtraBlacklist = extraBlacklist
        };

        ctx.ProjectRoot.Should().Be(projectRoot);
        ctx.AllowPaths.Should().Contain(allowPath);
        ctx.DenyPaths.Should().Contain(denyPath);
        ctx.ExtraBlacklist.Should().Contain(@"\bkubectl\s+delete\b");
    }

    // —— 端到端装配验证：SecurityGuard + SecurityConfig 协作 ——

    [Fact]
    public async Task SecurityGuard_WithConfigContext_StrictBlocksOutsidePath()
    {
        // 模拟 App 装配：从 SecurityConfig 构造 SecurityContext + SecurityGuard
        var projectRoot = Path.Combine(Path.GetTempPath(), "parrotcode-8c-e2e");
        var externalPath = OperatingSystem.IsWindows() ? @"C:\parrotcode-8c-external" : "/parrotcode-8c-external";

        // 模拟 SecurityConfig：Strict + 空 AllowPaths
        var secConfig = new SecurityConfig
        {
            Level = "strict",
            AllowPaths = Array.Empty<string>(),
            DenyPaths = Array.Empty<string>(),
            ExtraBlacklist = Array.Empty<string>()
        };

        // 模拟 App.NormalizePaths 装配
        var secCtx = new SecurityContext
        {
            ProjectRoot = projectRoot,
            AllowPaths = App.NormalizePaths(secConfig.AllowPaths, projectRoot),
            DenyPaths = App.NormalizePaths(secConfig.DenyPaths, projectRoot),
            ExtraBlacklist = (secConfig.ExtraBlacklist ?? Array.Empty<string>()).ToArray()
        };
        var level = SecurityLevelParser.Parse(secConfig.Level);
        var guard = new SecurityGuard(secCtx, level);

        // 验证装配后真实拦截行为
        var call = new ToolCall("id", "read_file",
            System.Text.Json.JsonDocument.Parse($"{{\"path\":{System.Text.Json.JsonSerializer.Serialize(externalPath)}}}").RootElement.Clone());

        var result = await guard.CheckAsync(call, CancellationToken.None);

        result.Should().NotBeNull();
        result!.Success.Should().BeFalse();
        result.Error.Should().StartWith("[路径沙箱]");
    }
}
