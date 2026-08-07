using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace ParrotCode;

/// <summary>
/// 主 TUI 应用（7a 展示层 + 7b HITL 接线）。
/// 装配流式渲染 + 状态栏 + 事件消费 + 输入循环 + HITL 交互。
/// 替代迭代 6 App.cs 的内联渲染。
///
/// 7b 最终方案（方案 A：流式渲染，不用 Live）：
/// - 放弃 Spectre.Console Live——Live 的 off-by-one、行数跳变、跨轮残影、滚屏失效等固有限制无法根治。
/// - 改用 ConsoleEventRenderer 流式渲染：逐字输出文本 + Panel 输出工具调用/结果。
/// - 状态栏在每轮对话开始时显示一次（Panel 样式保持不变）。
/// - HITL 直接用 AnsiConsole.Write(Panel) + Console.ReadKey，不需要 Channel/TaskCompletionSource。
///
/// 关键约束：
/// - 不用 Live——避免所有 Live 相关的残影问题。
/// - HitlPrompt 注入 IConsole，直接渲染 + ReadKey，同步返回决策。
/// - enable_hitl: false 或降级模式时注入 NullHitlGate（等价 7a）。
/// - 不给 AgentLoop 传 logger——避免 stderr 与 stdout 交错。
/// </summary>
internal sealed class TuiApp
{
    private readonly IBaseProvider _provider;
    private readonly ProviderConfig _providerConfig;
    private readonly AgentConfig _agentConfig;
    private readonly TuiConfig _tuiConfig;
    private readonly SecurityLevel _securityLevel;
    private readonly ILogger? _logger;
    private readonly CancellationToken _ct;
    private readonly IConsole _console;
    private readonly bool _enableHitl;

    public TuiApp(IBaseProvider provider,
                  ProviderConfig providerConfig,
                  AgentConfig? agentConfig,
                  TuiConfig? tuiConfig,
                  SecurityLevel securityLevel,
                  ILogger? logger,
                  CancellationToken ct,
                  IConsole? console = null,
                  bool useLive = true)  // useLive 参数保留兼容但不再使用
    {
        _provider = provider ?? throw new ArgumentNullException(nameof(provider));
        _providerConfig = providerConfig ?? throw new ArgumentNullException(nameof(providerConfig));
        _agentConfig = agentConfig ?? new AgentConfig();
        _tuiConfig = tuiConfig ?? new TuiConfig();
        _securityLevel = securityLevel;
        _logger = logger;
        _ct = ct;
        _console = console ?? new SystemConsole();
        _enableHitl = _tuiConfig.EnableHitl ?? true;
    }

