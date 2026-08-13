using System.IO;
using System.Text.Json;
using ParrotCode;
using FluentAssertions;
using Xunit;
using Microsoft.Extensions.Logging.Abstractions;

namespace ParrotCode.xUnit;

/// <summary>
/// 迭代 15b 集成测试：Hook 引擎与 AgentLoop / SecureBatchToolExecutor / ActionExecutor 的接入。
/// 覆盖：tool_pre_exec 拦截、安全层先于 Hook、null HookEngine 兼容、ParseToolParams 转换、sub_agent 动作。
/// </summary>
public class HookIntegrationTests
{
    // ===== 复用 SecureBatchToolExecutorTests 的测试替身 =====

    private static readonly string ProjRoot = Path.Combine(Path.GetTempPath(), "parrotcode-15b-hook");

    private static SecurityContext NewCtx() => new()
    {
        ProjectRoot = ProjRoot,
        AllowPaths = Array.Empty<string>(),
        DenyPaths = Array.Empty<string>(),
        ExtraBlacklist = Array.Empty<string>()
    };

    private static ToolCall MakeCall(string name, string argsJson = "{}")
    {
        using var doc = JsonDocument.Parse(argsJson);
        return new ToolCall("id", name, doc.RootElement.Clone());
    }

    private sealed class CountingReadTool : ToolBase
    {
        public int CallCount;
        public override string Name => "read_file";
        public override string Description => "test read";
        public override ToolCategory Category => ToolCategory.Read;
        public override IReadOnlyList<ToolParameter> Parameters => Array.Empty<ToolParameter>();
        public override Task<ToolResult> ExecuteAsync(JsonElement input, CancellationToken ct)
        {
            CallCount++;
            return Task.FromResult(ToolResult.Ok("read-ok"));
        }
    }

    private sealed class CountingWriteTool : ToolBase
    {
        public int CallCount;
        public override string Name => "write_file";
        public override string Description => "test write";
        public override ToolCategory Category => ToolCategory.Write;
        public override IReadOnlyList<ToolParameter> Parameters => Array.Empty<ToolParameter>();
        public override Task<ToolResult> ExecuteAsync(JsonElement input, CancellationToken ct)
        {
            CallCount++;
            return Task.FromResult(ToolResult.Ok("write-ok"));
        }
    }

    private sealed class FakeHitlGate : IHitlGate
    {
        public Task<HitlDecision?> RequestAsync(ToolCall call, CancellationToken ct)
            => Task.FromResult<HitlDecision?>(new HitlDecision(HitlChoice.AllowOnce));
        public bool IsAllowedThisSession(string toolName) => false;
    }

    /// <summary>
    /// 构造带 HookEngine 的 SecureBatchToolExecutor。
    /// </summary>
    private static (SecureBatchToolExecutor batch, CountingReadTool readTool, CountingWriteTool writeTool) NewSecureWithHook(HookEngine? hookEngine)
    {
        Directory.CreateDirectory(ProjRoot);
        var registry = new ToolRegistry();
        var readTool = new CountingReadTool();
        var writeTool = new CountingWriteTool();
        registry.Register(readTool);
        registry.Register(writeTool);

        var executor = new ToolExecutor(registry, TimeSpan.FromSeconds(5));
        var guard = new SecurityGuard(NewCtx(), SecurityLevel.Permissive);
        var gate = new FakeHitlGate();
        var batch = new SecureBatchToolExecutor(executor, registry, guard,
                                                hitlGate: gate,
                                                hookEngine: hookEngine);
        return (batch, readTool, writeTool);
    }

    /// <summary>
    /// 构造一个简单的 tool_pre_exec 拦截 HookEngine。
    /// 当 tool_name 匹配 blockTool 时，返回 rejectionText。
    /// </summary>
    private static HookEngine CreateInterceptHook(string blockTool, string rejectionText)
    {
        var rule = new HookRule
        {
            Name = "block-" + blockTool,
            Event = "tool_pre_exec",
            EventType = HookEvent.ToolPreExec,
            Condition = new HookCondition
            {
                MatchMode = HookMatchMode.All,
                Rules = new()
                {
                    new ConditionRule { Field = "tool_name", OperatorEnum = HookOperator.Exact, Value = blockTool }
                }
            },
            Actions = new()
            {
                new HookAction
                {
                    Type = "prompt_inject",
                    ActionType = HookActionType.PromptInject,
                    Text = rejectionText
                }
            }
        };
        return new HookEngine(new[] { rule });
    }

    // ===== tool_pre_exec 拦截 =====

