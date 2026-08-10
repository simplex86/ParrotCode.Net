namespace ParrotCode;

/// <summary>
/// MCP 配置（迭代 11c 新增）。null 时不启用 MCP。
/// </summary>
public sealed record McpConfig
{
    /// <summary>
    /// 是否启用 MCP 客户端。默认 true。false 时不连接任何 MCP server。
    /// </summary>
    public bool? Enable { get; init; }

    /// <summary>MCP server 配置列表。
    /// </summary>
    public IList<McpServerConfig> Servers { get; init; } = Array.Empty<McpServerConfig>();
}
