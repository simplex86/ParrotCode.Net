using System.Linq;
using System.Text;
using Microsoft.Extensions.Logging;

namespace ParrotCode;

/// <summary>
/// 收集型事件 Sink（迭代 14b）：收集子 Agent 事件，不渲染到 TUI。
/// 提取最终报告 + 轮次计数。
///
/// 报告来源（优先级）：
/// 1. <see cref="FinalText"/>（正常完成——<see cref="AgentEvent.AgentDoneEvent"/> 设置）
/// 2. <see cref="LastAssistantText"/>（MaxRounds 兜底——缓存每轮 <see cref="AgentEvent.AssistantMessageEvent"/> 的文本）
/// 3. <see cref="FinalText"/> 含错误信息（<see cref="AgentEvent.ErrorEvent"/> 转错误报告）
///
/// 子 Agent 的中间事件（TextDelta / ToolCall / ToolResult 等）被丢弃——
/// 主 Agent 只看最终报告（作为 sub_agent 工具的 ToolResult）。
///
/// 设计修正（源码审查发现）：
///   AgentLoop 在 MaxRounds 路径只发 MaxRoundsReachedEvent，不发 AgentDoneEvent。
///   因此必须额外监听 AssistantMessageEvent 缓存 LastAssistantText 作为 fallback。
/// </summary>
internal sealed class CollectingEventSink : IAgentEventSink
{
    /// <summary>
    /// 正常完成时的最终文本（AgentDoneEvent 设置）。
    /// MaxRounds 路径不会设置此字段（保持 null）。
    /// </summary>
    public string? FinalText { get; private set; }

    /// <summary>
    /// 最后一轮 assistant 文本（AssistantMessageEvent 缓存）。
    /// 作为 MaxRounds 时的 fallback 报告——AgentLoop 在 MaxRounds 路径不发 AgentDoneEvent，
    /// 但每轮都会发 AssistantMessageEvent（含本轮 LLM 的完整回复）。
    /// </summary>
    public string? LastAssistantText { get; private set; }

    /// <summary>
    /// 已完成的轮次数（RoundEndEvent 递增）。
    /// </summary>
    public int RoundsCompleted { get; private set; }

    public ValueTask WriteAsync(AgentEvent evt, CancellationToken cancellationToken)
    {
        switch (evt)
        {
            case AgentEvent.AgentDoneEvent(var text):
                // 正常完成——最终文本优先
                FinalText = text;
                break;
            case AgentEvent.AssistantMessageEvent(var content):
                // 缓存每轮 assistant 文本——MaxRounds 时作为 fallback 报告
                LastAssistantText = content;
                break;
            case AgentEvent.RoundEndEvent:
                RoundsCompleted++;
                break;
            case AgentEvent.ErrorEvent(var msg, _):
                // 错误转为错误报告（覆盖 FinalText，让 SubAgentRunner 提取到错误信息）
                FinalText = $"[子 Agent 错误] {msg}";
                break;
            // MaxRoundsReachedEvent / CancelledEvent / TextDelta / ToolCall* / RoundStart 等不改变 FinalText
            // MaxRounds 时由 SubAgentRunner 用 LastAssistantText 兜底
        }
        return ValueTask.CompletedTask;
    }

    public void Complete()
    {
        // 无资源释放
    }
}

/// <summary>
/// 子 Agent 运行器（迭代 14b）：创建嵌套 <see cref="AgentLoop"/> 实例执行子任务。
/// 复用父 Provider / SecurityGuard，新建独立 history / registry / sink / system prompt。
/// 子 Agent 用 <see cref="NullHitlGate"/>（自主运行，不问用户）；安全层仍生效（黑名单 + 沙箱）。
///
/// <see cref="AgentLoop"/> 零改动——子 Agent 是新建实例，不感知"被嵌套调用"。
/// </summary>
public sealed class SubAgentRunner
{
    private readonly IBaseProvider _provider;
    private readonly ToolRegistry _parentRegistry;
    private readonly SecurityGuard _securityGuard;
    private readonly RoleRegistry _roleRegistry;
    private readonly SubAgentConfig _config;
    private readonly ILogger? _logger;

