using System.Text;
using System.Text.Json;
using ParrotCode;

namespace ParrotCode.xUnit;

/// <summary>
/// 迭代 14a 端到端 mock 场景验证：
/// 不用手搓 RoleDefinition，而是用真实的 RoleLoader 加载 Builtin 角色文件，
/// 构造一个模拟主 Agent 工具集的父 ToolRegistry，
/// 验证 (explorer / planner / general) × (Definitional / Fork) 共 6 个组合的过滤矩阵。
///
/// 这条路径覆盖 14a 的完整流水线：
///   Builtin/*.md → RoleLoader.Load → RoleRegistry → ToolFilter.Build → filtered ToolRegistry
/// </summary>
public class SubAgentFilterScenarioTests
{
    // ---- 测试替身 ----

    /// <summary>
    /// 可配置名称的 mock 工具，模拟主 Agent 的各类工具。
    /// </summary>
    private sealed class MockTool : ToolBase
    {
        private readonly string _name;
        public override string Name => _name;
        public override string Description => "mock tool";
        public override ToolCategory Category { get; }
        public override IReadOnlyList<ToolParameter> Parameters { get; } = Array.Empty<ToolParameter>();
        public override Task<ToolResult> ExecuteAsync(JsonElement input, CancellationToken cancellationToken)
            => Task.FromResult(ToolResult.Ok(""));
        public MockTool(string name, ToolCategory category = ToolCategory.Read) { _name = name; Category = category; }
    }

    /// <summary>
    /// 构造模拟主 Agent 的完整工具集（含内置工具 + MCP 工具 + sub_agent 占位）。
    /// sub_agent 工具在 14b 才真正实现，这里先注册一个同名 mock 用于验证"全局禁嵌套"过滤。
    /// </summary>
    private static ToolRegistry CreateMainAgentToolRegistry()
    {
        var registry = new ToolRegistry();
        registry.Register(new MockTool("read_file", ToolCategory.Read));
        registry.Register(new MockTool("write_file", ToolCategory.Write));
        registry.Register(new MockTool("edit_file", ToolCategory.Write));
        registry.Register(new MockTool("glob", ToolCategory.Read));
        registry.Register(new MockTool("grep", ToolCategory.Read));
        registry.Register(new MockTool("run_command", ToolCategory.Write));
        registry.Register(new MockTool("skill_loader", ToolCategory.Read));
        registry.Register(new MockTool("sub_agent", ToolCategory.Write));  // 14b 才实现，这里仅占位
        registry.Register(new MockTool("filesystem-echo", ToolCategory.Read));   // MCP 工具（hyphen 命名）
        registry.Register(new MockTool("git-status", ToolCategory.Read));        // 另一个 MCP 工具
        return registry;
    }

    /// <summary>
    /// 用 RoleLoader 加载真实 Builtin 角色文件。
    /// builtinDir 指向测试输出目录下的 SubAgent/Roles/Builtin
    /// （ParrotCode.Net.csproj 已配置 CopyToOutputDirectory）。
    /// </summary>
    private static RoleRegistry LoadBuiltinRoles()
    {
        var builtinDir = Path.Combine(AppContext.BaseDirectory, "SubAgent", "Roles", "Builtin");
        Directory.Exists(builtinDir).Should().BeTrue(
            $"Builtin 角色目录应存在：{builtinDir}（检查 csproj 的 CopyToOutputDirectory 配置）");

        var loader = new RoleLoader(
            builtinDir: builtinDir,
            userHome: "/nonexistent",   // 隔离全局目录
            projectRoot: "/nonexistent"); // 隔离项目目录
        return new RoleRegistry(loader.Load());
    }

    /// <summary>
    /// 提取过滤后 ToolRegistry 的工具名集合，方便断言。
    /// </summary>
    private static HashSet<string> GetToolNames(ToolRegistry registry)
        => registry.GetAll().Select(t => t.Name).ToHashSet(StringComparer.Ordinal);

    // ============================================================
    // 场景 1：Builtin 角色加载完整性
    // ============================================================

    [Fact]
    public void Scenario_BuiltinRoles_AllLoadedFromDisk()
    {
        var roles = LoadBuiltinRoles();

        roles.HasRoles.Should().BeTrue();
        roles.Get("explorer").Should().NotBeNull("explorer 角色应从 Builtin 加载");
        roles.Get("planner").Should().NotBeNull("planner 角色应从 Builtin 加载");
        roles.Get("general").Should().NotBeNull("general 角色应从 Builtin 加载");
        roles.GetAll().Should().HaveCount(3, "Builtin 目录应恰好含 3 个角色文件");
    }

