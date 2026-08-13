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

    /// <summary>
    /// Agent 配置（迭代 6 新增）。null 时用默认值。
    /// </summary>
    public AgentConfig? Agent { get; init; }

    /// <summary>
    /// TUI 配置（迭代 7a 新增）。null 时用默认值（Live 模式）。
    /// </summary>
    public TuiConfig? Tui { get; init; }

    /// <summary>
    /// 安全配置（7b 占位，迭代 8 接入真实拦截）。null 时默认 Normal。
    /// </summary>
    public SecurityConfig? Security { get; init; }

    /// <summary>
    /// 上下文管理配置（迭代 9 新增）。null 时用默认值。
    /// </summary>
    public ContextConfig? Context { get; init; }

    /// <summary>
    /// 会话持久化配置（迭代 10b 新增）。null 时用默认值。
    /// </summary>
    public SessionConfig? Session { get; init; }

    /// <summary>
    /// 项目指令配置（迭代 10c 新增）。null 时用默认值。
    /// </summary>
    public InstructionsConfig? Instructions { get; init; }

    /// <summary>
    /// MCP 客户端配置（迭代 11 新增）。null 时用默认值。
    /// </summary>
    public McpConfig? Mcp { get; init; }

    /// <summary>
    /// Skill 系统配置（迭代 12 新增）。null 时用默认值。
    /// </summary>
    public SkillConfig? Skills { get; init; }

    /// <summary>
    /// 子 Agent 系统配置（迭代 14 新增）。null 时用默认值。
    /// </summary>
    public SubAgentConfig? SubAgent { get; init; }

    /// <summary>
    /// Hook 引擎配置（迭代 15 新增）。null 时用默认值。
    /// </summary>
    public HooksConfig? Hooks { get; init; }
}

/// <summary>
/// Skill 系统配置（迭代 12 新增）。所有字段可选，缺省用默认值。
/// </summary>
public sealed record SkillConfig
{
    /// <summary>是否启用 Skill 系统。默认 true。false 时 skill_loader 不注册，/commit 返回"未启用"。</summary>
    public bool? Enable { get; init; }

    /// <summary>同时激活的 Skill 上限。默认 3。防止 LLM 激活过多 Skill 污染上下文。</summary>
    public int? MaxActiveSkills { get; init; }
}

/// <summary>
/// 子 Agent 系统配置（迭代 14 新增）。所有字段可选，缺省用默认值。
/// </summary>
public sealed record SubAgentConfig
{
    /// <summary>
    /// 是否启用子 Agent 系统。默认 true。false 时 sub_agent 工具不注册。
    /// </summary>
    public bool? Enable { get; init; }

    /// <summary>
    /// 子 Agent 最大轮次。默认 5（比主 Agent 的 10 少，防止失控）。
    /// </summary>
    public int? MaxRounds { get; init; }

    /// <summary>
    /// 报告最大字符数。默认 2000（超长截断 + 提示）。
    /// </summary>
    public int? ReportMaxChars { get; init; }

    /// <summary>
    /// 最大并发后台任务数。默认 3（BackgroundTaskManager 用）。
    /// </summary>
    public int? MaxConcurrentBackground { get; init; }
}

/// <summary>
/// Agent 循环配置。所有字段可选，缺省用默认值。
/// </summary>
public sealed record AgentConfig
{
    /// <summary>
    /// 最大 ReAct 轮次，默认 10。防止无限循环。
    /// </summary>
    public int? MaxRounds { get; init; }

    /// <summary>
    /// tool_choice：auto（默认）/ none / required。
    /// </summary>
    public string? ToolChoice { get; init; }

    /// <summary>
    /// Read 工具最大并发度，默认 5。
    /// </summary>
    public int? MaxParallelism { get; init; }

    /// <summary>
    /// 工具执行超时秒数，默认 30（透传给 ToolExecutor）。
    /// </summary>
    public int? ToolTimeoutSeconds { get; init; }

    /// <summary>
    /// system prompt，null 用默认。
    /// </summary>
    public string? SystemPrompt { get; init; }
}

/// <summary>
/// TUI 渲染配置（迭代 7a 新增，7b 扩展 EnableHitl）。所有字段可选，缺省用默认值。
/// </summary>
public sealed record TuiConfig
{
    /// <summary>
    /// 渲染模式："live"（默认）| "console"（降级行模式）。
    /// </summary>
    public string? Mode { get; init; }

    /// <summary>
    /// 是否显示状态栏，默认 true。
    /// </summary>
    public bool? ShowStatusBar { get; init; }

    /// <summary>
    /// 上下文窗口 token 数（状态栏占比分母），默认 64000。
    /// </summary>
    public int? ContextWindowTokens { get; init; }

