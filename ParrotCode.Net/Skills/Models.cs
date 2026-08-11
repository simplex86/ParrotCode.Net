namespace ParrotCode;

/// <summary>
/// Skill 元数据（对应 frontmatter）。
/// 用 YamlDotNet + UnderscoredNamingConvention 反序列化，snake_case 自动映射 PascalCase。
/// 迭代 12：Skill 系统。
/// </summary>
public sealed class SkillMeta
{
    /// <summary>
    /// Skill 名称（必填，与文件名建议一致）。
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Skill 描述（写给 LLM，说明何时调用此 Skill）。
    /// </summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>
    /// 允许的工具白名单（可选，工具名列表）。
    /// </summary>
    public List<string> ToolsAllow { get; set; } = new();

    /// <summary>
    /// 禁用的工具黑名单（可选，工具名列表）。
    /// </summary>
    public List<string> ToolsDeny { get; set; } = new();
}

/// <summary>
/// 完整 Skill 定义：元数据 + SOP 正文 + 来源路径 + 子资源（迭代 13a 扩展）。
/// </summary>
public sealed record SkillDefinition
{
    /// <summary>
    /// 元数据（frontmatter 解析结果）。
    /// </summary>
    public required SkillMeta Meta { get; init; }

    /// <summary>
    /// SOP 正文（frontmatter 之后的 Markdown，注入给 LLM）。
    /// </summary>
    public string Body { get; init; } = string.Empty;

    /// <summary>
    /// 来源文件绝对路径（调试/日志用）。
    /// </summary>
    public string SourcePath { get; init; } = string.Empty;

    /// <summary>
    /// 来源层级（用于覆盖优先级判定与显示）。
    /// </summary>
    public SkillSource Source { get; init; }

    /// <summary>
    /// Skill 目录绝对路径（目录格式 Skill 才有，单文件 Skill 为 null）。
    /// 用于扫描子资源 + 调试定位。
    /// </summary>
    public string? SkillDir { get; init; }

    /// <summary>
    /// 子资源列表（Phase 3 按需加载项）。
    /// 单文件 Skill 或无子目录的目录 Skill 均为空列表。
    /// </summary>
    public IReadOnlyList<SkillResource> Resources { get; init; } = Array.Empty<SkillResource>();
}

/// <summary>
/// Skill 子资源类型（Phase 3 按需加载）。
/// 类型由所在子目录决定，不校验文件扩展名。
/// </summary>
public enum SkillResourceKind
{
    /// <summary>
    /// scripts/ 下的脚本，用 run_command 执行。
    /// </summary>
    Script,
    /// <summary>
    /// references/ 下的参考文档，用 read_file 读取。
    /// </summary>
    Reference,
    /// <summary>
    /// assets/ 下的资产，用 read_file / write_file 访问。
    /// </summary>
    Asset
}

/// <summary>
/// Skill 子资源（Phase 3 按需加载项）。
/// 不随 SOP 正文加载，只在资源清单中列出绝对路径，LLM 按需用现有工具访问。
/// </summary>
public sealed record SkillResource
{
    /// <summary>
    /// 资源类型（决定 LLM 应使用哪个工具访问）。
    /// </summary>
    public required SkillResourceKind Kind { get; init; }

    /// <summary>
    /// 相对 SkillDir 的路径（如 "scripts/clean.py"），用于清单可读性。
    /// </summary>
    public required string RelativePath { get; init; }

    /// <summary>
    /// 绝对路径（LLM 传给 read_file / run_command 的值）。
    /// </summary>
    public required string AbsolutePath { get; init; }
}

/// <summary>
/// Skill 来源层级（决定同名覆盖优先级：Project > Global > Builtin）。
/// </summary>
public enum SkillSource
{
    Builtin,
    Global,
    Project
}

/// <summary>
/// 激活/停用操作的结果。
/// </summary>
public sealed record SkillActivateResult
{
    /// <summary>
    /// 是否成功。
    /// </summary>
    public bool Success { get; init; }

    /// <summary>
    /// 激活成功时的 Skill 名称。
    /// </summary>
    public string? SkillName { get; init; }

    /// <summary>
    /// 激活成功时的完整 SOP 内容（注入 history）。
    /// </summary>
    public string? SopContent { get; init; }

    /// <summary>
    /// 失败原因。
    /// </summary>
    public string? Error { get; init; }
}
