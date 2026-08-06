using System.Text.Json;
using ParrotCode;

namespace ParrotCode.xUnit;

/// <summary>
/// WriteFileTool 单元测试：覆盖新建、覆盖、空内容、父目录创建、参数校验。
/// 用临时目录，Dispose 时清理。
/// </summary>
public class WriteFileToolTests : IDisposable
{
    private readonly WriteFileTool _tool = new();
    private readonly string _tempDir;

    public WriteFileToolTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"parrotcode_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* 忽略 */ }
    }

    private string TempPath(string relative) => Path.Combine(_tempDir, relative);

    private static JsonElement Input(object obj) => JsonSerializer.SerializeToElement(obj);
    private static JsonElement InputJson(string json) => JsonDocument.Parse(json).RootElement;

    // —— 元信息 ——

    [Fact]
    public void Name_IsWriteFile()
    {
        _tool.Name.Should().Be("write_file");
    }

    [Fact]
    public void Category_IsWrite()
    {
        _tool.Category.Should().Be(ToolCategory.Write);
    }

    [Fact]
    public void Parameters_HasPathAndContent_BothRequired()
    {
        _tool.Parameters.Should().HaveCount(2);
        _tool.Parameters.Select(p => p.Name).Should().Contain(new[] { "path", "content" });
        _tool.Parameters.All(p => p.Required).Should().BeTrue();
    }

    // —— 正常写入 ——

    [Fact]
    public async Task WriteFile_NewFile_CreatesAndWrites()
    {
        var path = TempPath("new.txt");

        var result = await _tool.ExecuteAsync(
            Input(new { path, content = "hello" }), CancellationToken.None);

        result.Success.Should().BeTrue();
        File.Exists(path).Should().BeTrue();
        File.ReadAllText(path).Should().Be("hello");
    }

    [Fact]
    public async Task WriteFile_ExistingFile_Overwrites()
    {
        var path = TempPath("overwrite.txt");
        File.WriteAllText(path, "旧内容");

        var result = await _tool.ExecuteAsync(
            Input(new { path, content = "新内容" }), CancellationToken.None);

        result.Success.Should().BeTrue();
        File.ReadAllText(path).Should().Be("新内容");
    }

    [Fact]
    public async Task WriteFile_EmptyContent_CreatesEmptyFile()
    {
        var path = TempPath("empty.txt");

        var result = await _tool.ExecuteAsync(
            Input(new { path, content = "" }), CancellationToken.None);

        result.Success.Should().BeTrue();
        File.Exists(path).Should().BeTrue();
        new FileInfo(path).Length.Should().Be(0);
    }

    [Fact]
    public async Task WriteFile_ChineseContent_WritesUtf8()
    {
        var path = TempPath("chinese.txt");

        var result = await _tool.ExecuteAsync(
            Input(new { path, content = "你好世界" }), CancellationToken.None);

        result.Success.Should().BeTrue();
        File.ReadAllText(path).Should().Be("你好世界");
    }

    [Fact]
    public async Task WriteFile_PathWithMissingParentDir_CreatesDir()
    {
        var path = TempPath(Path.Combine("subdir1", "subdir2", "file.txt"));

        var result = await _tool.ExecuteAsync(
            Input(new { path, content = "x" }), CancellationToken.None);

        result.Success.Should().BeTrue();
        File.Exists(path).Should().BeTrue();
        File.ReadAllText(path).Should().Be("x");
    }

    // —— 返回内容 ——

    [Fact]
    public async Task WriteFile_ReturnsByteCountInContent()
    {
        var path = TempPath("bytes.txt");

        // "你好" UTF-8 = 6 字节
        var result = await _tool.ExecuteAsync(
            Input(new { path, content = "你好" }), CancellationToken.None);

        result.Success.Should().BeTrue();
        result.Content.Should().Contain("6");
        result.Content.Should().Contain("字节");
    }

    [Fact]
    public async Task WriteFile_ReturnsPathInContent()
    {
        var path = TempPath("pathtest.txt");

        var result = await _tool.ExecuteAsync(
            Input(new { path, content = "x" }), CancellationToken.None);

        result.Success.Should().BeTrue();
        result.Content.Should().Contain(path);
    }

    // —— 参数校验 ——

    [Fact]
    public async Task WriteFile_MissingPath_ReturnsError()
    {
        var result = await _tool.ExecuteAsync(
            Input(new { content = "x" }), CancellationToken.None);

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("缺少必需参数");
        result.Error.Should().Contain("path");
    }

    [Fact]
    public async Task WriteFile_MissingContent_ReturnsError()
    {
        var path = TempPath("missing_content.txt");

        var result = await _tool.ExecuteAsync(
            Input(new { path }), CancellationToken.None);

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("缺少必需参数");
        result.Error.Should().Contain("content");
    }

    [Fact]
    public async Task WriteFile_EmptyPath_ReturnsError()
    {
        var result = await _tool.ExecuteAsync(
            Input(new { path = "", content = "x" }), CancellationToken.None);

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("不能为空");
    }

    [Fact]
    public async Task WriteFile_PathWrongType_ReturnsError()
    {
        var result = await _tool.ExecuteAsync(
            InputJson("""{"path":123,"content":"x"}"""), CancellationToken.None);

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("类型错误");
    }

    [Fact]
    public async Task WriteFile_ContentWrongType_ReturnsError()
    {
        var path = TempPath("wrong_type.txt");

        var result = await _tool.ExecuteAsync(
            InputJson($$"""{"path":{{JsonSerializer.Serialize(path)}},"content":123}"""),
            CancellationToken.None);

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("类型错误");
        result.Error.Should().Contain("content");
    }

    // —— 取消 ——

    [Fact]
    public async Task WriteAsync_AlreadyCancelledToken_ThrowsOperationCanceledException()
    {
        var path = TempPath("cancelled.txt");
        var cts = new CancellationTokenSource();
        cts.Cancel();

        var act = async () => await _tool.ExecuteAsync(
            Input(new { path, content = "x" }), cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }
}
