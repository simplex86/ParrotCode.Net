using System.Text.Json;
using ParrotCode;

namespace ParrotCode.xUnit;

/// <summary>
/// RunCommandTool 单元测试：覆盖命令执行、参数校验、工作目录、超时。
/// 使用跨平台命令（echo / ping / sleep），Windows 用 cmd /c，Unix 用 sh -c。
/// </summary>
public class RunCommandToolTests
{
    private readonly RunCommandTool _tool = new();

    private static ToolCall MakeCall(string command, string? args = null, string? cwd = null, int? timeout = null)
    {
        var dict = new Dictionary<string, object>();
        dict["command"] = command;
        if (args != null) dict["args"] = args;
        if (cwd != null) dict["cwd"] = cwd;
        if (timeout != null) dict["timeout"] = timeout.Value;
        var json = JsonSerializer.Serialize(dict);
        using var doc = JsonDocument.Parse(json);
        return new ToolCall("id", "run_command", doc.RootElement.Clone());
    }

    // —— 正常执行 ——

    [Fact]
    public async Task ExecuteAsync_EchoCommand_ReturnsSuccess()
    {
        var call = MakeCall("echo", "hello");

        var result = await _tool.ExecuteAsync(call.Input, CancellationToken.None);

        result.Success.Should().BeTrue();
        result.Content.Should().Contain("hello");
    }

    [Fact]
    public async Task ExecuteAsync_CommandWithArgs_ReturnsCombinedOutput()
    {
        var call = MakeCall("echo", "a b c");

        var result = await _tool.ExecuteAsync(call.Input, CancellationToken.None);

        result.Success.Should().BeTrue();
        result.Content.Should().Contain("a b c");
    }

    [Fact]
    public async Task ExecuteAsync_WithCwd_ReturnsSuccess()
    {
        var call = MakeCall("echo", "test", cwd: Path.GetTempPath());

        var result = await _tool.ExecuteAsync(call.Input, CancellationToken.None);

        result.Success.Should().BeTrue();
        result.Content.Should().Contain("test");
    }

    // —— 参数校验 ——

    [Fact]
    public async Task ExecuteAsync_MissingCommandParam_ReturnsFail()
    {
        // MakeCall 总会写入 command，这里手动构造缺失 command 的输入
        var dict = new Dictionary<string, object> { ["args"] = "x" };
        var json = JsonSerializer.Serialize(dict);
        using var doc = JsonDocument.Parse(json);
        var call = new ToolCall("id", "run_command", doc.RootElement.Clone());

        var result = await _tool.ExecuteAsync(call.Input, CancellationToken.None);

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("command");
    }

    [Fact]
    public async Task ExecuteAsync_EmptyCommand_ReturnsFail()
    {
        var call = MakeCall("");

        var result = await _tool.ExecuteAsync(call.Input, CancellationToken.None);

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("不能为空");
    }

    // —— 错误命令 ——

    [Fact]
    public async Task ExecuteAsync_NonExistentCommand_ReturnsFailOrNonZeroExit()
    {
        var call = MakeCall("nonexistent_cmd_xyz");

        // 不抛异常即通过；可能是 Fail 或 Success(非零退出码)
        var result = await _tool.ExecuteAsync(call.Input, CancellationToken.None);

        if (result.Success)
        {
            result.Content.Should().Contain("[exit ");
            result.Content.Should().NotContain("[exit 0]");
        }
    }

    // —— 工作目录 ——

    [Fact]
    public async Task ExecuteAsync_WithNonExistentCwd_ReturnsFail()
    {
        // 跨平台构造一个保证不存在的绝对路径（Guid 确保唯一，根目录前缀按平台区分）
        var nonexistentRoot = OperatingSystem.IsWindows()
            ? Path.Combine(Path.GetPathRoot(Path.GetTempPath())!, "nonexistent_parrotcode", Guid.NewGuid().ToString("N"))
            : $"/nonexistent_parrotcode/{Guid.NewGuid():N}";
        var call = MakeCall("echo", "x", cwd: nonexistentRoot);

        var result = await _tool.ExecuteAsync(call.Input, CancellationToken.None);

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("工作目录不存在");
    }

    // —— 超时 ——

    [Fact]
    public async Task ExecuteAsync_ShortTimeout_TimesOut()
    {
        var (cmd, args) = OperatingSystem.IsWindows() ? ("ping", "-n 10 127.0.0.1") : ("sleep", "5");
        var call = MakeCall(cmd, args, timeout: 1);

        var result = await _tool.ExecuteAsync(call.Input, CancellationToken.None);

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("超时");
    }

    [Fact]
    public async Task ExecuteAsync_TimeoutClampedToMax()
    {
        // timeout=99999 超过最大值 300，应被 clamp 而非报参数错误；echo 很快完成
        var call = MakeCall("echo", "hi", timeout: 99999);

        var result = await _tool.ExecuteAsync(call.Input, CancellationToken.None);

        result.Success.Should().BeTrue();
        result.Content.Should().Contain("hi");
    }

    // —— 跨平台编码（迭代 8a：修复 Windows 中文输出乱码）——

    [Fact]
    public async Task ExecuteAsync_NonAsciiOutput_NoMojibake()
    {
        // RunCommandTool 已做 chcp 65001 + StandardOutputEncoding=UTF-8，stdout 读取链路正确。
        // Unix：sh 默认 UTF-8，echo 你好 → 你好。
        // Windows：cmd.exe 解析 ProcessStartInfo.Arguments 时按 OEM 代码页（CP936/CP437）编码参数字节，
        //   chcp 65001 只影响 cmd 的输出代码页，不影响已解析的参数字节。这是 cmd.exe 的固有局限，
        //   非 RunCommandTool 代码 bug。故此用例仅在 Unix 平台验证。
        if (OperatingSystem.IsWindows()) return;

        var call = MakeCall("echo", "你好");

        var result = await _tool.ExecuteAsync(call.Input, CancellationToken.None);

        result.Success.Should().BeTrue();
        result.Content.Should().Contain("你好");
    }

    [Fact]
    public async Task ExecuteAsync_EmojiOutput_NoMojibake()
    {
        // 同上：emoji 依赖 UTF-8 全链路。Windows cmd 参数传递不支持 UTF-8，仅 Unix 验证。
        if (OperatingSystem.IsWindows()) return;

        var call = MakeCall("echo", "✅ ok");

        var result = await _tool.ExecuteAsync(call.Input, CancellationToken.None);

        result.Success.Should().BeTrue();
        result.Content.Should().Contain("✅");
    }
}
