using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.Extensions.Logging;

namespace ParrotCode;

/// <summary>
/// Hook 动作执行器（4 种动作 + 错误隔离）。
/// 所有动作的异常被捕获并记日志——Hook 失败不中断 Agent 主循环。
///
/// 15a 实现：shell / prompt_inject / http
/// 15b 追加：sub_agent（依赖 SubAgentRunner，通过 SetSubAgentRunner 注入）
/// </summary>
public sealed class ActionExecutor
{
    private readonly TemplateEngine _templates;
    private readonly HttpClient _httpClient;
    private readonly ILogger? _logger;

    // 15b 追加：sub_agent 动作用的子 Agent 运行器（setter 注入）
    // 15a 中这些字段未被赋值（SetSubAgentRunner 为空壳），15b 取消注释填充
#pragma warning disable CS0649, CS0169 // 字段从未赋值/未使用——15b 中 SetSubAgentRunner 填充
    private SubAgentRunner? _subAgentRunner;
    private BackgroundTaskManager? _backgroundTaskManager;
    private ConversationHistory? _parentHistory;
#pragma warning restore CS0649, CS0169

    /// <summary>
    /// 构造函数。接受 HttpMessageHandler（而非 HttpClient）以支持单测 mock。
    /// 生产环境传 null——内部用默认 HttpClientHandler。
    /// 单测中传 mock HttpMessageHandler 拦截 HTTP 请求。
    /// </summary>
    public ActionExecutor(TemplateEngine? templates = null,
                          HttpMessageHandler? handler = null,
                          ILogger? logger = null)
    {
        _templates = templates ?? new TemplateEngine();
        _httpClient = new HttpClient(handler ?? new HttpClientHandler());
        _logger = logger;
    }

    /// <summary>
    /// 注入 SubAgentRunner（15b 中 TerminalApp.RunAsync 调用）。
    /// 15a 中此方法为空壳——sub_agent 动作会记警告并跳过。
    /// </summary>
    public void SetSubAgentRunner(SubAgentRunner? runner,
                                   BackgroundTaskManager? backgroundManager = null,
                                   ConversationHistory? parentHistory = null)
    {
        // 15a：空实现（_subAgentRunner 保持 null，sub_agent 动作跳过）
        // 15b：取消注释填充实现
        // _subAgentRunner = runner;
        // _backgroundTaskManager = backgroundManager;
        // _parentHistory = parentHistory;
    }

    /// <summary>
    /// 执行单个动作。返回结果文本（可能为 null）。
    /// 异常被捕获——Hook 失败只记日志，不抛出。
    /// 使用 action.ActionType（Loader 解析后的强类型）。
    /// </summary>
    public async Task<string?> ExecuteAsync(HookAction action,
                                            Dictionary<string, object?> context,
                                            double timeoutSeconds = 30.0,
                                            CancellationToken cancellationToken = default)
    {
        try
        {
            return action.ActionType switch
            {
                HookActionType.Shell => await ExecShellAsync(action, context, timeoutSeconds, cancellationToken),
                HookActionType.PromptInject => ExecPromptInject(action, context),
                HookActionType.Http => await ExecHttpAsync(action, context, timeoutSeconds, cancellationToken),
                HookActionType.SubAgent => await ExecSubAgentAsync(action, context, cancellationToken),
                _ => null
            };
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Hook 动作 [{Type}] 执行失败", action.ActionType);
            return null;
        }
    }

    // ===== shell =====

    private async Task<string?> ExecShellAsync(HookAction action, 
                                               Dictionary<string, object?> context,
                                               double timeoutSeconds, 
                                               CancellationToken ct)
    {
        var command = _templates.Render(action.Command, context);
        if (string.IsNullOrWhiteSpace(command))
            return null;

        var (fileName, args) = PrepareShellCommand(command);

        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = args,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var proc = new Process { StartInfo = psi };
        proc.Start();

        var stdoutTask = proc.StandardOutput.ReadToEndAsync(ct);
        var stderrTask = proc.StandardError.ReadToEndAsync(ct);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));

        try
        {
            await proc.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            _logger?.LogWarning("Hook shell 动作超时（{Sec}s）：{Cmd}", timeoutSeconds, command);
            try { proc.Kill(); } catch { }
            return $"（超时 {timeoutSeconds}s）";
        }

        var stdout = await stdoutTask;
        var stderr = await stderrTask;

        var result = stdout;
        if (!string.IsNullOrEmpty(stderr))
            result += $"\n[stderr]\n{stderr}";

        return result.Length > 2000 ? result[..2000] + "\n...（截断）" : result;
    }

    private static (string FileName, string Args) PrepareShellCommand(string command)
    {
        return OperatingSystem.IsWindows() ? ("cmd.exe", $"/c \"{command}\"")
                                           : ("/bin/sh", $"-c \"{command}\"");
    }

    // ===== prompt_inject =====

    private string? ExecPromptInject(HookAction action, Dictionary<string, object?> context)
    {
        return _templates.Render(action.Text, context);
    }

    // ===== http =====

    private async Task<string?> ExecHttpAsync(HookAction action, 
                                              Dictionary<string, object?> context,
                                              double timeoutSeconds, 
                                              CancellationToken ct)
    {
        var url = _templates.Render(action.Url, context);
        if (string.IsNullOrWhiteSpace(url))
            return null;

        var body = string.IsNullOrEmpty(action.Body) ? null : _templates.Render(action.Body, context);

        using var req = new HttpRequestMessage(new(action.Method.ToUpperInvariant()), url);
        foreach (var (key, value) in action.Headers)
        {
            req.Headers.TryAddWithoutValidation(key, value);
        }
        if (body is not null)
        {
            req.Content = new StringContent(body, Encoding.UTF8);
            req.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));

        try
        {
            var resp = await _httpClient.SendAsync(req, cts.Token);
            var text = await resp.Content.ReadAsStringAsync(cts.Token);
            return text.Length > 2000 ? text[..2000] + "\n...（截断）" : text;
        }
        catch (OperationCanceledException)
        {
            _logger?.LogWarning("Hook http 动作超时（{Sec}s）：{Url}", timeoutSeconds, url);
            return $"（超时 {timeoutSeconds}s）";
        }
    }

    // ===== sub_agent（15b 实现）=====

    private Task<string?> ExecSubAgentAsync(HookAction action, Dictionary<string, object?> context, CancellationToken ct)
    {
        // 15a：sub_agent 动作未实现，记警告并跳过
        // 15b：取消下方注释填充实现
        if (_subAgentRunner is null)
        {
            _logger?.LogWarning("Hook sub_agent 动作未注入 SubAgentRunner，跳过（15a 未实现，需 15b 接入）");
            return Task.FromResult<string?>(null);
        }

        // 15b 实现代码见 iter-15b-design.md 第 3.1 节
        return Task.FromResult<string?>(null);
    }
}
