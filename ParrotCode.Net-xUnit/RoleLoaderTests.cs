using System.IO;
using ParrotCode;

namespace ParrotCode.xUnit;

/// <summary>
/// RoleLoader 单元测试（迭代 14a）。
/// 覆盖三级扫描、同名覆盖、frontmatter 解析、格式错误降级。
/// 与 SkillLoaderTests 同构——跨平台用 Path.GetTempPath + Path.Combine 构造临时目录。
/// </summary>
public class RoleLoaderTests
{
    /// <summary>创建临时目录并返回其路径，测试后由调用方清理。</summary>
    private static string CreateTempDir()
        => Path.Combine(Path.GetTempPath(), "roletest_" + Guid.NewGuid().ToString("N"));

    private static void WriteRole(string dir, string fileName, string content)
    {
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, fileName), content);
    }

    private static void Cleanup(params string[] dirs)
    {
        foreach (var d in dirs)
        {
            try { if (Directory.Exists(d)) Directory.Delete(d, recursive: true); }
            catch { /* 测试清理忽略 */ }
        }
    }

    private const string ValidRole = """
        ---
        name: explorer
        description: explore role
        tools_allow:
          - read_file
          - glob
          - grep
          - run_command
        tools_deny:
          - write_file
          - sub_agent
        ---
        # Explorer 角色
        do explore
        """;

    // ---- 三级扫描 ----

    [Fact]
    public void Load_BuiltinOnly_LoadsRole()
    {
        var builtin = CreateTempDir();
        try
        {
            WriteRole(builtin, "explorer.md", ValidRole);
            var loader = new RoleLoader(builtinDir: builtin, userHome: "/nonexistent", projectRoot: "/nonexistent");
            var roles = loader.Load();

            roles.Should().HaveCount(1);
            roles.Should().ContainKey("explorer");
            var def = roles["explorer"];
            def.Meta.Description.Should().Be("explore role");
            def.Meta.ToolsAllow.Should().Contain(new[] { "read_file", "glob", "grep", "run_command" });
            def.Meta.ToolsDeny.Should().Contain(new[] { "write_file", "sub_agent" });
            def.Body.Should().Contain("# Explorer 角色");
            def.Source.Should().Be(RoleSource.Builtin);
        }
        finally { Cleanup(builtin); }
    }

    [Fact]
    public void Load_ProjectOnly_LoadsRole()
    {
        var project = CreateTempDir();
        try
        {
            WriteRole(Path.Combine(project, ".parrotcode", "roles"), "custom.md", ValidRole);
            var loader = new RoleLoader(builtinDir: "/nonexistent", userHome: "/nonexistent", projectRoot: project);
            var roles = loader.Load();

            roles.Should().HaveCount(1);
            roles["explorer"].Source.Should().Be(RoleSource.Project);
        }
        finally { Cleanup(project); }
    }

    [Fact]
    public void Load_GlobalOnly_LoadsRole()
    {
        var global = CreateTempDir();
        try
        {
            WriteRole(Path.Combine(global, ".parrotcode", "roles"), "explorer.md", ValidRole);
            var loader = new RoleLoader(builtinDir: "/nonexistent", userHome: global, projectRoot: "/nonexistent");
            var roles = loader.Load();

            roles.Should().HaveCount(1);
            roles["explorer"].Source.Should().Be(RoleSource.Global);
        }
        finally { Cleanup(global); }
    }

    [Fact]
    public void Load_AllThreeLevels_AllLoaded()
    {
        var builtin = CreateTempDir();
        var global = CreateTempDir();
        var project = CreateTempDir();
        try
        {
            WriteRole(builtin, "explorer.md", ValidRole);
            WriteRole(Path.Combine(global, ".parrotcode", "roles"), "planner.md", """
                ---
                name: planner
                description: plan role
                ---
                planner body
                """);
            WriteRole(Path.Combine(project, ".parrotcode", "roles"), "general.md", """
                ---
                name: general
                description: general role
                ---
                general body
                """);
            var loader = new RoleLoader(builtinDir: builtin, userHome: global, projectRoot: project);
            var roles = loader.Load();

            roles.Should().HaveCount(3);
            roles.Should().ContainKeys("explorer", "planner", "general");
        }
        finally { Cleanup(builtin, global, project); }
    }

    // ---- 同名覆盖 ----

    [Fact]
    public void Load_ProjectOverridesBuiltin()
    {
        var builtin = CreateTempDir();
        var project = CreateTempDir();
        try
        {
            WriteRole(builtin, "explorer.md", ValidRole);
            WriteRole(Path.Combine(project, ".parrotcode", "roles"), "explorer.md", """
                ---
                name: explorer
                description: project overridden
                ---
                project body
                """);
            var loader = new RoleLoader(builtinDir: builtin, userHome: "/nonexistent", projectRoot: project);
            var roles = loader.Load();

            roles["explorer"].Meta.Description.Should().Be("project overridden");
            roles["explorer"].Source.Should().Be(RoleSource.Project);
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
            WriteRole(builtin, "explorer.md", ValidRole);
            WriteRole(Path.Combine(global, ".parrotcode", "roles"), "explorer.md", """
                ---
                name: explorer
                description: global override
                ---
                global body
                """);
            var loader = new RoleLoader(builtinDir: builtin, userHome: global, projectRoot: "/nonexistent");
            var roles = loader.Load();

            roles["explorer"].Meta.Description.Should().Be("global override");
            roles["explorer"].Source.Should().Be(RoleSource.Global);
        }
        finally { Cleanup(builtin, global); }
    }

    [Fact]
    public void Load_ProjectOverridesGlobalAndBuiltin()
    {
        var builtin = CreateTempDir();
        var global = CreateTempDir();
        var project = CreateTempDir();
        try
        {
            WriteRole(builtin, "explorer.md", ValidRole);
            WriteRole(Path.Combine(global, ".parrotcode", "roles"), "explorer.md", """
                ---
                name: explorer
                description: global version
                ---
                global body
                """);
            WriteRole(Path.Combine(project, ".parrotcode", "roles"), "explorer.md", """
                ---
                name: explorer
                description: project version
                ---
                project body
                """);
            var loader = new RoleLoader(builtinDir: builtin, userHome: global, projectRoot: project);
            var roles = loader.Load();

            roles["explorer"].Meta.Description.Should().Be("project version");
            roles["explorer"].Source.Should().Be(RoleSource.Project);
        }
        finally { Cleanup(builtin, global, project); }
    }

    // ---- frontmatter 解析 / 错误降级 ----

    [Fact]
    public void Load_MissingFrontmatter_SkipsFile()
    {
        var dir = CreateTempDir();
        try
        {
            WriteRole(dir, "bad.md", "# just markdown, no frontmatter");
            var loader = new RoleLoader(builtinDir: dir, userHome: "/nonexistent", projectRoot: "/nonexistent");
            var roles = loader.Load();

            roles.Should().BeEmpty();
        }
        finally { Cleanup(dir); }
    }

    [Fact]
    public void Load_EmptyName_SkipsFile()
    {
        var dir = CreateTempDir();
        try
        {
            WriteRole(dir, "bad.md", """
                ---
                name: ""
                description: no name
                ---
                body
                """);
            var loader = new RoleLoader(builtinDir: dir, userHome: "/nonexistent", projectRoot: "/nonexistent");
            var roles = loader.Load();

            roles.Should().BeEmpty();
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
            WriteRole(dir, "bad.md", """
                ---
                name: bad
                description: malformed
                tools_allow: not_a_list
                ---
                body
                """);
            var loader = new RoleLoader(builtinDir: dir, userHome: "/nonexistent", projectRoot: "/nonexistent");
            var roles = loader.Load();

            roles.Should().BeEmpty();
        }
        finally { Cleanup(dir); }
    }

    [Fact]
    public void Load_EmptyBody_ParsesSuccessfully()
    {
        var dir = CreateTempDir();
        try
        {
            WriteRole(dir, "empty.md", """
                ---
                name: empty
                description: empty body
                ---

                """);
            var loader = new RoleLoader(builtinDir: dir, userHome: "/nonexistent", projectRoot: "/nonexistent");
            var roles = loader.Load();

            roles.Should().HaveCount(1);
            roles["empty"].Body.Should().BeEmpty();
        }
        finally { Cleanup(dir); }
    }

    [Fact]
    public void Load_PartialFrontmatter_OnlyNameRequired()
    {
        var dir = CreateTempDir();
        try
        {
            WriteRole(dir, "minimal.md", """
                ---
                name: minimal
                ---
                body
                """);
            var loader = new RoleLoader(builtinDir: dir, userHome: "/nonexistent", projectRoot: "/nonexistent");
            var roles = loader.Load();

            roles.Should().HaveCount(1);
            roles["minimal"].Meta.Description.Should().BeEmpty();
            roles["minimal"].Meta.ToolsAllow.Should().BeEmpty();
            roles["minimal"].Meta.ToolsDeny.Should().BeEmpty();
        }
        finally { Cleanup(dir); }
    }

    // ---- 目录不存在 ----

    [Fact]
    public void Load_NonExistentDirs_ReturnsEmpty()
    {
        var loader = new RoleLoader(
            builtinDir: "/nonexistent_builtin",
            userHome: "/nonexistent_home",
            projectRoot: "/nonexistent_project");
        var roles = loader.Load();

        roles.Should().BeEmpty();
    }

    // ---- tools_allow / tools_deny 列表解析 ----

    [Fact]
    public void Load_ToolsAllowDeny_ParsedAsLists()
    {
        var dir = CreateTempDir();
        try
        {
            WriteRole(dir, "multi.md", """
                ---
                name: multi
                description: multi tools
                tools_allow:
                  - read_file
                  - glob
                  - grep
                tools_deny:
                  - write_file
                  - edit_file
                  - run_command
                ---
                body
                """);
            var loader = new RoleLoader(builtinDir: dir, userHome: "/nonexistent", projectRoot: "/nonexistent");
            var roles = loader.Load();

            var def = roles["multi"];
            def.Meta.ToolsAllow.Should().HaveCount(3)
                .And.Contain(new[] { "read_file", "glob", "grep" });
            def.Meta.ToolsDeny.Should().HaveCount(3)
                .And.Contain(new[] { "write_file", "edit_file", "run_command" });
        }
        finally { Cleanup(dir); }
    }

    // ---- AppContext.BaseDirectory 定位 Builtin ----

    [Fact]
    public void Constructor_DefaultBuiltinDir_UsesAppContextBaseDirectory()
    {
        var loader = new RoleLoader();

        // 默认 _builtinDir 应基于 AppContext.BaseDirectory + SubAgent/Roles/Builtin
        // 无法直接断言私有字段，但可以通过加载内置角色验证路径正确
        // （编译输出目录含 Builtin 角色文件，由 csproj Content Include 部署）
        var roles = loader.Load();
        // 内置角色应至少含 explorer / planner / general
        roles.Should().ContainKey("explorer");
        roles.Should().ContainKey("planner");
        roles.Should().ContainKey("general");
    }

    // ---- 自定义参数 ----

    [Fact]
    public void Constructor_CustomPaths_Respected()
    {
        var builtin = CreateTempDir();
        var global = CreateTempDir();
        var project = CreateTempDir();
        try
        {
            WriteRole(builtin, "a.md", """
                ---
                name: role_a
                description: builtin role
                ---
                body a
                """);
            WriteRole(Path.Combine(global, ".parrotcode", "roles"), "b.md", """
                ---
                name: role_b
                description: global role
                ---
                body b
                """);
            WriteRole(Path.Combine(project, ".parrotcode", "roles"), "c.md", """
                ---
                name: role_c
                description: project role
                ---
                body c
                """);
            var loader = new RoleLoader(
                builtinDir: builtin,
                userHome: global,
                projectRoot: project);
            var roles = loader.Load();

            roles.Should().HaveCount(3);
            roles["role_a"].Source.Should().Be(RoleSource.Builtin);
            roles["role_b"].Source.Should().Be(RoleSource.Global);
            roles["role_c"].Source.Should().Be(RoleSource.Project);
        }
        finally { Cleanup(builtin, global, project); }
    }

    [Fact]
    public void Constructor_CustomProjectRoot_ProjectRolesLoaded()
    {
        var project = CreateTempDir();
        try
        {
            WriteRole(Path.Combine(project, ".parrotcode", "roles"), "proj.md", """
                ---
                name: proj_role
                description: project role
                ---
                project body
                """);
            var loader = new RoleLoader(
                builtinDir: "/nonexistent",
                userHome: "/nonexistent",
                projectRoot: project);
            var roles = loader.Load();

            roles.Should().HaveCount(1);
            roles["proj_role"].Source.Should().Be(RoleSource.Project);
        }
        finally { Cleanup(project); }
    }
}
