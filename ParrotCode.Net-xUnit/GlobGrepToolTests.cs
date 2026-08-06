using System.IO;
using System.Text.Json;
using ParrotCode;

namespace ParrotCode.xUnit;

/// <summary>
/// GlobTool / GrepTool 单元测试：基于真实文件系统。
/// 每个测试创建独立临时目录，try/finally 清理。
/// </summary>
public class GlobGrepToolTests
{
    private static string CreateTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ParrotCode-Test-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static ToolCall MakeCall(string name, params (string key, string value)[] args)
    {
        var dict = new Dictionary<string, string>();
        foreach (var (k, v) in args) dict[k] = v;
        var json = JsonSerializer.Serialize(dict);
        using var doc = JsonDocument.Parse(json);
        return new ToolCall("id", name, doc.RootElement.Clone());
    }

    private static void WriteFile(string dir, string relative, string content)
    {
        var full = Path.Combine(dir, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
    }

    private static void TryDelete(string dir)
    {
        try { Directory.Delete(dir, recursive: true); } catch { /* 忽略 */ }
    }

    // —— GlobTool ——

    [Fact]
    public async Task GlobTool_NoMatch_ReturnsNotFoundMessage()
    {
        var dir = CreateTempDir();
        try
        {
            var tool = new GlobTool();
            var call = MakeCall("glob", ("pattern", "*.nonexistent"), ("path", dir));

            var result = await tool.ExecuteAsync(call.Input, CancellationToken.None);

            result.Success.Should().BeTrue();
            result.Content.Should().Contain("未找到");
        }
        finally { TryDelete(dir); }
    }

    [Fact]
    public async Task GlobTool_MatchesByExtension()
    {
        var dir = CreateTempDir();
        try
        {
            WriteFile(dir, "a.cs", "");
            WriteFile(dir, "b.cs", "");
            WriteFile(dir, "c.txt", "");
            var tool = new GlobTool();
            var call = MakeCall("glob", ("pattern", "*.cs"), ("path", dir));

            var result = await tool.ExecuteAsync(call.Input, CancellationToken.None);

            result.Success.Should().BeTrue();
            result.Content.Should().Contain("找到 2 个文件");
            result.Content.Should().Contain("a.cs");
            result.Content.Should().Contain("b.cs");
            result.Content.Should().NotContain("c.txt");
        }
        finally { TryDelete(dir); }
    }

    [Fact]
    public async Task GlobTool_RecursiveMatch_WithDoubleStar()
    {
        var dir = CreateTempDir();
        try
        {
            WriteFile(dir, "root.cs", "");
            WriteFile(dir, "sub/a.cs", "");
            WriteFile(dir, "sub/deep/b.cs", "");
            var tool = new GlobTool();
            var call = MakeCall("glob", ("pattern", "**/*.cs"), ("path", dir));

            var result = await tool.ExecuteAsync(call.Input, CancellationToken.None);

            result.Success.Should().BeTrue();
            result.Content.Should().Contain("找到 3 个文件");
            result.Content.Should().Contain("root.cs");
            result.Content.Should().Contain("sub/a.cs");
            result.Content.Should().Contain("sub/deep/b.cs");
        }
        finally { TryDelete(dir); }
    }

    [Fact]
    public async Task GlobTool_MissingPatternParam_ReturnsFail()
    {
        var dir = CreateTempDir();
        try
        {
            var tool = new GlobTool();
            var call = MakeCall("glob", ("path", dir));

            var result = await tool.ExecuteAsync(call.Input, CancellationToken.None);

            result.Success.Should().BeFalse();
            result.Error.Should().Contain("pattern");
        }
        finally { TryDelete(dir); }
    }

    [Fact]
    public async Task GlobTool_NonExistentPath_ReturnsFail()
    {
        var dir = CreateTempDir();
        try
        {
            var nonexistent = Path.Combine(dir, "does_not_exist");
            var tool = new GlobTool();
            var call = MakeCall("glob", ("pattern", "*.cs"), ("path", nonexistent));

            var result = await tool.ExecuteAsync(call.Input, CancellationToken.None);

            result.Success.Should().BeFalse();
            result.Error.Should().Contain("目录不存在");
        }
        finally { TryDelete(dir); }
    }

    [Fact]
    public async Task GlobTool_SortsResults()
    {
        var dir = CreateTempDir();
        try
        {
            WriteFile(dir, "z.cs", "");
            WriteFile(dir, "a.cs", "");
            WriteFile(dir, "m.cs", "");
            var tool = new GlobTool();
            var call = MakeCall("glob", ("pattern", "*.cs"), ("path", dir));

            var result = await tool.ExecuteAsync(call.Input, CancellationToken.None);

            result.Success.Should().BeTrue();
            result.Content.Should().Contain("找到 3 个文件");
            var idxA = result.Content.IndexOf("a.cs");
            var idxM = result.Content.IndexOf("m.cs");
            var idxZ = result.Content.IndexOf("z.cs");
            idxA.Should().BeLessThan(idxM);
            idxM.Should().BeLessThan(idxZ);
        }
        finally { TryDelete(dir); }
    }

    // —— GrepTool ——

    [Fact]
    public async Task GrepTool_FindsMatch_ReturnsFileLineContent()
    {
        var dir = CreateTempDir();
        try
        {
            WriteFile(dir, "hello.txt", "hello world");
            var tool = new GrepTool();
            var call = MakeCall("grep", ("pattern", "hello"), ("path", dir));

            var result = await tool.ExecuteAsync(call.Input, CancellationToken.None);

            result.Success.Should().BeTrue();
            result.Content.Should().Contain("1:hello world");
            result.Content.Should().Contain("hello.txt");
        }
        finally { TryDelete(dir); }
    }

    [Fact]
    public async Task GrepTool_NoMatch_ReturnsNotFoundMessage()
    {
        var dir = CreateTempDir();
        try
        {
            WriteFile(dir, "a.txt", "hello world");
            var tool = new GrepTool();
            var call = MakeCall("grep", ("pattern", "zzz"), ("path", dir));

            var result = await tool.ExecuteAsync(call.Input, CancellationToken.None);

            result.Success.Should().BeTrue();
            result.Content.Should().Contain("未找到");
        }
        finally { TryDelete(dir); }
    }

    [Fact]
    public async Task GrepTool_IncludeFilter_OnlySearchesMatchingFiles()
    {
        var dir = CreateTempDir();
        try
        {
            WriteFile(dir, "a.cs", "foo");
            WriteFile(dir, "b.txt", "foo");
            var tool = new GrepTool();
            var call = MakeCall("grep", ("pattern", "foo"), ("path", dir), ("include", "*.cs"));

            var result = await tool.ExecuteAsync(call.Input, CancellationToken.None);

            result.Success.Should().BeTrue();
            result.Content.Should().Contain("a.cs:1:foo");
            result.Content.Should().NotContain("b.txt");
        }
        finally { TryDelete(dir); }
    }

    [Fact]
    public async Task GrepTool_InvalidRegex_ReturnsFail()
    {
        var dir = CreateTempDir();
        try
        {
            WriteFile(dir, "a.txt", "hello");
            var tool = new GrepTool();
            var call = MakeCall("grep", ("pattern", "("), ("path", dir));

            var result = await tool.ExecuteAsync(call.Input, CancellationToken.None);

            result.Success.Should().BeFalse();
            result.Error.Should().Contain("正则非法");
        }
        finally { TryDelete(dir); }
    }

    [Fact]
    public async Task GrepTool_MissingPatternParam_ReturnsFail()
    {
        var dir = CreateTempDir();
        try
        {
            var tool = new GrepTool();
            var call = MakeCall("grep", ("path", dir));

            var result = await tool.ExecuteAsync(call.Input, CancellationToken.None);

            result.Success.Should().BeFalse();
            result.Error.Should().Contain("pattern");
        }
        finally { TryDelete(dir); }
    }

    [Fact]
    public async Task GrepTool_MultipleMatches_ReturnsAllWithLineNumbers()
    {
        var dir = CreateTempDir();
        try
        {
            WriteFile(dir, "multi.txt", "x\nx\nx");
            var tool = new GrepTool();
            var call = MakeCall("grep", ("pattern", "x"), ("path", dir));

            var result = await tool.ExecuteAsync(call.Input, CancellationToken.None);

            result.Success.Should().BeTrue();
            result.Content.Should().Contain("找到 3 条匹配");
            result.Content.Should().Contain("multi.txt:1:x");
            result.Content.Should().Contain("multi.txt:2:x");
            result.Content.Should().Contain("multi.txt:3:x");
        }
        finally { TryDelete(dir); }
    }
}
