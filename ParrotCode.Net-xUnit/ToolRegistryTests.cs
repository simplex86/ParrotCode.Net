using System.Text.Json;
using ParrotCode;

namespace ParrotCode.xUnit;

/// <summary>
/// ToolRegistry 单元测试：覆盖注册、查找、重名、批量 schema 转换。
/// </summary>
public class ToolRegistryTests
{
    private readonly ToolRegistry _registry = new();

    // ---- Register ----

    [Fact]
    public void Register_AddsTool()
    {
        var tool = new ReadFileTool();

        _registry.Register(tool);

        _registry.Get("read_file").Should().BeSameAs(tool);
    }

    [Fact]
    public void Register_DuplicateName_Throws()
    {
        _registry.Register(new ReadFileTool());

        var act = () => _registry.Register(new ReadFileTool());

        act.Should().Throw<ArgumentException>().Which.Message.Should().Contain("read_file");
    }

    [Fact]
    public void Register_NullTool_Throws()
    {
        var act = () => _registry.Register(null!);

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Register_EmptyName_Throws()
    {
        var tool = new EmptyNameTool();

        var act = () => _registry.Register(tool);

        act.Should().Throw<ArgumentException>().Which.Message.Should().Contain("工具名");
    }

    [Fact]
    public void Register_MultipleTools_AllGettable()
    {
        _registry.Register(new ReadFileTool());
        _registry.Register(new WriteFileTool());
        _registry.Register(new EditFileTool());

        _registry.Get("read_file").Should().NotBeNull();
        _registry.Get("write_file").Should().NotBeNull();
        _registry.Get("edit_file").Should().NotBeNull();
    }

    // ---- Get ----

    [Fact]
    public void Get_UnknownName_ReturnsNull()
    {
        _registry.Get("nonexistent").Should().BeNull();
    }

    [Fact]
    public void Get_CaseSensitive_ReturnsNullForDifferentCase()
    {
        _registry.Register(new ReadFileTool());

        _registry.Get("Read_File").Should().BeNull();
        _registry.Get("READ_FILE").Should().BeNull();
    }

    // ---- Require ----

    [Fact]
    public void Require_RegisteredTool_ReturnsInstance()
    {
        var tool = new ReadFileTool();
        _registry.Register(tool);

        _registry.Require("read_file").Should().BeSameAs(tool);
    }

    [Fact]
    public void Require_UnknownName_Throws()
    {
        var act = () => _registry.Require("nonexistent");

        act.Should().Throw<ArgumentException>().Which.Message.Should().Contain("未注册工具");
    }

    // ---- GetAll ----

    [Fact]
    public void GetAll_EmptyRegistry_ReturnsEmptyList()
    {
        _registry.GetAll().Should().BeEmpty();
    }

    [Fact]
    public void GetAll_ReturnsAllRegisteredTools()
    {
        _registry.Register(new ReadFileTool());
        _registry.Register(new WriteFileTool());
        _registry.Register(new EditFileTool());

        _registry.GetAll().Count.Should().Be(3);
    }

    [Fact]
    public void GetAll_AfterRegisterReflectsNewTools()
    {
        _registry.Register(new ReadFileTool());
        _registry.GetAll().Count.Should().Be(1);

        _registry.Register(new WriteFileTool());
        _registry.GetAll().Count.Should().Be(2);
    }

    [Fact]
    public void GetAll_ReturnsSnapshot_ModifyingDoesNotAffectRegistry()
    {
        _registry.Register(new ReadFileTool());
        var snapshot = _registry.GetAll();

        // 修改返回的快照不影响 registry 内部
        // （ToArray 已经保证是新数组，这里只验证 Count 不变）
        _registry.Register(new WriteFileTool());

        snapshot.Count.Should().Be(1);  // 原快照仍是 1
        _registry.GetAll().Count.Should().Be(2);  // registry 已有 2
    }

    // ---- ToOpenAiSchemas ----

    [Fact]
    public void ToOpenAiSchemas_EmptyRegistry_ReturnsEmptyArray()
    {
        var schemas = _registry.ToOpenAiSchemas();

        schemas.ValueKind.Should().Be(JsonValueKind.Array);
        schemas.GetArrayLength().Should().Be(0);
    }

    [Fact]
    public void ToOpenAiSchemas_ReturnsJsonArrayOfRegisteredTools()
    {
        _registry.Register(new ReadFileTool());
        _registry.Register(new WriteFileTool());

        var schemas = _registry.ToOpenAiSchemas();

        schemas.ValueKind.Should().Be(JsonValueKind.Array);
        schemas.GetArrayLength().Should().Be(2);
        // 每个元素应是 OpenAI schema 结构：含 type=function
        schemas[0].GetProperty("type").GetString().Should().Be("function");
        schemas[1].GetProperty("type").GetString().Should().Be("function");
    }

    // ---- ToAnthropicSchemas ----

    [Fact]
    public void ToAnthropicSchemas_EmptyRegistry_ReturnsEmptyArray()
    {
        var schemas = _registry.ToAnthropicSchemas();

        schemas.ValueKind.Should().Be(JsonValueKind.Array);
        schemas.GetArrayLength().Should().Be(0);
    }

    [Fact]
    public void ToAnthropicSchemas_ReturnsJsonArrayOfRegisteredTools()
    {
        _registry.Register(new ReadFileTool());

        var schemas = _registry.ToAnthropicSchemas();

        schemas.GetArrayLength().Should().Be(1);
        schemas[0].TryGetProperty("input_schema", out _).Should().BeTrue();
    }

    // ---- 测试替身 ----

    /// <summary>工具名为空，用于测试 Register 的空名校验。</summary>
    private sealed class EmptyNameTool : ToolBase
    {
        public override string Name => "";
        public override string Description => "test";
        public override ToolCategory Category => ToolCategory.Read;
        public override IReadOnlyList<ToolParameter> Parameters { get; } = Array.Empty<ToolParameter>();
        public override Task<ToolResult> ExecuteAsync(JsonElement input, CancellationToken cancellationToken)
            => Task.FromResult(ToolResult.Ok(""));
    }
}
