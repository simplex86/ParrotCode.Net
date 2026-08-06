using System.Text.Json;

namespace ParrotCode;

/// <summary>
/// 工具基类：集中实现 schema 转换的样板代码，具体工具只关心 ExecuteAsync。
/// 三个文件工具的 schema 转换逻辑完全一致（基于 Name + Description + Parameters 拼装），
/// 提取到基类避免每个工具重复实现。
/// 同时提供参数提取辅助方法，统一参数校验的错误格式。
/// </summary>
public abstract class ToolBase : IBaseTool
{
    public abstract string Name { get; }
    public abstract string Description { get; }
    public abstract ToolCategory Category { get; }
    public abstract IReadOnlyList<ToolParameter> Parameters { get; }
    public abstract Task<ToolResult> ExecuteAsync(JsonElement input, CancellationToken cancellationToken);

    /// <summary>
    /// 转 OpenAI 协议的 tools 数组元素：
    /// {"type":"function","function":{"name":...,"description":...,"parameters":{...}}}
    /// 匿名对象属性名用 camelCase / snake_case，与协议 wire format 完全一致，
    /// 避免全局 JsonSerializerOptions 配置影响其他序列化路径。
    /// </summary>
    public JsonElement ToOpenAiSchema()
    {
        var schema = new
        {
            type = "function",
            function = new
            {
                name = Name,
                description = Description,
                parameters = BuildParametersSchema(Parameters)
            }
        };
        return JsonSerializer.SerializeToElement(schema);
    }

    /// <summary>
    /// 转 Anthropic 协议的 tools 数组元素：
    /// {"name":...,"description":...,"input_schema":{...}}
    /// </summary>
    public JsonElement ToAnthropicSchema()
    {
        var schema = new
        {
            name = Name,
            description = Description,
            input_schema = BuildParametersSchema(Parameters)
        };
        return JsonSerializer.SerializeToElement(schema);
    }

    /// <summary>
    /// 基于 Parameters 列表构造 JSON Schema 的 parameters / input_schema 对象。
    /// {"type":"object","properties":{name:{"type":...,"description":...}},"required":[...]}
    /// </summary>
    private static JsonElement BuildParametersSchema(IReadOnlyList<ToolParameter> parameters)
    {
        // 匿名对象 + Dictionary 混合：properties 是动态键的对象，无法用强类型匿名表达。
        // 用 Dictionary<string, object> 让 JsonSerializer 输出为 JSON 对象。
        var properties = new Dictionary<string, object>();
        foreach (var p in parameters)
        {
            properties[p.Name] = new { type = p.Type, description = p.Description };
        }
        var required = parameters.Where(p => p.Required).Select(p => p.Name).ToArray();

        var schema = new
        {
            type = "object",
            properties,
            required
        };
        return JsonSerializer.SerializeToElement(schema);
    }

    // —— 参数提取辅助方法：统一参数校验的错误格式 ——

    /// <summary>
    /// 提取必需的 string 参数。缺失或类型错误时返回 string.Empty 并设置 error。
    /// 用 out 模式而非 (Value, Error) 元组——让编译器能正确推断返回值为非空。
    /// 调用方应立即判断 error 是否非空，非空则 return ToolResult.Fail(err)。
    /// </summary>
    protected static string GetRequiredString(JsonElement input, string name, out string? error)
    {
        if (!input.TryGetProperty(name, out var el))
        {
            error = $"缺少必需参数：{name}";
            return string.Empty;
        }
        if (el.ValueKind != JsonValueKind.String)
        {
            error = $"参数 {name} 类型错误：期望 string，实际 {el.ValueKind}";
            return string.Empty;
        }
        error = null;
        return el.GetString() ?? string.Empty;
    }

    /// <summary>
    /// 提取可选的 string 参数。缺失返回 defaultValue，类型错误返回 string.Empty 并设置 error。
    /// </summary>
    protected static string GetOptionalString(
        JsonElement input, string name, out string? error, string defaultValue = "")
    {
        if (!input.TryGetProperty(name, out var el))
        {
            error = null;
            return defaultValue;
        }
        if (el.ValueKind != JsonValueKind.String)
        {
            error = $"参数 {name} 类型错误：期望 string，实际 {el.ValueKind}";
            return string.Empty;
        }
        error = null;
        return el.GetString() ?? string.Empty;
    }

    /// <summary>
    /// 提取可选的 int 参数。缺失返回 defaultValue，类型错误返回 0 并设置 error。
    /// </summary>
    protected static int GetOptionalInt(
        JsonElement input, string name, out string? error, int defaultValue = 0)
    {
        if (!input.TryGetProperty(name, out var el))
        {
            error = null;
            return defaultValue;
        }
        if (el.ValueKind == JsonValueKind.Number && el.TryGetInt32(out var v))
        {
            error = null;
            return v;
        }
        error = $"参数 {name} 类型错误：期望 integer，实际 {el.ValueKind}";
        return 0;
    }
}
