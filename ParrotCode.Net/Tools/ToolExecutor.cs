using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace ParrotCode;

/// <summary>
/// 工具单次执行器：超时 + 异常捕获，把所有失败转译为 ToolResult。
/// 不让异常逃逸到调用方——这是 Agent 自我修正能力的物质基础：
/// 失败原因作为 ToolResult.Error 回灌给 LLM，让它调整策略。
/// 本迭代只做单次执行；Read 并发 / Write 串行的分批执行在迭代 6 AgentLoop。
/// </summary>
public sealed class ToolExecutor
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);

    private readonly ToolRegistry _registry;
    private readonly TimeSpan _timeout;
    private readonly ILogger? _logger;

    /// <summary>
    /// 构造执行器。
    /// timeout：单次工具执行的最大时长，默认 30 秒。超时不杀工具任务（除非工具响应取消令牌），
    /// 只是不再等待结果——返回 ToolResult.Fail("工具执行超时")。
    /// </summary>
    public ToolExecutor(ToolRegistry registry, TimeSpan? timeout = null, ILogger? logger = null)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _timeout = timeout ?? DefaultTimeout;
        _logger = logger;
    }

    /// <summary>
    /// 执行单个 ToolCall。
    /// 流程：查找工具 → 构造取消令牌 → 执行（带超时）→ 异常捕获 → 返回 ToolResult。
    /// 任何异常（包括超时、IO、权限、参数错误）都转为 ToolResult.Fail，不抛异常。
    /// 唯一例外：外部 cancellationToken 取消时透传 OperationCanceledException。
    /// </summary>
    public async Task<ToolResult> ExecuteAsync(ToolCall call, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(call);
        // 外部取消优先——已取消的 token 立即抛 OCE，不进入工具查找 / 执行
        cancellationToken.ThrowIfCancellationRequested();

        // 1. 查找工具
        var tool = _registry.Get(call.Name);
        if (tool is null)
            return ToolResult.Fail($"未注册工具：{call.Name}");

        // 2. 构造带超时的取消令牌：外部的 cancellationToken + 内部的超时取并集
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(_timeout);

        var sw = Stopwatch.StartNew();

        // 3. 执行：把工具执行放到 Task.Run，避免工具同步阻塞调用线程
        Task<ToolResult> executeTask;
        try
        {
            executeTask = Task.Run(() => tool.ExecuteAsync(call.Input, timeoutCts.Token), timeoutCts.Token);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // 外部取消（用户 Ctrl+C）——透传
            throw;
        }
        catch (Exception ex)
        {
            // Task.Run 同步抛（如工具 ExecuteAsync 内部 sync throw before first await）
            _logger?.LogWarning(ex, "工具 {Name} 启动失败", call.Name);
            return ToolResult.Fail($"工具 {call.Name} 启动失败：{ex.Message}");
        }

        // 4. 等待工具完成或超时（Task.Delay 用外部 ct，外部取消时立即结束等待）
        var delayTask = Task.Delay(_timeout, cancellationToken);
        var completed = await Task.WhenAny(executeTask, delayTask);

        // 优先检查外部取消：即使 delayTask 先完成（被 ct 取消），若是外部取消则透传 OCE
        // 这避免"外部取消 + delay 先完成"被误判为超时
        if (cancellationToken.IsCancellationRequested)
            throw new OperationCanceledException(cancellationToken);

        if (completed == executeTask)
        {
            // 工具任务完成（成功或抛异常）
            try
            {
                var result = await executeTask;
                _logger?.LogInformation("工具 {Name} 执行完成，耗时 {Ms}ms，成功={Success}", call.Name, sw.ElapsedMilliseconds, result.Success);
                return result;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw; // 外部取消透传
            }
            catch (OperationCanceledException)
            {
                // 超时触发的取消（timeoutCts 已取消但外部 ct 未取消）
                _logger?.LogWarning("工具 {Name} 执行超时（{Timeout}s）", call.Name, _timeout.TotalSeconds);
                return ToolResult.Fail($"工具 {call.Name} 执行超时（{_timeout.TotalSeconds}s）");
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "工具 {Name} 执行抛异常", call.Name);
                return ToolResult.Fail($"工具 {call.Name} 执行失败：{ex.Message}");
            }
        }

        // Task.Delay 先完成且外部未取消——纯超时
        _logger?.LogWarning("工具 {Name} 执行超时（{Timeout}s）", call.Name, _timeout.TotalSeconds);
        return ToolResult.Fail($"工具 {call.Name} 执行超时（{_timeout.TotalSeconds}s）");
    }
}
