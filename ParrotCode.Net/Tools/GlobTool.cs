using System.Text.Json;

namespace ParrotCode;

/// <summary>
/// 文件名模式匹配工具。Category=Read（幂等、可并发）。
/// 参数：pattern（glob 模式，如 *.cs）+ path（搜索根目录，默认当前目录）。
/// 递归搜索匹配文件，返回相对路径列表（按路径排序，最多 200 条）。
/// </summary>
public sealed class GlobTool : ToolBase
{
    private const int MaxResults = 200;

    public override string Name => "glob";

    public override string Description =>
        "按 glob 模式递归查找文件。pattern 支持 * / ** / ?（如 **/*.cs 匹配所有 .cs 文件）。" +
        "返回匹配文件的相对路径列表（按路径排序，最多返回 200 条）。";

    public override ToolCategory Category => ToolCategory.Read;

    public override IReadOnlyList<ToolParameter> Parameters { get; } = new[]
    {
        new ToolParameter("pattern", "string", "glob 模式（如 **/*.cs / *.md）", Required: true),
        new ToolParameter("path", "string", "搜索根目录（默认当前目录）", Required: false)
    };

    public override async Task<ToolResult> ExecuteAsync(JsonElement input, CancellationToken cancellationToken)
    {
        var pattern = GetRequiredString(input, "pattern", out var err1);
        if (err1 is not null) return ToolResult.Fail(err1);
        var path = GetOptionalString(input, "path", out var err2, ".");
        if (err2 is not null) return ToolResult.Fail(err2);

        if (string.IsNullOrWhiteSpace(pattern))
            return ToolResult.Fail("参数 pattern 不能为空");
        if (!Directory.Exists(path))
            return ToolResult.Fail($"目录不存在：{path}");

        try
        {
            var regex = GlobPattern.ToRegex(pattern);
            var matches = new List<string>();

            // 用 Task.Run 包装同步枚举，避免阻塞调用线程
            await Task.Run(() =>
            {
                foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var relative = Path.GetRelativePath(path, file).Replace('\\', '/');
                    if (regex.IsMatch(relative))
                    {
                        matches.Add(relative);
                        if (matches.Count >= MaxResults) break;
                    }
                }
            }, cancellationToken);

            matches.Sort(StringComparer.Ordinal);

            return ToolResult.Ok(matches.Count == 0
                ? "未找到匹配文件"
                : $"找到 {matches.Count} 个文件：\n{string.Join('\n', matches)}");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return ToolResult.Fail($"查找失败：{ex.Message}");
        }
    }
}
