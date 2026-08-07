using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace ParrotCode;

/// <summary>
/// 执行 shell 命令工具。Category=Write（有副作用）。
/// 参数：command（命令名）+ args（参数）+ cwd（工作目录，可选）+ timeout（秒，默认 30）。
/// 用 Process.Start + 重定向 stdout/stderr。
/// 本迭代不接安全层——黑名单拦截在迭代 8。
/// </summary>
public sealed class RunCommandTool : ToolBase
{
    private const int DefaultTimeoutSeconds = 30;
    private const int MaxTimeoutSeconds = 300;

    public override string Name => "run_command";

    public override string Description =>
        "执行 shell 命令并返回 stdout/stderr。用于编译、测试、git 等操作。" +
        "命令在 shell 中执行（Windows 用 cmd /c，Unix 用 sh -c）。" +
        "默认超时 30 秒，可通过 timeout 参数调整（最大 300 秒）。";

    public override ToolCategory Category => ToolCategory.Write;

    public override IReadOnlyList<ToolParameter> Parameters { get; } = new[]
    {
        new ToolParameter("command", "string", "要执行的命令（如 git status / dotnet build）", Required: true),
        new ToolParameter("args", "string", "命令参数（已拼接好的字符串，如 'status --short'）", Required: false),
        new ToolParameter("cwd", "string", "工作目录（默认当前目录）", Required: false),
        new ToolParameter("timeout", "integer", "超时秒数（默认 30，最大 300）", Required: false)
    };

    public override async Task<ToolResult> ExecuteAsync(JsonElement input, CancellationToken cancellationToken)
    {
        var command = GetRequiredString(input, "command", out var err1);
        if (err1 is not null) return ToolResult.Fail(err1);
        var args = GetOptionalString(input, "args", out var err2, "");
        if (err2 is not null) return ToolResult.Fail(err2);
        var cwd = GetOptionalString(input, "cwd", out var err3, "");
        if (err3 is not null) return ToolResult.Fail(err3);
        var timeoutSec = GetOptionalInt(input, "timeout", out var err4, DefaultTimeoutSeconds);
        if (err4 is not null) return ToolResult.Fail(err4);

        if (string.IsNullOrWhiteSpace(command))
            return ToolResult.Fail("参数 command 不能为空");

        timeoutSec = Math.Clamp(timeoutSec, 1, MaxTimeoutSeconds);

        var psi = new ProcessStartInfo
        {
            FileName = OperatingSystem.IsWindows() ? "cmd.exe" : "/bin/sh",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            // 统一用 UTF-8 读取子进程输出（Windows 需配合下方 chcp 65001 切代码页）
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        // 构造完整命令行：Windows cmd /c "command args" / Unix sh -c "command args"
        var fullCommand = string.IsNullOrEmpty(args) ? command : $"{command} {args}";
        if (OperatingSystem.IsWindows())
            // chcp 65001 切换 cmd 代码页为 UTF-8，防止中文输出（git log / dotnet test / dir 中文文件名）乱码
            psi.Arguments = $"/c chcp 65001 >nul && {fullCommand}";
        else
            psi.ArgumentList.Add("-c");

        if (!OperatingSystem.IsWindows())
        {
            // sh -c 后面跟完整命令字符串作为一个参数
            psi.ArgumentList.Add(fullCommand);
        }

        if (!string.IsNullOrEmpty(cwd))
        {
            if (!Directory.Exists(cwd))
                return ToolResult.Fail($"工作目录不存在：{cwd}");
            psi.WorkingDirectory = cwd;
        }

        try
        {
            using var proc = Process.Start(psi);
            if (proc is null) return ToolResult.Fail("无法启动进程");

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(timeoutSec));

            var stdoutTask = proc.StandardOutput.ReadToEndAsync(cts.Token);
            var stderrTask = proc.StandardError.ReadToEndAsync(cts.Token);
            var exited = await WaitForExitAsync(proc, cts.Token);

            if (!exited)
            {
                try { proc.Kill(entireProcessTree: true); } catch { /* 忽略 */ }
                return ToolResult.Fail($"命令执行超时（{timeoutSec}s）");
            }

            var stdout = await stdoutTask;
            var stderr = await stderrTask;
            var combined = string.IsNullOrEmpty(stderr)
                ? stdout
                : $"{stdout}\n[stderr]\n{stderr}";
            return ToolResult.Ok($"[exit {proc.ExitCode}]\n{combined}");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return ToolResult.Fail($"命令执行失败：{ex.Message}");
        }
    }

    private static async Task<bool> WaitForExitAsync(Process proc, CancellationToken ct)
    {
        var tcs = new TaskCompletionSource<bool>();
        await using var _ = ct.Register(() => tcs.TrySetResult(false));
        proc.EnableRaisingEvents = true;
        proc.Exited += (_, _) => tcs.TrySetResult(true);
        if (proc.HasExited) return true;
        return await tcs.Task;
    }
}