    [Fact]
    public void Scenario_BuiltinRoles_FrontmatterParsedCorrectly()
    {
        var roles = LoadBuiltinRoles();

        // explorer：白名单 + 黑名单都应解析
        var explorer = roles.Get("explorer")!;
        explorer.Meta.Description.Should().Contain("探索项目结构");
        explorer.Meta.ToolsAllow.Should().Contain(new[] { "read_file", "glob", "grep", "run_command" });
        explorer.Meta.ToolsDeny.Should().Contain(new[] { "write_file", "edit_file", "sub_agent", "skill_loader" });
        explorer.Body.Should().Contain("# Explorer 角色");
        explorer.Source.Should().Be(RoleSource.Builtin);

        // planner：白名单更窄（无 run_command）
        var planner = roles.Get("planner")!;
        planner.Meta.ToolsAllow.Should().Contain(new[] { "read_file", "glob", "grep" });
        planner.Meta.ToolsAllow.Should().NotContain("run_command");
        planner.Meta.ToolsDeny.Should().Contain("run_command");

        // general：无白名单（继承），仅黑名单
        var general = roles.Get("general")!;
        general.Meta.ToolsAllow.Should().BeEmpty("general 角色不声明白名单，继承父工具集");
        general.Meta.ToolsDeny.Should().Contain(new[] { "sub_agent", "skill_loader" });
    }

    // ============================================================
    // 场景 2：过滤矩阵（3 角色 × 2 模式 = 6 组合）
    // ============================================================

    /// <summary>
    /// 过滤矩阵的预期结果（手工推导，作为 oracle）：
    /// 行 = 角色，列 = 模式。每个单元格列出过滤后保留的工具名。
    ///
    /// 父工具集（10 个）：
    ///   read_file, write_file, edit_file, glob, grep, run_command,
    ///   skill_loader, sub_agent, filesystem-echo, git-status
    /// </summary>
    private static readonly Dictionary<(string Role, SubAgentMode Mode), string[]> ExpectedMatrix = new()
    {
        // explorer：白名单 [read_file, glob, grep, run_command]
        //   → 无论模式，只保留白名单 ∩ 父集，且 deny 的 sub_agent/skill_loader 不在白名单里自然排除
        [("explorer", SubAgentMode.Definitional)] = new[] { "read_file", "glob", "grep", "run_command" },
        [("explorer", SubAgentMode.Fork)] = new[] { "read_file", "glob", "grep", "run_command" },

        // planner：白名单 [read_file, glob, grep]
        [("planner", SubAgentMode.Definitional)] = new[] { "read_file", "glob", "grep" },
        [("planner", SubAgentMode.Fork)] = new[] { "read_file", "glob", "grep" },

        // general：无白名单（继承全部），deny=[sub_agent, skill_loader]
        //   Definitional：Fork 不额外禁 skill_loader，但角色已 deny → 排除 sub_agent + skill_loader
        //   Fork：同样排除 sub_agent + skill_loader（角色已 deny，Fork 的额外禁是冗余但一致）
        [("general", SubAgentMode.Definitional)] = new[] { "read_file", "write_file", "edit_file", "glob", "grep", "run_command", "filesystem-echo", "git-status" },
        [("general", SubAgentMode.Fork)] = new[] { "read_file", "write_file", "edit_file", "glob", "grep", "run_command", "filesystem-echo", "git-status" },
    };

    [Theory]
    [InlineData("explorer", SubAgentMode.Definitional)]
    [InlineData("explorer", SubAgentMode.Fork)]
    [InlineData("planner", SubAgentMode.Definitional)]
    [InlineData("planner", SubAgentMode.Fork)]
    [InlineData("general", SubAgentMode.Definitional)]
    [InlineData("general", SubAgentMode.Fork)]
    public void Scenario_FilterMatrix_AllCombinationsMatchOracle(string roleName, SubAgentMode mode)
    {
        var parent = CreateMainAgentToolRegistry();
        var roles = LoadBuiltinRoles();
        var role = roles.Get(roleName);
        role.Should().NotBeNull($"角色 {roleName} 应已加载");

        var filtered = ToolFilter.Build(parent, role!, mode);
        var actual = GetToolNames(filtered);
        var expected = ExpectedMatrix[(roleName, mode)].ToHashSet(StringComparer.Ordinal);

        actual.Should().BeEquivalentTo(expected,
            $"角色={roleName}, 模式={mode} 的过滤结果应与 oracle 一致");
    }

    // ============================================================
    // 场景 3：关键不变量（无论角色如何配置，这些规则始终成立）
    // ============================================================

    [Theory]
    [InlineData("explorer", SubAgentMode.Definitional)]
    [InlineData("explorer", SubAgentMode.Fork)]
    [InlineData("planner", SubAgentMode.Definitional)]
    [InlineData("planner", SubAgentMode.Fork)]
    [InlineData("general", SubAgentMode.Definitional)]
    [InlineData("general", SubAgentMode.Fork)]
    public void Invariant_SubAgentAlwaysExcluded(string roleName, SubAgentMode mode)
    {
        var parent = CreateMainAgentToolRegistry();
        var roles = LoadBuiltinRoles();
        var role = roles.Get(roleName)!;

        var filtered = ToolFilter.Build(parent, role, mode);

        filtered.Get("sub_agent").Should().BeNull(
            "第 1 层全局禁嵌套：无论角色和模式，sub_agent 必须被排除");
    }

