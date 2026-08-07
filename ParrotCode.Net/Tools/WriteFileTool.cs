using System.Text;
using System.Text.Json;

namespace ParrotCode;

/// <summary>
/// 写文件工具：创建或覆盖写入指定路径。
/// Category=Write（有副作用、需串行）。
/// 父目录不存在时自动创建（与 mkdir -p 语义一致）。
/// 不弹 HITL 确认——本迭代无安全层，HITL 在迭代 7 TUI + 迭代 8 SecurityGuard 接入。
/// </summary>
public sealed class WriteFileTool : ToolBase
{
    public override string Name => "write_file";

    public override string Description =>
        "创建或覆盖写入文件。若父目录不存在会自动创建。" +
        "已存在的文件会被覆盖——如需保留原内容请先 read_file。" +
        "返回写入的字节数（UTF-8 编码）。";

    public override ToolCategory Category => ToolCategory.Write;

    public override IReadOnlyList<ToolParameter> Parameters { get; } = new[]
    {
        new ToolParameter("path", "string", "要写入的文件路径（相对或绝对）", Required: true),
        new ToolParameter("content", "string", "文件内容（完整覆盖）", Required: true)
    };

    public override async Task<ToolResult> ExecuteAsync(JsonElement input, CancellationToken cancellationToken)
    {
        var path = GetRequiredString(input, "path", out var err1);
        if (err1 is not null) return ToolResult.Fail(err1);
        var content = GetRequiredString(input, "content", out var err2);
        if (err2 is not null) return ToolResult.Fail(err2);

        if (string.IsNullOrWhiteSpace(path))
            return ToolResult.Fail("参数 path 不能为空");

        try
        {
            // 父目录不存在则创建（与 mkdir -p 语义一致）
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            // UTF-8 无 BOM 写入（与源码文件惯例一致；显式 false 与 EditFileTool 统一）
            var bytes = new UTF8Encoding(false).GetBytes(content);
            await using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, bufferSize: 4096, useAsync: true);
            await fs.WriteAsync(bytes.AsMemory(0, bytes.Length), cancellationToken);

            return ToolResult.Ok($"已写入 {bytes.Length} 字节到 {path}");
        }
        catch (UnauthorizedAccessException ex)
        {
            return ToolResult.Fail($"无写入权限：{ex.Message}");
        }
        catch (IOException ex)
        {
            return ToolResult.Fail($"写入文件失败：{ex.Message}");
        }
    }
}
