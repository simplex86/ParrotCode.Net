using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace ParrotCode;

/// <summary>
/// sub_agent 工具（迭代 14b）：LLM 调用此工具委派子任务给子 Agent。
/// 子 Agent 独立运行（独立 history / 工具子集 / system prompt），完成后报告作为 ToolResult 返回。
///
/// Category=Read（工具本身幂等——子 Agent 的副作用由其内部工具控制，sub_agent 只编排）。
/// SecurityGuard 天然豁免：非 run_command（不触发黑名单），参数为 task/role/mode（不匹配 path/cwd）。
/// 仍加入 SystemTools 白名单作为防御性编程（见 SecurityGuard）。
/// </summary>
public sealed class SubAgentTool : ToolBase
{
    /// <inheritdoc/>
    public override string Name => "sub_agent";

    /// <inheritdoc/>
    public override string Description =>
        "委派子任务给子 Agent 执行。子 Agent 拥有独立的对话上下文和工具子集，" +
        "完成后返回结构化报告（≤500字）。适合探索、规划、分析等可独立完成的子任务。" +
        "mode='definitional'（默认）：空白对话+角色SOP；mode='fork'：继承当前对话上下文。";

    /// <inheritdoc/>
    public override ToolCategory Category => ToolCategory.Read;

    /// <inheritdoc/>
    public override IReadOnlyList<ToolParameter> Parameters { get; } = new[]
    {
        new ToolParameter("task", "string",
            "子任务描述（清晰、自包含的任务指令）", Required: true),
        new ToolParameter("role", "string",
            "角色名（explorer=只读探索 / planner=只读规划 / general=通用，默认 general）",
            Required: false),
        new ToolParameter("mode", "string",
            "运行模式（definitional=空白对话+角色SOP / fork=继承当前上下文，默认 definitional）",
            Required: false)
    };

    private readonly SubAgentRunner _runner;
    private readonly ConversationHistory _parentHistory;

    public SubAgentTool(SubAgentRunner runner, ConversationHistory parentHistory)
    {
        _runner = runner ?? throw new ArgumentNullException(nameof(runner));
        _parentHistory = parentHistory ?? throw new ArgumentNullException(nameof(parentHistory));
    }

    /// <inheritdoc/>
    public override async Task<ToolResult> ExecuteAsync(JsonElement input, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var task = GetRequiredString(input, "task", out var err1);
        if (err1 is not null) return ToolResult.Fail(err1);

        var role = GetOptionalString(input, "role", out var err2, "general");
        if (err2 is not null) return ToolResult.Fail(err2);

        var modeStr = GetOptionalString(input, "mode", out var err3, "definitional");
        if (err3 is not null) return ToolResult.Fail(err3);

        if (string.IsNullOrWhiteSpace(task))
            return ToolResult.Fail("参数 task 不能为空");

        // 解析 mode（大小写不敏感）
        if (!Enum.TryParse<SubAgentMode>(modeStr, ignoreCase: true, out var mode))
            return ToolResult.Fail($"参数 mode 无效：{modeStr}（可选值：definitional / fork）");

        var request = new SubAgentRequest
        {
            Task = task,
            Role = role,
            Mode = mode
        };

        // Fork 模式传父历史，Definitional 模式传 null（不继承）
        var parentHistory = mode == SubAgentMode.Fork ? _parentHistory : null;

        var result = await _runner.RunAsync(request, parentHistory, cancellationToken);

        if (!result.Success)
            return ToolResult.Fail(result.Error ?? "子 Agent 执行失败");

        // 报告作为 ToolResult 返回，AgentLoop 会把它入 history（后续轮主 Agent 可见）
        var report = result.Report ?? string.Empty;
        var reportWithMeta = $"[子 Agent 报告 | 角色={role} | 模式={mode} | 轮次={result.RoundsUsed}]\n\n{report}";
        return ToolResult.Ok(reportWithMeta);
    }
}
