using ParrotCode;

namespace ParrotCode.xUnit;

/// <summary>
/// CommandParser 单元测试（迭代 10a）。
/// 覆盖 Parse + SplitArgs 各种输入。
/// </summary>
public class CommandParserTests
{
    [Fact]
    public void Parse_NonSlashPrefix_ReturnsNull()
    {
        CommandParser.Parse("hello").Should().BeNull();
    }

    [Fact]
    public void Parse_EmptyString_ReturnsNull()
    {
        CommandParser.Parse("").Should().BeNull();
    }

    [Fact]
    public void Parse_NoArgs_ReturnsEmptyArgs()
    {
        var result = CommandParser.Parse("/clear");
        result.Should().NotBeNull();
        result!.Value.Name.Should().Be("clear");
        result.Value.Args.Should().BeEmpty();
    }

    [Fact]
    public void Parse_WithArgs_ReturnsNameAndArgs()
    {
        var result = CommandParser.Parse("/mode strict");
        result!.Value.Name.Should().Be("mode");
        result.Value.Args.Should().Be("strict");
    }

    [Fact]
    public void Parse_WithMultiWordArgs_PreservesArgs()
    {
        var result = CommandParser.Parse("/session save my-title");
        result!.Value.Name.Should().Be("session");
        result.Value.Args.Should().Be("save my-title");
    }

    [Fact]
    public void Parse_TrailingSpace_HandledCorrectly()
    {
        var result = CommandParser.Parse("/help ");
        result!.Value.Name.Should().Be("help");
        result.Value.Args.Should().Be("");
    }

    [Fact]
    public void SplitArgs_EmptyString_ReturnsEmptyArray()
    {
        CommandParser.SplitArgs("").Should().BeEmpty();
    }

    [Fact]
    public void SplitArgs_WhitespaceOnly_ReturnsEmptyArray()
    {
        CommandParser.SplitArgs("   ").Should().BeEmpty();
    }

    [Fact]
    public void SplitArgs_SingleArg_ReturnsOneElement()
    {
        var result = CommandParser.SplitArgs("save");
        result.Should().ContainSingle().Which.Should().Be("save");
    }

    [Fact]
    public void SplitArgs_MultipleArgs_ReturnsAll()
    {
        var result = CommandParser.SplitArgs("save my-session");
        result.Should().Equal("save", "my-session");
    }

    [Fact]
    public void SplitArgs_QuotedArgWithSpaces_PreservesSpaces()
    {
        var result = CommandParser.SplitArgs("\"strict mode\"");
        result.Should().ContainSingle().Which.Should().Be("strict mode");
    }

    [Fact]
    public void SplitArgs_MixedQuotedAndUnquoted()
    {
        var result = CommandParser.SplitArgs("save \"my session title\"");
        result.Should().Equal("save", "my session title");
    }
}
