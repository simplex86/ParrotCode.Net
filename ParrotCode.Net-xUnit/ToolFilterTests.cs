using System.Text.Json;
using ParrotCode;

namespace ParrotCode.xUnit;

/// <summary>
/// ToolFilter 单元测试（迭代 14a）。
/// 覆盖三层过滤：全局禁嵌套 / 角色 allow-deny / 模式约束。
/// 使用 MockTool 创建测试用工具，避免依赖真实工具实现。
/// </summary>
public class ToolFilterTests
{
    // ---- 测试替身 ----

    /// <summary>
    /// 可配置名称的 mock 工具，用于测试 ToolFilter 的按名过滤逻辑。
    /// </summary>
    private sealed class MockTool : ToolBase
    {
        private readonly string _name;
        public override string Name => _name;
        public override string Description => "mock tool";
        public override ToolCategory Category => ToolCategory.Read;
        public override IReadOnlyList<ToolParameter> Parameters { get; } = Array.Empty<ToolParameter>();
        public override Task<ToolResult> ExecuteAsync(JsonElement input, CancellationToken cancellationToken)
            => Task.FromResult(ToolResult.Ok(""));
        public MockTool(string name) => _name = name;
    }

    /// <summary>
    /// 创建含全部标准工具的父 ToolRegistry（模拟主 Agent 的工具集）。
    /// </summary>
    private static ToolRegistry CreateFullParentRegistry()
    {
        var registry = new ToolRegistry();
        registry.Register(new MockTool("read_file"));
        registry.Register(new MockTool("write_file"));
        registry.Register(new MockTool("edit_file"));
        registry.Register(new MockTool("glob"));
        registry.Register(new MockTool("grep"));
        registry.Register(new MockTool("run_command"));
        registry.Register(new MockTool("skill_loader"));
        registry.Register(new MockTool("sub_agent"));
        registry.Register(new MockTool("filesystem-echo"));  // MCP 工具（hyphen 命名）
        return registry;
    }

    private static RoleDefinition MakeRole(
        string name,
        List<string>? toolsAllow = null,
        List<string>? toolsDeny = null)
        => new()
        {
            Meta = new RoleMeta
            {
                Name = name,
                Description = name + " role",
                ToolsAllow = toolsAllow ?? new List<string>(),
                ToolsDeny = toolsDeny ?? new List<string>()
            },
            Body = "body",
            Source = RoleSource.Builtin
        };

    // ---- 第 1 层：全局排除 sub_agent ----

    [Fact]
    public void Build_AlwaysExcludesSubAgent_RegardlessOfRole()
    {
        var parent = CreateFullParentRegistry();
        // general 角色只声明 tools_deny: [sub_agent, skill_loader]
        // 但即使不声明 sub_agent，全局也会排除
        var role = MakeRole("custom", toolsDeny: new List<string>());

        var filtered = ToolFilter.Build(parent, role, SubAgentMode.Definitional);

        filtered.Get("sub_agent").Should().BeNull("全局禁止子 Agent 嵌套");
    }

    [Fact]
    public void Build_AlwaysExcludesSubAgent_EvenIfRoleAllowsIt()
    {
        var parent = CreateFullParentRegistry();
        // 角色显式 allow sub_agent——全局仍应排除
        var role = MakeRole("custom", toolsAllow: new List<string> { "sub_agent", "read_file" });

        var filtered = ToolFilter.Build(parent, role, SubAgentMode.Definitional);

        filtered.Get("sub_agent").Should().BeNull("全局禁止优先于角色 tools_allow");
    }

    // ---- explorer 角色白名单 ----

    [Fact]
    public void Build_ExplorerRole_Definitional_OnlyAllowsDeclaredTools()
    {
        var parent = CreateFullParentRegistry();
        var explorer = MakeRole("explorer",
            toolsAllow: new List<string> { "read_file", "glob", "grep", "run_command" },
            toolsDeny: new List<string> { "write_file", "edit_file", "sub_agent", "skill_loader" });

        var filtered = ToolFilter.Build(parent, explorer, SubAgentMode.Definitional);

        // 白名单内的工具存在
        filtered.Get("read_file").Should().NotBeNull();
        filtered.Get("glob").Should().NotBeNull();
        filtered.Get("grep").Should().NotBeNull();
        filtered.Get("run_command").Should().NotBeNull();
        // 白名单外 + deny 内的工具不存在
        filtered.Get("write_file").Should().BeNull();
        filtered.Get("edit_file").Should().BeNull();
        filtered.Get("sub_agent").Should().BeNull();
        filtered.Get("skill_loader").Should().BeNull();
    }

