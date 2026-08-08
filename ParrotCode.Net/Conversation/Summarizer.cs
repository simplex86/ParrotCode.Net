using System.Text;
using Microsoft.Extensions.Logging;

namespace ParrotCode;

/// <summary>
/// 摘要结果。
/// </summary>
public sealed record SummaryResult
{
    public bool Success { get; init; }
    public string? SummaryText { get; init; }
    public int MessagesCompressed { get; init; }
    public int EstimatedTokensSaved { get; init; }
    public string? Error { get; init; }
}

/// <summary>
/// 层 2：结构化摘要器。昂贵，需 LLM 调用。
/// token > triggerFraction × 窗口时触发，把旧消息压缩成 9 段摘要 + 保留最近 N 条原文。
/// 含熔断器：连续失败 maxFailures 次停止自动触发。
/// 摘要用 ChatStreamAsync(messages, ct)（不传 tools）——LLM 协议层面无法产出 tool_calls。
/// </summary>
internal sealed class StructuredSummarizer
{
    private readonly IBaseProvider _provider;
    private readonly int _contextWindow;
    private readonly int _warningThreshold;
    private readonly int _triggerThreshold;
    private readonly int _keepRecent;
    private readonly CircuitBreaker _breaker;
    private readonly ILogger? _logger;

    // 9 段结构化摘要 Prompt（首尾强调禁止工具调用 + draft 两步走）
    private const string SummaryPrompt = """
        你是一个对话摘要生成器。**只生成摘要，不要调用任何工具。**

        请分析以下对话，按指定结构生成摘要。每个部分用 ## 标题分隔：

        ## 主要请求
        用户的核心需求——他们想完成什么

        ## 关键概念
        涉及的技术栈、框架、API、库

        ## 文件与代码
        已检查或修改的文件、关键代码片段及其位置

        ## 错误与修复
        遇到的错误信息和修复方式

        ## 解决过程
        问题解决的步骤顺序和时间线

        ## 用户原话
        用户的关键原话（用 > 引用，逐字保留，不要改写）

        ## 待办事项
        尚未完成的任务

        ## 当前工作
        当前正在进行的具体工作

        ## 下一步
        建议的下一步操作

        ---

        先将你的分析写成草稿，用 ```draft ... ``` 包裹。草稿写完后再输出正式摘要。

        **再次强调：不要调用任何工具，只输出摘要文本。**
        """;

    private const string BoundaryMessage =
        "[对话上下文已压缩] 上方的结构化摘要替代了早期的详细对话。" +
        "如果你需要某个文件的完整内容或某段具体代码，请使用 read_file 或 grep " +
        "重新读取，不要根据摘要脑补不存在的细节。";

    public StructuredSummarizer(IBaseProvider provider,
                                int contextWindowTokens,
                                double warningFraction = 0.7,
                                double triggerFraction = 0.9,
                                int keepRecent = 4,
                                int maxCircuitFailures = 2,
                                ILogger? logger = null)
    {
        _provider = provider ?? throw new ArgumentNullException(nameof(provider));
        _contextWindow = contextWindowTokens;
        _warningThreshold = (int)(contextWindowTokens * warningFraction);
        _triggerThreshold = (int)(contextWindowTokens * triggerFraction);
        _keepRecent = keepRecent;
        _breaker = new CircuitBreaker(maxCircuitFailures);
        _logger = logger;
    }

    public int ContextWindow => _contextWindow;
    public int WarningThreshold => _warningThreshold;
    public int TriggerThreshold => _triggerThreshold;
    public bool CircuitOpen => _breaker.IsOpen;
    public int CircuitFailures => _breaker.FailureCount;

    public void ResetCircuit() => _breaker.Reset();

    /// <summary>
    /// 是否需要发警告（token > 警告阈值）。
    /// </summary>
    public bool NeedsWarning(IReadOnlyList<Message> messages) => TokenEstimator.Estimate(messages) > _warningThreshold;

    /// <summary>
    /// 是否需要触发摘要（token > 触发阈值）。
    /// </summary>
    public bool NeedsCompression(IReadOnlyList<Message> messages) => TokenEstimator.Estimate(messages) > _triggerThreshold;

