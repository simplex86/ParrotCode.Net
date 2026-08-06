using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace ParrotCode;

/// <summary>
/// ReAct Agent 核心循环：Reason（LLM 推理）+ Act（工具执行）+ Observe（结果回灌）迭代。
/// 每轮：调 Provider 流式 → 累积文本 + tool_calls → assistant 入历史 →
///       若无 tool_calls 则 AgentDone → 否则分批执行 → tool 结果入历史 → 下一轮。
/// 最大轮次默认 10，防止无限循环。CancellationToken 全链路贯穿。
/// 事件流通过 IAgentEventSink 产出，解耦生产与展示。
/// </summary>
internal sealed class AgentLoop
{
    private readonly IBaseProvider _provider;
    private readonly ToolRegistry _registry;
    private readonly BatchToolExecutor _batchExecutor;
    private readonly int _maxRounds;
    private readonly string _toolChoice;
    private readonly string _systemPrompt;
    private readonly ILogger? _logger;

    public AgentLoop(IBaseProvider provider,
                     ToolRegistry registry,
                     BatchToolExecutor batchExecutor,
                     int maxRounds = 10,
                     string toolChoice = "auto",
                     string? systemPrompt = null,
                     ILogger? logger = null)
    {
        _provider = provider ?? throw new ArgumentNullException(nameof(provider));
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _batchExecutor = batchExecutor ?? throw new ArgumentNullException(nameof(batchExecutor));
        if (maxRounds < 1) throw new ArgumentOutOfRangeException(nameof(maxRounds));
        _maxRounds = maxRounds;
        _toolChoice = toolChoice;
        _systemPrompt = systemPrompt ?? DefaultSystemPrompt;
        _logger = logger;
    }

    private static string DefaultSystemPrompt =>
        "你是 ParrotCode.Net 的 AI 编程助手。你可以调用工具读写文件、执行命令、搜索代码。" +
        "每次只调用必要的工具，拿到结果后用简洁中文回复用户。";

    /// <summary>
    /// 运行 ReAct 循环。用户输入已由调用方 AddUser 到 history。
    /// 事件流写入 sink，结束时调 sink.Complete()。
    /// 异常不逃逸——Provider/工具错误转为 Error 事件，取消转为 Cancelled 事件。
    /// </summary>
    public async Task RunAsync(ConversationHistory history, IAgentEventSink sink, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(history);
        ArgumentNullException.ThrowIfNull(sink);

        try
        {
            await RunCoreAsync(history, sink, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await sink.WriteAsync(new AgentEvent.CancelledEvent(), CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "AgentLoop 致命错误");
            await sink.WriteAsync(new AgentEvent.ErrorEvent(ex.Message, ex), CancellationToken.None);
        }
        finally
        {
            sink.Complete();
        }
    }

    private async Task RunCoreAsync(ConversationHistory history, IAgentEventSink sink, CancellationToken cancellationToken)
    {
        var tools = _registry.GetAll().Count > 0 ? _registry.ToOpenAiSchemas() : (JsonElement?)null;

        for (var round = 1; round <= _maxRounds; round++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await sink.WriteAsync(new AgentEvent.RoundStartEvent(round), cancellationToken);

            // 构造消息：system prompt + 历史快照
            var messages = BuildMessagesWithSystem(history);

            // 流式调用 LLM
            var textBuf = new StringBuilder();
            var tcAcc = new ToolCallAccumulator();

            await foreach (var chunk in _provider.ChatStreamAsync(messages, tools, _toolChoice, cancellationToken))
            {
                switch (chunk)
                {
                    case ChatChunk.TextDelta(var text):
                        textBuf.Append(text);
                        await sink.WriteAsync(new AgentEvent.TextDeltaEvent(text), cancellationToken);
                        break;
                    case ChatChunk.ToolCallDelta(var idx, var id, var name, var args):
                        tcAcc.Accumulate(idx, id, name, args);
                        break;
                    case ChatChunk.Done:
                        break;
                }
            }

            var assistantText = textBuf.ToString();
            var toolCalls = tcAcc.Build();

            // assistant 消息入历史
            if (toolCalls.Count > 0)
            {
                history.AddAssistant(assistantText, toolCalls);
            }
            else
            {
                history.AddAssistant(assistantText);
            }

            if (!string.IsNullOrEmpty(assistantText))
            {
                await sink.WriteAsync(new AgentEvent.AssistantMessageEvent(assistantText), cancellationToken);
            }

            // 无工具调用 → Agent 完成
            if (toolCalls.Count == 0)
            {
                await sink.WriteAsync(new AgentEvent.AgentDoneEvent(assistantText), cancellationToken);
                _logger?.LogInformation("Agent 完成，共 {Rounds} 轮", round);
                return;
            }

            // 有工具调用 → 通知开始 + 分批执行
            foreach (var call in toolCalls)
            {
                await sink.WriteAsync(new AgentEvent.ToolCallStartEvent(call), cancellationToken);
            }

            var results = await _batchExecutor.ExecuteAsync(toolCalls, cancellationToken);

            for (var i = 0; i < toolCalls.Count; i++)
            {
                var call = toolCalls[i];
                var result = results[i];

                // 7b 新增：HITL/安全层拒绝 → emit ToolBlockedEvent；否则 ToolResultEvent
                // 启发式：拒绝原因含"用户拒绝"或"被拦截"标记为 blocked。
                if (!result.Success && IsHitlDenial(result))
                {
                    await sink.WriteAsync(new AgentEvent.ToolBlockedEvent(call, result.Error ?? "被拦截"), cancellationToken);
                }
                else
                {
                    await sink.WriteAsync(new AgentEvent.ToolResultEvent(call, result), cancellationToken);
                }

                // 失败原因（含 HITL 拒绝）回灌历史，让 LLM 自我修正
                history.AddTool(result.Success ? result.Content : $"错误：{result.Error}", call.Id);
            }

            await sink.WriteAsync(new AgentEvent.RoundEndEvent(round), cancellationToken);
        }

        // 达到最大轮次
        await sink.WriteAsync(new AgentEvent.MaxRoundsReachedEvent(_maxRounds), cancellationToken);
        _logger?.LogWarning("Agent 达到最大轮次 {Rounds}，强制停止", _maxRounds);
    }

    /// <summary>
    /// 启发式判断是否为 HITL/安全层拒绝（区别于工具自身执行失败）。
    /// 拒绝原因含"用户拒绝"或"被拦截"标记为 blocked。
    /// 更严谨的做法：BatchToolExecutor 返回带 Blocked 标志的结构（迭代 8 再精细化）。
    /// </summary>
    private static bool IsHitlDenial(ToolResult result) =>
        !result.Success && (result.Error?.Contains("用户拒绝") == true ||
                            result.Error?.Contains("被拦截") == true);

    /// <summary>
    /// 构造带 system prompt 的消息列表。
    /// system prompt 放头部，history 快照跟后。
    /// 每轮重新构造——history 在工具结果入历史后变化。
    /// </summary>
    private IReadOnlyList<Message> BuildMessagesWithSystem(ConversationHistory history)
    {
        var snapshot = history.ToProviderMessages();
        if (string.IsNullOrEmpty(_systemPrompt))
            return snapshot;
        var withSystem = new List<Message>(snapshot.Count + 1)
        {
            new(MessageRole.System, _systemPrompt)
        };
        withSystem.AddRange(snapshot);
        return withSystem;
    }
}
