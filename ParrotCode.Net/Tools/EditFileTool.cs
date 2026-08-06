using System.Text;
using System.Text.Json;

namespace ParrotCode;

/// <summary>
/// 改文件工具：精确匹配替换。old_text 必须在文件中唯一匹配，否则报错。
/// 0 次匹配：报"未找到"，附文件前 200 字符作为上下文。
/// 多次匹配：报"找到 N 处"，附前 3 处匹配的行号 + 上下文。
/// 这是 Agent 自我修正能力的关键约束——多次匹配意味着 LLM 的描述不够精确，
/// 应让它重试或提供更多上下文（如带行号或更多周边代码）。
/// Category=Write（有副作用、需串行）。
/// </summary>
public sealed class EditFileTool : ToolBase
{
    private const int ContextPreviewLength = 200;
    private const int MaxMatchContextsToShow = 3;
    private const int MaxLineContextLength = 80;

    public override string Name => "edit_file";

    public override string Description =>
        "在文件中精确替换文本。old_text 必须在文件中唯一匹配——" +
        "0 次或多次匹配都报错，附上下文帮助修正。" +
        "匹配区分大小写、保留所有空白字符（包括缩进）。" +
        "用于精确编辑文件中已存在的代码片段。";

    public override ToolCategory Category => ToolCategory.Write;

    public override IReadOnlyList<ToolParameter> Parameters { get; } = new[]
    {
        new ToolParameter("path", "string", "要编辑的文件路径", Required: true),
        new ToolParameter("old_text", "string", "要被替换的原文（必须在文件中唯一匹配）", Required: true),
        new ToolParameter("new_text", "string", "替换后的新文本", Required: true)
    };

    public override async Task<ToolResult> ExecuteAsync(JsonElement input, CancellationToken cancellationToken)
    {
        var path = GetRequiredString(input, "path", out var err1);
        if (err1 is not null) return ToolResult.Fail(err1);
        var oldText = GetRequiredString(input, "old_text", out var err2);
        if (err2 is not null) return ToolResult.Fail(err2);
        var newText = GetRequiredString(input, "new_text", out var err3);
        if (err3 is not null) return ToolResult.Fail(err3);

        if (string.IsNullOrWhiteSpace(path))
            return ToolResult.Fail("参数 path 不能为空");
        if (oldText.Length == 0)
            return ToolResult.Fail("参数 old_text 不能为空（如需清空文件请用 write_file）");

        // 先检查目录（File.Exists 对目录返回 false，会误报"文件不存在"）
        if (Directory.Exists(path))
            return ToolResult.Fail($"路径是目录而非文件：{path}");
        if (!File.Exists(path))
            return ToolResult.Fail($"文件不存在：{path}");

        string content;
        try
        {
            content = await File.ReadAllTextAsync(path, cancellationToken);
        }
        catch (IOException ex)
        {
            return ToolResult.Fail($"读取文件失败：{ex.Message}");
        }

        // 查找所有匹配（非重叠）
        var matches = FindAllMatches(content, oldText);

        if (matches.Count == 0)
        {
            var preview = content.Length <= ContextPreviewLength ? content
                                                                 : content[..ContextPreviewLength] + "...（截断）";
            return ToolResult.Fail($"未在 {path} 中找到匹配的 old_text。\n文件前 {ContextPreviewLength} 字符预览：\n{preview}");
        }

        if (matches.Count > 1)
        {
            var contexts = new StringBuilder();
            for (var i = 0; i < Math.Min(matches.Count, MaxMatchContextsToShow); i++)
            {
                var (lineNo, lineContext) = GetLineContext(content, matches[i]);
                contexts.AppendLine($"  第 {i + 1} 处：行 {lineNo}，上下文：{lineContext}");
            }
            if (matches.Count > MaxMatchContextsToShow)
                contexts.AppendLine($"  ...（共 {matches.Count} 处，仅显示前 {MaxMatchContextsToShow} 处）");

            return ToolResult.Fail($"在 {path} 中找到 {matches.Count} 处匹配的 old_text，无法确定替换哪一处。\n请提供更精确的 old_text（如包含更多周边代码）：\n{contexts}");
        }

        // 唯一匹配——执行替换
        var newContent = content.Remove(matches[0], oldText.Length).Insert(matches[0], newText);
        try
        {
            await File.WriteAllTextAsync(path, newContent, new UTF8Encoding(false), cancellationToken);
        }
        catch (IOException ex)
        {
            return ToolResult.Fail($"写入文件失败：{ex.Message}");
        }

        return ToolResult.Ok($"已在 {path} 中替换 1 处（{oldText.Length} 字符 → {newText.Length} 字符）");
    }

    /// <summary>
    /// 查找所有非重叠匹配的位置。
    /// 非重叠：从当前位置开始找下一个匹配，找到后跳过整个匹配长度继续找。
    /// 这与 Python str.count / str.replace 的语义一致。
    /// </summary>
    private static List<int> FindAllMatches(string content, string needle)
    {
        var matches = new List<int>();
        var pos = 0;
        while (pos <= content.Length - needle.Length)
        {
            var idx = content.IndexOf(needle, pos, StringComparison.Ordinal);
            if (idx < 0) break;
            matches.Add(idx);
            pos = idx + needle.Length; // 非重叠
        }
        return matches;
    }

    /// <summary>
    /// 获取匹配位置所在的行号（1-based）+ 当前行内容。
    /// 用于错误信息中的上下文展示，帮助 LLM 定位歧义位置。
    /// </summary>
    private static (int LineNo, string LineContext) GetLineContext(string content, int matchPos)
    {
        var lineNo = 1;
        var lineStart = 0;
        for (var i = 0; i < matchPos; i++)
        {
            if (content[i] == '\n')
            {
                lineNo++;
                lineStart = i + 1;
            }
        }
        // 找行尾
        var lineEnd = content.IndexOf('\n', matchPos);
        if (lineEnd < 0) lineEnd = content.Length;
        var line = content[lineStart..lineEnd].Trim();
        // 截断过长的行
        if (line.Length > MaxLineContextLength) line = line[..(MaxLineContextLength - 3)] + "...";
        return (lineNo, line);
    }
}
