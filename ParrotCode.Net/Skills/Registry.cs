using System.Text;
using Microsoft.Extensions.Logging;

namespace ParrotCode;

/// <summary>
/// Skill 注册表（迭代 12）：管理已加载 Skill + 激活状态 + Phase 1 摘要生成。
/// 线程安全：激活/停用加锁（AgentLoop 单线程消费，但 /commit 命令也访问）。
/// </summary>
public sealed class SkillRegistry
{
    private readonly Dictionary<string, SkillDefinition> _skills;
    private readonly HashSet<string> _active = new(StringComparer.Ordinal);
    private readonly int _maxActive;
    private readonly object _lock = new();
    private readonly ILogger? _logger;

    public SkillRegistry(IReadOnlyDictionary<string, SkillDefinition> skills,
                         int maxActive = 3,
                         ILogger? logger = null)
    {
        _skills = new Dictionary<string, SkillDefinition>(skills, StringComparer.Ordinal);
        _maxActive = maxActive;
        _logger = logger;
    }

    /// <summary>
    /// 是否含任何 Skill。
    /// </summary>
    public bool HasSkills => _skills.Count > 0;

    /// <summary>
    /// 已加载 Skill 总数。
    /// </summary>
    public int Count => _skills.Count;

    /// <summary>
    /// 获取 Skill（不存在返回 null）。
    /// </summary>
    public SkillDefinition? Get(string name)
    {
        _skills.TryGetValue(name, out var def);
        return def;
    }

    /// <summary>
    /// 所有已加载 Skill 的快照。
    /// </summary>
    public IReadOnlyCollection<SkillDefinition> GetAll() => _skills.Values.ToList().AsReadOnly();

    /// <summary>
    /// 激活 Skill。返回 SkillActivateResult（含 SOP 内容用于注入 history）。
    /// 已激活的 Skill 重复激活幂等（返回 SOP，不重复计数）。
    /// </summary>
    public SkillActivateResult Activate(string name)
    {
        lock (_lock)
        {
            if (!_skills.TryGetValue(name, out var def))
                return new SkillActivateResult { Success = false, Error = $"未找到 Skill：{name}" };

            if (_active.Contains(name))
            {
                _logger?.LogDebug("Skill {Name} 已激活，幂等返回 SOP", name);
                return new SkillActivateResult
                {
                    Success = true,
                    SkillName = name,
                    SopContent = BuildSop(def)
                };
            }

            if (_active.Count >= _maxActive)
            {
                return new SkillActivateResult
                {
                    Success = false,
                    Error = $"已激活 {_active.Count} 个 Skill，达到上限 {_maxActive}，请先停用其他 Skill"
                };
            }

            _active.Add(name);
            _logger?.LogInformation("激活 Skill：{Name}（来源 {Source}）", name, def.Source);
            return new SkillActivateResult
            {
                Success = true,
                SkillName = name,
                SopContent = BuildSop(def)
            };
        }
    }

    /// <summary>
    /// 停用 Skill。返回是否确实停用了（之前处于激活状态）。
    /// </summary>
    public bool Deactivate(string name)
    {
        lock (_lock)
        {
            var removed = _active.Remove(name);
            if (removed)
                _logger?.LogInformation("停用 Skill：{Name}", name);
            return removed;
        }
    }

    /// <summary>
    /// 是否处于激活状态。
    /// </summary>
    public bool IsActive(string name)
    {
        lock (_lock) { return _active.Contains(name); }
    }

    /// <summary>
    /// 当前激活的 Skill 列表快照。
    /// </summary>
    public IReadOnlyList<SkillDefinition> GetActiveSkills()
    {
        lock (_lock)
        {
            return _active.Select(n => _skills[n]).ToList();
        }
    }

    /// <summary>
    /// Phase 1 摘要：注入 system prompt，让 LLM 知道有哪些 Skill 可调。
    /// 仅含 name + description，不含 SOP 正文（避免 prompt 膨胀）。
    /// </summary>
    public string GetSummary()
    {
        if (_skills.Count == 0) return string.Empty;
        var sb = new StringBuilder();
        sb.AppendLine("## 可用 Skills");
        sb.AppendLine("（调用 `skill_loader` 工具加载完整 SOP 后按其指引工作）");
        foreach (var def in _skills.Values.OrderBy(d => d.Meta.Name, StringComparer.Ordinal))
        {
            sb.AppendLine($"- {def.Meta.Name}: {def.Meta.Description}");
        }
        return sb.ToString();
    }

    /// <summary>
    /// 构建注入 history 的 SOP 文本（含元信息提示，便于 LLM 理解约束）。
    /// 作为 skill_loader 工具的 ToolResult.Content 返回。
    /// </summary>
    private static string BuildSop(SkillDefinition def)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# Skill: {def.Meta.Name}");
        sb.AppendLine();
        sb.AppendLine(def.Body);
        if (def.Meta.ToolsAllow.Count > 0)
            sb.AppendLine().AppendLine($"**可用工具**：{string.Join(", ", def.Meta.ToolsAllow)}");
        if (def.Meta.ToolsDeny.Count > 0)
            sb.AppendLine().AppendLine($"**禁用工具**：{string.Join(", ", def.Meta.ToolsDeny)}");
        return sb.ToString();
    }
}
