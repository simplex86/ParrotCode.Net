using Microsoft.Extensions.Logging;

namespace ParrotCode;

/// <summary>
/// 压缩结果。
/// </summary>
public sealed record CompressionResult
{
    public bool WasCompressed { get; init; }
    public int MessagesCompressed { get; init; }
    public int EstimatedTokensSaved { get; init; }
    public bool WarningIssued { get; init; }
    public string? WarningMessage { get; init; }
    public bool CircuitOpen { get; init; }
}

/// <summary>
/// 两层 token 管理协调器（迭代 9 新增）。
/// 层 1（TruncateBatch）：轻量，工具结果入历史前调用。
/// 层 2（CheckAndCompressAsync）：昂贵，每轮 LLM 调用前调用。
/// enableAutoCompress=false 时层 2 跳过（层 1 始终生效）。
/// </summary>
public sealed class ContextCompressor
{
    private readonly ToolResultTruncator _truncator;
    private readonly StructuredSummarizer _summarizer;
    private readonly bool _enableAutoCompress;
    private bool _warningEmitted;
    private readonly ILogger? _logger;

    public ContextCompressor(IBaseProvider provider,
                             int contextWindowTokens,
                             TruncateConfig? truncateConfig = null,
                             double warningFraction = 0.7,
                             double triggerFraction = 0.9,
                             int keepRecent = 4,
                             int maxCircuitFailures = 2,
                             bool enableAutoCompress = true,
                             string? projectRoot = null,
                             ILogger? logger = null)
    {
        _truncator = new ToolResultTruncator(truncateConfig, projectRoot);
        _summarizer = new StructuredSummarizer(provider, 
                                               contextWindowTokens,
                                               warningFraction, 
                                               triggerFraction,
                                               keepRecent, 
                                               maxCircuitFailures, 
                                               logger);
        _enableAutoCompress = enableAutoCompress;
        _logger = logger;
    }

    // ── 层 1：截断 ──

    public (string[] TruncatedContents, IReadOnlyList<TruncationInfo> Infos) TruncateBatch(IReadOnlyList<string> contents, IReadOnlyList<string> toolNames)
        => _truncator.TruncateBatch(contents, toolNames);

    // ── 层 2：压缩 ──

    public int ContextWindow => _summarizer.ContextWindow;
    public int WarningThreshold => _summarizer.WarningThreshold;
    public int TriggerThreshold => _summarizer.TriggerThreshold;
    public bool CircuitOpen => _summarizer.CircuitOpen;
    public int CircuitFailures => _summarizer.CircuitFailures;

    public void ResetCircuit() => _summarizer.ResetCircuit();

    /// <summary>
    /// 重置警告标志（/clear 或压缩成功后调用）。
    /// </summary>
    public void ResetWarning() => _warningEmitted = false;

    /// <summary>
    /// 检查并执行压缩。在每轮 LLM 调用前调用。
    /// 1. enableAutoCompress=false → 直接返回
    /// 2. token > 警告阈值 → 发警告（仅一次）
    /// 3. 熔断器 open → 跳过
    /// 4. token > 触发阈值 → 触发摘要
    /// </summary>
    public async Task<CompressionResult> CheckAndCompressAsync(ConversationHistory history, CancellationToken cancellationToken)
    {
        // enable_auto_compress: false → 跳过层 2（层 1 截断不受影响）
        if (!_enableAutoCompress)
            return new CompressionResult();

        var result = new CompressionResult();
        var messages = history.ToProviderMessages();

        // 1. 警告检查（仅一次）
        if (!_warningEmitted && _summarizer.NeedsWarning(messages))
        {
            _warningEmitted = true;
            result = result with
            {
                WarningIssued = true,
                WarningMessage = "上下文即将不足，建议保存当前会话并开启新对话"
            };
        }

        // 2. 熔断器检查
        if (_summarizer.CircuitOpen)
        {
            result = result with
            {
                CircuitOpen = true,
                WarningMessage = "自动压缩已禁用（摘要连续失败），请手动 /compress 或开启新会话"
            };
            return result;
        }

        // 3. 触发摘要
        if (!_summarizer.NeedsCompression(messages))
            return result;

        var summary = await _summarizer.SummarizeAsync(history, cancellationToken);

        if (!summary.Success)
        {
            // 摘要失败（熔断器已递增）
            if (_summarizer.CircuitOpen)
            {
                result = result with
                {
                    CircuitOpen = true,
                    WarningMessage = "自动压缩已禁用（摘要连续失败 2 次），请手动 /compress 或开启新会话"
                };
            }
            return result;
        }

        // 摘要成功 → 压缩后 token 降下来，重置警告
        _warningEmitted = false;

        return result with
        {
            WasCompressed = true,
            MessagesCompressed = summary.MessagesCompressed,
            EstimatedTokensSaved = summary.EstimatedTokensSaved
        };
    }
}