    [Fact]
    public async Task HookIntercept_BlocksTool_WithRejectionText()
    {
        var hookEngine = CreateInterceptHook("write_file", "禁止写文件");
        var (batch, _, writeTool) = NewSecureWithHook(hookEngine);

        var results = await batch.ExecuteAsync(
            new[] { MakeCall("write_file", "{\"path\":\"test.txt\",\"content\":\"x\"}") },
            CancellationToken.None);

        results.Should().HaveCount(1);
        results[0].Success.Should().BeFalse("Hook 应拦截 write_file");
        results[0].Error.Should().Contain("[Hook 拦截]");
        results[0].Error.Should().Contain("禁止写文件");
        writeTool.CallCount.Should().Be(0, "被拦截的工具不应执行");
    }

    [Fact]
    public async Task HookIntercept_NullRejection_AllowsTool()
    {
        // Hook 规则条件不匹配 → 返回 null → 工具正常执行
        var hookEngine = CreateInterceptHook("nonexistent_tool", "不应触发");
        var (batch, readTool, _) = NewSecureWithHook(hookEngine);

        var results = await batch.ExecuteAsync(
            new[] { MakeCall("read_file", "{\"path\":\"test.txt\"}") },
            CancellationToken.None);

        results.Should().HaveCount(1);
        results[0].Success.Should().BeTrue("Hook 条件不匹配，应放行");
        readTool.CallCount.Should().Be(1, "工具应正常执行");
    }

    [Fact]
    public async Task HookIntercept_NullHookEngine_BackwardCompatible()
    {
        // 不注入 HookEngine（null）→ 行为等价改动前
        var (batch, readTool, _) = NewSecureWithHook(null);

        var results = await batch.ExecuteAsync(
            new[] { MakeCall("read_file", "{\"path\":\"test.txt\"}") },
            CancellationToken.None);

        results.Should().HaveCount(1);
        results[0].Success.Should().BeTrue();
        readTool.CallCount.Should().Be(1);
    }

    [Fact]
    public async Task SecurityCheck_FailsBefore_Hook()
    {
        // 安全层先于 Hook：Strict 模式 + 越界路径 → 安全层拦截，Hook 不触发
        // 验证方式：结果 Error 不含 "[Hook 拦截]" 前缀（Hook 未触发）
        Directory.CreateDirectory(ProjRoot);
        var registry = new ToolRegistry();
        var writeTool = new CountingWriteTool();
        registry.Register(writeTool);

        var executor = new ToolExecutor(registry, TimeSpan.FromSeconds(5));
        var strictCtx = new SecurityContext
        {
            ProjectRoot = ProjRoot,
            AllowPaths = Array.Empty<string>(),
            DenyPaths = Array.Empty<string>(),
            ExtraBlacklist = Array.Empty<string>()
        };
        var guard = new SecurityGuard(strictCtx, SecurityLevel.Strict);
        var hookEngine = CreateInterceptHook("write_file", "Hook 拦截");

        var batch = new SecureBatchToolExecutor(executor, registry, guard,
                                                hitlGate: new FakeHitlGate(),
                                                hookEngine: hookEngine);

        // Strict 模式写项目外路径 → 安全层拦截
        var externalPath = OperatingSystem.IsWindows() ? @"C:\parrotcode-15b-external\test.txt"
                                                        : "/parrotcode-15b-external/test.txt";
        var results = await batch.ExecuteAsync(
            new[] { MakeCall("write_file", $"{{\"path\":{JsonSerializer.Serialize(externalPath)},\"content\":\"x\"}}") },
            CancellationToken.None);

        results.Should().HaveCount(1);
        results[0].Success.Should().BeFalse("安全层应拦截");
        results[0].Error.Should().NotContain("[Hook 拦截]", "安全层先于 Hook，Hook 不应触发");
        writeTool.CallCount.Should().Be(0);
    }

    [Fact]
    public async Task HookIntercept_TemplateRendersContext()
    {
        // Hook 文本中的 {{tool_name}} 被替换为实际工具名
        var rule = new HookRule
        {
            Name = "template-test",
            Event = "tool_pre_exec",
            EventType = HookEvent.ToolPreExec,
            Condition = new HookCondition
            {
                MatchMode = HookMatchMode.All,
                Rules = new()
                {
                    new ConditionRule { Field = "tool_name", OperatorEnum = HookOperator.Exact, Value = "write_file" }
                }
            },
            Actions = new()
            {
                new HookAction
                {
                    Type = "prompt_inject",
                    ActionType = HookActionType.PromptInject,
                    Text = "禁止 {{tool_name}} 操作"
                }
            }
        };
        var hookEngine = new HookEngine(new[] { rule });
        var (batch, _, writeTool) = NewSecureWithHook(hookEngine);

        var results = await batch.ExecuteAsync(
            new[] { MakeCall("write_file", "{\"path\":\"test.txt\",\"content\":\"x\"}") },
            CancellationToken.None);

        results[0].Error.Should().Contain("禁止 write_file 操作");
        writeTool.CallCount.Should().Be(0);
    }

