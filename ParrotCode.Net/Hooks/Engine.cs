using Microsoft.Extensions.Logging;

namespace ParrotCode;

/// <summary>
/// Hook 引擎：注册规则，在生命周期节点触发事件。
///
/// 触发流程：
/// 1. 遍历规则，过滤 event 匹配（用 rule.EventType 强类型）
/// 2. once 检查（已触发过的跳过）
/// 3. 条件评估（ConditionEvaluator）
/// 4. 执行动作（ActionExecutor）
/// 5. 拦截事件收集 prompt_inject 返回值作为拒绝原因
///
/// 错误隔离：
/// - ActionExecutor 内部 try-catch，动作失败只记日志
/// - HookEngine 自身不抛异常——FireAsync 永远正常返回
/// </summary>
public sealed class HookEngine
{
    private readonly List<HookRule> _rules;
    private readonly ConditionEvaluator _conditions;
    private readonly ActionExecutor _actions;
    private readonly ILogger? _logger;

    /// <summary>once:true 已触发的规则名集合。</summary>
    private readonly HashSet<string> _firedOnce = new(StringComparer.Ordinal);

    public HookEngine(IReadOnlyList<HookRule> rules,
                      ActionExecutor? actions = null,
                      ILogger? logger = null)
    {
        _rules = rules?.ToList() ?? new();
        _conditions = new ConditionEvaluator();
        _actions = actions ?? new ActionExecutor(logger: logger);
        _logger = logger;
    }

    /// <summary>
    /// 获取内部 ActionExecutor（供 15b 中 TerminalApp 调 SetSubAgentRunner）。
    /// </summary>
    public ActionExecutor Actions => _actions;

    /// <summary>
    /// 触发事件。执行所有匹配的规则。
    ///
    /// 拦截事件（tool_pre_exec）：返回第一个 prompt_inject 动作的渲染文本作为拒绝原因。
    ///   调用方应把拒绝原因包装为 ToolResult.Fail 回灌 LLM。
    /// 非拦截事件：返回 null——动作结果被丢弃（仅记日志）。
    ///
    /// async=true 的规则：动作用 fire-and-forget 执行（不等待）。
    /// async=false 的规则：动作顺序 await 执行。
    /// </summary>
    public async Task<string?> FireAsync(HookEvent @event,
                                         Dictionary<string, object?>? context = null,
                                         CancellationToken cancellationToken = default)
    {
        var ctx = context ?? new Dictionary<string, object?>();
        ctx["_event"] = @event.ToString();

        string? rejection = null;

        foreach (var rule in _rules)
        {
            // 使用 EventType（Loader 解析后的强类型）
            if (rule.EventType != @event)
                continue;

            if (rule.Control.Once && _firedOnce.Contains(rule.Name))
                continue;

            if (!_conditions.Evaluate(rule.Condition, ctx))
                continue;

            if (rule.Control.Once)
                _firedOnce.Add(rule.Name);

            if (rule.Control.Async)
            {
                _ = FireActionsAsync(rule, ctx, cancellationToken);
            }
            else
            {
                var result = await FireActionsAsync(rule, ctx, cancellationToken);

                if (rule.IsIntercept && result is not null && rejection is null)
                    rejection = result;
            }
        }

        return rejection;
    }

    private async Task<string?> FireActionsAsync(HookRule rule, Dictionary<string, object?> ctx, CancellationToken ct)
    {
        string? firstResult = null;

        foreach (var action in rule.Actions)
        {
            var result = await _actions.ExecuteAsync(action, ctx, rule.Control.Timeout, ct);

            // 使用 ActionType（Loader 解析后的强类型）
            if (rule.IsIntercept && action.ActionType == HookActionType.PromptInject && result is not null && firstResult is null)
                firstResult = result;
        }

        return firstResult;
    }

    /// <summary>
    /// 清除 once 跟踪（新会话时调用）。
    /// </summary>
    public void ResetOnce() => _firedOnce.Clear();
}
