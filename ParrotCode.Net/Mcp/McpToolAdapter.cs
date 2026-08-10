using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace ParrotCode;

/// <summary>
/// MCP 工具 → IBaseTool 适配器（迭代 11b）。
/// 把 MCP server 暴露的单个工具包装成 IBaseTool，注册到 ToolRegistry，
/// AgentLoop 和 BatchToolExecutor 透明调用（不感知 MCP vs 内置）。
///
/// 工具名前缀：{serverName}/{toolName}，防多 server 冲突。
/// Category 判定：根据 MCP annotations.readOnlyHint，无注解默认 Write（安全优先）。
/// </summary>
public sealed class McpToolAdapter : IBaseTool
{
    private readonly McpClient _client;
    private readonly McpToolInfo _toolInfo;
    private readonly ILogger? _logger;
    private IReadOnlyList<ToolParameter>? _parameters;

    /// <summary>
    /// 全局工具名（含 server 前缀）：{serverName}/{toolName}。
    /// </summary>
    public string Name => $"{_client.ServerName}/{_toolInfo.Name}";

    public string Description => _toolInfo.Description;

    /// <summary>
    /// 工具分类：annotations.readOnlyHint=true → Read；否则 Write（安全优先）。
    /// MCP 工具副作用不确定，无注解时默认 Write，让安全层和 HITL 覆盖。
    /// </summary>
    public ToolCategory Category => _toolInfo.Annotations?.ReadOnlyHint == true ? ToolCategory.Read : ToolCategory.Write;

    /// <summary>
    /// 参数列表：从 MCP InputSchema（JSON Schema）解析。
    /// 仅提取顶层 properties 的 name/type/description/required，
    /// 不做嵌套 object 递归（MCP 工具参数通常是扁平结构）。
    /// </summary>
    public IReadOnlyList<ToolParameter> Parameters => _parameters ??= ParseParameters();

    internal McpToolAdapter(McpClient client, McpToolInfo toolInfo, ILogger? logger = null)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _toolInfo = toolInfo ?? throw new ArgumentNullException(nameof(toolInfo));
        _logger = logger;
    }

    /// <summary>
    /// 执行 MCP 工具调用：委托 McpClient.CallToolAsync，将结果转为 ToolResult。
    /// </summary>
    public async Task<ToolResult> ExecuteAsync(JsonElement input, CancellationToken cancellationToken)
    {
        try
        {
            var result = await _client.CallToolAsync(_toolInfo.Name, input, cancellationToken);

            if (result.IsError)
            {
                var errorText = string.Join("\n", result.Content.Where(c => c.Type == "text").Select(c => c.Text));
                return ToolResult.Fail(string.IsNullOrWhiteSpace(errorText) ? "MCP 工具调用失败" : errorText);
            }

            var contentText = string.Join("\n", result.Content.Where(c => c.Type == "text").Select(c => c.Text));
            return ToolResult.Ok(contentText);
        }
        catch (JsonRpcException ex)
        {
            _logger?.LogWarning(ex, "MCP 工具 {Name} 调用失败", Name);
            return ToolResult.Fail($"MCP 工具 {Name} 调用失败：{ex.Message}");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;  // 外部取消透传
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "MCP 工具 {Name} 执行异常", Name);
            return ToolResult.Fail($"MCP 工具 {Name} 执行失败：{ex.Message}");
        }
    }

    public JsonElement ToOpenAiSchema()
    {
        var schema = new
        {
            type = "function",
            function = new
            {
                name = Name,
                description = Description,
                parameters = GetParametersSchema()
            }
        };
        return JsonSerializer.SerializeToElement(schema);
    }

    public JsonElement ToAnthropicSchema()
    {
        var schema = new
        {
            name = Name,
            description = Description,
            input_schema = GetParametersSchema()
        };
        return JsonSerializer.SerializeToElement(schema);
    }

    /// <summary>
    /// 获取参数 JSON Schema（透传 MCP InputSchema，无则默认空 object）。
    /// </summary>
    private object GetParametersSchema()
    {
        if (_toolInfo.InputSchema.ValueKind == JsonValueKind.Object)
        {
            // 透传原始 InputSchema
            return JsonSerializer.Deserialize<JsonElement>(_toolInfo.InputSchema.GetRawText());
        }
        return new { type = "object", properties = new { } };
    }

    /// <summary>
    /// 从 MCP InputSchema 解析参数列表（仅顶层 properties）。
    /// </summary>
    private IReadOnlyList<ToolParameter> ParseParameters()
    {
        if (_toolInfo.InputSchema.ValueKind != JsonValueKind.Object) return Array.Empty<ToolParameter>();
        if (!_toolInfo.InputSchema.TryGetProperty("properties", out var props)) return Array.Empty<ToolParameter>();
        if (props.ValueKind != JsonValueKind.Object) return Array.Empty<ToolParameter>();

        var required = new HashSet<string>();
        if (_toolInfo.InputSchema.TryGetProperty("required", out var reqEl) && reqEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var r in reqEl.EnumerateArray())
                if (r.ValueKind == JsonValueKind.String) required.Add(r.GetString()!);
        }

        var parameters = new List<ToolParameter>();
        foreach (var prop in props.EnumerateObject())
        {
            var type = prop.Value.TryGetProperty("type", out var t) ? t.GetString() ?? "string" : "string";
            var desc = prop.Value.TryGetProperty("description", out var d) ? d.GetString() ?? "" : "";
            parameters.Add(new ToolParameter(prop.Name, type, desc, required.Contains(prop.Name)));
        }
        return parameters;
    }
}
