using System.Text.Json;
using ParrotCode;

namespace ParrotCode.xUnit;

/// <summary>
/// 工具系统闭环集成测试：模拟"LLM 返回 tool_call → 执行 → 拿到结果"的完整流程。
/// 不接 LLM，ToolCall 手工构造，验证 ToolRegistry + ToolExecutor + 三个工具的端到端集成。
/// </summary>
public class ToolClosedLoopTests : IDisposable
{
    private readonly string _tempDir;

    public ToolClosedLoopTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"parrotcode_loop_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* 忽略 */ }
    }

    /// <summary>从对象序列化构造 ToolCall（避免手拼 JSON 时路径反斜杠转义问题）。</summary>
    private static ToolCall MakeCall(string id, string name, object input)
    {
        var element = JsonSerializer.SerializeToElement(input);
        return new ToolCall(id, name, element);
    }

    /// <summary>从 JSON 字符串构造 ToolCall（用于无路径插值的简单场景）。</summary>
    private static ToolCall MakeCallJson(string id, string name, string json)
    {
        var doc = JsonDocument.Parse(json);
        return new ToolCall(id, name, doc.RootElement);
    }

    private static (ToolRegistry registry, ToolExecutor executor) Setup()
    {
        var registry = new ToolRegistry();
        registry.Register(new ReadFileTool());
        registry.Register(new WriteFileTool());
        registry.Register(new EditFileTool());
        var executor = new ToolExecutor(registry, timeout: TimeSpan.FromSeconds(10));
        return (registry, executor);
    }

    // —— 闭环：write → read → edit ——

    [Fact]
    public async Task ClosedLoop_WriteReadEdit_Succeeds()
    {
        var (_, executor) = Setup();
        var path = Path.Combine(_tempDir, "loop1.txt");

        // 1. write_file
        var writeResult = await executor.ExecuteAsync(
            MakeCall("w1", "write_file", new { path, content = "hello world" }),
            CancellationToken.None);
        writeResult.Success.Should().BeTrue();
        File.Exists(path).Should().BeTrue();

        // 2. read_file（读回刚写的）
        var readResult = await executor.ExecuteAsync(
            MakeCall("r1", "read_file", new { path }),
            CancellationToken.None);
        readResult.Success.Should().BeTrue();
        readResult.Content.Should().Be("hello world");

        // 3. edit_file（修改）
        var editResult = await executor.ExecuteAsync(
            MakeCall("e1", "edit_file", new { path, old_text = "hello", new_text = "hi" }),
            CancellationToken.None);
        editResult.Success.Should().BeTrue();
        File.ReadAllText(path).Should().Be("hi world");
    }

    [Fact]
    public async Task ClosedLoop_EditFileAmbiguousMatch_ReturnsError()
    {
        var (_, executor) = Setup();
        var path = Path.Combine(_tempDir, "loop_ambig.txt");

        // 先写带重复的内容
        await executor.ExecuteAsync(
            MakeCall("w1", "write_file", new { path, content = "foo\nfoo\nfoo" }),
            CancellationToken.None);

        // edit_file 应失败
        var editResult = await executor.ExecuteAsync(
            MakeCall("e1", "edit_file", new { path, old_text = "foo", new_text = "bar" }),
            CancellationToken.None);

        editResult.Success.Should().BeFalse();
        editResult.Error.Should().Contain("找到 3 处");
        // 文件内容不变
        File.ReadAllText(path).Should().Be("foo\nfoo\nfoo");
    }

    [Fact]
    public async Task ClosedLoop_EditFileAfterRead_PreservesContent()
    {
        var (_, executor) = Setup();
        var path = Path.Combine(_tempDir, "loop_preserve.txt");

        // write "abc"
        await executor.ExecuteAsync(
            MakeCall("w1", "write_file", new { path, content = "abc" }),
            CancellationToken.None);

        // read 回
        var read1 = await executor.ExecuteAsync(
            MakeCall("r1", "read_file", new { path }),
            CancellationToken.None);
        read1.Success.Should().BeTrue();
        read1.Content.Should().Be("abc");

        // edit "abc" → "xyz"
        await executor.ExecuteAsync(
            MakeCall("e1", "edit_file", new { path, old_text = "abc", new_text = "xyz" }),
            CancellationToken.None);

        // read 回验证
        var read2 = await executor.ExecuteAsync(
            MakeCall("r2", "read_file", new { path }),
            CancellationToken.None);
        read2.Success.Should().BeTrue();
        read2.Content.Should().Be("xyz");
    }

    [Fact]
    public async Task ClosedLoop_UnknownTool_ReturnsError()
    {
        var (_, executor) = Setup();

        var result = await executor.ExecuteAsync(
            MakeCallJson("u1", "nonexistent_tool", "{}"),
            CancellationToken.None);

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("未注册工具");
        result.Error.Should().Contain("nonexistent_tool");
    }

    [Fact]
    public async Task ClosedLoop_MissingParameter_ReturnsError()
    {
        var (_, executor) = Setup();
        var path = Path.Combine(_tempDir, "loop_missing.txt");

        // write_file 缺 content
        var result = await executor.ExecuteAsync(
            MakeCall("w1", "write_file", new { path }),  // 只有 path，缺 content
            CancellationToken.None);

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("缺少必需参数");
        result.Error.Should().Contain("content");
        File.Exists(path).Should().BeFalse("写入失败，文件不应被创建");
    }

    [Fact]
    public void ClosedLoop_RegistrySchemas_AreNonEmpty()
    {
        var (registry, _) = Setup();

        var openAiSchemas = registry.ToOpenAiSchemas();
        openAiSchemas.GetArrayLength().Should().Be(3);

        var anthropicSchemas = registry.ToAnthropicSchemas();
        anthropicSchemas.GetArrayLength().Should().Be(3);
    }

    [Fact]
    public async Task ClosedLoop_MultipleSequentialEdits_AllSucceed()
    {
        var (_, executor) = Setup();
        var path = Path.Combine(_tempDir, "loop_multi_edit.txt");

        // 初始写入
        await executor.ExecuteAsync(
            MakeCall("w1", "write_file", new { path, content = "aaa bbb ccc" }),
            CancellationToken.None);

        // 依次 edit 三个不冲突的子串
        var r1 = await executor.ExecuteAsync(
            MakeCall("e1", "edit_file", new { path, old_text = "aaa", new_text = "AAA" }),
            CancellationToken.None);
        r1.Success.Should().BeTrue();

        var r2 = await executor.ExecuteAsync(
            MakeCall("e2", "edit_file", new { path, old_text = "bbb", new_text = "BBB" }),
            CancellationToken.None);
        r2.Success.Should().BeTrue();

        var r3 = await executor.ExecuteAsync(
            MakeCall("e3", "edit_file", new { path, old_text = "ccc", new_text = "CCC" }),
            CancellationToken.None);
        r3.Success.Should().BeTrue();

        File.ReadAllText(path).Should().Be("AAA BBB CCC");
    }

    [Fact]
    public async Task ClosedLoop_DemoRun_CompletesWithoutException()
    {
        // ClosedLoopDemo 会写 demo_output.txt 到 cwd，运行后清理
        var demoOutputPath = Path.Combine(Environment.CurrentDirectory, "demo_output.txt");
        try
        {
            await ClosedLoopDemo.RunAsync(CancellationToken.None);
        }
        finally
        {
            if (File.Exists(demoOutputPath))
            {
                try { File.Delete(demoOutputPath); } catch { /* 忽略 */ }
            }
        }
        // 不抛异常即视为通过
    }
}
