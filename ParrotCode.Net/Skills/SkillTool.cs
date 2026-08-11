using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace ParrotCode;

/// <summary>
/// skill_loader 工具（迭代 12）：LLM 调用此工具按需加载 Skill SOP（Phase 2）。
/// 激活成功后 SOP 作为 ToolResult.Content 返回，进入 history，后续轮 LLM 可见。
/// Category=Read（幂等、无副作用、可并发）。
/// SecurityGuard 天然豁免：非 run_command（不触发黑名单），参数为 name（不匹配 path/cwd，不触发沙箱）。
/// </summary>
public sealed class SkillTool : ToolBase
{
    /// <inheritdoc/>
    public override string Name => "skill_loader";

    /// <inheritdoc/>
    public override string Description => "加载并激活指定 Skill 的标准作业流程(SOP)。调用后 SOP 内容会注入对话,后续轮次 Agent 按此 SOP 工作。";

    /// <inheritdoc/>
    public override ToolCategory Category => ToolCategory.Read;

    /// <inheritdoc/>
    public override IReadOnlyList<ToolParameter> Parameters { get; } = new[]
    {
        new ToolParameter("name", "string", "要加载的 Skill 名称（如 commit / review / test）", Required: true)
    };

    private readonly SkillRegistry _registry;

    public SkillTool(SkillRegistry registry)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
    }

    /// <inheritdoc/>
    public override Task<ToolResult> ExecuteAsync(JsonElement input, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var name = GetRequiredString(input, "name", out var error);
        if (error is not null)
            return Task.FromResult(ToolResult.Fail(error));

        if (string.IsNullOrWhiteSpace(name))
            return Task.FromResult(ToolResult.Fail("Skill 名称不能为空"));

        var result = _registry.Activate(name);
        if (!result.Success)
            return Task.FromResult(ToolResult.Fail(result.Error ?? "激活失败"));

        // SOP 作为工具结果返回，AgentLoop 会把它入 history（后续轮 LLM 可见）
        return Task.FromResult(ToolResult.Ok(result.SopContent ?? string.Empty));
    }
}