    /// <summary>
    /// 生成结构化摘要，替换历史中的旧消息。
    /// 返回 SummaryResult。失败时熔断器递增，历史不变。
    /// </summary>
    public async Task<SummaryResult> SummarizeAsync(ConversationHistory history, CancellationToken cancellationToken)
    {
        var messages = history.ToProviderMessages();

        // 消息太少不值得压缩
        if (messages.Count < _keepRecent + 4)
            return new SummaryResult { Success = false, Error = "消息太少，不值得压缩" };

        // 熔断器检查
        if (_breaker.IsOpen)
            return new SummaryResult { Success = false, Error = "熔断器已打开，自动压缩已禁用" };

        // 分割：旧消息摘要 + 近期消息保留
        var split = messages.Count - _keepRecent;
        var old = messages.Take(split).ToList();
        var recent = messages.Skip(split).ToList();

        // 构造摘要请求
        var summaryInput = SummaryPrompt + "\n\n---\n对话内容:\n" + FormatForSummary(old);
        var summaryMessages = new List<Message>
        {
            new(MessageRole.User, summaryInput)
        };

        try
        {
            // 调 LLM（不传 tools → LLM 不会 tool_call）
            var sb = new StringBuilder();
            await foreach (var token in _provider.ChatStreamAsync(summaryMessages, cancellationToken))
            {
                sb.Append(token);
            }

            var raw = sb.ToString();

            // 去除 draft 块，提取正式摘要
            var summaryText = ExtractFormalSummary(raw);

            if (string.IsNullOrWhiteSpace(summaryText))
                throw new InvalidOperationException("摘要生成返回空内容");

            // 成功
            _breaker.RecordSuccess();

            // 计算节省的 token
            var oldTokens = TokenEstimator.Estimate(old);
            var summaryTokens = TokenEstimator.Estimate(summaryText);
            var saved = Math.Max(0, oldTokens - summaryTokens);

            // 构造新消息列表：[system: 摘要] + [system: 边界提示] + recent
            var newMessages = new List<Message>
            {
                new(MessageRole.System, $"[结构化摘要]\n{summaryText}"),
                new(MessageRole.System, BoundaryMessage)
            };
            newMessages.AddRange(recent);

            history.ReplaceMessages(newMessages);

            return new SummaryResult
            {
                Success = true,
                SummaryText = summaryText,
                MessagesCompressed = old.Count,
                EstimatedTokensSaved = saved
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw; // 取消不记失败
        }
        catch (Exception ex)
        {
            _breaker.RecordFailure();
            _logger?.LogWarning(ex, "摘要生成失败，熔断器计数 {Count}/{Max}", _breaker.FailureCount, _breaker.MaxFailures);
            return new SummaryResult { Success = false, Error = ex.Message };
        }
    }

    /// <summary>
    /// 从 LLM 输出中去除 draft 块，提取正式摘要。
    /// </summary>
    internal static string ExtractFormalSummary(string raw)
    {
        // 查找 ```draft ... ``` 块，取其后的内容
        var draftStart = raw.IndexOf("```draft", StringComparison.OrdinalIgnoreCase);
        if (draftStart == -1)
        {
            // 无 draft 标记，整体作为摘要
            return raw.Trim();
        }

        var draftEnd = raw.IndexOf("```", draftStart + 8, StringComparison.OrdinalIgnoreCase);
        if (draftEnd == -1)
        {
            // draft 未闭合，取 draftStart 之前的内容
            return raw[..draftStart].Trim();
        }

        // 取 draft 闭合后的内容
        var afterDraft = raw[(draftEnd + 3)..];
        return afterDraft.Trim();
    }

    /// <summary>
    /// 格式化消息列表供摘要 Prompt 使用（每条截断 3000 字符）。
    /// </summary>
    private static string FormatForSummary(IReadOnlyList<Message> messages)
    {
        var parts = new List<string>(messages.Count);
        foreach (var msg in messages)
        {
            var role = msg.Role.ToString().ToLowerInvariant();
            var content = msg.Content;
            if (content.Length > 3000)
                content = content[..3000] + "...(截断)";
            parts.Add($"[{role}]: {content}");
        }
        return string.Join("\n\n", parts);
    }
}