    public async Task RunAsync()
    {
        var history = new ConversationHistory();
        var inputReader = new InputReader(_console);

        // 装配工具注册中心（与迭代 6 App 一致）
        var registry = new ToolRegistry();
        registry.Register(new ReadFileTool());
        registry.Register(new WriteFileTool());
        registry.Register(new EditFileTool());
        registry.Register(new GlobTool());
        registry.Register(new GrepTool());
        registry.Register(new RunCommandTool());

        var toolTimeout = TimeSpan.FromSeconds(_agentConfig.ToolTimeoutSeconds ?? 30);
        var executor = new ToolExecutor(registry, toolTimeout, _logger);

        // 7b：装配 HitlPrompt（enable_hitl: true 时）
        // HitlPrompt 直接用 IConsole 渲染 + ReadKey，不依赖 Live/Channel。
        // enable_hitl: false 时用 NullHitlGate（等价 7a）。
        IHitlGate hitlGate = _enableHitl
            ? new HitlPrompt(_console)
            : new NullHitlGate();

        var batchExecutor = new BatchToolExecutor(executor,
                                                  registry,
                                                  _agentConfig.MaxParallelism ?? 5,
                                                  hitlGate: hitlGate,
                                                  _logger);

        var statusBar = new StatusBar
        {
            Provider = _providerConfig.Name,
            Model = _providerConfig.Model,
            SecurityLevel = _securityLevel,
            ContextWindowTokens = _tuiConfig.ContextWindowTokens ?? 64000,
            ToolCount = registry.GetAll().Count
        };

        // 启动横幅
        _console.WriteMarkupLine($"[grey]ParrotCode.Net[/] [green]TUI 模式[/] | " +
                                 $"provider=[cyan]{Markup.Escape(_providerConfig.Name)}[/] " +
                                 $"model=[cyan]{Markup.Escape(_providerConfig.Model)}[/] " +
                                 $"security=[cyan]{_securityLevel}[/] " +
                                 $"tools=[cyan]{registry.GetAll().Count}[/]");

        while (!_ct.IsCancellationRequested)
        {
            statusBar.EstimatedTokens = history.EstimatedTokens;
            statusBar.CurrentRound = 0;

            // 1. 读输入
            var line = await inputReader.ReadLineWithCompletionAsync(_ct);
            if (line is null) break;

            // 2. 斜杠命令硬编码分发
            if (line is "/exit" or "/quit") break;
            if (line is "/clear")
            {
                history.Clear();
                _console.WriteMarkupLine("[grey]已清空对话历史。[/]");
                continue;
            }
            if (line is "/help")
            {
                RenderHelp();
                continue;
            }
            if (line is "/status")
            {
                _console.Write(statusBar.Render());
                _console.WriteLine();
                continue;
            }
            if (string.IsNullOrWhiteSpace(line)) continue;

            _console.WriteMarkupLine($"[grey]你：[/]{Markup.Escape(line)}");
            history.AddUser(line);

            // 3. 显示状态栏（每轮对话开始时显示一次）
            _console.Write(statusBar.Render());
            _console.WriteLine();

            // 4. 构造 AgentLoop + 事件流
            // 不给 AgentLoop 传 logger——其 info 日志走 stderr，
            // 会与 stdout 在终端屏幕交错，破坏布局。
            var agentLoop = new AgentLoop(_provider,
                                          registry,
                                          batchExecutor,
                                          _agentConfig.MaxRounds ?? 10,
                                          _agentConfig.ToolChoice ?? "auto",
                                          _agentConfig.SystemPrompt,
                                          logger: null);
            var sink = new ChannelEventSink();
            var agentTask = agentLoop.RunAsync(history, sink, _ct);

            // 5. 流式渲染事件
            await RenderStreamingAsync(sink.Reader, statusBar);

            await agentTask;
        }

        _logger?.LogInformation("程序退出");
    }

    /// <summary>
    /// 流式渲染事件流（不用 Live）。
    /// 用 ConsoleEventRenderer 逐事件渲染：
    /// - 文本逐字 Console.Write
    /// - 工具调用/结果用 Panel 输出
    /// - 状态栏轮次在 RoundStart 时更新（但不重绘状态栏 Panel，只在 "── Round N ──" 分隔线中体现）
    /// </summary>
    private async Task RenderStreamingAsync(ChannelReader<AgentEvent> reader, StatusBar statusBar)
    {
        var renderer = new ConsoleEventRenderer(_console);
        await foreach (var evt in reader.ReadAllAsync(_ct))
        {
            if (evt is AgentEvent.RoundStartEvent(var r))
                statusBar.CurrentRound = r;
            renderer.RenderEvent(evt);
        }
    }

    internal static bool IsCompletingEvent(AgentEvent evt) =>
        evt is AgentEvent.AgentDoneEvent
            or AgentEvent.MaxRoundsReachedEvent
            or AgentEvent.ErrorEvent
            or AgentEvent.CancelledEvent;

    private void RenderHelp()
    {
        _console.WriteMarkupLine("[grey]可用命令：[/]");
        _console.WriteMarkupLine("  [cyan]/clear[/]  清空对话历史");
        _console.WriteMarkupLine("  [cyan]/status[/] 显示状态栏");
        _console.WriteMarkupLine("  [cyan]/help[/]   显示帮助");
        _console.WriteMarkupLine("  [cyan]/exit[/]   退出");
    }
}
