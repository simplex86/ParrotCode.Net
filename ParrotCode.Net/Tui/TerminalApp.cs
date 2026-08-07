using Microsoft.Extensions.Logging;
using Terminal.Gui;
using Attribute = Terminal.Gui.Attribute;

namespace ParrotCode;

/// <summary>
/// Terminal.Gui v2 主应用（迭代 7c-2：接入 AgentLoop + 事件流 + 流式渲染）。
/// 装配三段式布局：顶部状态栏 + 中间对话区 + 底部输入框。
/// 7c-2：enable_hitl=false（NullHitlGate），所有工具直接执行。
/// </summary>
internal sealed class TerminalApp : IDisposable
{
    private readonly IBaseProvider _provider;
    private readonly ProviderConfig _providerConfig;
    private readonly AgentConfig _agentConfig;
    private readonly TuiConfig _tuiConfig;
    private readonly SecurityLevel _securityLevel;
    private readonly ILogger? _logger;
    private readonly CancellationToken _ct;

    private Toplevel? _top;
    private StatusBarView? _statusBarView;
    private ChatView? _chatView;
    private InputFieldView? _inputFieldView;
    private ToolRegistry? _registry;
    private ConversationHistory? _history;

    // 事件流状态机
    private ChannelEventSink? _sink;
    private Task? _agentTask;

    public TerminalApp(IBaseProvider provider,
                       ProviderConfig providerConfig,
                       AgentConfig? agentConfig,
                       TuiConfig? tuiConfig,
                       SecurityLevel securityLevel,
                       ILogger? logger,
                       CancellationToken ct)
    {
        _provider = provider ?? throw new ArgumentNullException(nameof(provider));
        _providerConfig = providerConfig ?? throw new ArgumentNullException(nameof(providerConfig));
        _agentConfig = agentConfig ?? new AgentConfig();
        _tuiConfig = tuiConfig ?? new TuiConfig();
        _securityLevel = securityLevel;
        _logger = logger;
        _ct = ct;
    }

    public Task RunAsync()
    {
        // 1. 装配工具注册中心
        _registry = new ToolRegistry();
        _registry.Register(new ReadFileTool());
        _registry.Register(new WriteFileTool());
        _registry.Register(new EditFileTool());
        _registry.Register(new GlobTool());
        _registry.Register(new GrepTool());
        _registry.Register(new RunCommandTool());

        _history = new ConversationHistory();

        // 2. Terminal.Gui 初始化（静态 API）
        Application.Init();

        // 用 Attribute.Default 覆盖全局 TopLevel 配色——空属性不发送颜色转义码，
        // 让终端原生前景/背景色透出来，避免 Terminal.Gui 默认的蓝底
        var defaultAttr = Attribute.Default;
        Colors.ColorSchemes["TopLevel"] = new ColorScheme(defaultAttr, defaultAttr, defaultAttr, defaultAttr, defaultAttr);

        // 3. 构建三段式布局
        BuildLayout();

        // 4. 注册 AddIdle 状态机——分帧处理输入和事件流，不阻塞事件循环
        Application.AddIdle(IdleCallback);

        // 5. 运行应用（阻塞直到 RequestStop）
        Application.Run(_top!);

        // 6. 清理
        Application.Shutdown();

        return Task.CompletedTask;
    }

    private void BuildLayout()
    {
        // Toplevel 无边框无标题栏，继承全局 TopLevel 配色（终端原生色）
        _top = new Toplevel();

        // 顶部状态栏（固定 1 行）——内置 Label 子类
        _statusBarView = new StatusBarView
        {
            X = 0,
            Y = 0,
            Width = Dim.Fill(),
            Height = 1
        };
        _statusBarView.Update(_providerConfig, _securityLevel, _tuiConfig, _registry!);

        // 分割线：状态栏下方（内置 LineView）
        var sep1 = new LineView
        {
            X = 0,
            Y = Pos.Bottom(_statusBarView),
            Width = Dim.Fill(),
            Height = 1
        };

        // 中间对话区（内置 ListView 子类，填充剩余，底部留 2 行给分割线+输入框）
        _chatView = new ChatView
        {
            X = 0,
            Y = Pos.Bottom(sep1),
            Width = Dim.Fill(),
            Height = Dim.Fill(2)  // 底部预留 2 行（分割线 + 输入框）
        };
        _chatView.AppendStaticMessage("ParrotCode.Net Terminal 模式（7c-2 事件流接入）");
        _chatView.AppendStaticMessage("输入消息开始对话，/exit 退出，/clear 清空");

        // 分割线：输入框上方（内置 LineView）
        var sep2 = new LineView
        {
            X = 0,
            Y = Pos.Bottom(_chatView),
            Width = Dim.Fill(),
            Height = 1
        };

        // 底部输入提示符 ">"（内置 Label，固定 1 行，始终可见）
        var promptLabel = new Label
        {
            X = 0,
            Y = Pos.Bottom(sep2),
            Width = 2,   // "> " 占 2 列
            Height = 1,
            Text = "> "
        };

        // 底部输入框（内置 TextField 子类，贴在提示符右侧，固定 1 行）
        _inputFieldView = new InputFieldView
        {
            X = Pos.Right(promptLabel),
            Y = Pos.Bottom(sep2),
            Width = Dim.Fill(),
            Height = 1
        };
        _inputFieldView.ExitRequested += () => Application.RequestStop(_top!);

        _top.Add(_statusBarView, sep1, _chatView, sep2, promptLabel, _inputFieldView);
        _inputFieldView.SetFocus();
    }

