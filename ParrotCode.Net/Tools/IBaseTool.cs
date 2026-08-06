using System.Text.Json;

namespace ParrotCode;

/// <summary>
/// 工具抽象接口：定义工具的统一契约。
/// LLM 通过 Name + Description + Parameters(JSON Schema) 知道工具能做什么、需要什么参数。
/// 宿主通过 ExecuteAsync(JsonElement input) 执行真实操作，返回 ToolResult。
/// ToOpenAiSchema / ToAnthropicSchema 把工具元数据转成 Provider 协议的 wire format。
/// </summary>
public interface IBaseTool
{
    /// <summary>
    /// 工具名（LLM 调用时使用）。snake_case，跨工具唯一。
    /// </summary>
    string Name { get; }

    /// <summary>
    /// 工具描述（LLM 据此判断何时调用）。应说明用途、参数语义、副作用。
    /// </summary>
    string Description { get; }

    /// <summary>
    /// 工具分类：Read 可并发、Write 需串行。
    /// </summary>
    ToolCategory Category { get; }

    /// <summary>
    /// 参数列表（用于生成 JSON Schema）。
    /// </summary>
    IReadOnlyList<ToolParameter> Parameters { get; }

    /// <summary>
    /// 执行工具。input 是 LLM 生成的 JSON 参数。失败应返回 ToolResult.Fail，不抛异常。
    /// </summary>
    Task<ToolResult> ExecuteAsync(JsonElement input, CancellationToken cancellationToken);

    /// <summary>
    /// 转 OpenAI 协议的 tools 数组元素（含 type/function 包裹层）。
    /// </summary>
    JsonElement ToOpenAiSchema();

    /// <summary>
    /// 转 Anthropic 协议的 tools 数组元素（含 input_schema）。
    /// </summary>
    JsonElement ToAnthropicSchema();
}
