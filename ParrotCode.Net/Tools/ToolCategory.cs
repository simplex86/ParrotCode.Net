namespace ParrotCode;

/// <summary>
/// 工具分类：决定执行策略。
/// Read：幂等、无副作用、可并发（迭代 6 AgentLoop 用 Task.WhenAll 批量执行）。
/// Write：有副作用、可能冲突、需串行（迭代 6 顺序 await）。
/// 本迭代 ToolExecutor 单次执行，分类仅作元信息；分批执行在迭代 6。
/// </summary>
public enum ToolCategory
{
    Read,
    Write
}