    // ---- planner 角色白名单 ----

    [Fact]
    public void Build_PlannerRole_Definitional_OnlyAllowsReadTools()
    {
        var parent = CreateFullParentRegistry();
        var planner = MakeRole("planner",
            toolsAllow: new List<string> { "read_file", "glob", "grep" },
            toolsDeny: new List<string> { "write_file", "edit_file", "run_command", "sub_agent", "skill_loader" });

        var filtered = ToolFilter.Build(parent, planner, SubAgentMode.Definitional);

        filtered.Get("read_file").Should().NotBeNull();
        filtered.Get("glob").Should().NotBeNull();
        filtered.Get("grep").Should().NotBeNull();
        filtered.Get("run_command").Should().BeNull();
        filtered.Get("write_file").Should().BeNull();
        filtered.Get("sub_agent").Should().BeNull();
        filtered.Get("skill_loader").Should().BeNull();
    }

    // ---- general 角色继承 ----

    [Fact]
    public void Build_GeneralRole_Definitional_InheritsAllExceptDeny()
    {
        var parent = CreateFullParentRegistry();
        var general = MakeRole("general",
            toolsDeny: new List<string> { "sub_agent", "skill_loader" });

        var filtered = ToolFilter.Build(parent, general, SubAgentMode.Definitional);

        // 继承父全部工具（除 sub_agent / skill_loader）
        filtered.Get("read_file").Should().NotBeNull();
        filtered.Get("write_file").Should().NotBeNull();
        filtered.Get("edit_file").Should().NotBeNull();
        filtered.Get("glob").Should().NotBeNull();
        filtered.Get("grep").Should().NotBeNull();
        filtered.Get("run_command").Should().NotBeNull();
        filtered.Get("filesystem-echo").Should().NotBeNull();
        // deny 的工具不存在
        filtered.Get("sub_agent").Should().BeNull();
        filtered.Get("skill_loader").Should().BeNull();
    }

    // ---- 第 3 层：模式约束 ----

    [Fact]
    public void Build_ForkMode_ExcludesSkillLoader()
    {
        var parent = CreateFullParentRegistry();
        // general 角色只声明 tools_deny: [sub_agent]——不含 skill_loader
        var general = MakeRole("general",
            toolsDeny: new List<string> { "sub_agent" });

        var filtered = ToolFilter.Build(parent, general, SubAgentMode.Fork);

        filtered.Get("skill_loader").Should().BeNull("Fork 模式额外排除 skill_loader");
        filtered.Get("sub_agent").Should().BeNull();
        // 其他工具仍可用
        filtered.Get("read_file").Should().NotBeNull();
        filtered.Get("write_file").Should().NotBeNull();
    }

    [Fact]
    public void Build_DefinitionalMode_DoesNotExcludeSkillLoader_UnlessRoleDenies()
    {
        var parent = CreateFullParentRegistry();
        // general 角色不声明 skill_loader 在 tools_deny 中
        var general = MakeRole("general",
            toolsDeny: new List<string> { "sub_agent" });

        var filtered = ToolFilter.Build(parent, general, SubAgentMode.Definitional);

        filtered.Get("skill_loader").Should().NotBeNull("Definitional 模式不排除 skill_loader（仅角色 tools_deny 决定）");
    }

    [Fact]
    public void Build_DefinitionalMode_RoleDeniesSkillLoader_Excluded()
    {
        var parent = CreateFullParentRegistry();
        var role = MakeRole("custom",
            toolsDeny: new List<string> { "sub_agent", "skill_loader" });

        var filtered = ToolFilter.Build(parent, role, SubAgentMode.Definitional);

        filtered.Get("skill_loader").Should().BeNull("角色 tools_deny 排除 skill_loader");
    }

    // ---- 第 2 层：角色 tools_deny 并入全局 deny ----

