namespace ParrotCode;

/// <summary>
/// 工具参数元数据。用于生成 JSON Schema（OpenAI / Anthropic 的 tools 字段）。
/// Type 用 JSON Schema 类型字符串："string" / "number" / "integer" / "boolean" / "array" / "object"。
/// 本迭代三个文件工具的参数全部为 string。
/// </summary>
public sealed record ToolParameter(string Name, string Type, string Description, bool Required);
