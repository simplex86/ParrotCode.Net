using ParrotCode;
using FluentAssertions;
using Xunit;

namespace ParrotCode.xUnit;

public class ConditionEvaluatorTests
{
    private readonly ConditionEvaluator _evaluator = new();

    private static Dictionary<string, object?> Ctx(params (string, object?)[] pairs)
        => pairs.ToDictionary(p => p.Item1, p => p.Item2);

    private static ConditionRule Rule(string field, HookOperator op, string value) => new()
    {
        Field = field,
        OperatorEnum = op,
        Value = value
    };

    [Fact]
    public void Null_Condition_Returns_True()
    {
        _evaluator.Evaluate(null, Ctx()).Should().BeTrue();
    }

    [Fact]
    public void Empty_Rules_Returns_True()
    {
        var cond = new HookCondition { Rules = new() };
        _evaluator.Evaluate(cond, Ctx()).Should().BeTrue();
    }

    [Fact]
    public void Exact_Match_Equal_Returns_True()
    {
        var cond = new HookCondition { Rules = new() { Rule("tool_name", HookOperator.Exact, "write_file") } };
        _evaluator.Evaluate(cond, Ctx(("tool_name", "write_file"))).Should().BeTrue();
    }

    [Fact]
    public void Exact_Match_NotEqual_Returns_False()
    {
        var cond = new HookCondition { Rules = new() { Rule("tool_name", HookOperator.Exact, "write_file") } };
        _evaluator.Evaluate(cond, Ctx(("tool_name", "read_file"))).Should().BeFalse();
    }

    [Fact]
    public void Not_Match_NotEqual_Returns_True()
    {
        var cond = new HookCondition { Rules = new() { Rule("tool_name", HookOperator.Not, "write_file") } };
        _evaluator.Evaluate(cond, Ctx(("tool_name", "read_file"))).Should().BeTrue();
    }

    [Fact]
    public void Glob_Match_Star_Returns_True()
    {
        var cond = new HookCondition { Rules = new() { Rule("tool_name", HookOperator.Glob, "*_file") } };
        _evaluator.Evaluate(cond, Ctx(("tool_name", "write_file"))).Should().BeTrue();
    }

    [Fact]
    public void Glob_Match_QuestionMark_Returns_True()
    {
        var cond = new HookCondition { Rules = new() { Rule("tool_name", HookOperator.Glob, "write?file") } };
        _evaluator.Evaluate(cond, Ctx(("tool_name", "write_file"))).Should().BeTrue();
    }

    [Fact]
    public void Regex_Match_Returns_True()
    {
        var cond = new HookCondition { Rules = new() { Rule("params.path", HookOperator.Regex, "^/etc/") } };
        var ctx = Ctx(("params", (object?)new Dictionary<string, object?> { ["path"] = "/etc/passwd" }));
        _evaluator.Evaluate(cond, ctx).Should().BeTrue();
    }

    [Fact]
    public void Regex_NoMatch_Returns_False()
    {
        var cond = new HookCondition { Rules = new() { Rule("params.path", HookOperator.Regex, "^/etc/") } };
        var ctx = Ctx(("params", (object?)new Dictionary<string, object?> { ["path"] = "/home/user" }));
        _evaluator.Evaluate(cond, ctx).Should().BeFalse();
    }

    [Fact]
    public void Regex_Invalid_Pattern_Returns_False()
    {
        var cond = new HookCondition { Rules = new() { Rule("tool_name", HookOperator.Regex, "[") } };
        _evaluator.Evaluate(cond, Ctx(("tool_name", "test"))).Should().BeFalse();
    }

    [Fact]
    public void Regex_Timeout_Returns_False()
    {
        // ReDoS 正则 + 长输入 → 超时返回 False
        var cond = new HookCondition { Rules = new() { Rule("input", HookOperator.Regex, "(a+)+$") } };
        var input = new string('a', 30) + "!";
        _evaluator.Evaluate(cond, Ctx(("input", input))).Should().BeFalse();
    }

    [Fact]
    public void All_Mode_AllSatisfied_Returns_True()
    {
        var cond = new HookCondition
        {
            MatchMode = HookMatchMode.All,
            Rules = new()
            {
                Rule("tool_name", HookOperator.Exact, "write_file"),
                Rule("params.path", HookOperator.Regex, "^/etc/")
            }
        };
        var ctx = Ctx(("tool_name", "write_file"),
                      ("params", (object?)new Dictionary<string, object?> { ["path"] = "/etc/passwd" }));
        _evaluator.Evaluate(cond, ctx).Should().BeTrue();
    }

    [Fact]
    public void All_Mode_PartialSatisfied_Returns_False()
    {
        var cond = new HookCondition
        {
            MatchMode = HookMatchMode.All,
            Rules = new()
            {
                Rule("tool_name", HookOperator.Exact, "write_file"),
                Rule("tool_name", HookOperator.Exact, "read_file")
            }
        };
        _evaluator.Evaluate(cond, Ctx(("tool_name", "write_file"))).Should().BeFalse();
    }

    [Fact]
    public void Any_Mode_OneSatisfied_Returns_True()
    {
        var cond = new HookCondition
        {
            MatchMode = HookMatchMode.Any,
            Rules = new()
            {
                Rule("tool_name", HookOperator.Exact, "write_file"),
                Rule("tool_name", HookOperator.Exact, "read_file")
            }
        };
        _evaluator.Evaluate(cond, Ctx(("tool_name", "write_file"))).Should().BeTrue();
    }

    [Fact]
    public void Any_Mode_NoneSatisfied_Returns_False()
    {
        var cond = new HookCondition
        {
            MatchMode = HookMatchMode.Any,
            Rules = new()
            {
                Rule("tool_name", HookOperator.Exact, "write_file"),
                Rule("tool_name", HookOperator.Exact, "read_file")
            }
        };
        _evaluator.Evaluate(cond, Ctx(("tool_name", "grep"))).Should().BeFalse();
    }

    [Fact]
    public void DotPath_Resolves_Nested_Value()
    {
        var cond = new HookCondition { Rules = new() { Rule("params.path", HookOperator.Exact, "/etc/passwd") } };
        var ctx = Ctx(("params", (object?)new Dictionary<string, object?> { ["path"] = "/etc/passwd" }));
        _evaluator.Evaluate(cond, ctx).Should().BeTrue();
    }

    [Fact]
    public void DotPath_Missing_Middle_Returns_False()
    {
        var cond = new HookCondition { Rules = new() { Rule("a.b.c", HookOperator.Exact, "deep") } };
        var ctx = Ctx(("a", (object?)new Dictionary<string, object?>()));
        _evaluator.Evaluate(cond, ctx).Should().BeFalse();
    }
}
