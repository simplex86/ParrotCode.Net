namespace ParrotCode;

/// <summary>
/// Skill 执行器（迭代 12）：激活/停用 + 计算激活 Skill 的工具白名单交集。
/// /commit 命令通过此执行器激活 Skill。
///
/// 注意：工具白名单的"实际拦截"在 SecurityGuard/BatchToolExecutor 层（迭代 15 Hook 引擎再统一接）；
/// 本迭代 Executor 仅计算交集并暴露给查询方，不强制拦截，保持 Skill 系统独立可验收。
/// </summary>
public sealed class SkillExecutor
{
    private readonly SkillRegistry _registry;

    public SkillExecutor(SkillRegistry registry)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
    }

    /// <summary>
    /// 激活 Skill，返回激活结果（含 SOP 内容）。
    /// </summary>
    public SkillActivateResult Activate(string name) => _registry.Activate(name);

    /// <summary>
    /// 停用 Skill。
    /// </summary>
    public bool Deactivate(string name) => _registry.Deactivate(name);

    /// <summary>
    /// 当前激活的 Skill 列表快照。
    /// </summary>
    public IReadOnlyList<SkillDefinition> GetActive() => _registry.GetActiveSkills();

    /// <summary>
    /// 所有已加载 Skill 的快照（迭代 13b 新增，/skill list 用）。
    /// </summary>
    public IReadOnlyCollection<SkillDefinition> GetAll() => _registry.GetAll();

    /// <summary>
    /// 是否处于激活状态。
    /// </summary>
    public bool IsActive(string name) => _registry.IsActive(name);

    /// <summary>
    /// 计算当前激活 Skill 的工具白名单交集。
    /// 规则：
    ///   - tools_deny 并集（任一 Skill 禁用则禁用）
    ///   - tools_allow 交集（仅当所有激活 Skill 都声明了 tools_allow 时取交集；
    ///     任一未声明 tools_allow 表示不限制，则整体不限制 allow）
    /// </summary>
    public (IReadOnlyList<string> Allowed, IReadOnlyList<string> Denied) GetEffectiveToolFilter()
    {
        var active = _registry.GetActiveSkills();
        if (active.Count == 0)
            return (Array.Empty<string>(), Array.Empty<string>());

        // tools_deny 并集
        var denied = active.SelectMany(d => d.Meta.ToolsDeny)
                           .Distinct(StringComparer.Ordinal)
                           .ToList();

        // tools_allow：仅当所有激活 Skill 都声明了非空 tools_allow 时取交集
        if (active.All(d => d.Meta.ToolsAllow.Count > 0))
        {
            var sets = active.Select(d => d.Meta.ToolsAllow.ToHashSet(StringComparer.Ordinal)).ToList();
            var allowed = sets.First();
            foreach (var s in sets.Skip(1))
                allowed.IntersectWith(s);
            return (allowed.ToList(), denied);
        }

        // 有 Skill 不限制 allow → 整体不限制
        return (Array.Empty<string>(), denied);
    }
}
