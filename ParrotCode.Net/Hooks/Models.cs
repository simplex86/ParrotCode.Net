using YamlDotNet.Serialization;

namespace ParrotCode;

/// <summary>
/// Hook 事件类型（12 种，五类）。tool_pre_exec 是唯一的拦截事件。
/// 迭代 15a：Hook 引擎数据模型。
/// </summary>
public enum HookEvent
{
    // 会话类
    SessionStart,       // 会话开始（用户输入前）
    SessionEnd,         // 会话结束（Agent 完成或取消后）

    // 轮次类
    RoundStart,         // ReAct 轮次开始
    RoundEnd,           // ReAct 轮次结束

    // 消息类
    MessagePreSend,     // 发送给 LLM 前（消息列表已构建）
    MessagePostReceive, // 收到 LLM 回复后（assistant 消息入历史）

    // 工具类
    ToolPreExec,        // 工具执行前（拦截——可返回拒绝原因）
    ToolPostExec,       // 工具执行后（通知——拿不到拒绝能力）

    // 系统类
    SystemStartup,      // 程序启动
    SystemShutdown,     // 程序关闭
    SystemError,        // 致命错误（AgentLoop ErrorEvent）
    SystemCompress      // 上下文压缩完成
}

/// <summary>
/// 拦截事件集合——这些事件的 prompt_inject 动作返回值作为拒绝原因。
/// 拦截事件不允许 async: true（必须同步返回结果）。
/// </summary>
internal static class InterceptEvents
{
    public static readonly HashSet<HookEvent> Set = new() { HookEvent.ToolPreExec };
}

/// <summary>
/// 条件匹配算子。
/// </summary>
public enum HookOperator
{
    /// <summary>
    /// 精确相等（字符串比较）
    /// </summary>
    Exact,
    /// <summary>
    /// 不等于
    /// </summary>
    Not,
    /// <summary>
    /// 正则匹配（Regex.IsMatch）
    /// </summary>
    Regex,
    /// <summary>
    /// 通配符匹配（支持 * 和 ?）
    /// </summary>
    Glob
}

/// <summary>
/// 条件匹配模式。
/// </summary>
public enum HookMatchMode
{
    /// <summary>所有规则都满足（默认）</summary>
    All,
    /// <summary>任一规则满足</summary>
    Any
}

/// <summary>
/// 动作类型。
/// </summary>
public enum HookActionType
{
    /// <summary>
    /// 执行 shell 命令
    /// </summary>
    Shell,
    /// <summary>
    /// 注入提示文本（拦截事件中作为拒绝原因）
    /// </summary>
    PromptInject,
    /// <summary>
    /// 调用 HTTP webhook
    /// </summary>
    Http,
    /// <summary>
    /// 起子 Agent 执行自动化任务（依赖迭代 14 SubAgentRunner，15b 实现）
    /// </summary>
    SubAgent
}

/// <summary>
/// 单条条件规则：字段路径 + 算子 + 目标值。
/// field 是 dot-path，如 "tool_name" / "params.path" / "round"。
/// Operator 是字符串（YAML 反序列化用），OperatorEnum 是解析后的强类型（Loader 填充）。
/// </summary>
public sealed class ConditionRule
{
    public string Field { get; set; } = string.Empty;

    /// <summary>
    /// YAML 中的算子字符串（exact/not/regex/glob）。Loader 解析为 OperatorEnum。
    /// </summary>
    public string Operator { get; set; } = "exact";

    public string Value { get; set; } = string.Empty;

    /// <summary>
    /// 解析后的强类型算子（Loader.ValidateAndNormalize 填充，YAML 忽略）。
    /// </summary>
    [YamlIgnore]
    public HookOperator OperatorEnum { get; set; }
}

/// <summary>
/// 条件：match 模式 + 规则列表。null 或空规则列表 = 无条件触发。
/// Match 是字符串（YAML 反序列化用），MatchMode 是解析后的强类型（Loader 填充）。
/// </summary>
public sealed class HookCondition
{
    /// <summary>
    /// YAML 中的匹配模式字符串（ALL/ANY）。Loader 解析为 MatchMode。
    /// </summary>
    public string Match { get; set; } = "ALL";

    public List<ConditionRule> Rules { get; set; } = new();

    /// <summary>
    /// 解析后的强类型匹配模式（Loader.ValidateAndNormalize 填充，YAML 忽略）。
    /// </summary>
    [YamlIgnore]
    public HookMatchMode MatchMode { get; set; }
}

/// <summary>
/// Hook 动作。根据 Type 使用不同字段。
/// Type 是字符串（YAML 反序列化用），ActionType 是解析后的强类型（Loader 填充）。
/// </summary>
public sealed class HookAction
{
    /// <summary>
    /// YAML 中的动作类型字符串（shell/prompt_inject/http/sub_agent）。
    /// Loader 解析为 ActionType。
    /// </summary>
    public string Type { get; set; } = string.Empty;

    // shell
    public string Command { get; set; } = string.Empty;

    // prompt_inject
    public string Text { get; set; } = string.Empty;

    // http
    public string Url { get; set; } = string.Empty;
    public string Method { get; set; } = "POST";
    public Dictionary<string, string> Headers { get; set; } = new();
    public string Body { get; set; } = string.Empty;

    // sub_agent（15b 实现）
    public string Task { get; set; } = string.Empty;
    public string Role { get; set; } = "general";
    public string Mode { get; set; } = "definitional";

    /// <summary>
    /// 解析后的强类型动作类型（Loader.ValidateAndNormalize 填充，YAML 忽略）。
    /// </summary>
    [YamlIgnore]
    public HookActionType ActionType { get; set; }
}

/// <summary>
/// 控制选项。
/// </summary>
public sealed class HookControl
{
    /// <summary>
    /// 只触发一次（触发后自动跳过后续匹配）。用 rule.Name 跟踪。
    /// </summary>
    public bool Once { get; set; }

    /// <summary>
    /// 异步执行（fire-and-forget）。拦截事件禁止 async=true。
    /// </summary>
    public bool Async { get; set; }

    /// <summary>
    /// 动作执行超时（秒）。默认 30。
    /// </summary>
    public double Timeout { get; set; } = 30.0;
}

/// <summary>
/// Hook 规则：事件 + 条件 + 动作列表 + 控制选项。
/// Event 是字符串（YAML 反序列化用），EventType 是解析后的强类型（Loader 填充）。
/// </summary>
public sealed class HookRule
{
    /// <summary>
    /// YAML 中的事件字符串（session_start/tool_pre_exec 等）。
    /// Loader 解析为 EventType。
    /// </summary>
    public string Event { get; set; } = string.Empty;

    public HookCondition? Condition { get; set; }
    public List<HookAction> Actions { get; set; } = new();
    public HookControl Control { get; set; } = new();
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// 解析后的强类型事件（Loader.ValidateAndNormalize 填充，YAML 忽略）。
    /// </summary>
    [YamlIgnore]
    public HookEvent EventType { get; set; }

    /// <summary>
    /// 是否为拦截事件。
    /// </summary>
    public bool IsIntercept => InterceptEvents.Set.Contains(EventType);
}

/// <summary>
/// Hook 配置异常（校验失败）。
/// </summary>
public sealed class HookConfigException : Exception
{
    public HookConfigException(string message) : base(message) { }
}
