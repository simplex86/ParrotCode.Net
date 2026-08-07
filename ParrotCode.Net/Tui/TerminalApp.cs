using Microsoft.Extensions.Logging;
using Terminal.Gui;
using Attribute = Terminal.Gui.Attribute;

namespace ParrotCode;

/// <summary>
/// Terminal.Gui v2 主应用（迭代 7c-3：HITL 模态对话框 + Spinner + 收尾）。
/// 装配三段式布局：顶部状态栏 + 中间对话区 + 底部输入框。
/// 7c-3：enable_hitl=true 时注入 HitlPrompt（模态 Dialog），工具执行时显示 Spinner。
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
    private SpinnerIndicator? _spinner;
    private View? _hitlBar;           // HITL 状态行容器（Label + 4 Button）
    private Label? _hitlLabel;        // HITL 提示文本（左侧）
    private Button[]? _hitlButtons;   // HITL 4 个 Button（A/S/P/D）
    private ToolRegistry? _registry;
    private ConversationHistory? _history;
    private HitlPrompt? _hitlPrompt;

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

        // 3. 构建三段式布局（创建 _spinner, _inputFieldView 等）
        BuildLayout();

        // 4. 装配 HITL（7c-3：内联提示，不用模态 Dialog）
        //    UI 回调引用 _spinner 等，需在 BuildLayout 之后创建
        if (_tuiConfig.EnableHitl ?? true)
            _hitlPrompt = new HitlPrompt(ShowHitlPromptAsync);

        // 5. 注册 AddIdle 状态机——分帧处理输入和事件流，不阻塞事件循环
        Application.AddIdle(IdleCallback);

        // 6. 运行应用（阻塞直到 RequestStop）
        Application.Run(_top!);

        // 7. 清理
        Application.Shutdown();

        return Task.CompletedTask;
    }

    private void BuildLayout()
    {
        // Toplevel 无边框无标题栏
        _top = new Toplevel();

        // 只设置 _top 控件的 ColorScheme 为透明（Attribute.Default 不发送颜色转义码），
        // 让终端原生前景/背景色透出，避免 Terminal.Gui 默认的蓝底。
        // 注意：不覆盖全局 Colors.ColorSchemes["TopLevel"]，确保 Dialog 用默认不透明配色。
        var defaultAttr = Attribute.Default;
        _top.ColorScheme = new ColorScheme(defaultAttr, defaultAttr, defaultAttr, defaultAttr, defaultAttr);

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

        // 中间对话区（内置 ListView 子类，填充剩余，底部留 3 行给状态行+分割线+输入框）
        _chatView = new ChatView
        {
            X = 0,
            Y = Pos.Bottom(sep1),
            Width = Dim.Fill(),
            Height = Dim.Fill(3)  // 底部预留 3 行（Spinner 状态行 + 分割线 + 输入框）
        };
        _chatView.AppendStaticMessage("ParrotCode.Net Terminal 模式（7c-3 HITL + Spinner）");
        _chatView.AppendStaticMessage("输入消息开始对话，/exit 退出，/clear 清空");

        // 7c-3：Spinner 状态行（独立 1 行，不叠加在 ChatView 上，避免覆盖对话内容）
        // 工具执行时显示 "Thinking ⠋" 动画，不执行时 Text 为空（占位但不重绘内容）
        _spinner = new SpinnerIndicator
        {
            X = 0,
            Y = Pos.Bottom(_chatView),  // ChatView 正下方，独立行
            Width = Dim.Fill(),
            Height = 1,
            Visible = false  // 默认隐藏（Visible=false 时 Terminal.Gui 不绘制，但布局占位不变）
        };

        // 7c-3：HITL 状态行（与 Spinner 同一位置，Label 左 + 4 Button 右，初始隐藏）
        // HITL 时隐藏 Spinner，显示此状态行；用户点击 Button 做决策
        _hitlBar = new View
        {
            X = 0,
            Y = Pos.Bottom(_chatView),
            Width = Dim.Fill(),
            Height = 1,
            Visible = false
        };
        _hitlLabel = new Label
        {
            X = 0,
            Y = 0,
            Width = Dim.Fill(36),  // 右侧留 36 列给 4 个 Button
            Height = 1,
            Text = ""
        };
        var btnA = new Button { X = Pos.Right(_hitlLabel), Y = 0, Text = "本次" };
        var btnS = new Button { X = Pos.Right(btnA), Y = 0, Text = "会话" };
        var btnP = new Button { X = Pos.Right(btnS), Y = 0, Text = "永久" };
        var btnD = new Button { X = Pos.Right(btnP), Y = 0, Text = "拒绝" };
        _hitlBar.Add(_hitlLabel, btnA, btnS, btnP, btnD);
        _hitlButtons = new[] { btnA, btnS, btnP, btnD };

        // 分割线：状态行下方、输入框上方（内置 LineView）
        var sep2 = new LineView
        {
            X = 0,
            Y = Pos.Bottom(_spinner),  // Spinner 和 HitlBar 同一 Y，Bottom 相同
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

        _top.Add(_statusBarView, sep1, _chatView, _spinner, _hitlBar, sep2, promptLabel, _inputFieldView);
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

                // 确保 Spinner 停止
                _spinner?.Stop();

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

    /// <summary>
    /// 处理用户输入行。
    /// </summary>
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

    /// <summary>
    /// 启动一轮 AgentLoop，事件流通过 IdleCallback 消费。
    /// </summary>
    private void StartAgentRound()
    {
        var executor = new ToolExecutor(_registry!, TimeSpan.FromSeconds(_agentConfig.ToolTimeoutSeconds ?? 30), _logger);

        // 7c-3：注入 HitlPrompt（如果启用），否则 NullHitlGate
        IHitlGate? hitlGate = _hitlPrompt is null ? new NullHitlGate() : (IHitlGate)_hitlPrompt;

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

    /// <summary>
    /// 处理单个 Agent 事件（在主线程 AddIdle 回调中执行）。
    /// </summary>
    private void ProcessEvent(AgentEvent evt)
    {
        // 更新状态栏轮次
        if (evt is AgentEvent.RoundStartEvent(var r))
            _statusBarView!.CurrentRound = r;

        // 渲染到对话区
        _chatView!.RenderEvent(evt);

        // 7c-3：工具调用时启动 Spinner，结果时停止
        if (evt is AgentEvent.ToolCallStartEvent)
            _spinner?.Start();
        else if (evt is AgentEvent.ToolResultEvent or AgentEvent.ToolBlockedEvent)
            _spinner?.Stop();
    }

    /// <summary>
    /// HITL 内联提示（7c-3：不用模态 Dialog，用状态行 Label + Button）。
    /// 显示 HITL 状态行（Label 左 + 4 Button 右），用户点击 Button 做决策。
    /// HITL 期间禁止输入（InputFieldView.ReadOnly = true）。
    ///
    /// 职责分离：
    /// - Spinner 逻辑独立（Start/Stop 由 ProcessEvent 控制），HITL 不操作 Spinner 的动画逻辑
    /// - HITL 只在 UI 层面临时隐藏 Spinner（Visible=false），避免视觉重叠；决策后恢复
    /// </summary>
    private async Task<HitlDecision> ShowHitlPromptAsync(ToolCall call, CancellationToken ct)
    {
        var tcs = new TaskCompletionSource<HitlDecision>();

        Application.Invoke(() =>
        {
            // UI 层面隐藏 Spinner（不调 Stop，Spinner 动画逻辑独立）
            _spinner!.Visible = false;

            // 显示 HITL 状态行
            _hitlLabel!.Text = $"  即将执行 {call.Name}";
            _hitlBar!.Visible = true;

            // 禁止输入
            _inputFieldView!.ReadOnly = true;

            // 注册 Button 点击事件
            for (int i = 0; i < _hitlButtons!.Length; i++)
            {
                _hitlButtons[i].Accepting += OnHitlButtonClick;
            }

            void OnHitlButtonClick(object? sender, EventArgs e)
            {
                // 移除所有 Button 事件
                foreach (var btn in _hitlButtons!)
                    btn.Accepting -= OnHitlButtonClick;

                // 恢复 UI
                _hitlBar!.Visible = false;
                _spinner!.Visible = true;  // 恢复 Spinner 可见（动画逻辑不受影响）
                _inputFieldView!.ReadOnly = false;
                _inputFieldView.SetFocus();

                // 从 sender 判断决策
                var clickedBtn = (Button)sender!;
                var choice = Array.IndexOf(_hitlButtons, clickedBtn) switch
                {
                    0 => HitlChoice.AllowOnce,
                    1 => HitlChoice.AllowSession,
                    2 => HitlChoice.AllowPermanent,
                    _ => HitlChoice.Deny
                };

                var decision = choice == HitlChoice.Deny
                    ? HitlDecision.Deny("用户拒绝执行")
                    : new HitlDecision(choice);
                tcs.SetResult(decision);
            }
        });

        return await tcs.Task;
    }

    public void Dispose()
    {
        _top?.Dispose();
    }
}
