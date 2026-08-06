using System.Text.Json;

namespace ParrotCode;

/// <summary>
/// 工具注册中心：按名查找 + 批量 schema 转换。
/// AgentLoop（迭代 6）在调用 LLM 前注入 ToolRegistry.ToOpenAiSchemas()，
/// 让 LLM 知道有哪些工具可用。
/// 本迭代由 ClosedLoopDemo 与单测使用，迭代 6 才在 App 主循环接入。
/// </summary>
public sealed class ToolRegistry
{
    private readonly Dictionary<string, IBaseTool> _tools = new(StringComparer.Ordinal);

    /// <summary>
    /// 注册工具。重名抛 ArgumentException（工具名应跨工具唯一）。
    /// </summary>
    public void Register(IBaseTool tool)
    {
        ArgumentNullException.ThrowIfNull(tool);
        if (string.IsNullOrWhiteSpace(tool.Name))
            throw new ArgumentException("工具名不能为空", nameof(tool));
        if (!_tools.TryAdd(tool.Name, tool))
            throw new ArgumentException($"工具名 '{tool.Name}' 已注册", nameof(tool));
    }

    /// <summary>
    /// 按名查找。未注册返回 null，调用方决定是否抛错。
    /// </summary>
    public IBaseTool? Get(string name) => _tools.TryGetValue(name, out var tool) ? tool : null;

    /// <summary>
    /// 按名查找。未注册抛 ArgumentException（用于"工具必须存在"的场景）。
    /// </summary>
    public IBaseTool Require(string name) => Get(name) ?? throw new ArgumentException($"未注册工具：{name}");

    /// <summary>
    /// 所有已注册工具的快照（顺序不保证，按需排序）。
    /// </summary>
    public IReadOnlyList<IBaseTool> GetAll() => _tools.Values.ToArray();

    /// <summary>
    /// 批量转 OpenAI tools 数组（用于 ChatRequest 的 tools 字段）。
    /// </summary>
    public JsonElement ToOpenAiSchemas() => JsonSerializer.SerializeToElement(_tools.Values.Select(t => t.ToOpenAiSchema()).ToArray());

    /// <summary>
    /// 批量转 Anthropic tools 数组。
    /// </summary>
    public JsonElement ToAnthropicSchemas() => JsonSerializer.SerializeToElement(_tools.Values.Select(t => t.ToAnthropicSchema()).ToArray());
}