    public SubAgentRunner(IBaseProvider provider,
                          ToolRegistry parentRegistry,
                          SecurityGuard securityGuard,
                          RoleRegistry roleRegistry,
                          SubAgentConfig? config = null,
                          ILogger? logger = null)
    {
        _provider = provider ?? throw new ArgumentNullException(nameof(provider));
        _parentRegistry = parentRegistry ?? throw new ArgumentNullException(nameof(parentRegistry));
        _securityGuard = securityGuard ?? throw new ArgumentNullException(nameof(securityGuard));
        _roleRegistry = roleRegistry ?? throw new ArgumentNullException(nameof(roleRegistry));
        _config = config ?? new SubAgentConfig();
        _logger = logger;
    }

    /// <summary>
    /// 同步运行子 Agent，返回报告。
    /// 阻塞调用方直到子 Agent 完成（AgentDone 或 MaxRounds）。
    /// </summary>
    /// <param name="request">子 Agent 请求（task / role / mode）。</param>
    /// <param name="parentHistory">
    /// 父对话历史（仅 Fork 模式用，作为副本源）。
    /// Definitional 模式传 null——子 Agent 不继承父上下文。
    /// </param>
    /// <param name="cancellationToken">取消令牌。</param>
    public async Task<SubAgentResult> RunAsync(SubAgentRequest request,
                                               ConversationHistory? parentHistory,
                                               CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        // 1. 获取角色定义
        var role = _roleRegistry.Get(request.Role);
        if (role is null)
            return new SubAgentResult { Success = false, Error = $"未找到角色：{request.Role}" };

        // 2. 构建过滤后的工具注册表（14a 的 ToolFilter）
        var filteredRegistry = ToolFilter.Build(_parentRegistry, role, request.Mode);

        // 3. 构建子 Agent 的 history
        var history = BuildHistory(request, parentHistory);

        // 4. 构建 system prompt
        var systemPrompt = BuildSystemPrompt(request, role);

        // 5. 构建子 Agent 的 BatchToolExecutor（NullHitlGate，无交互）
        var executor = new ToolExecutor(filteredRegistry,
                                        TimeSpan.FromSeconds(30),
                                        _logger);
        var batchExecutor = new SecureBatchToolExecutor(executor,
                                                        filteredRegistry,
                                                        _securityGuard,
                                                        maxParallelism: 5,
                                                        hitlGate: new NullHitlGate(),  // 子 Agent 不问用户
                                                        logger: _logger);

        // 6. 构建收集型 EventSink
        var sink = new CollectingEventSink();

        // 7. 构建并运行 AgentLoop（嵌套实例，零改动 AgentLoop 类）
        var maxRounds = _config.MaxRounds ?? 5;
        var loop = new AgentLoop(_provider,
                                 filteredRegistry,
                                 batchExecutor,
                                 maxRounds: maxRounds,
                                 toolChoice: "auto",
                                 systemPrompt: systemPrompt,
                                 compressor: null,  // 子 Agent 不做压缩
                                 logger: null);  // 不给 logger，避免 stderr 交错（与主 AgentLoop 一致的硬约束）

        _logger?.LogInformation("启动子 Agent：role={Role}, mode={Mode}, maxRounds={Max}", request.Role, request.Mode, maxRounds);

        await loop.RunAsync(history, sink, cancellationToken);

        // 8. 提取报告（FinalText 优先，MaxRounds 时用 LastAssistantText 兜底）
        var report = sink.FinalText ?? sink.LastAssistantText ?? string.Empty;
        var maxChars = _config.ReportMaxChars ?? 2000;
        if (report.Length > maxChars)
        {
            report = report[..maxChars] + "\n\n...（报告过长，已截断）";
            _logger?.LogWarning("子 Agent 报告截断：原始 {Orig} 字符 → 截断到 {Max}", report.Length, maxChars);
        }

        _logger?.LogInformation("子 Agent 完成：role={Role}, rounds={Rounds}, report={Len} 字符", request.Role, sink.RoundsCompleted, report.Length);

        return new SubAgentResult
        {
            Success = true,
            Report = report,
            RoundsUsed = sink.RoundsCompleted
        };
    }

