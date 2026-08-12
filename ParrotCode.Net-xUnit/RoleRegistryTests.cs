using ParrotCode;

namespace ParrotCode.xUnit;

/// <summary>
/// RoleRegistry 单元测试（迭代 14a）。
/// 覆盖 Get / GetAll / HasRoles / 构造函数拷贝隔离。
/// </summary>
public class RoleRegistryTests
{
    private static RoleDefinition MakeRole(string name, string description = "")
        => new()
        {
            Meta = new RoleMeta { Name = name, Description = description },
            Body = "body",
            SourcePath = "/test/" + name + ".md",
            Source = RoleSource.Builtin
        };

    // ---- Get ----

    [Fact]
    public void Get_ExistingRole_ReturnsDefinition()
    {
        var roles = new Dictionary<string, RoleDefinition>
        {
            ["explorer"] = MakeRole("explorer", "explore")
        };
        var registry = new RoleRegistry(roles);

        var def = registry.Get("explorer");

        def.Should().NotBeNull();
        def!.Meta.Name.Should().Be("explorer");
        def.Meta.Description.Should().Be("explore");
    }

    [Fact]
    public void Get_NonExistentRole_ReturnsNull()
    {
        var registry = new RoleRegistry(new Dictionary<string, RoleDefinition>());

        registry.Get("nonexistent").Should().BeNull();
    }

    [Fact]
    public void Get_CaseSensitive_ReturnsNullForDifferentCase()
    {
        var roles = new Dictionary<string, RoleDefinition>
        {
            ["explorer"] = MakeRole("explorer")
        };
        var registry = new RoleRegistry(roles);

        registry.Get("Explorer").Should().BeNull();
        registry.Get("EXPLORER").Should().BeNull();
    }

    // ---- GetAll ----

    [Fact]
    public void GetAll_ReturnsAllRoles()
    {
        var roles = new Dictionary<string, RoleDefinition>
        {
            ["explorer"] = MakeRole("explorer"),
            ["planner"] = MakeRole("planner"),
            ["general"] = MakeRole("general")
        };
        var registry = new RoleRegistry(roles);

        var all = registry.GetAll();

        all.Should().HaveCount(3);
        all.Select(r => r.Meta.Name).Should().Contain(new[] { "explorer", "planner", "general" });
    }

    [Fact]
    public void GetAll_EmptyRegistry_ReturnsEmptyCollection()
    {
        var registry = new RoleRegistry(new Dictionary<string, RoleDefinition>());

        registry.GetAll().Should().BeEmpty();
    }

    // ---- HasRoles ----

    [Fact]
    public void HasRoles_EmptyRegistry_ReturnsFalse()
    {
        var registry = new RoleRegistry(new Dictionary<string, RoleDefinition>());

        registry.HasRoles.Should().BeFalse();
    }

    [Fact]
    public void HasRoles_NonEmptyRegistry_ReturnsTrue()
    {
        var roles = new Dictionary<string, RoleDefinition>
        {
            ["explorer"] = MakeRole("explorer")
        };
        var registry = new RoleRegistry(roles);

        registry.HasRoles.Should().BeTrue();
    }

    // ---- 构造函数拷贝隔离 ----

    [Fact]
    public void Constructor_CopiesInputDictionary_ModifyingInputDoesNotAffectRegistry()
    {
        var input = new Dictionary<string, RoleDefinition>
        {
            ["explorer"] = MakeRole("explorer")
        };
        var registry = new RoleRegistry(input);

        // 修改输入字典
        input["planner"] = MakeRole("planner");
        input.Remove("explorer");

        // registry 不受影响
        registry.Get("explorer").Should().NotBeNull();
        registry.Get("planner").Should().BeNull();
        registry.GetAll().Should().HaveCount(1);
    }
}
