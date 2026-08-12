using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace ParrotCode;

/// <summary>
/// 后台任务状态（迭代 14b）。
/// </summary>
public enum BackgroundTaskStatus
{
    /// <summary>
    /// 运行中。
    /// </summary>
    Running,

    /// <summary>
    /// 已完成（成功）。
    /// </summary>
    Completed,

    /// <summary>
    /// 已失败（异常或错误）。
    /// </summary>
    Failed
}

/// <summary>
/// 后台任务条目（迭代 14b）：跟踪单个异步子 Agent 任务的执行状态。
/// </summary>
internal sealed class BackgroundTask
{
    public string TaskId { get; }
    public SubAgentRequest Request { get; }
    public BackgroundTaskStatus Status { get; private set; } = BackgroundTaskStatus.Running;
    public SubAgentResult? Result { get; private set; }
    public string? Error { get; private set; }
    public DateTime StartedAt { get; } = DateTime.UtcNow;
    public DateTime? CompletedAt { get; private set; }

    public BackgroundTask(string taskId, SubAgentRequest request)
    {
        TaskId = taskId;
        Request = request;
    }

    public void Complete(SubAgentResult result)
    {
        Status = BackgroundTaskStatus.Completed;
        Result = result;
        CompletedAt = DateTime.UtcNow;
    }

    public void Fail(string error)
    {
        Status = BackgroundTaskStatus.Failed;
        Error = error;
        CompletedAt = DateTime.UtcNow;
    }
}

/// <summary>
/// 后台任务管理器（迭代 14b）：管理异步子 Agent 任务。
/// 本迭代作为基础设施预留——sub_agent 工具仅支持同步模式。
///
/// 后台模式（background=true）的接线留进阶练习：
/// 1. sub_agent 工具调 <see cref="StartTask"/> 返回 taskId
/// 2. 主 Agent 下一轮调 sub_agent 时，<see cref="GetCompletedReports"/> 的报告注入 ToolResult
/// 3. 或新增 /tasks 命令查看后台任务状态
/// </summary>
public sealed class BackgroundTaskManager
{
    private readonly SubAgentRunner _runner;
    private readonly ConcurrentDictionary<string, BackgroundTask> _tasks = new();
    private readonly int _maxConcurrent;
    private readonly ILogger? _logger;

    public BackgroundTaskManager(SubAgentRunner runner,
                                  int maxConcurrent = 3,
                                  ILogger? logger = null)
    {
        _runner = runner ?? throw new ArgumentNullException(nameof(runner));
        _maxConcurrent = maxConcurrent;
        _logger = logger;
    }

    /// <summary>
    /// 启动后台子 Agent 任务。立即返回 taskId，不阻塞。
    /// </summary>
    /// <param name="request">子 Agent 请求。</param>
    /// <param name="parentHistory">父对话历史（Fork 模式用）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>任务 ID（8 字符 hex）。</returns>
    public string StartTask(SubAgentRequest request,
                             ConversationHistory? parentHistory,
                             CancellationToken cancellationToken)
    {
        // 并发数检查
        var running = _tasks.Values.Count(t => t.Status == BackgroundTaskStatus.Running);
        if (running >= _maxConcurrent)
            throw new InvalidOperationException($"已达到最大并发后台任务数 {_maxConcurrent}");

        var taskId = Guid.NewGuid().ToString("N")[..8];
        var task = new BackgroundTask(taskId, request);
        _tasks[taskId] = task;

        _ = Task.Run(async () =>
        {
            try
            {
                var result = await _runner.RunAsync(request, parentHistory, cancellationToken);
                task.Complete(result);
            }
            catch (Exception ex)
            {
                task.Fail(ex.Message);
                _logger?.LogWarning(ex, "后台子 Agent 任务 {Id} 失败", taskId);
            }
        }, cancellationToken);

        _logger?.LogInformation("启动后台子 Agent 任务 {Id}：role={Role}", taskId, request.Role);
        return taskId;
    }

    /// <summary>
    /// 获取所有已完成任务的报告（含成功和失败）。
    /// 调用方负责把报告注入主对话 history。
    /// </summary>
    public IReadOnlyList<(string TaskId, SubAgentResult Result)> GetCompletedReports()
    {
        return _tasks.Values
            .Where(t => t.Status == BackgroundTaskStatus.Completed && t.Result is not null)
            .Select(t => (t.TaskId, t.Result!))
            .ToList();
    }

    /// <summary>
    /// 查询任务状态。
    /// </summary>
    /// <param name="taskId">任务 ID。</param>
    /// <returns>(状态, 结果, 错误信息)。结果/错误可能为 null（取决于状态）。</returns>
    public (BackgroundTaskStatus Status, SubAgentResult? Result, string? Error) GetStatus(string taskId)
    {
        if (!_tasks.TryGetValue(taskId, out var task))
            throw new ArgumentException($"未找到任务：{taskId}", nameof(taskId));
        return (task.Status, task.Result, task.Error);
    }
}