    [Theory]
    [InlineData("explorer", SubAgentMode.Definitional)]
    [InlineData("explorer", SubAgentMode.Fork)]
    [InlineData("planner", SubAgentMode.Definitional)]
    [InlineData("planner", SubAgentMode.Fork)]
    [InlineData("general", SubAgentMode.Definitional)]
    [InlineData("general", SubAgentMode.Fork)]
    public void Invariant_FilteredNeverExceedsParent(string roleName, SubAgentMode mode)
    {
        var parent = CreateMainAgentToolRegistry();
        var roles = LoadBuiltinRoles();
        var role = roles.Get(roleName)!;

        var filtered = ToolFilter.Build(parent, role, mode);

        filtered.GetAll().Count.Should().BeLessOrEqualTo(parent.GetAll().Count,
            "过滤后的工具数不能超过父工具集");
    }

    // ============================================================
    // 场景 4：Fork 模式的额外约束（skill_loader）
    // ============================================================

    [Fact]
    public void Scenario_ForkMode_ExcludesSkillLoader_WhenRoleDoesNotDenyIt()
    {
        // 构造一个不声明 skill_loader 在 tools_deny 的角色
        // （与 general 不同——general 显式 deny 了 skill_loader）
        var parent = CreateMainAgentToolRegistry();
        var role = new RoleDefinition
        {
            Meta = new RoleMeta
            {
                Name = "fork_test",
                ToolsDeny = new List<string> { "sub_agent" }  // 只 deny sub_agent
            },
            Body = "",
            Source = RoleSource.Builtin
        };

        var definitional = ToolFilter.Build(parent, role, SubAgentMode.Definitional);
        var fork = ToolFilter.Build(parent, role, SubAgentMode.Fork);

        definitional.Get("skill_loader").Should().NotBeNull(
            "Definitional 模式 + 角色未 deny skill_loader → 保留");
        fork.Get("skill_loader").Should().BeNull(
            "Fork 模式额外排除 skill_loader，即使角色未声明 deny");
    }

    // ============================================================
    // 场景 5：MCP 工具受相同规则约束
    // ============================================================

    [Fact]
    public void Scenario_McpTools_SubjectToSameFilterRules()
    {
        var parent = CreateMainAgentToolRegistry();
        var roles = LoadBuiltinRoles();

        // explorer 的白名单不含 MCP 工具 → 应被排除
        var explorerFiltered = ToolFilter.Build(parent, roles.Get("explorer")!, SubAgentMode.Definitional);
        explorerFiltered.Get("filesystem-echo").Should().BeNull("explorer 白名单不含 MCP 工具");
        explorerFiltered.Get("git-status").Should().BeNull("explorer 白名单不含 MCP 工具");

        // general 无白名单 → MCP 工具应继承
        var generalFiltered = ToolFilter.Build(parent, roles.Get("general")!, SubAgentMode.Definitional);
        generalFiltered.Get("filesystem-echo").Should().NotBeNull("general 继承 MCP 工具");
        generalFiltered.Get("git-status").Should().NotBeNull("general 继承 MCP 工具");
    }

    // ============================================================
    // 场景 6：可视化输出过滤矩阵（辅助调试，不断言）
    // ============================================================

    /// <summary>
    /// 这个测试不断言，只输出过滤矩阵到测试日志，方便人工审视。
    /// 标记为 Trait 方便按需运行：dotnet test --filter "Category=MatrixDump"
    /// </summary>
    [Fact]
    [Trait("Category", "MatrixDump")]
    public void Scenario_DumpFilterMatrix_ForHumanInspection()
    {
        var parent = CreateMainAgentToolRegistry();
        var roles = LoadBuiltinRoles();
        var parentNames = parent.GetAll().Select(t => t.Name).OrderBy(n => n).ToList();

        var sb = new StringBuilder();
        sb.AppendLine();
        sb.AppendLine("=== 14a 过滤矩阵（Builtin 角色 × 模式）===");
        sb.AppendLine($"父工具集（{parentNames.Count}）: {string.Join(", ", parentNames)}");
        sb.AppendLine();

        foreach (var roleName in new[] { "explorer", "planner", "general" })
        {
            var role = roles.Get(roleName)!;
            sb.AppendLine($"--- 角色: {roleName} ---");
            sb.AppendLine($"  tools_allow: [{string.Join(", ", role.Meta.ToolsAllow)}]");
            sb.AppendLine($"  tools_deny:  [{string.Join(", ", role.Meta.ToolsDeny)}]");

            foreach (var mode in new[] { SubAgentMode.Definitional, SubAgentMode.Fork })
            {
                var filtered = ToolFilter.Build(parent, role, mode);
                var names = filtered.GetAll().Select(t => t.Name).OrderBy(n => n).ToList();
                sb.AppendLine($"  {mode,-13}: ({names.Count}) {string.Join(", ", names)}");
            }
            sb.AppendLine();
        }

        // 输出到测试输出（在 dotnet test -v normal 时可见）
        Console.WriteLine(sb.ToString());
        // 同时断言一个简单条件确保测试"通过"
        true.Should().BeTrue();
    }
}
