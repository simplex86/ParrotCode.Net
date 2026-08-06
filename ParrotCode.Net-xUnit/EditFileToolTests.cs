using System.Text.Json;
using ParrotCode;

namespace ParrotCode.xUnit;

/// <summary>
/// EditFileTool 单元测试：覆盖唯一匹配、0 次匹配、多次匹配、大小写敏感、参数校验。
/// 关键约束：old_text 必须唯一匹配，0 次或多次都报错并附上下文。
/// </summary>
public class EditFileToolTests : IDisposable
{
    private readonly EditFileTool _tool = new();
    private readonly string _tempDir;

    public EditFileToolTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"parrotcode_edit_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* 忽略 */ }
    }

    private string CreateFile(string relative, string content)
    {
        var path = Path.Combine(_tempDir, relative);
        File.WriteAllText(path, content);
        return path;
    }

    private static JsonElement Input(object obj) => JsonSerializer.SerializeToElement(obj);
    private static JsonElement InputJson(string json) => JsonDocument.Parse(json).RootElement;

    // —— 元信息 ——

    [Fact]
    public void Name_IsEditFile()
    {
        _tool.Name.Should().Be("edit_file");
    }

    [Fact]
    public void Category_IsWrite()
    {
        _tool.Category.Should().Be(ToolCategory.Write);
    }

    [Fact]
    public void Parameters_HasThreeRequiredParams()
    {
        _tool.Parameters.Should().HaveCount(3);
        _tool.Parameters.Select(p => p.Name).Should().Contain(new[] { "path", "old_text", "new_text" });
        _tool.Parameters.All(p => p.Required).Should().BeTrue();
    }

    // —— 唯一匹配（成功路径）——

    [Fact]
    public async Task EditFile_UniqueMatch_ReplacesAndReturnsSuccess()
    {
        var path = CreateFile("unique.txt", "hello world");

        var result = await _tool.ExecuteAsync(
            Input(new { path, old_text = "hello", new_text = "hi" }),
            CancellationToken.None);

        result.Success.Should().BeTrue();
        result.Content.Should().Contain("替换 1 处");
        File.ReadAllText(path).Should().Be("hi world");
    }

    [Fact]
    public async Task EditFile_UniqueMatch_WithMultilineJson()
    {
        var path = CreateFile("multi2.txt", "before\nTARGET\nafter");
        // 用 Dictionary 让 new_text 含换行符
        var input = new Dictionary<string, object?>
        {
            ["path"] = path,
            ["old_text"] = "TARGET",
            ["new_text"] = "REPLACED\nMULTILINE"
        };

        var result = await _tool.ExecuteAsync(Input(input), CancellationToken.None);

        result.Success.Should().BeTrue();
        File.ReadAllText(path).Should().Be("before\nREPLACED\nMULTILINE\nafter");
    }

    [Fact]
    public async Task EditFile_EmptyNewText_DeletesOldText()
    {
        var path = CreateFile("delete.txt", "hello world");

        var result = await _tool.ExecuteAsync(
            Input(new { path, old_text = "hello ", new_text = "" }),
            CancellationToken.None);

        result.Success.Should().BeTrue();
        File.ReadAllText(path).Should().Be("world");
    }

    [Fact]
    public async Task EditFile_ContentContainsOldNewLengths()
    {
        var path = CreateFile("lengths.txt", "abc");

        var result = await _tool.ExecuteAsync(
            Input(new { path, old_text = "abc", new_text = "xy" }),
            CancellationToken.None);

        result.Success.Should().BeTrue();
        result.Content.Should().Contain("3 字符");
        result.Content.Should().Contain("2 字符");
    }

    // —— 0 次匹配 ——

    [Fact]
    public async Task EditFile_ZeroMatch_ReturnsErrorWithPreview()
    {
        var path = CreateFile("zero.txt", "the quick brown fox");

        var result = await _tool.ExecuteAsync(
            Input(new { path, old_text = "nonexistent", new_text = "x" }),
            CancellationToken.None);

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("未在");
        result.Error.Should().Contain("the quick brown fox");  // 包含文件预览
    }

    [Fact]
    public async Task EditFile_ZeroMatch_LongFile_TruncatesPreview()
    {
        var longContent = new string('a', 500);
        var path = CreateFile("long.txt", longContent);

        var result = await _tool.ExecuteAsync(
            Input(new { path, old_text = "notfound", new_text = "x" }),
            CancellationToken.None);

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("未在");
        result.Error.Should().Contain("截断");
    }

    // —— 多次匹配 ——

    [Fact]
    public async Task EditFile_MultipleMatches_ReturnsErrorWithContexts()
    {
        var path = CreateFile("multi.txt", "foo\nbar\nfoo\nbaz");

        var result = await _tool.ExecuteAsync(
            Input(new { path, old_text = "foo", new_text = "x" }),
            CancellationToken.None);

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("找到 2 处");
        result.Error.Should().Contain("行 1");
        result.Error.Should().Contain("行 3");
    }

    [Fact]
    public async Task EditFile_FiveMatches_ShowsAtMost3Contexts()
    {
        var path = CreateFile("five.txt", "x\nx\nx\nx\nx\n");

        var result = await _tool.ExecuteAsync(
            Input(new { path, old_text = "x", new_text = "y" }),
            CancellationToken.None);

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("找到 5 处");
        result.Error.Should().Contain("共 5 处");
        result.Error.Should().Contain("仅显示前 3 处");
    }

    [Fact]
    public async Task EditFile_MultipleMatches_LineNumbersCorrect()
    {
        // 第 2 行和第 4 行有 "target"
        var path = CreateFile("lines.txt", "a\ntarget\nb\ntarget\nc");

        var result = await _tool.ExecuteAsync(
            Input(new { path, old_text = "target", new_text = "x" }),
            CancellationToken.None);

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("行 2");
        result.Error.Should().Contain("行 4");
    }

    // —— 大小写敏感 ——

    [Fact]
    public async Task EditFile_CaseSensitive_DoesNotMatchDifferentCase()
    {
        var path = CreateFile("case.txt", "Hello World");

        var result = await _tool.ExecuteAsync(
            Input(new { path, old_text = "hello", new_text = "hi" }),
            CancellationToken.None);

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("未在");
        // 文件内容不变
        File.ReadAllText(path).Should().Be("Hello World");
    }

    // —— 空白保留 ——

    [Fact]
    public async Task EditFile_PreservesWhitespace_IndentedMatch()
    {
        var path = CreateFile("indent.txt", "    indented line\n");
        var input = new Dictionary<string, object?>
        {
            ["path"] = path,
            ["old_text"] = "    indented line",
            ["new_text"] = "    replaced"
        };

        var result = await _tool.ExecuteAsync(Input(input), CancellationToken.None);

        result.Success.Should().BeTrue();
        File.ReadAllText(path).Should().Be("    replaced\n");
    }

    [Fact]
    public async Task EditFile_PreservesWhitespace_MismatchFailsWhenIndentMissing()
    {
        // 文件第二行有 4 空格缩进，old_text 不含缩进——精确匹配失败
        var path = CreateFile("indent2.txt", "line1\n    line2\nline3");

        var result = await _tool.ExecuteAsync(
            Input(new { path, old_text = "line1\nline2", new_text = "x" }),
            CancellationToken.None);

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("未在");
    }

    // —— 非重叠匹配语义 ——

    [Fact]
    public async Task EditFile_NonOverlapping_AaaFindsAaOnce()
    {
        // "aaa" 中查找 "aa"：非重叠只匹配 1 处（位置 0），跳过 2 字符后位置 2 已不够 "aa"
        var path = CreateFile("overlap.txt", "aaa");

        var result = await _tool.ExecuteAsync(
            Input(new { path, old_text = "aa", new_text = "bb" }),
            CancellationToken.None);

        result.Success.Should().BeTrue();
        File.ReadAllText(path).Should().Be("bba");
    }

    // —— 文件状态错误 ——

    [Fact]
    public async Task EditFile_NonExistentFile_ReturnsError()
    {
        var path = Path.Combine(_tempDir, "nonexistent.txt");

        var result = await _tool.ExecuteAsync(
            Input(new { path, old_text = "x", new_text = "y" }),
            CancellationToken.None);

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("文件不存在");
    }

    [Fact]
    public async Task EditFile_DirectoryPath_ReturnsError()
    {
        var path = _tempDir;

        var result = await _tool.ExecuteAsync(
            Input(new { path, old_text = "x", new_text = "y" }),
            CancellationToken.None);

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("目录");
    }

    // —— 参数校验 ——

    [Fact]
    public async Task EditFile_EmptyOldText_ReturnsError()
    {
        var path = CreateFile("empty_old.txt", "content");

        var result = await _tool.ExecuteAsync(
            Input(new { path, old_text = "", new_text = "x" }),
            CancellationToken.None);

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("不能为空");
        result.Error.Should().Contain("write_file");
    }

    [Fact]
    public async Task EditFile_MissingPath_ReturnsError()
    {
        var result = await _tool.ExecuteAsync(
            Input(new { old_text = "x", new_text = "y" }),
            CancellationToken.None);

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("缺少必需参数");
        result.Error.Should().Contain("path");
    }

    [Fact]
    public async Task EditFile_MissingOldText_ReturnsError()
    {
        var path = CreateFile("missing_old.txt", "content");

        var result = await _tool.ExecuteAsync(
            Input(new { path, new_text = "y" }),
            CancellationToken.None);

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("缺少必需参数");
        result.Error.Should().Contain("old_text");
    }

    [Fact]
    public async Task EditFile_MissingNewText_ReturnsError()
    {
        var path = CreateFile("missing_new.txt", "content");

        var result = await _tool.ExecuteAsync(
            Input(new { path, old_text = "x" }),
            CancellationToken.None);

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("缺少必需参数");
        result.Error.Should().Contain("new_text");
    }

    [Fact]
    public async Task EditFile_PathWrongType_ReturnsError()
    {
        var result = await _tool.ExecuteAsync(
            InputJson("""{"path":123,"old_text":"x","new_text":"y"}"""), CancellationToken.None);

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("类型错误");
    }

    // —— old_text == new_text ——

    [Fact]
    public async Task EditFile_OldTextEqualsNewText_WritesSameContent()
    {
        var path = CreateFile("same.txt", "hello world");

        var result = await _tool.ExecuteAsync(
            Input(new { path, old_text = "hello", new_text = "hello" }),
            CancellationToken.None);

        // 文档说明：不专门拒绝，会写入相同内容
        result.Success.Should().BeTrue();
        File.ReadAllText(path).Should().Be("hello world");
    }

    // —— 取消 ——

    [Fact]
    public async Task EditAsync_AlreadyCancelledToken_ThrowsOperationCanceledException()
    {
        var path = CreateFile("cancel.txt", "hello");
        var cts = new CancellationTokenSource();
        cts.Cancel();

        var act = async () => await _tool.ExecuteAsync(
            Input(new { path, old_text = "hello", new_text = "x" }), cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }
}
