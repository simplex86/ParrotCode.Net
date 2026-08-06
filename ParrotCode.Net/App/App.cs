using System.Text;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Spectre.Console;

namespace ParrotCode;

/// <summary>
/// 主循环：读输入 → 调 AgentLoop（ReAct）→ 消费事件流打印。
/// 维护 ConversationHistory 实现多轮上下文；/clear 清空历史。
/// 迭代 6：从迭代 3/4 的纯文本流式切换到 AgentLoop + 工具调用 + 事件流。
/// </summary>
internal sealed class App
{
    private readonly IBaseProvider _provider;
    private readonly ProviderConfig _providerConfig;
    private readonly AgentConfig _agentConfig;
    private readonly ILogger _logger;
    private readonly CancellationToken _ct;

    public App(
        IBaseProvider provider,
        ProviderConfig providerConfig,
        AgentConfig? agentConfig,
        ILogger logger,
        CancellationToken ct)
    {
        _provider = provider ?? throw new ArgumentNullException(nameof(provider));
        _providerConfig = providerConfig ?? throw new ArgumentNullException(nameof(providerConfig));
        _agentConfig = agentConfig ?? new AgentConfig();
        _logger = logger;
        _ct = ct;
    }

    public async Task RunAsync()
    {
        var history = new ConversationHistory();

        // 装配工具注册中心
        var registry = new ToolRegistry();
        registry.Register(new ReadFileTool());
        registry.Register(new WriteFileTool());
        registry.Register(new EditFileTool());
        registry.Register(new GlobTool());
        registry.Register(new GrepTool());
        registry.Register(new RunCommandTool());

        var toolTimeout = TimeSpan.FromSeconds(_agentConfig.ToolTimeoutSeconds ?? 30);
        var executor = new ToolExecutor(registry, toolTimeout, _logger);
        var batchExecutor = new BatchToolExecutor(
            executor,
            registry,
            _agentConfig.MaxParallelism ?? 5,
            _logger);

        AnsiConsole.MarkupLine(
            $"[grey]ParrotCode.Net[/] {(providerIsMock(_providerConfig)
                ? "[green]mock 模式[/]"
                : "[green]stream 模式[/]")} | " +
            $"provider=[cyan]{Markup.Escape(_providerConfig.Name)}[/] " +
            $"model=[cyan]{Markup.Escape(_providerConfig.Model)}[/] " +
            $"protocol=[cyan]{Markup.Escape(_providerConfig.Protocol)}[/] " +
            $"tools=[cyan]{registry.GetAll().Count}[/]");

        while (!_ct.IsCancellationRequested)
        {
            AnsiConsole.Markup("[bold blue]> [/]");
            var line = Console.ReadLine();
            if (line is null) break; // EOF（Ctrl+Z / 管道关闭）
            if (line is "exit" or "quit") break;

            if (line is "/clear")
            {
                history.Clear();
                AnsiConsole.MarkupLine("[grey]已清空对话历史。[/]");
                continue;
            }

            if (string.IsNullOrWhiteSpace(line)) continue;

            AnsiConsole.MarkupLine($"[grey]你：[/]{Markup.Escape(line)}");
            _logger.LogInformation("用户输入，长度 {Len}", line.Length);

            history.AddUser(line);

            // 构造 AgentLoop（每轮新建——maxRounds/toolChoice 可配置，且无跨用户输入的状态）
            var agentLoop = new AgentLoop(_provider,
                                          registry,
                                          batchExecutor,
                                          _agentConfig.MaxRounds ?? 10,
                                          _agentConfig.ToolChoice ?? "auto",
                                          _agentConfig.SystemPrompt,
                                          _logger);

            var sink = new ChannelEventSink();
            var agentTask = agentLoop.RunAsync(history, sink, _ct);

            // 消费事件流并渲染
            await RenderEventsAsync(sink.Reader);

            await agentTask;
        }

        _logger.LogInformation("程序退出");
    }

    /// <summary>
    /// 消费 ChannelReader 事件流并渲染到控制台。
    /// 每个事件类型对应不同的渲染样式，保持迭代 3/4 的视觉风格。
    /// </summary>
    private async Task RenderEventsAsync(ChannelReader<AgentEvent> reader)
    {
        AnsiConsole.Markup("[green]AI：[/]");
        await foreach (var evt in reader.ReadAllAsync(_ct))
        {
            switch (evt)
            {
                case AgentEvent.TextDeltaEvent(var text):
                    Console.Write(text);
                    break;
                case AgentEvent.ToolCallStartEvent(var call):
                    Console.WriteLine();
                    AnsiConsole.MarkupLine(
                        $"[cyan]→[/] {Markup.Escape(call.Name)}({Markup.Escape(call.Input.GetRawText())})");
                    break;
                case AgentEvent.ToolResultEvent(_, var result):
                    if (result.Success)
                        AnsiConsole.MarkupLine($"[green]✓[/] {Markup.Escape(Truncate(result.Content, 80))}");
                    else
                        AnsiConsole.MarkupLine($"[red]✗[/] {Markup.Escape(result.Error ?? "未知错误")}");
                    break;
                case AgentEvent.AssistantMessageEvent:
                    // 文本已在 TextDelta 中实时打印；AssistantMessage 供未来 TUI 整段刷新用
                    break;
                case AgentEvent.AgentDoneEvent:
                    Console.WriteLine();  // 回复结束换行
                    break;
                case AgentEvent.MaxRoundsReachedEvent(var rounds):
                    Console.WriteLine();
                    AnsiConsole.MarkupLine($"[yellow]⚠ 已达最大轮次 {rounds}[/]");
                    break;
                case AgentEvent.WarningEvent(var msg):
                    AnsiConsole.MarkupLine($"[yellow]⚠[/] {Markup.Escape(msg)}");
                    break;
                case AgentEvent.ErrorEvent(var msg, _):
                    Console.WriteLine();
                    AnsiConsole.MarkupLine($"[red]✗ 错误：[/]{Markup.Escape(msg)}");
                    break;
                case AgentEvent.CancelledEvent:
                    AnsiConsole.MarkupLine("\n[grey]已取消。[/]");
                    break;
                case AgentEvent.RoundStartEvent:
                case AgentEvent.RoundEndEvent:
                case AgentEvent.ToolBlockedEvent:
                    // 本迭代不渲染这些事件
                    break;
            }
        }
    }

    private static bool providerIsMock(ProviderConfig config) => config.Protocol == "mock";

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..(max - 3)] + "...";
}
