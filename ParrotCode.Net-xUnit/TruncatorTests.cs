using ParrotCode;

namespace ParrotCode.xUnit;

/// <summary>
/// ToolResultTruncator 单元测试：单条截断、合计截断、写盘、降级、文件名、目录创建。
/// </summary>
public class TruncatorTests
{
    private static string MakeString(int len) => new('x', len);

    private string GetTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "parrotcode_truncator_tests_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Fact]
    public void TruncateBatch_SmallResult_NotTruncated()
    {
        var dir = GetTempDir();
        try
        {
            var truncator = new ToolResultTruncator(
                new TruncateConfig { PerResultThreshold = 100, RoundTotalThreshold = 1000, PreviewLength = 10 },
                dir);

            var (contents, infos) = truncator.TruncateBatch(
                new[] { "short" }, new[] { "read_file" });

            contents[0].Should().Be("short");
            infos.Should().BeEmpty();
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public void TruncateBatch_SingleResultOverThreshold_TruncatesWithPreview()
    {
        var dir = GetTempDir();
        try
        {
            var truncator = new ToolResultTruncator(
                new TruncateConfig { PerResultThreshold = 100, RoundTotalThreshold = 10000, PreviewLength = 10 },
                dir);
            var big = MakeString(500);

            var (contents, infos) = truncator.TruncateBatch(
                new[] { big }, new[] { "read_file" });

            contents[0].Should().Contain("[工具结果过大，完整内容已保存到磁盘]");
            contents[0].Should().Contain("预览");
            contents[0].Length.Should().BeLessThan(big.Length);
            infos.Should().HaveCount(1);
            infos[0].ToolName.Should().Be("read_file");
            infos[0].OriginalChars.Should().Be(500);
            infos[0].FilePath.Should().NotBeNull();
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public void TruncateBatch_FilePath_WrittenToDisk()
    {
        var dir = GetTempDir();
        try
        {
            var truncator = new ToolResultTruncator(
                new TruncateConfig { PerResultThreshold = 100, RoundTotalThreshold = 10000, PreviewLength = 10 },
                dir);
            var big = MakeString(500);

            var (_, infos) = truncator.TruncateBatch(
                new[] { big }, new[] { "read_file" });

            var filePath = infos[0].FilePath!;
            File.Exists(filePath).Should().BeTrue();
            File.ReadAllText(filePath).Should().Be(big);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public void TruncateBatch_PreviewContent_ContainsFirstNChars()
    {
        var dir = GetTempDir();
        try
        {
            var truncator = new ToolResultTruncator(
                new TruncateConfig { PerResultThreshold = 100, RoundTotalThreshold = 10000, PreviewLength = 20 },
                dir);
            var big = "ABCDEFGHIJKLMNOPQRSTUVWXYZ".PadRight(500, '0');

            var (contents, _) = truncator.TruncateBatch(
                new[] { big }, new[] { "read_file" });

            contents[0].Should().Contain("ABCDEFGHIJKLMNOPQRST");
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public void TruncateBatch_RoundTotalOverThreshold_TruncatesLargest()
    {
        var dir = GetTempDir();
        try
        {
            // PerResult=1000 → 单条都不触发；RoundTotal=1500 → 合计触发
            var truncator = new ToolResultTruncator(
                new TruncateConfig { PerResultThreshold = 1000, RoundTotalThreshold = 1500, PreviewLength = 10 },
                dir);
            var a = MakeString(800);
            var b = MakeString(800);  // 合计 1600 > 1500，截断最大的

            var (contents, infos) = truncator.TruncateBatch(
                new[] { a, b }, new[] { "tool_a", "tool_b" });

            infos.Should().HaveCount(1);
            // 两个都是 800，按排序取第一个（index=0）
            infos[0].Index.Should().Be(0);
            contents[0].Should().Contain("[工具结果过大");
            contents[1].Should().Be(b);  // 第二个未截断
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public void TruncateBatch_AlreadyTruncated_NotDoubleProcessed()
    {
        var dir = GetTempDir();
        try
        {
            // 单条 > 100 → 截断；截断后预览远小于 RoundTotal=200
            var truncator = new ToolResultTruncator(
                new TruncateConfig { PerResultThreshold = 100, RoundTotalThreshold = 200, PreviewLength = 10 },
                dir);
            var big = MakeString(500);

            var (_, infos) = truncator.TruncateBatch(
                new[] { big }, new[] { "read_file" });

            // 只被单条截断一次，不被合计截断再处理
            infos.Should().HaveCount(1);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public void TruncateBatch_DirectoryAutoCreated()
    {
        var dir = Path.Combine(Path.GetTempPath(), "parrotcode_auto_create_" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            Directory.Exists(dir).Should().BeFalse();
            var truncator = new ToolResultTruncator(
                new TruncateConfig { PerResultThreshold = 100, RoundTotalThreshold = 10000, PreviewLength = 10 },
                dir);

            var _ = truncator.TruncateBatch(new[] { MakeString(500) }, new[] { "read_file" });

            Directory.Exists(dir).Should().BeTrue();
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public void TruncateBatch_FileName_ContainsTimestampAndToolName()
    {
        var dir = GetTempDir();
        try
        {
            var truncator = new ToolResultTruncator(
                new TruncateConfig { PerResultThreshold = 100, RoundTotalThreshold = 10000, PreviewLength = 10 },
                dir);

            var (_, infos) = truncator.TruncateBatch(
                new[] { MakeString(500) }, new[] { "read_file" });

            var fileName = Path.GetFileName(infos[0].FilePath!);
            fileName.Should().StartWith("20");  // 年份前缀
            fileName.Should().Contain("read_file");
            fileName.Should().EndWith(".txt");
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public void TruncateBatch_ToolNameWithSpecialChars_ReplacedInFileName()
    {
        var dir = GetTempDir();
        try
        {
            var truncator = new ToolResultTruncator(
                new TruncateConfig { PerResultThreshold = 100, RoundTotalThreshold = 10000, PreviewLength = 10 },
                dir);

            var (_, infos) = truncator.TruncateBatch(
                new[] { MakeString(500) }, new[] { "mcp/server.tool" });

            var fileName = Path.GetFileName(infos[0].FilePath!);
            var baseName = Path.GetFileNameWithoutExtension(fileName);
            baseName.Should().NotContain("/");
            baseName.Should().NotContain(".");
            fileName.Should().EndWith(".txt");
            baseName.Should().Contain("mcp_server_tool");
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public void TruncateBatch_EmptyInput_ReturnsEmpty()
    {
        var dir = GetTempDir();
        try
        {
            var truncator = new ToolResultTruncator(
                new TruncateConfig { PerResultThreshold = 100, RoundTotalThreshold = 1000, PreviewLength = 10 },
                dir);

            var (contents, infos) = truncator.TruncateBatch(
                Array.Empty<string>(), Array.Empty<string>());

            contents.Should().BeEmpty();
            infos.Should().BeEmpty();
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public void TruncateBatch_MismatchedLengths_Throws()
    {
        var dir = GetTempDir();
        try
        {
            var truncator = new ToolResultTruncator(
                new TruncateConfig { PerResultThreshold = 100, RoundTotalThreshold = 1000, PreviewLength = 10 },
                dir);

            var act = () => truncator.TruncateBatch(
                new[] { "a", "b" }, new[] { "tool_a" });

            act.Should().Throw<ArgumentException>();
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }
}
