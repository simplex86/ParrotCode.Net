using System.Text.Json;
using ParrotCode;

namespace ParrotCode.xUnit;

/// <summary>
/// ReadFileTool 单元测试：覆盖正常读取、空文件、不存在、目录、参数校验。
/// 用临时文件，try/finally 清理。
/// </summary>
public class ReadFileToolTests : IDisposable
{
    private readonly ReadFileTool _tool = new();
    private readonly List<string> _tempPaths = new();

    public void Dispose()
    {
        foreach (var path in _tempPaths)
        {
            try { File.Delete(path); } catch { /* 忽略 */ }
        }
    }

    /// <summary>创建临时文件，写入内容，返回路径。Dispose 时自动清理。</summary>
    private string CreateTempFile(string content)
    {
        var path = Path.GetTempFileName();
        File.WriteAllText(path, content);
        _tempPaths.Add(path);
        return path;
    }

    /// <summary>从对象序列化为 JsonElement（避免手拼 JSON 字符串时的反斜杠转义问题）。</summary>
    private static JsonElement Input(object obj) => JsonSerializer.SerializeToElement(obj);

    /// <summary>从 JSON 字符串构造 JsonElement（用于测试错误类型 / null 等 literal 场景）。</summary>
    private static JsonElement InputJson(string json) => JsonDocument.Parse(json).RootElement;

    // —— 元信息 ——

    [Fact]
    public void Name_IsReadFile()
    {
        _tool.Name.Should().Be("read_file");
    }

    [Fact]
    public void Category_IsRead()
    {
        _tool.Category.Should().Be(ToolCategory.Read);
    }

    [Fact]
    public void Parameters_HasOneRequiredPath()
    {
        _tool.Parameters.Should().HaveCount(1);
        _tool.Parameters[0].Name.Should().Be("path");
        _tool.Parameters[0].Required.Should().BeTrue();
    }

    // —— 正常读取 ——

    [Fact]
    public async Task ReadFile_ExistingFile_ReturnsContent()
    {
        var path = CreateTempFile("hello world");

        var result = await _tool.ExecuteAsync(Input(new { path }), CancellationToken.None);

        result.Success.Should().BeTrue();
        result.Content.Should().Be("hello world");
    }

    [Fact]
    public async Task ReadFile_EmptyFile_ReturnsEmptyContent()
    {
        var path = CreateTempFile("");

        var result = await _tool.ExecuteAsync(Input(new { path }), CancellationToken.None);

        result.Success.Should().BeTrue();
        result.Content.Should().Be("");
    }

    [Fact]
    public async Task ReadFile_ChineseContent_ReturnsCorrectly()
    {
        var path = CreateTempFile("你好世界");

        var result = await _tool.ExecuteAsync(Input(new { path }), CancellationToken.None);

        result.Success.Should().BeTrue();
        result.Content.Should().Be("你好世界");
    }

    [Fact]
    public async Task ReadFile_MultilineContent_ReturnsCorrectly()
    {
        var path = CreateTempFile("line1\nline2\nline3\n");

        var result = await _tool.ExecuteAsync(Input(new { path }), CancellationToken.None);

        result.Success.Should().BeTrue();
        result.Content.Should().Be("line1\nline2\nline3\n");
    }

    // —— 错误路径 ——

    [Fact]
    public async Task ReadFile_NonExistentFile_ReturnsError()
    {
        var path = Path.Combine(Path.GetTempPath(), $"parrotcode_nonexistent_{Guid.NewGuid()}.txt");

        var result = await _tool.ExecuteAsync(Input(new { path }), CancellationToken.None);

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("文件不存在");
        result.Error.Should().Contain(path);
    }

    [Fact]
    public async Task ReadFile_DirectoryPath_ReturnsError()
    {
        var path = Path.GetTempPath();

        var result = await _tool.ExecuteAsync(Input(new { path }), CancellationToken.None);

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("目录");
    }

    // —— 参数校验 ——

    [Fact]
    public async Task ReadFile_MissingPathParameter_ReturnsError()
    {
        var result = await _tool.ExecuteAsync(Input(new { content = "x" }), CancellationToken.None);

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("缺少必需参数");
        result.Error.Should().Contain("path");
    }

    [Fact]
    public async Task ReadFile_PathWrongType_ReturnsError()
    {
        var result = await _tool.ExecuteAsync(InputJson("""{"path":123}"""), CancellationToken.None);

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("类型错误");
        result.Error.Should().Contain("path");
    }

    [Fact]
    public async Task ReadFile_EmptyPath_ReturnsError()
    {
        var result = await _tool.ExecuteAsync(Input(new { path = "" }), CancellationToken.None);

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("不能为空");
    }

    [Fact]
    public async Task ReadFile_NullPath_ReturnsError()
    {
        // 用 Dictionary 让 JsonSerializer 输出 null（匿名对象会忽略 null 属性）
        var dict = new Dictionary<string, object?> { ["path"] = null };
        var result = await _tool.ExecuteAsync(Input(dict), CancellationToken.None);

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("path");
    }
}
