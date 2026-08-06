namespace ParrotCode;

/// <summary>
/// 单个 Provider 配置。Protocol 决定由哪个 Provider 实现处理。
/// 迭代 2a：作为工厂入参载体，由 Program 硬编码传入；BaseUrl/ApiKey 暂未使用。
/// 迭代 2b：由 ConfigLoader 从 YAML 加载，BaseUrl/ApiKey 启用。
/// </summary>
public sealed record ProviderConfig
{
    public string Name { get; init; } = string.Empty;
    public string Protocol { get; init; } = string.Empty;   // mock | openai | anthropic
    public string Model { get; init; } = string.Empty;
    public string BaseUrl { get; init; } = string.Empty;    // 2a 未用，2b 启用
    public string ApiKey { get; init; } = string.Empty;     // 2a 未用，2b 启用
}

/// <summary>
/// 顶层配置，对应 .parrotcode.yaml 的根结构。
/// </summary>
public sealed record AppConfig
{
    /// <summary>
    /// 当前激活的 Provider 名称；为 null 时回退到 providers[0].name。
    /// </summary>
    public string? ActiveProvider { get; init; }

    /// <summary>
    /// Provider 列表。无配置文件时由 Loader 提供默认 mock 项。
    /// 用 IList 而非 IReadOnlyList：YamlDotNet 需要可变集合来填充（消费方仍按只读语义使用）。
    /// </summary>
    public IList<ProviderConfig> Providers { get; init; } = Array.Empty<ProviderConfig>();

    /// <summary>Agent 配置（迭代 6 新增）。null 时用默认值。</summary>
    public AgentConfig? Agent { get; init; }

    /// <summary>TUI 配置（迭代 7a 新增）。null 时用默认值（Live 模式）。</summary>
    public TuiConfig? Tui { get; init; }
}

/// <summary>
/// Agent 循环配置。所有字段可选，缺省用默认值。
/// </summary>
public sealed record AgentConfig
{
    /// <summary>最大 ReAct 轮次，默认 10。防止无限循环。</summary>
    public int? MaxRounds { get; init; }

    /// <summary>tool_choice：auto（默认）/ none / required。</summary>
    public string? ToolChoice { get; init; }

    /// <summary>Read 工具最大并发度，默认 5。</summary>
    public int? MaxParallelism { get; init; }

    /// <summary>工具执行超时秒数，默认 30（透传给 ToolExecutor）。</summary>
    public int? ToolTimeoutSeconds { get; init; }

    /// <summary>system prompt，null 用默认。</summary>
    public string? SystemPrompt { get; init; }
}

/// <summary>
/// TUI 渲染配置（迭代 7a 新增）。所有字段可选，缺省用默认值。
/// 7a 不含 EnableHitl（留 7b）/ SecurityConfig（留 7b/迭代 8）。
/// </summary>
public sealed record TuiConfig
{
    /// <summary>渲染模式："live"（默认）| "console"（降级行模式）。</summary>
    public string? Mode { get; init; }

    /// <summary>是否显示状态栏，默认 true。</summary>
    public bool? ShowStatusBar { get; init; }

    /// <summary>上下文窗口 token 数（状态栏占比分母），默认 64000。</summary>
    public int? ContextWindowTokens { get; init; }
}
