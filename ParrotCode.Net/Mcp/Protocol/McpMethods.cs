using System.Text.Json;

namespace ParrotCode;

/// <summary>
/// MCP 方法名常量（迭代 11a）。
/// </summary>
internal static class McpMethods
{
    /// <summary>MCP 协议版本（Streamable HTTP 传输在 initialize 后的请求需通过 MCP-Protocol-Version header 携带）。</summary>
    public const string ProtocolVersion = "2025-06-18";

    public const string Initialize = "initialize";
    public const string Initialized = "notifications/initialized";
    public const string ToolsList = "tools/list";
    public const string ToolsCall = "tools/call";
}

/// <summary>
/// MCP initialize 请求参数。
/// </summary>
internal sealed record McpInitializeParams
{
    public string ProtocolVersion { get; init; } = McpMethods.ProtocolVersion;
    public McpClientCapabilities Capabilities { get; init; } = new();
    public McpClientInfo ClientInfo { get; init; } = new();
}

internal sealed record McpClientCapabilities
{
    // 本迭代不声明任何能力（tools 只需 server 端声明）
}

internal sealed record McpClientInfo
{
    public string Name { get; init; } = "ParrotCode.Net";
    public string Version { get; init; } = "0.11.0";
}

/// <summary>
/// MCP tools/list 响应中的工具描述。
/// </summary>
public sealed record McpToolInfo
{
    public string Name { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public JsonElement InputSchema { get; init; }
    /// <summary>
    /// MCP 协议的 annotations 字段（可能含 readOnlyHint）。
    /// </summary>
    public McpToolAnnotations? Annotations { get; init; }
}

/// <summary>
/// MCP 工具注解（2025-03-26 spec 新增）。
/// </summary>
public sealed record McpToolAnnotations
{
    /// <summary>
    /// 提示此工具是否为只读（无副作用）。null 时默认 false（安全优先）。
    /// </summary>
    public bool? ReadOnlyHint { get; init; }
    /// <summary>
    /// 提示此工具是否可能破坏性操作。null 时默认 false。
    /// </summary>
    public bool? DestructiveHint { get; init; }
}

/// <summary>
/// MCP tools/call 请求参数。
/// </summary>
internal sealed record McpToolCallParams
{
    public string Name { get; init; } = string.Empty;
    public JsonElement Arguments { get; init; }
}

/// <summary>
/// MCP tools/call 响应。
/// </summary>
public sealed record McpToolCallResult
{
    /// <summary>
    /// 工具调用结果内容列表（可能含多个 text/image/resource）。
    /// </summary>
    public IReadOnlyList<McpContentBlock> Content { get; init; } = Array.Empty<McpContentBlock>();
    /// <summary>
    /// 是否调用出错。
    /// </summary>
    public bool IsError { get; init; }
}

/// <summary>
/// MCP 内容块（text 类型用于工具结果）。
/// </summary>
public sealed record McpContentBlock
{
    public string Type { get; init; } = "text";
    public string Text { get; init; } = string.Empty;
}

/// <summary>
/// 单个 MCP server 配置（迭代 11a 定义，11c 接入 AppConfig）。
/// transport=stdio 时需要 command + args；
/// transport=http 时需要 url（Streamable HTTP，2025-03-26 起取代旧 HTTP+SSE）；
/// "sse" 为兼容别名，等价于 "http"；
/// 两者都需要 name。
/// </summary>
public sealed record McpServerConfig
{
    /// <summary>
    /// Server 名称（用于工具名前缀和日志）。
    /// 必填。
    /// </summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>
    /// 传输类型："stdio"（默认）| "streamable-http"（Streamable HTTP，推荐）| "http"/"sse"（兼容别名）。
    /// </summary>
    public string Transport { get; init; } = "stdio";

    /// <summary>
    /// Stdio：可执行文件路径。
    /// HTTP 时忽略。
    /// </summary>
    public string? Command { get; init; }

    /// <summary>
    /// Stdio：命令行参数。
    /// HTTP 时忽略。
    /// </summary>
    public string? Args { get; init; }

    /// <summary>
    /// Stdio：工作目录。
    /// null 时用当前目录。
    /// </summary>
    public string? WorkingDir { get; init; }

    /// <summary>
    /// Stdio：环境变量。
    /// </summary>
    public IReadOnlyDictionary<string, string>? Env { get; init; }

    /// <summary>
    /// HTTP：Server URL。
    /// Stdio 时忽略。
    /// </summary>
    public string? Url { get; init; }

    /// <summary>
    /// HTTP：Bearer Token / API Key。null 时无认证。
    /// </summary>
    public string? ApiKey { get; init; }
}
