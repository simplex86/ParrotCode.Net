using System.Diagnostics;
using System.IO;
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

    public StdioTransport(McpServerConfig config, ILogger? logger = null)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _logger = logger;
    }

    public Task ConnectAsync(CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = _config.Command,
            Arguments = _config.Args ?? string.Empty,
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

        _process = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"无法启动 MCP server：{_config.Command}");

        _stdoutReader = _process.StandardOutput;
        _stdinWriter = _process.StandardInput;
        _stdinWriter.AutoFlush = true;

        // stderr 独立线程收集（不污染 JSON-RPC 通道）
        _ = Task.Run(async () =>
        {
            try
            {
                while (!_process.StandardError.EndOfStream)
                {
                    var line = await _process.StandardError.ReadLineAsync(cancellationToken);
                    if (line is not null)
                        _logger?.LogDebug("MCP server [{Name}] stderr: {Line}", _config.Name, line);
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
}
