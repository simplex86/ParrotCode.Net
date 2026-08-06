using System.Text.Json;
using ParrotCode;

namespace ParrotCode.xUnit;

/// <summary>
/// ToolCallAccumulator 单元测试：覆盖分片累积、参数拼接、按 index 排序、
/// 空/非法 JSON 兜底、Build 不清空 entries 等行为。
/// </summary>
public class ToolCallAccumulatorTests
{
    [Fact]
    public void Constructor_IsEmpty_True()
    {
        var acc = new ToolCallAccumulator();

        acc.IsEmpty.Should().BeTrue();
        acc.Build().Should().BeEmpty();
    }

    [Fact]
    public void Accumulate_SingleToolCall_BuildsCorrectly()
    {
        var acc = new ToolCallAccumulator();

        acc.Accumulate(0, "call_0", "read_file", """{"path":"a.txt"}""");

        acc.IsEmpty.Should().BeFalse();
        var calls = acc.Build();
        calls.Should().HaveCount(1);
        calls[0].Id.Should().Be("call_0");
        calls[0].Name.Should().Be("read_file");
        calls[0].Input.GetProperty("path").GetString().Should().Be("a.txt");
    }

    [Fact]
    public void Accumulate_MultipleFragmentsForSameIndex_ConcatenatesArguments()
    {
        var acc = new ToolCallAccumulator();

        acc.Accumulate(0, "call_0", "read_file", "{\"path\":");
        acc.Accumulate(0, null, null, "\"a.txt\"}");

        var calls = acc.Build();
        calls.Should().HaveCount(1);
        calls[0].Input.GetProperty("path").GetString().Should().Be("a.txt");
    }

    [Fact]
    public void Accumulate_MultipleToolCalls_ReturnsByIndexOrder()
    {
        var acc = new ToolCallAccumulator();

        acc.Accumulate(1, "call_1", "tool_b", "{}");
        acc.Accumulate(0, "call_0", "tool_a", "{}");

        var calls = acc.Build();
        calls.Should().HaveCount(2);
        calls[0].Id.Should().Be("call_0");
        calls[0].Name.Should().Be("tool_a");
        calls[1].Id.Should().Be("call_1");
        calls[1].Name.Should().Be("tool_b");
    }

    [Fact]
    public void Accumulate_IdAndNameOnlyFirstFragmentUsed()
    {
        var acc = new ToolCallAccumulator();

        acc.Accumulate(0, "call_0", "read_file", "{\"path\":");
        acc.Accumulate(0, null, null, "\"a.txt\"}");

        var calls = acc.Build();
        calls[0].Id.Should().Be("call_0");
        calls[0].Name.Should().Be("read_file");
    }

    [Fact]
    public void Build_EmptyArguments_ReturnsEmptyObject()
    {
        var acc = new ToolCallAccumulator();

        acc.Accumulate(0, "call_0", "read_file", null);

        var calls = acc.Build();
        calls[0].Input.ValueKind.Should().Be(JsonValueKind.Object);
        calls[0].Input.EnumerateObject().Should().BeEmpty();
    }

    [Theory]
    [InlineData("{bad")]
    [InlineData("{unterminated")]
    [InlineData("\"unterminated string")]
    [InlineData("{\"a\":}")]
    public void Build_InvalidJsonArguments_ReturnsParseErrorObject(string badJson)
    {
        var acc = new ToolCallAccumulator();

        acc.Accumulate(0, "call_0", "read_file", badJson);

        var calls = acc.Build();
        calls[0].Input.TryGetProperty("_parse_error", out var err).Should().BeTrue();
        err.GetString().Should().Be("arguments 非法 JSON");
    }

    [Fact]
    public void Build_CalledTwice_ReturnsSameResults()
    {
        var acc = new ToolCallAccumulator();

        acc.Accumulate(0, "call_0", "read_file", """{"path":"a.txt"}""");

        var first = acc.Build();
        var second = acc.Build();

        first.Should().HaveCount(1);
        second.Should().HaveCount(1);
        second[0].Id.Should().Be(first[0].Id);
        second[0].Name.Should().Be(first[0].Name);
        second[0].Input.GetProperty("path").GetString().Should().Be("a.txt");
    }
}
