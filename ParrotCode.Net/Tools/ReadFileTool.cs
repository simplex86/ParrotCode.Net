using System.Text.Json;

namespace ParrotCode;

/// <summary>
/// 读文件工具：读取指定路径的文件内容，返回完整文本。
/// Category=Read（幂等、无副作用、可并发）。
/// 本迭代读整个文件不做截断——超长结果的截断（50K 字符阈值）在迭代 9 Truncator。
/// 路径校验（沙箱、.. 遍历拦截）在迭代 8 SecurityGuard。
/// </summary>
public sealed class ReadFileTool : ToolBase
{
    public override string Name => "read_file";

    public override string Description =>
        "读取指定路径的文件内容，返回完整文本。路径可以是相对或绝对路径。" +
        "不支持读取目录；文件不存在或无权限会返回错误。";

    public override ToolCategory Category => ToolCategory.Read;

    public override IReadOnlyList<ToolParameter> Parameters { get; } = new[]
    {
        new ToolParameter("path", "string", "要读取的文件路径（相对或绝对）", Required: true)
    };

    public override async Task<ToolResult> ExecuteAsync(JsonElement input, CancellationToken cancellationToken)
    {
        var path = GetRequiredString(input, "path", out var err);
        if (err is not null) return ToolResult.Fail(err);
        if (string.IsNullOrWhiteSpace(path))
            return ToolResult.Fail("参数 path 不能为空");

        // 目录检测：File.ReadAllTextAsync 对目录抛 UnauthorizedAccessException，错误信息不友好
        if (Directory.Exists(path))
            return ToolResult.Fail($"路径是目录而非文件：{path}");

        try
        {
            var content = await File.ReadAllTextAsync(path, cancellationToken);
            return ToolResult.Ok(content);
        }
        catch (FileNotFoundException)
        {
            return ToolResult.Fail($"文件不存在：{path}");
        }
        catch (DirectoryNotFoundException)
        {
            return ToolResult.Fail($"路径不存在：{path}");
        }
        catch (IOException ex)
        {
            return ToolResult.Fail($"读取文件失败：{ex.Message}");
        }
        // UnauthorizedAccessException 等其他异常由 ToolExecutor 兜底捕获
    }
}
