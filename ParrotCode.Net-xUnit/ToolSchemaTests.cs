using System.Text.Json;
using ParrotCode;

namespace ParrotCode.xUnit;

/// <summary>
/// ToolBase schema 转换单元测试：覆盖 ToOpenAiSchema / ToAnthropicSchema 的字段结构。
/// 不依赖文件系统，纯 JSON 结构验证。
/// </summary>
public class ToolSchemaTests
{
    private readonly ReadFileTool _readTool = new();
    private readonly WriteFileTool _writeTool = new();
    private readonly EditFileTool _editTool = new();

    // ---- OpenAI schema ----

    [Fact]
    public void ToOpenAiSchema_ContainsTypeFunction()
    {
        var schema = _readTool.ToOpenAiSchema();

        schema.GetProperty("type").GetString().Should().Be("function");
    }

    [Fact]
    public void ToOpenAiSchema_ContainsFunctionField()
    {
        var schema = _readTool.ToOpenAiSchema();

        schema.TryGetProperty("function", out _).Should().BeTrue();
    }

    [Fact]
    public void ToOpenAiSchema_FunctionContainsName()
    {
        var schema = _readTool.ToOpenAiSchema();

        schema.GetProperty("function").GetProperty("name").GetString().Should().Be("read_file");
    }

    [Fact]
    public void ToOpenAiSchema_FunctionContainsDescription()
    {
        var schema = _readTool.ToOpenAiSchema();

        var desc = schema.GetProperty("function").GetProperty("description").GetString();
        desc.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public void ToOpenAiSchema_FunctionContainsParameters()
    {
        var schema = _readTool.ToOpenAiSchema();

        var parameters = schema.GetProperty("function").GetProperty("parameters");
        parameters.ValueKind.Should().Be(JsonValueKind.Object);
    }

    [Fact]
    public void ToOpenAiSchema_ParametersTypeIsObject()
    {
        var schema = _readTool.ToOpenAiSchema();

        schema.GetProperty("function").GetProperty("parameters").GetProperty("type").GetString()
            .Should().Be("object");
    }

    [Fact]
    public void ToOpenAiSchema_ParametersContainsPropertiesPath()
    {
        var schema = _readTool.ToOpenAiSchema();

        var properties = schema.GetProperty("function").GetProperty("parameters").GetProperty("properties");
        properties.TryGetProperty("path", out _).Should().BeTrue();
    }

    [Fact]
    public void ToOpenAiSchema_ParametersContainsRequiredArrayWithPath()
    {
        var schema = _readTool.ToOpenAiSchema();

        var required = schema.GetProperty("function").GetProperty("parameters").GetProperty("required");
        required.GetArrayLength().Should().Be(1);
        required[0].GetString().Should().Be("path");
    }

    [Fact]
    public void ToOpenAiSchema_PropertyDescriptionContainsText()
    {
        var schema = _readTool.ToOpenAiSchema();

        var pathDesc = schema.GetProperty("function").GetProperty("parameters")
            .GetProperty("properties").GetProperty("path").GetProperty("description").GetString();
        pathDesc.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public void ToOpenAiSchema_PropertyTypeIsString()
    {
        var schema = _readTool.ToOpenAiSchema();

        var pathType = schema.GetProperty("function").GetProperty("parameters")
            .GetProperty("properties").GetProperty("path").GetProperty("type").GetString();
        pathType.Should().Be("string");
    }

    // ---- WriteFileTool schema ----

    [Fact]
    public void WriteFileTool_SchemaHasPathAndContent()
    {
        var schema = _writeTool.ToOpenAiSchema();

        var properties = schema.GetProperty("function").GetProperty("parameters").GetProperty("properties");
        properties.TryGetProperty("path", out _).Should().BeTrue();
        properties.TryGetProperty("content", out _).Should().BeTrue();

        var required = schema.GetProperty("function").GetProperty("parameters").GetProperty("required");
        required.GetArrayLength().Should().Be(2);
    }

    // ---- EditFileTool schema ----

    [Fact]
    public void EditFileTool_SchemaHasThreeRequiredParams()
    {
        var schema = _editTool.ToOpenAiSchema();

        var required = schema.GetProperty("function").GetProperty("parameters").GetProperty("required");
        required.GetArrayLength().Should().Be(3);

        var requiredNames = new[] { required[0].GetString(), required[1].GetString(), required[2].GetString() };
        requiredNames.Should().Contain(new[] { "path", "old_text", "new_text" });
    }

    // ---- Anthropic schema ----

    [Fact]
    public void ToAnthropicSchema_ContainsName()
    {
        var schema = _readTool.ToAnthropicSchema();

        schema.GetProperty("name").GetString().Should().Be("read_file");
    }

    [Fact]
    public void ToAnthropicSchema_ContainsDescription()
    {
        var schema = _readTool.ToAnthropicSchema();

        schema.GetProperty("description").GetString().Should().NotBeNullOrEmpty();
    }

    [Fact]
    public void ToAnthropicSchema_ContainsInputSchema()
    {
        var schema = _readTool.ToAnthropicSchema();

        schema.TryGetProperty("input_schema", out _).Should().BeTrue();
    }

    [Fact]
    public void ToAnthropicSchema_InputSchemaContainsProperties()
    {
        var schema = _readTool.ToAnthropicSchema();

        var inputSchema = schema.GetProperty("input_schema");
        inputSchema.GetProperty("type").GetString().Should().Be("object");
        inputSchema.TryGetProperty("properties", out _).Should().BeTrue();
    }

    [Fact]
    public void ToAnthropicSchema_InputSchemaContainsRequired()
    {
        var schema = _readTool.ToAnthropicSchema();

        var required = schema.GetProperty("input_schema").GetProperty("required");
        required.GetArrayLength().Should().Be(1);
        required[0].GetString().Should().Be("path");
    }
}
