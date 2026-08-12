namespace ParrotCode;

/// <summary>
/// 角色元数据（对应角色文件 frontmatter）。与 <see cref="SkillMeta"/> 同构。
/// 迭代 14a：子 Agent 角色系统。
/// </summary>
public sealed class RoleMeta
{
    /// <summary>
    /// 角色名称（必填，与文件名建议一致，如 explorer.md → name: explorer）。
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// 角色描述（写给主 Agent 看，说明"何时用此角色"）。
    /// </summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>
    /// 允许的工具白名单（可选，工具名列表）。空表示不限制。
    /// 非空时取白名单与父工具集的交集。
    /// </summary>
    public List<string> ToolsAllow { get; set; } = new();

    /// <summary>
    /// 禁用的工具黑名单（可选，工具名列表）。与全局 deny 取并集。
    /// </summary>
    public List<string> ToolsDeny { get; set; } = new();
}

/// <summary>
/// 完整角色定义：元数据 + SOP 正文 + 来源路径。
/// </summary>
public sealed record RoleDefinition
{
    /// <summary>
    /// 元数据（frontmatter 解析结果）。
    /// </summary>
    public required RoleMeta Meta { get; init; }

    /// <summary>
    /// SOP 正文（frontmatter 之后的 Markdown，注入为子 Agent 的 system prompt）。
    /// </summary>
    public string Body { get; init; } = string.Empty;

    /// <summary>
    /// 来源文件绝对路径（调试/日志用）。
    /// </summary>
    public string SourcePath { get; init; } = string.Empty;

    /// <summary>
    /// 来源层级（用于覆盖优先级判定）。
    /// </summary>
    public RoleSource Source { get; init; }
}

/// <summary>
/// 角色来源层级（决定同名覆盖优先级：Project > Global > Builtin）。
/// </summary>
public enum RoleSource
{
    Builtin,
    Global,
    Project
}

/// <summary>
/// 子 Agent 运行模式（迭代 14a 定义，供 <see cref="ToolFilter"/> 使用；14b 的 SubAgentRequest/Result 复用）。
/// </summary>
public enum SubAgentMode
{
    /// <summary>
    /// 定义式：空白对话 + 角色 SOP 作为 system prompt。
    /// 不继承父上下文，从零开始执行任务。适合独立子任务。
    /// </summary>
    Definitional,

    /// <summary>
    /// Fork 式：继承父对话历史 + 注入强硬指令。
    /// 子 Agent 能看到父上下文，但被约束为不创建子 worker、不对话、直接干活、结构化报告。
    /// Fork 模式额外排除 skill_loader（子 Agent 不加载 Skill）。
    /// </summary>
    Fork
}

// ===== 迭代 14b 追加：子 Agent 运行时类型 =====

/// <summary>
/// 子 Agent 请求（sub_agent 工具的参数载体，迭代 14b 新增）。
/// </summary>
public sealed record SubAgentRequest
{
    /// <summary>任务描述（作为子 Agent 的 user 消息）。必填。</summary>
    public required string Task { get; init; }

    /// <summary>角色名（definitional 模式用，默认 general）。角色定义 system prompt + 工具过滤。</summary>
    public string Role { get; init; } = "general";

    /// <summary>运行模式。默认 Definitional。</summary>
    public SubAgentMode Mode { get; init; } = SubAgentMode.Definitional;
}

/// <summary>
/// 子 Agent 运行结果（迭代 14b 新增）。
/// </summary>
public sealed record SubAgentResult
{
    /// <summary>是否成功完成（含 MaxRounds 兜底——只要拿到报告就 true）。</summary>
    public bool Success { get; init; }

    /// <summary>
    /// 子 Agent 的最终报告（AgentDoneEvent.FinalText 或 MaxRounds 时的 LastAssistantText，可能截断）。
    /// </summary>
    public string? Report { get; init; }

    /// <summary>
    /// 失败原因（角色不存在 / 异常等）。
    /// </summary>
    public string? Error { get; init; }

    /// <summary>
    /// 子 Agent 实际执行的轮次数。
    /// </summary>
    public int RoundsUsed { get; init; }
}