    /// <summary>
    /// AddIdle 回调：状态机分帧处理输入和 Agent 事件流，不阻塞事件循环。
    /// 返回 true 保持 Idle 活跃。
    /// </summary>
    private bool IdleCallback()
    {
        // 优先消费 Agent 事件流（每帧批量处理最多 20 个事件）
        if (_sink is not null && _agentTask is not null)
        {
            for (int i = 0; i < 20; i++)
            {
                if (!_sink.Reader.TryRead(out var evt))
                    break;  // 暂无事件
                ProcessEvent(evt);
            }

            // Agent 任务完成后清理状态
            if (_agentTask.IsCompleted)
            {
                // 消费剩余事件
                while (_sink.Reader.TryRead(out var evt))
                    ProcessEvent(evt);

                // 更新状态栏 token 估算
                _statusBarView!.EstimatedTokens = _history!.EstimatedTokens;

                _sink = null;
                _agentTask = null;
            }
        }

        // 轮询输入 Channel
        if (_inputFieldView!.Submits.TryRead(out var line))
        {
            HandleUserInput(line);
        }

        return true;  // 保持 Idle
    }

    /// <summary>处理用户输入行。</summary>
    private void HandleUserInput(string line)
    {
        // 斜杠命令硬编码分发
        if (line is "/exit" or "/quit")
        {
            Application.RequestStop(_top!);
            return;
        }
        if (line is "/clear")
        {
            _chatView!.ClearMessages();
            _history!.Clear();
            return;
        }
        if (line is "/help")
        {
            _chatView!.AppendStaticMessage("可用命令：/clear /exit /help");
            return;
        }
        if (string.IsNullOrWhiteSpace(line)) return;

        // Agent 正在运行时忽略新输入
        if (_agentTask is not null && !_agentTask.IsCompleted)
            return;

        // 显示用户消息
        _chatView!.AppendUserMessage(line);
        _history!.AddUser(line);

        // 更新状态栏
        _statusBarView!.CurrentRound = 0;
        _statusBarView.EstimatedTokens = _history.EstimatedTokens;

        // 启动 AgentLoop
        StartAgentRound();
    }

    /// <summary>启动一轮 AgentLoop，事件流通过 IdleCallback 消费。</summary>
    private void StartAgentRound()
    {
        // 7c-2：enable_hitl=false，注入 NullHitlGate
        var executor = new ToolExecutor(_registry!, TimeSpan.FromSeconds(_agentConfig.ToolTimeoutSeconds ?? 30), _logger);
        var hitlGate = new NullHitlGate();  // 7c-2 不启用 HITL
        var batchExecutor = new BatchToolExecutor(executor, _registry!,
                                                   _agentConfig.MaxParallelism ?? 5,
                                                   hitlGate: hitlGate,
                                                   _logger);

        _sink = new ChannelEventSink();
        var agentLoop = new AgentLoop(_provider,
                                      _registry!,
                                      batchExecutor,
                                      _agentConfig.MaxRounds ?? 10,
                                      _agentConfig.ToolChoice ?? "auto",
                                      _agentConfig.SystemPrompt,
                                      logger: null);  // 不给 logger，避免 stderr 交错

        _agentTask = agentLoop.RunAsync(_history!, _sink, _ct);
    }

    /// <summary>处理单个 Agent 事件（在主线程 AddIdle 回调中执行）。</summary>
    private void ProcessEvent(AgentEvent evt)
    {
        // 更新状态栏轮次
        if (evt is AgentEvent.RoundStartEvent(var r))
            _statusBarView!.CurrentRound = r;

        // 渲染到对话区
        _chatView!.RenderEvent(evt);
    }

    public void Dispose()
    {
        _top?.Dispose();
    }
}