    /// <summary>
    /// 构建子 Agent 的对话历史。
    /// Definitional：空 history + task 作为首条 user 消息。
    /// Fork：复制父 history + task 作为追加 user 消息。
    ///
    /// Fork 副本隔离安全性（源码审查验证）：
    /// - <see cref="ConversationHistory.ToProviderMessages"/> 返回 _messages.ToArray()——数组是新数组
    /// - <see cref="ConversationHistory.ReplaceMessages"/> 清空 + AddRange——浅拷贝（共享 Message 引用）
    /// - Message 是 sealed record + init 属性——不可变，子 Agent 无法修改已有消息
    /// - 子 Agent 只能通过 AddUser/AddAssistant 在自己的 _messages 末尾追加，不影响 parent
    ///
    /// Fork 历史清理（bug 修复）：
    /// 主 Agent 调用 sub_agent 时，AgentLoop 已把 assistant(tool_calls) 入历史，
    /// 但 tool 结果还没入（工具正在执行中）。直接复制会导致 OpenAI API 400：
    /// "assistant message with 'tool_calls' must be followed by tool messages"。
    /// 需先截断末尾未完成的 assistant(tool_calls)。
    /// </summary>
    private static ConversationHistory BuildHistory(
        SubAgentRequest request, ConversationHistory? parentHistory)
    {
        var history = new ConversationHistory();

        if (request.Mode == SubAgentMode.Fork && parentHistory is not null)
        {
            // Fork：复制父历史（副本，不修改父）
            // 截断末尾未完成的 assistant(tool_calls)，避免 OpenAI 协议违规
            var cleaned = TrimIncompleteToolCalls(parentHistory.ToProviderMessages());
            history.ReplaceMessages(cleaned);
        }

        // 追加任务作为 user 消息
        history.AddUser(request.Task);
        return history;
    }

    /// <summary>
    /// 截断历史中"未完全响应"的 assistant(tool_calls) 及其后续消息。
    ///
    /// 主 Agent 调用 sub_agent 工具时，AgentLoop 已经把 assistant(tool_calls) 入历史，
    /// 但工具结果（tool 消息）还没入历史（工具正在执行中）。
    /// Fork 模式如果直接复制这个历史，子 Agent 发给 LLM 的消息序列会违反 OpenAI 协议：
    /// "assistant message with 'tool_calls' must be followed by tool messages"。
    ///
    /// 此方法从后往前扫描，找到第一个"未完全响应"的 assistant(tool_calls)，
    /// 截断到它之前（保留它之前的所有完整消息）。
    /// </summary>
    internal static IReadOnlyList<Message> TrimIncompleteToolCalls(IReadOnlyList<Message> messages)
    {
        if (messages.Count == 0) return messages;

        for (var i = messages.Count - 1; i >= 0; i--)
        {
            var msg = messages[i];
            if (msg.Role != MessageRole.Assistant || msg.ToolCalls is not { Count: > 0 } calls)
                continue;

            // 收集 i 之后所有 tool 消息的 tool_call_id
            var responded = new HashSet<string>(StringComparer.Ordinal);
            for (var j = i + 1; j < messages.Count; j++)
            {
                if (messages[j].Role == MessageRole.Tool && messages[j].ToolCallId is { } id)
                    responded.Add(id);
            }

            // 检查是否所有 tool_call 都有对应的 tool 响应
            var allResponded = true;
            foreach (var c in calls)
            {
                if (!responded.Contains(c.Id))
                {
                    allResponded = false;
                    break;
                }
            }

            if (!allResponded)
                return messages.Take(i).ToArray();
        }

        return messages;
    }

    /// <summary>
    /// 构建子 Agent 的 system prompt。
    /// Definitional：角色 SOP 正文 + 子 Agent 约束。
    /// Fork：Fork 指令 + 子 Agent 约束（不含角色 SOP，角色仅用于工具过滤）。
    /// </summary>
    private static string BuildSystemPrompt(SubAgentRequest request, RoleDefinition role)
    {
        var sb = new StringBuilder();

        if (request.Mode == SubAgentMode.Definitional)
        {
            // Definitional：角色 SOP 是 system prompt 主体
            sb.AppendLine(role.Body);
        }
        else
        {
            // Fork：角色 SOP 不注入（角色仅用于工具过滤），用 Fork 指令
            sb.AppendLine("你是一个 Fork 子 Agent，继承了父 Agent 的对话上下文。");
            sb.AppendLine("请在父上下文基础上完成分配的子任务。");
        }

        // 子 Agent 通用约束（两种模式都追加）
        sb.AppendLine();
        sb.AppendLine("## 子 Agent 严格约束");
        sb.AppendLine("1. 不要调用 sub_agent 工具（禁止创建子 worker，防止无限递归）");
        sb.AppendLine("2. 不要与用户对话（你是子任务执行者，不是对话者）");
        sb.AppendLine("3. 直接完成分配的任务，不要询问澄清");
        sb.AppendLine("4. 完成后输出结构化报告，不超过 500 字");
        sb.AppendLine("5. 报告应包含：执行摘要、关键发现/产出、结论");

        return sb.ToString();
    }
}