    [Fact]
    public void Build_RoleToolsDeny_MergedIntoGlobalDeny()
    {
        var parent = CreateFullParentRegistry();
        var role = MakeRole("custom",
            toolsDeny: new List<string> { "read_file", "glob" });

        var filtered = ToolFilter.Build(parent, role, SubAgentMode.Definitional);

        filtered.Get("read_file").Should().BeNull("角色 tools_deny 排除 read_file");
        filtered.Get("glob").Should().BeNull("角色 tools_deny 排除 glob");
        filtered.Get("sub_agent").Should().BeNull("全局排除 sub_agent");
        // 未被 deny 的工具仍可用
        filtered.Get("write_file").Should().NotBeNull();
        filtered.Get("grep").Should().NotBeNull();
    }

    // ---- 角色 tools_allow 空时不限制 ----

    [Fact]
    public void Build_EmptyToolsAllow_NoRestriction()
    {
        var parent = CreateFullParentRegistry();
        var role = MakeRole("general");  // tools_allow 为空

        var filtered = ToolFilter.Build(parent, role, SubAgentMode.Definitional);

        // 继承父全部工具（除 deny 列表中的 sub_agent——角色未声明 tools_deny，但全局排除）
        filtered.GetAll().Count.Should().Be(parent.GetAll().Count - 1);  // 排除 sub_agent
    }

    // ---- MCP 工具也被过滤 ----

    [Fact]
    public void Build_McpToolsFilteredByName()
    {
        var parent = CreateFullParentRegistry();
        var explorer = MakeRole("explorer",
            toolsAllow: new List<string> { "read_file", "filesystem-echo" });

        var filtered = ToolFilter.Build(parent, explorer, SubAgentMode.Definitional);

        filtered.Get("filesystem-echo").Should().NotBeNull("MCP 工具在白名单中应保留");
    }

    [Fact]
    public void Build_McpToolsExcludedByWhitelist()
    {
        var parent = CreateFullParentRegistry();
        var explorer = MakeRole("explorer",
            toolsAllow: new List<string> { "read_file" });  // 白名单不含 MCP 工具

        var filtered = ToolFilter.Build(parent, explorer, SubAgentMode.Definitional);

        filtered.Get("filesystem-echo").Should().BeNull("MCP 工具不在白名单中应排除");
    }

    [Fact]
    public void Build_McpToolsExcludedByDeny()
    {
        var parent = CreateFullParentRegistry();
        var role = MakeRole("custom",
            toolsDeny: new List<string> { "filesystem-echo" });

        var filtered = ToolFilter.Build(parent, role, SubAgentMode.Definitional);

        filtered.Get("filesystem-echo").Should().BeNull("MCP 工具在 tools_deny 中应排除");
    }

    // ---- 空父 ToolRegistry ----

    [Fact]
    public void Build_EmptyParent_ReturnsEmptyRegistry()
    {
        var parent = new ToolRegistry();
        var role = MakeRole("explorer",
            toolsAllow: new List<string> { "read_file" });

        var filtered = ToolFilter.Build(parent, role, SubAgentMode.Definitional);

        filtered.GetAll().Should().BeEmpty();
    }

    // ---- 综合测试：explorer + Fork ----

    [Fact]
    public void Build_ExplorerFork_CombinesAllThreeLayers()
    {
        var parent = CreateFullParentRegistry();
        var explorer = MakeRole("explorer",
            toolsAllow: new List<string> { "read_file", "glob", "grep", "run_command" },
            toolsDeny: new List<string> { "write_file", "edit_file", "sub_agent", "skill_loader" });

        var filtered = ToolFilter.Build(parent, explorer, SubAgentMode.Fork);

        // 白名单工具存在
        filtered.Get("read_file").Should().NotBeNull();
        filtered.Get("glob").Should().NotBeNull();
        filtered.Get("grep").Should().NotBeNull();
        filtered.Get("run_command").Should().NotBeNull();
        // 第 1 层：sub_agent 全局排除
        filtered.Get("sub_agent").Should().BeNull();
        // 第 2 层：write_file / edit_file 角色排除
        filtered.Get("write_file").Should().BeNull();
        filtered.Get("edit_file").Should().BeNull();
        // 第 3 层：Fork 模式 + 角色 deny 共同排除 skill_loader
        filtered.Get("skill_loader").Should().BeNull();
        // 白名单外的工具也被排除（tools_allow 非空时取交集）
        filtered.Get("filesystem-echo").Should().BeNull();
    }
}
