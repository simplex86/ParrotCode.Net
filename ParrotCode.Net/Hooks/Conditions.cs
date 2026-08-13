using System.Text.RegularExpressions;

namespace ParrotCode;

/// <summary>
/// 条件评估器：对上下文字典评估 Condition。
/// - null 或空规则列表 → 无条件 True
/// - ALL → 所有规则都满足
/// - ANY → 任一规则满足
/// dot-path 解析：field "params.path" → context["params"]["path"]
/// </summary>
public sealed class ConditionEvaluator
{
    /// <summary>
    /// 评估条件。null 或空规则 → True。
    /// 使用 Condition.MatchMode（Loader 解析后的强类型）。
    /// </summary>
    public bool Evaluate(HookCondition? condition, Dictionary<string, object?> context)
    {
        if (condition is null || condition.Rules.Count == 0)
            return true;

        var results = condition.Rules.Select(r => EvalRule(r, context));

        return condition.MatchMode == HookMatchMode.All ? results.All(x => x)
                                                        : results.Any(x => x);
    }

    /// <summary>
    /// 使用 ConditionRule.OperatorEnum（Loader 解析后的强类型）。
    /// </summary>
    private bool EvalRule(ConditionRule rule, Dictionary<string, object?> context)
    {
        var actual = ResolveField(rule.Field, context)?.ToString() ?? string.Empty;
        var target = rule.Value;

        return rule.OperatorEnum switch
        {
            HookOperator.Exact => string.Equals(actual, target, StringComparison.Ordinal),
            HookOperator.Not => !string.Equals(actual, target, StringComparison.Ordinal),
            HookOperator.Glob => GlobMatch(actual, target),
            HookOperator.Regex => SafeRegexMatch(actual, target),
            _ => false
        };
    }

    /// <summary>
    /// dot-path 解析：a.b.c → context[a][b][c]。
    /// 中间节点非 Dictionary 或不存在 → 返回 null（空字符串语义）。
    /// </summary>
    private static object? ResolveField(string field, Dictionary<string, object?> context)
    {
        var parts = field.Split('.');
        object? current = context;

        foreach (var part in parts)
        {
            if (current is Dictionary<string, object?> dict && dict.TryGetValue(part, out var val))
            {
                current = val;
            }
            else
            {
                return null;
            }
        }

        return current;
    }

    /// <summary>
    /// 通配符匹配：* 匹配任意字符序列，? 匹配单个字符。
    /// 用 Regex 实现（将 glob 模式转正则）。
    /// </summary>
    private static bool GlobMatch(string input, string pattern)
    {
        var regexPattern = "^" + Regex.Escape(pattern)
                                      .Replace("\\*", ".*")
                                      .Replace("\\?", ".") + "$";
        return Regex.IsMatch(input, regexPattern, RegexOptions.CultureInvariant);
    }

    /// <summary>
    /// 安全正则匹配：正则语法错误或超时返回 False（不抛异常）。
    /// 使用带 TimeSpan 超时的重载——防止 ReDoS（正则灾难性回溯）。
    /// </summary>
    private static bool SafeRegexMatch(string input, string pattern)
    {
        try
        {
            return Regex.IsMatch(input, pattern, RegexOptions.None, TimeSpan.FromSeconds(1));
        }
        catch (RegexMatchTimeoutException)
        {
            return false;   // 正则回溯超时
        }
        catch (ArgumentException)
        {
            return false;   // 无效正则
        }
    }
}