    /// <summary>
    /// 是否启用 HITL，默认 true。false 时注入 NullHitlGate（7b 新增）。
    /// </summary>
    public bool? EnableHitl { get; init; }
}

/// <summary>
/// 安全配置（7b 占位，迭代 8 接入真实拦截）。
/// 迭代 8c 扩展 AllowPaths / DenyPaths / ExtraBlacklist，由 App.RunAsync 规范化后传入 SecurityGuard。
/// </summary>
public sealed record SecurityConfig
{
    /// <summary>
    /// 安全等级："strict" | "normal"（默认）| "permissive"。大小写不敏感；兼容旧拼法 "permisive"。
    /// 7b 仅状态栏显示，迭代 8 接入真实拦截。
    /// </summary>
    public string? Level { get; init; }

    /// <summary>
    /// 额外允许的路径白名单（绝对或相对项目根）。Strict 模式下只允许这些 + 项目根的读写。
    /// 相对路径基于项目根（当前工作目录）解析为绝对。
    /// </summary>
    public IList<string> AllowPaths { get; init; } = Array.Empty<string>();

    /// <summary>
    /// 显式拒绝的路径（最高优先级，所有非 Permissive 档位生效）。
    /// </summary>
    public IList<string> DenyPaths { get; init; } = Array.Empty<string>();

    /// <summary>
    /// 额外黑名单命令正则模式（与硬编码黑名单合并）。YAML 中反斜杠需转义（如 \\b）。
    /// </summary>
    public IList<string> ExtraBlacklist { get; init; } = Array.Empty<string>();
}

/// <summary>
/// 上下文管理配置（迭代 9 新增）。null 时用默认值。
/// </summary>
public sealed record ContextConfig
{
    /// <summary>
    /// 上下文窗口 token 数。null 时回退 TuiConfig.ContextWindowTokens ?? 64000。
    /// </summary>
    public int? ContextWindowTokens { get; init; }

    /// <summary>
    /// 警告阈值（占窗口比例）。默认 0.7。
    /// </summary>
    public double? WarningFraction { get; init; }

    /// <summary>
    /// 触发摘要阈值（占窗口比例）。默认 0.9。
    /// </summary>
    public double? TriggerFraction { get; init; }

    /// <summary>
    /// 单条工具结果截断阈值（字符数）。默认 50_000。
    /// </summary>
    public int? PerResultThreshold { get; init; }

    /// <summary>
    /// 一轮内工具结果合计截断阈值（字符数）。默认 200_000。
    /// </summary>
    public int? RoundTotalThreshold { get; init; }

    /// <summary>
    /// 截断后保留预览长度（字符数）。默认 2_000。
    /// </summary>
    public int? PreviewLength { get; init; }

    /// <summary>
    /// 摘要时保留的最近消息数。默认 4。
    /// </summary>
    public int? KeepRecentMessages { get; init; }

    /// <summary>
    /// 熔断器最大连续失败次数。默认 2。
    /// </summary>
    public int? MaxCircuitFailures { get; init; }

    /// <summary>
    /// 是否启用自动压缩。默认 true。false 时仅截断不摘要。
    /// </summary>
    public bool? EnableAutoCompress { get; init; }
}

/// <summary>
/// 会话持久化配置（迭代 10b 新增）。null 时用默认值。
/// </summary>
public sealed record SessionConfig
{
    /// <summary>
    /// 会话存储目录。默认 ".parrotcode/sessions"（项目根下）。
    /// </summary>
    public string? StorageDir { get; init; }

    /// <summary>
    /// 是否启用会话持久化。默认 true。false 时 /session 命令不可用。
    /// </summary>
    public bool? Enable { get; init; }
}

/// <summary>
/// 项目指令配置（迭代 10c 新增）。null 时用默认值。
/// </summary>
public sealed record InstructionsConfig
{
    /// <summary>
    /// 是否启用项目指令加载。默认 true。false 时不扫描任何指令文件。
    /// </summary>
    public bool? Enable { get; init; }

    /// <summary>
    /// @include 最大嵌套深度。默认 3。
    /// </summary>
    public int? MaxIncludeDepth { get; init; }

    /// <summary>
    /// 自定义项目指令文件路径（覆盖默认的 ./PARROTCODE.md）。
    /// </summary>
    public string? ProjectInstructionsPath { get; init; }
}

/// <summary>
/// Hook 引擎配置（迭代 15 新增）。所有字段可选，缺省用默认值。
/// </summary>
public sealed record HooksConfig
{
    /// <summary>
    /// 是否启用 Hook 引擎。默认 false（Hook 可执行 shell/http，安全敏感，需显式开启）。
    /// false 时不加载 Hook 规则，HookEngine 传 null。
    /// </summary>
    public bool? Enable { get; init; }
}
