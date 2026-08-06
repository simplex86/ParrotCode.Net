using System.Text.Json;
using System.Text.RegularExpressions;

namespace ParrotCode;

/// <summary>
/// 内容正则搜索工具。Category=Read（幂等、可并发）。
/// 参数：pattern（正则）+ path（搜索目录，默认当前）+ include（文件名 glob 过滤，如 *.cs）。
/// 逐文件逐行搜索，返回匹配的 文件:行号:行内容 列表（最多 100 条）。
/// </summary>
public sealed class GrepTool : ToolBase
{
    public const int MaxMatches = 100;
    private const int MaxLinePreviewLength = 120;

    public override string Name => "grep";

    public override string Description =>
        "在文件内容中搜索正则匹配。返回 文件:行号:行内容 列表。" +
        "默认搜当前目录所有文件，可用 include 过滤文件类型（如 *.cs）。" +
        "最多返回 100 条匹配，超出截断并提示。";

    public override ToolCategory Category => ToolCategory.Read;

    public override IReadOnlyList<ToolParameter> Parameters { get; } = new[]
    {
        new ToolParameter("pattern", "string", "正则表达式", Required: true),
        new ToolParameter("path", "string", "搜索根目录（默认当前目录）", Required: false),
        new ToolParameter("include", "string", "文件名 glob 过滤（如 *.cs，默认所有文件）", Required: false)
    };

    public override async Task<ToolResult> ExecuteAsync(JsonElement input, CancellationToken cancellationToken)
    {
        var pattern = GetRequiredString(input, "pattern", out var err1);
        if (err1 is not null) return ToolResult.Fail(err1);
        var path = GetOptionalString(input, "path", out var err2, ".");
        if (err2 is not null) return ToolResult.Fail(err2);
        var include = GetOptionalString(input, "include", out var err3, "");
        if (err3 is not null) return ToolResult.Fail(err3);

        if (string.IsNullOrWhiteSpace(pattern))
            return ToolResult.Fail("参数 pattern 不能为空");
        if (!Directory.Exists(path))
            return ToolResult.Fail($"目录不存在：{path}");

        Regex regex;
        try { regex = new Regex(pattern, RegexOptions.Compiled); }
        catch (ArgumentException ex) { return ToolResult.Fail($"正则非法：{ex.Message}"); }

        var includeRegex = string.IsNullOrEmpty(include)
            ? null
            : GlobPattern.ToRegex(include);

        try
        {
            var matches = new List<string>();
            foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var relative = Path.GetRelativePath(path, file).Replace('\\', '/');
                if (includeRegex is not null && !includeRegex.IsMatch(relative)) continue;

                string[] lines;
                try { lines = await File.ReadAllLinesAsync(file, cancellationToken); }
                catch (IOException) { continue; } // 跳过无法读的文件

                for (var i = 0; i < lines.Length; i++)
                {
                    if (regex.IsMatch(lines[i]))
                    {
                        matches.Add($"{relative}:{i + 1}:{Truncate(lines[i], MaxLinePreviewLength)}");
                        if (matches.Count >= MaxMatches)
                        {
                            matches.Add($"...（已达 {MaxMatches} 条上限，可能还有更多匹配）");
                            return ToolResult.Ok(string.Join('\n', matches));
                        }
                    }
                }
            }
            return ToolResult.Ok(matches.Count == 0
                ? "未找到匹配"
                : $"找到 {matches.Count} 条匹配：\n{string.Join('\n', matches)}");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return ToolResult.Fail($"搜索失败：{ex.Message}");
        }
    }

    private static string Truncate(string s, int max) =>
        s.Length <= max ? s : s[..(max - 3)] + "...";
}
