using System.Text.RegularExpressions;

namespace ParrotCode;

/// <summary>
/// 模板变量替换：{{var}} 占位符从上下文字典替换。
/// 支持 dot-path：{{params.path}} → context["params"]["path"]。
/// 未定义变量 → 空字符串（永不抛异常）。
/// </summary>
public sealed class TemplateEngine
{
    private static readonly Regex VarRegex = new(@"\{\{(\w+(?:\.\w+)*)\}\}", RegexOptions.Compiled);

    /// <summary>
    /// 渲染模板。所有 {{var}} 替换为上下文中的值。
    /// </summary>
    public string Render(string template, Dictionary<string, object?> context)
    {
        if (string.IsNullOrEmpty(template))
            return string.Empty;

        return VarRegex.Replace(template, match =>
        {
            var path = match.Groups[1].Value;
            var value = ResolvePath(path, context);
            return value ?? string.Empty;
        });
    }

    /// <summary>
    /// dot-path 解析（与 ConditionEvaluator.ResolveField 同逻辑）。
    /// </summary>
    private static string? ResolvePath(string path, Dictionary<string, object?> context)
    {
        var parts = path.Split('.');
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

        return current?.ToString();
    }
}
