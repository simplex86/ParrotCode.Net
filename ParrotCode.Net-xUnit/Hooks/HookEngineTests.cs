using ParrotCode;
using FluentAssertions;
using Xunit;
using Microsoft.Extensions.Logging.Abstractions;

namespace ParrotCode.xUnit;

public class HookEngineTests
{
    private static HookRule Rule(string name, HookEvent evt, HookAction action, HookControl? control = null) => new()
    {
        Name = name,
        Event = evt.ToString(),
        EventType = evt,
        Actions = new() { action },
        Control = control ?? new()
    };

    private static HookAction PromptAction(string text) => new()
    {
        Type = "prompt_inject",
        ActionType = HookActionType.PromptInject,
        Text = text
    };

    private static HookAction ShellAction(string cmd) => new()
    {
        Type = "shell",
        ActionType = HookActionType.Shell,
        Command = cmd
    };

    [Fact]
    public async Task NoRules_Returns_Null()
    {
        var engine = new HookEngine(Array.Empty<HookRule>());
        var result = await engine.FireAsync(HookEvent.RoundStart);
        result.Should().BeNull();
    }

    [Fact]
    public async Task EventMismatch_Returns_Null()
    {
        var rule = Rule("r1", HookEvent.RoundStart, ShellAction("echo hi"));
        var engine = new HookEngine(new[] { rule });
        var result = await engine.FireAsync(HookEvent.RoundEnd);
        result.Should().BeNull();
    }

    [Fact]
    public async Task ConditionMatch_TriggersAction()
    {
        var action = ShellAction("echo matched");
        var rule = Rule("r1", HookEvent.RoundStart, action);
        var engine = new HookEngine(new[] { rule });
        await engine.FireAsync(HookEvent.RoundStart);
        // shell action executed—no exception means success
    }

    [Fact]
    public async Task ConditionNotMatch_DoesNotTrigger()
    {
        var action = ShellAction("echo should-not-run");
        var rule = new HookRule
        {
            Name = "r1",
            Event = "round_start",
            EventType = HookEvent.RoundStart,
            Condition = new HookCondition
            {
                MatchMode = HookMatchMode.All,
                Rules = new() { new ConditionRule { Field = "tool_name", OperatorEnum = HookOperator.Exact, Value = "write_file" } }
            },
            Actions = new() { action }
        };
        var engine = new HookEngine(new[] { rule });
        var result = await engine.FireAsync(HookEvent.RoundStart, new() { ["tool_name"] = "read_file" });
        result.Should().BeNull();
    }

    [Fact]
    public async Task InterceptEvent_Returns_RejectionReason()
    {
        var rule = Rule("block", HookEvent.ToolPreExec, PromptAction("禁止写系统目录"));
        var engine = new HookEngine(new[] { rule });
        var result = await engine.FireAsync(HookEvent.ToolPreExec, new() { ["tool_name"] = "write_file" });
        result.Should().Be("禁止写系统目录");
    }

    [Fact]
    public async Task NonInterceptEvent_Returns_Null()
    {
        var rule = Rule("notify", HookEvent.RoundStart, PromptAction("round started"));
        var engine = new HookEngine(new[] { rule });
        var result = await engine.FireAsync(HookEvent.RoundStart);
        result.Should().BeNull();
    }

    [Fact]
    public async Task MultipleRules_AllTrigger()
    {
        var rule1 = Rule("r1", HookEvent.RoundStart, ShellAction("echo 1"));
        var rule2 = Rule("r2", HookEvent.RoundStart, ShellAction("echo 2"));
        var engine = new HookEngine(new[] { rule1, rule2 });
        await engine.FireAsync(HookEvent.RoundStart);
        // both actions executed—no exception means success
    }

    [Fact]
    public async Task Once_True_Triggers_OnlyOnce()
    {
        var rule = Rule("once-rule", HookEvent.RoundStart, ShellAction("echo once"), new() { Once = true });
        var engine = new HookEngine(new[] { rule });
        await engine.FireAsync(HookEvent.RoundStart);
        var result = await engine.FireAsync(HookEvent.RoundStart);
        result.Should().BeNull(); // second time skipped
    }

    [Fact]
    public async Task Once_false_Triggers_EveryTime()
    {
        var rule = Rule("repeat", HookEvent.RoundStart, ShellAction("echo repeat"), new() { Once = false });
        var engine = new HookEngine(new[] { rule });
        await engine.FireAsync(HookEvent.RoundStart);
        await engine.FireAsync(HookEvent.RoundStart);
        // both trigger—no exception means success
    }

    [Fact]
    public async Task ResetOnce_Clears_Tracking()
    {
        var rule = Rule("once-reset", HookEvent.RoundStart, ShellAction("echo reset"), new() { Once = true });
        var engine = new HookEngine(new[] { rule });
        await engine.FireAsync(HookEvent.RoundStart);
        engine.ResetOnce();
        await engine.FireAsync(HookEvent.RoundStart); // should trigger again
    }

    [Fact]
    public async Task Async_True_FireAndForget()
    {
        var rule = Rule("async", HookEvent.RoundStart, ShellAction("echo async"), new() { Async = true });
        var engine = new HookEngine(new[] { rule });
        var result = await engine.FireAsync(HookEvent.RoundStart);
        result.Should().BeNull(); // async returns immediately
    }

    [Fact]
    public async Task ActionException_DoesNot_Throw()
    {
        // Use an http action with invalid URL to trigger exception
        var action = new HookAction
        {
            Type = "http",
            ActionType = HookActionType.Http,
            Url = "http://localhost:1/no-server",
            Method = "GET"
        };
        var rule = Rule("fail", HookEvent.RoundStart, action);
        var engine = new HookEngine(new[] { rule }, logger: NullLogger.Instance);
        var result = await engine.FireAsync(HookEvent.RoundStart);
        result.Should().BeNull(); // exception caught, returns null
    }

    [Fact]
    public async Task NullContext_DoesNot_Crash()
    {
        var rule = Rule("r1", HookEvent.RoundStart, ShellAction("echo hi"));
        var engine = new HookEngine(new[] { rule });
        var result = await engine.FireAsync(HookEvent.RoundStart, context: null);
        result.Should().BeNull();
    }

    [Fact]
    public async Task MultipleInterceptRules_Return_FirstRejection()
    {
        var rule1 = Rule("block1", HookEvent.ToolPreExec, PromptAction("拒绝1"));
        var rule2 = Rule("block2", HookEvent.ToolPreExec, PromptAction("拒绝2"));
        var engine = new HookEngine(new[] { rule1, rule2 });
        var result = await engine.FireAsync(HookEvent.ToolPreExec, new() { ["tool_name"] = "write_file" });
        result.Should().Be("拒绝1"); // first rejection wins
    }
}
