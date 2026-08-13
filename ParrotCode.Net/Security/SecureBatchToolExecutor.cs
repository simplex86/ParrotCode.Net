using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace ParrotCode;

/// <summary>
/// BatchToolExecutor 子类（迭代 8b）：注入 SecurityGuard，覆写 OnBeforeExecuteAsync。
/// 安全层在 HITL 之前执行（基类入口预扫描已统一对所有 calls 调 OnBeforeExecuteAsync）。
/// 安全层拒绝时不问用户（避免打扰已拦截的操作）；HITL 由基类在 Write 组执行阶段触发。
/// 迭代 15b：追加 HookEngine 注入——安全检查通过后触发 tool_pre_exec Hook（拦截）。
/// </summary>
public sealed class SecureBatchToolExecutor : BatchToolExecutor
{
    private readonly SecurityGuard _guard;
    private readonly HookEngine? _hookEngine;

    public SecureBatchToolExecutor(
        ToolExecutor executor,
        ToolRegistry registry,
        SecurityGuard guard,
        int maxParallelism = 5,
        IHitlGate? hitlGate = null,
        HookEngine? hookEngine = null,
        ILogger? logger = null)
        : base(executor, registry, maxParallelism, hitlGate, logger)
    {
        _guard = guard ?? throw new ArgumentNullException(nameof(guard));
        _hookEngine = hookEngine;
    }

    /// <summary>
    /// 安全层检查（黑名单 → 沙箱 → 策略）+ Hook 拦截（tool_pre_exec）。
    /// 安全层先于 Hook——安全层拒绝时不触发 Hook。
    /// Hook 返回非 null 拒绝原因时，包装为 ToolResult.Fail 回灌 LLM。
    /// </summary>
    protected override async Task<ToolResult?> OnBeforeExecuteAsync(ToolCall call, CancellationToken ct)
    {
        // 1. 安全层先于 Hook
        var securityResult = await _guard.CheckAsync(call, ct);
        if (securityResult is not null)
            return securityResult;

        // 2. Hook 拦截（迭代 15b 新增）
        if (_hookEngine is not null)
        {
            var context = new Dictionary<string, object?>
            {
                ["tool_name"] = call.Name,
                ["tool_call_id"] = call.Id,
                ["params"] = ParseToolParams(call.Input)
            };
            var rejection = await _hookEngine.FireAsync(HookEvent.ToolPreExec, context, ct);
            if (rejection is not null)
                return ToolResult.Fail($"[Hook 拦截] {rejection}");
        }

        return null;
    }

    /// <summary>
    /// 将 ToolCall.Input（JsonElement）递归转为 Dictionary<string, object?>。
    /// 支持 ConditionEvaluator 的 dot-path 解析和 TemplateEngine 的模板替换。
    /// </summary>
    private static Dictionary<string, object?> ParseToolParams(JsonElement input)
    {
        var result = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (input.ValueKind != JsonValueKind.Object)
            return result;

        foreach (var prop in input.EnumerateObject())
        {
            result[prop.Name] = prop.Value.ValueKind switch
            {
                JsonValueKind.String => (string?)prop.Value.GetString(),
                JsonValueKind.Number => prop.Value.GetRawText(),
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                JsonValueKind.Null => null,
                JsonValueKind.Object => ParseToolParams(prop.Value),
                _ => prop.Value.GetRawText()
            };
        }
        return result;
    }
}
