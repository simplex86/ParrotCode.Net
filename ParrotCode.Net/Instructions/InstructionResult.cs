namespace ParrotCode;

/// <summary>
/// 指令加载结果（迭代 10c）。
/// </summary>
public sealed record InstructionResult
{
    /// <summary>
    /// 合并后的指令文本（注入 system prompt）。
    /// </summary>
    public string Content { get; init; } = string.Empty;

    /// <summary>
    /// 来源文件列表（含全局/项目/本地 + @include 展开）。
    /// </summary>
    public IReadOnlyList<string> Sources { get; init; } = Array.Empty<string>();

    /// <summary>
    /// 是否有任何指令被加载。
    /// </summary>
    public bool HasInstructions => !string.IsNullOrWhiteSpace(Content);
}
