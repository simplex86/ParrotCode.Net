using System.Diagnostics;
using System.IO;
using System.Text;
using Microsoft.Extensions.Logging;

namespace ParrotCode;

/// <summary>
/// Stdio 传输：通过子进程的 stdin/stdout 通信，stderr 独立收集日志（迭代 11a）。
/// 子进程在 ConnectAsync 启动，CloseAsync 关闭。
///
/// 关键设计：
/// - stdin 写入 JSON-RPC 消息（每条一行）
/// - stdout 读取 JSON-RPC 响应（每条一行）
/// - stderr 独立线程读取，输出到日志（不混入 JSON-RPC 通道）
/// - CloseAsync 先关闭 stdin 触发 server 优雅退出，超时 3 秒后 Kill
/// </summary>
internal sealed class StdioTransport : ITransport
{
    private readonly McpServerConfig _config;
    private readonly ILogger? _logger;
    private Process? _process;
    private StreamReader? _stdoutReader;
    private StreamWriter? _stdinWriter;
    private readonly List<string> _stderrLines = new();  // 捕获 stderr（logger 为 null 时用于错误诊断）
    private Task? _stderrTask;  // stderr 收集线程，GetErrorContext 时等待其完成

    public StdioTransport(McpServerConfig config, ILogger? logger = null)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _logger = logger;
    }

    public Task ConnectAsync(CancellationToken cancellationToken)
    {
        var (fileName, args, resolvedDir) = ResolveCommand(_config.Command, _config.Args ?? string.Empty);

        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = args,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = _config.WorkingDir ?? string.Empty
        };

        // 设置环境变量
        if (_config.Env is not null)
        {
            foreach (var (key, value) in _config.Env)
                startInfo.Environment[key] = value;
        }

        // 如果命令从 fallback 目录找到（不在 PATH 中），将该目录加入子进程 PATH，
        // 使 npx.cmd 等批处理能找到 node.exe 等依赖。
        if (resolvedDir is not null)
        {
            var currentPath = startInfo.Environment.TryGetValue("PATH", out var p) ? p : "";
            startInfo.Environment["PATH"] = resolvedDir + Path.PathSeparator + currentPath;
        }

        _process = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"无法启动 MCP server：{_config.Command}");

        _stdoutReader = _process.StandardOutput;
        _stdinWriter = _process.StandardInput;
        _stdinWriter.AutoFlush = true;

        // stderr 独立线程收集（不污染 JSON-RPC 通道），同时缓存用于错误诊断
        _stderrTask = Task.Run(async () =>
        {
            try
            {
                while (!_process.StandardError.EndOfStream)
                {
                    var line = await _process.StandardError.ReadLineAsync(cancellationToken);
                    if (line is not null)
                    {
                        _stderrLines.Add(line);
                        _logger?.LogDebug("MCP server [{Name}] stderr: {Line}", _config.Name, line);
                    }
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                _logger?.LogDebug("MCP server [{Name}] stderr 读取结束：{Error}", _config.Name, ex.Message);
            }
        }, cancellationToken);

        _logger?.LogInformation("MCP Stdio server [{Name}] 已启动 (PID={Pid})", _config.Name, _process.Id);
        return Task.CompletedTask;
    }

    public async Task SendAsync(string json, CancellationToken cancellationToken)
    {
        if (_stdinWriter is null) throw new InvalidOperationException("Transport 未连接");
        await _stdinWriter.WriteLineAsync(json.AsMemory(), cancellationToken);
    }

    public async Task<string?> ReceiveAsync(CancellationToken cancellationToken)
    {
        if (_stdoutReader is null) throw new InvalidOperationException("Transport 未连接");
        try
        {
            var line = await _stdoutReader.ReadLineAsync(cancellationToken);
            return line;
        }
        catch (IOException)
        {
            return null;  // 进程已退出
        }
    }

    public Task CloseAsync(CancellationToken cancellationToken)
    {
        if (_process is null || _process.HasExited) return Task.CompletedTask;

        _logger?.LogInformation("MCP Stdio server [{Name}] 正在关闭", _config.Name);

        // 关闭 stdin 触发 server 优雅退出
        try { _stdinWriter?.Close(); } catch { }

        // 等待进程退出（最多 3 秒）
        if (!_process.WaitForExit(3000))
        {
            _logger?.LogWarning("MCP server [{Name}] 未在 3 秒内退出，强制终止", _config.Name);
            try { _process.Kill(entireProcessTree: true); } catch { }
        }

        _logger?.LogInformation("MCP Stdio server [{Name}] 已关闭 (exit={Code})", _config.Name, _process.ExitCode);
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        try { _process?.Dispose(); } catch { }
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// 返回子进程 stderr 输出，供上层在连接失败时诊断原因。
    /// 等待 stderr 收集线程结束（最多 2 秒），确保捕获所有输出。
    /// </summary>
    public string? GetErrorContext()
    {
        // 等待 stderr 收集线程完成（进程退出后 EndOfStream 变 true，线程自然结束）
        if (_stderrTask is not null)
        {
            try { _stderrTask.Wait(TimeSpan.FromSeconds(2)); } catch { }
        }

        return _stderrLines.Count > 0 ? string.Join("\n", _stderrLines) : null;
    }

    /// <summary>
    /// 解析命令路径。Windows 上 npx/node 等实际是 .cmd 批处理文件，
    /// UseShellExecute=false 时 CreateProcess 不能直接执行 .cmd/.bat，
    /// 需通过 cmd.exe /c 包装，并使用完整路径。
    /// 返回 (fileName, args, resolvedDir)：resolvedDir 为命令所在目录（fallback 找到时需加入子进程 PATH）。
    /// Unix 上直接返回原命令（依赖 PATH 查找）。
    /// </summary>
    private static (string fileName, string args, string? resolvedDir) ResolveCommand(string? command, string args)
    {
        if (string.IsNullOrWhiteSpace(command)) return (command ?? string.Empty, args, null);

        // Unix 直接返回（execvp 负责 PATH 查找）
        if (!OperatingSystem.IsWindows()) return (command, args, null);

        // 已含扩展名：检查是否为 .cmd/.bat
        if (Path.HasExtension(command))
        {
            var ext = Path.GetExtension(command).ToLowerInvariant();
            if (ext == ".cmd" || ext == ".bat")
            {
                var (fullPath, dir) = FindExecutable(command);
                if (fullPath is not null)
                    return ("cmd.exe", $"/c \"{fullPath}\" {args}", dir);
                return ("cmd.exe", $"/c \"{command}\" {args}", null);
            }
            return (command, args, null);
        }

        // 无扩展名：尝试 .cmd / .bat / .exe
        foreach (var ext in new[] { ".cmd", ".bat", ".exe" })
        {
            var withExt = command + ext;
            var (fullPath, dir) = FindExecutable(withExt);
            if (fullPath is not null)
            {
                if (ext is ".cmd" or ".bat")
                    return ("cmd.exe", $"/c \"{fullPath}\" {args}", dir);
                return (fullPath, args, dir);
            }
        }

        return (command, args, null);  // 找不到则返回原命令，让 Process.Start 抛出原始异常
    }

    /// <summary>
    /// 在 PATH 和常见安装目录中查找可执行文件。
    /// 返回 (完整路径, 所在目录)。目录在 fallback 查找时非 null，PATH 查找时为 null（已在 PATH 中）。
    /// </summary>
    private static (string? fullPath, string? dir) FindExecutable(string fileName)
    {
        // 如果已经是完整路径，直接检查
        if (Path.IsPathRooted(fileName))
            return (File.Exists(fileName) ? fileName : null, null);

        // 1. 在 PATH 中查找（返回 dir=null 表示已在 PATH 中，无需额外处理）
        var path = Environment.GetEnvironmentVariable("PATH");
        if (path is not null)
        {
            foreach (var dir in path.Split(Path.PathSeparator))
            {
                if (string.IsNullOrWhiteSpace(dir)) continue;
                try
                {
                    var full = Path.Combine(dir, fileName);
                    if (File.Exists(full)) return (full, null);
                }
                catch { }
            }
        }

        // 2. 在常见 Node.js 安装目录中查找（fallback）
        // Windows 上 Node.js 可能安装但未加入进程 PATH（终端在安装前已打开等）
        var fallbackDirs = new[]
        {
            @"C:\Program Files\nodejs",
            @"C:\Program Files (x86)\nodejs",
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "nodejs"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "npm"),
        };

        foreach (var dir in fallbackDirs)
        {
            try
            {
                var full = Path.Combine(dir, fileName);
                if (File.Exists(full)) return (full, dir);
            }
            catch { }
        }

        return (null, null);
    }
}
