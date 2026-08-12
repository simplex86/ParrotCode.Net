namespace ParrotCode;

/// <summary>
/// 工具过滤器（迭代 14a）：从父 <see cref="ToolRegistry"/> 构建子 Agent 的过滤 <see cref="ToolRegistry"/>。
/// 三层过滤（全部在构建时一次性完成）：
/// <list type="number">
///   <item>全局：始终排除 <c>sub_agent</c>（禁止子 Agent 嵌套，防止无限递归）</item>
///   <item>角色：<c>tools_allow</c>（白名单，声明了才取交集）/ <c>tools_deny</c>（黑名单并集）</item>
///   <item>模式：Fork 模式额外排除 <c>skill_loader</c>（子 Agent 不加载 Skill，保持聚焦）</item>
/// </list>
/// 纯函数：相同输入相同输出，无副作用。
/// </summary>
public static class ToolFilter
{
    /// <summary>
    /// 构建过滤后的 <see cref="ToolRegistry"/>。
    /// </summary>
    /// <param name="parent">父工具注册表（主 Agent 的完整工具集）。</param>
    /// <param name="role">角色定义（提供 tools_allow / tools_deny）。</param>
    /// <param name="mode">运行模式（Fork 额外排除 skill_loader）。</param>
    /// <returns>新的 <see cref="ToolRegistry"/>，仅含过滤后的工具。</returns>
    public static ToolRegistry Build(ToolRegistry parent, RoleDefinition role, SubAgentMode mode)
    {
        var filtered = new ToolRegistry();

        // 第 1 层 + 第 3 层：全局禁止 sub_agent + 模式约束
        var deny = new HashSet<string>(StringComparer.Ordinal) { "sub_agent" };
        if (mode == SubAgentMode.Fork)
            deny.Add("skill_loader");

        // 第 2 层：角色 tools_deny 并入 deny
        foreach (var t in role.Meta.ToolsDeny)
            deny.Add(t);

        // 第 2 层：角色 tools_allow（白名单，空表示不限制）
        var allow = role.Meta.ToolsAllow.Count > 0
            ? new HashSet<string>(role.Meta.ToolsAllow, StringComparer.Ordinal)
            : null;

        foreach (var tool in parent.GetAll())
        {
            if (deny.Contains(tool.Name)) continue;
            if (allow is not null && !allow.Contains(tool.Name)) continue;
            filtered.Register(tool);
        }

        return filtered;
    }
}