    [Fact]
    public async Task HookIntercept_DotPathCondition_MatchesNestedParam()
    {
        // 条件用 params.path 匹配嵌套参数
        var rule = new HookRule
        {
            Name = "path-check",
            Event = "tool_pre_exec",
            EventType = HookEvent.ToolPreExec,
            Condition = new HookCondition
            {
                MatchMode = HookMatchMode.All,
                Rules = new()
                {
                    new ConditionRule { Field = "params.path", OperatorEnum = HookOperator.Regex, Value = "^/etc/" }
                }
            },
            Actions = new()
            {
                new HookAction
                {
                    Type = "prompt_inject",
                    ActionType = HookActionType.PromptInject,
                    Text = "禁止写系统目录"
                }
            }
        };
        var hookEngine = new HookEngine(new[] { rule });
        var (batch, _, writeTool) = NewSecureWithHook(hookEngine);

        var results = await batch.ExecuteAsync(
            new[] { MakeCall("write_file", "{\"path\":\"/etc/passwd\",\"content\":\"x\"}") },
            CancellationToken.None);

        results[0].Success.Should().BeFalse();
        results[0].Error.Should().Contain("禁止写系统目录");
        writeTool.CallCount.Should().Be(0);
    }

    [Fact]
    public async Task HookIntercept_AllowsNonMatchingPath()
    {
        // 条件不匹配 → 放行
        var rule = new HookRule
        {
            Name = "path-check-allow",
            Event = "tool_pre_exec",
            EventType = HookEvent.ToolPreExec,
            Condition = new HookCondition
            {
                MatchMode = HookMatchMode.All,
                Rules = new()
                {
                    new ConditionRule { Field = "params.path", OperatorEnum = HookOperator.Regex, Value = "^/etc/" }
                }
            },
            Actions = new()
            {
                new HookAction
                {
                    Type = "prompt_inject",
                    ActionType = HookActionType.PromptInject,
                    Text = "禁止"
                }
            }
        };
        var hookEngine = new HookEngine(new[] { rule });
        var (batch, readTool, _) = NewSecureWithHook(hookEngine);

        var results = await batch.ExecuteAsync(
            new[] { MakeCall("read_file", "{\"path\":\"/home/user/file.txt\"}") },
            CancellationToken.None);

        results[0].Success.Should().BeTrue();
        readTool.CallCount.Should().Be(1);
    }

    [Fact]
    public async Task HookIntercept_MultipleTools_OnlyBlocksMatching()
    {
        // 两个工具调用，只有 write_file 被拦截，read_file 正常执行
        var hookEngine = CreateInterceptHook("write_file", "禁止写");
        var (batch, readTool, writeTool) = NewSecureWithHook(hookEngine);

        var results = await batch.ExecuteAsync(
            new[]
            {
                MakeCall("read_file", "{\"path\":\"test.txt\"}"),
                MakeCall("write_file", "{\"path\":\"test.txt\",\"content\":\"x\"}")
            },
            CancellationToken.None);

        results.Should().HaveCount(2);
        results[0].Success.Should().BeTrue("read_file 未被拦截");
        results[1].Success.Should().BeFalse("write_file 被拦截");
        readTool.CallCount.Should().Be(1);
        writeTool.CallCount.Should().Be(0);
    }

    // ===== ActionExecutor sub_agent =====

    [Fact]
    public async Task SubAgentAction_NullRunner_ReturnsNull()
    {
        var executor = new ActionExecutor(logger: NullLogger.Instance);
        var action = new HookAction
        {
            ActionType = HookActionType.SubAgent,
            Task = "summarize"
        };
        var result = await executor.ExecuteAsync(action, new());
        result.Should().BeNull();
    }

    [Fact]
    public void SetSubAgentRunner_AcceptsNullRunner()
    {
        var executor = new ActionExecutor(logger: NullLogger.Instance);
        // 不应抛异常
        executor.SetSubAgentRunner(null);
    }

    // ===== HooksConfig =====

    [Fact]
    public void HooksConfig_DefaultEnable_IsNull()
    {
        var config = new HooksConfig();
        config.Enable.Should().BeNull();
    }

    [Fact]
    public void AppConfig_Hooks_DefaultIsNull()
    {
        var config = new AppConfig();
        config.Hooks.Should().BeNull();
    }
}
