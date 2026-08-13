using ParrotCode;
using FluentAssertions;
using Xunit;

namespace ParrotCode.xUnit;

public class TemplateEngineTests
{
    private readonly TemplateEngine _engine = new();

    private static Dictionary<string, object?> Ctx(params (string, object?)[] pairs)
        => pairs.ToDictionary(p => p.Item1, p => p.Item2);

    [Fact]
    public void Simple_Variable_Replacement()
    {
        _engine.Render("{{tool_name}}", Ctx(("tool_name", "write_file")))
              .Should().Be("write_file");
    }

    [Fact]
    public void DotPath_Variable_Replacement()
    {
        var ctx = Ctx(("params", (object?)new Dictionary<string, object?> { ["path"] = "/etc" }));
        _engine.Render("{{params.path}}", ctx).Should().Be("/etc");
    }

    [Fact]
    public void Multiple_Variables_Replacement()
    {
        _engine.Render("{{a}}-{{b}}", Ctx(("a", "x"), ("b", "y")))
              .Should().Be("x-y");
    }

    [Fact]
    public void Undefined_Variable_Replaced_With_Empty()
    {
        _engine.Render("{{undefined}}", Ctx()).Should().Be("");
    }

    [Fact]
    public void Nested_DotPath_Replacement()
    {
        var ctx = Ctx(("a", (object?)new Dictionary<string, object?>
        {
            ["b"] = new Dictionary<string, object?> { ["c"] = "deep" }
        }));
        _engine.Render("{{a.b.c}}", ctx).Should().Be("deep");
    }

    [Fact]
    public void No_Placeholder_Returned_AsIs()
    {
        _engine.Render("hello world", Ctx()).Should().Be("hello world");
    }

    [Fact]
    public void Empty_Template_Returns_Empty()
    {
        _engine.Render("", Ctx()).Should().Be("");
    }

    [Fact]
    public void Null_Variable_Replaced_With_Empty()
    {
        _engine.Render("{{x}}", Ctx(("x", (object?)null))).Should().Be("");
    }

    [Fact]
    public void Number_Variable_Replaced_With_String()
    {
        _engine.Render("{{n}}", Ctx(("n", 42))).Should().Be("42");
    }

    [Fact]
    public void Bool_Variable_Replaced_With_String()
    {
        _engine.Render("{{b}}", Ctx(("b", true))).Should().Be("True");
    }
}
