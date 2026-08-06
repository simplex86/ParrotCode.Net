using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace ParrotCode;

/// <summary>
/// 主 TUI 应用（7a 简化版，无 HITL 接线）。
/// 装配 Live + 状态栏 + 事件消费 + 输入循环。
/// 替代迭代 6 App.cs 的内联渲染。降级时由 App 用 ConsoleEventRenderer 替代。
///
/// 7a 的 BatchToolExecutor 不注入 IHitlGate（等价迭代 6 行为，所有工具直接执行）。
/// 7b 接入时：在此处加 HitlPrompt 装配 + render/readKey 回调注入 EventRenderer.SetTransient。
///
/// Live 不可用（重定向/CI/测试）时降级到 ConsoleEventRenderer + StatusBar 更新。
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
    private readonly bool _useLive;

    public TuiApp(IBaseProvider provider,
                  ProviderConfig providerConfig,
                  AgentConfig? agentConfig,
                  TuiConfig? tuiConfig,
                  SecurityLevel securityLevel,
                  ILogger? logger,
                  CancellationToken ct,
                  IConsole? console = null,
                  bool useLive = true)
    {
        _provider = provider ?? throw new ArgumentNullException(nameof(provider));
        _providerConfig = providerConfig ?? throw new ArgumentNullException(nameof(providerConfig));
        _agentConfig = agentConfig ?? new AgentConfig();
        _tuiConfig = tuiConfig ?? new TuiConfig();
        _securityLevel = securityLevel;
        _logger = logger;
        _ct = ct;
        _console = console ?? new SystemConsole();
        // useLive=true 时还需终端支持交互；测试/CI 强制 useLive=false
        _useLive = useLive && ShouldUseLive();
    }

    private static bool ShouldUseLive() => !Console.IsOutputRedirected && Environment.UserInteractive;

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

        // 7a：不注入 IHitlGate（等价迭代 6 行为）
        var batchExecutor = new BatchToolExecutor(executor,
                                                  registry,
                                                  _agentConfig.MaxParallelism ?? 5,
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
        _console.WriteMarkupLine($"[grey]ParrotCode.Net[/] [green]{(_useLive ? "TUI" : "console")} 模式[/] | " +
                                 $"provider=[cyan]{Markup.Escape(_providerConfig.Name)}[/] " +
                                 $"model=[cyan]{Markup.Escape(_providerConfig.Model)}[/] " +
                                 $"security=[cyan]{_securityLevel}[/] " +
                                 $"tools=[cyan]{registry.GetAll().Count}[/]");

        while (!_ct.IsCancellationRequested)
        {
            statusBar.EstimatedTokens = history.EstimatedTokens;
            statusBar.CurrentRound = 0;

            // 1. 读输入（不在 Live 期间）
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

            // 3. 构造 AgentLoop + 事件流（迭代 6 不变）
            // Live 模式下不给 AgentLoop 传 logger——其 info 日志走 stderr，
            // 会与 Live 的 stdout 在终端屏幕交错，卡在 Live 区域中间破坏布局。
            // 降级模式（console）保留 logger 便于调试。
            var agentLogger = _useLive ? null : _logger;
            var agentLoop = new AgentLoop(_provider, 
                                          registry, 
                                          batchExecutor,
                                          _agentConfig.MaxRounds ?? 10, 
                                          _agentConfig.ToolChoice ?? "auto",
                                          _agentConfig.SystemPrompt, 
                                          agentLogger);
            var sink = new ChannelEventSink();
            var agentTask = agentLoop.RunAsync(history, sink, _ct);

            // 4. 事件渲染（Live 或降级）
            await RenderEventsAsync(sink.Reader, statusBar);

            await agentTask;
        }

        _logger?.LogInformation("程序退出");
    }

    private async Task RenderEventsAsync(ChannelReader<AgentEvent> reader, StatusBar statusBar)
    {
        if (_useLive)
            await RenderWithLiveAsync(reader, statusBar);
        else
            await RenderDegradedAsync(reader, statusBar);
    }

    /// <summary>
    /// Live 流式渲染活跃区（状态栏 + 文本 + 工具卡片）。
    /// 流式期间持续 UpdateTarget 刷新；事件流结束后 Live 自然退出，最后一帧作为本轮输出保留在屏幕上。
    /// 不在 Live 期间调 AnsiConsole.Write——会与 ANSI 重绘序列交错导致字节混乱。
    /// 不在完成事件后缩小活跃区——会导致旧内容行变成空白。
    /// 初始 target 用状态栏而非空 Text——避免从1行扩展到多行时的 ANSI 重绘残留导致双状态栏。
    /// </summary>
    private async Task RenderWithLiveAsync(ChannelReader<AgentEvent> reader, StatusBar statusBar)
    {
        var renderer = new EventRenderer();

        await AnsiConsole.Live(statusBar.Render()).StartAsync(async ctx =>
        {
            await foreach (var evt in reader.ReadAllAsync(_ct))
            {
                // 更新状态栏轮次
                if (evt is AgentEvent.RoundStartEvent(var r))
                    statusBar.CurrentRound = r;

                renderer.Render(evt);
                ctx.UpdateTarget(renderer.BuildActive(statusBar));
            }
        });
    }

    /// <summary>
    /// 降级行模式渲染（不用 Live）。
    /// 读事件，更新 StatusBar，调 ConsoleEventRenderer 逐事件渲染。
    /// </summary>
    private async Task RenderDegradedAsync(ChannelReader<AgentEvent> reader, StatusBar statusBar)
    {
        var consoleRenderer = new ConsoleEventRenderer(_console);
        consoleRenderer.WritePrefix();
        await foreach (var evt in reader.ReadAllAsync(_ct))
        {
            if (evt is AgentEvent.RoundStartEvent(var r))
                statusBar.CurrentRound = r;
            consoleRenderer.RenderEvent(evt);
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
