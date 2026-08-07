using Microsoft.Extensions.Logging;
using Terminal.Gui;

namespace ParrotCode;

/// <summary>
/// Terminal.Gui v2 主应用（迭代 7c-1：骨架 + 静态布局）。
/// 装配三段式布局：顶部状态栏 + 中间对话区 + 底部输入框。
/// 本迭代不接 AgentLoop——输入框只回显，不触发 Agent。
/// </summary>
internal sealed class TerminalApp : IDisposable
{
    private readonly ProviderConfig _providerConfig;
    private readonly AgentConfig _agentConfig;
    private readonly TuiConfig _tuiConfig;
    private readonly SecurityLevel _securityLevel;
    private readonly ILogger? _logger;
    private readonly CancellationToken _ct;

    private Window? _top;
    private StatusBarView? _statusBarView;
    private ChatView? _chatView;
    private InputFieldView? _inputFieldView;

    // 7c-1 不接 Agent，但保留字段供 7c-2 使用
    private ToolRegistry? _registry;

    public TerminalApp(ProviderConfig providerConfig,
                       AgentConfig? agentConfig,
                       TuiConfig? tuiConfig,
                       SecurityLevel securityLevel,
                       ILogger? logger,
                       CancellationToken ct)
    {
        _providerConfig = providerConfig ?? throw new ArgumentNullException(nameof(providerConfig));
        _agentConfig = agentConfig ?? new AgentConfig();
        _tuiConfig = tuiConfig ?? new TuiConfig();
        _securityLevel = securityLevel;
        _logger = logger;
        _ct = ct;
    }

    public Task RunAsync()
    {
        // 1. 装配工具注册中心（供状态栏显示 toolCount，7c-2 才真正使用）
        _registry = new ToolRegistry();
        _registry.Register(new ReadFileTool());
        _registry.Register(new WriteFileTool());
        _registry.Register(new EditFileTool());
        _registry.Register(new GlobTool());
        _registry.Register(new GrepTool());
        _registry.Register(new RunCommandTool());

        // 2. Terminal.Gui 初始化（静态 API）
        Application.Init();

        // 3. 构建三段式布局
        BuildLayout();

        // 4. 运行应用（阻塞直到 RequestStop）
        Application.Run(_top!);

        // 5. 清理
        Application.Shutdown();

        return Task.CompletedTask;
    }

    private void BuildLayout()
    {
        _top = new Window { Title = "ParrotCode.Net" };

        // 顶部状态栏（固定 1 行）
        _statusBarView = new StatusBarView
        {
            X = 0,
            Y = 0,
            Width = Dim.Fill(),
            Height = 1
        };
        _statusBarView.Update(_providerConfig, _securityLevel, _tuiConfig, _registry!);

        // 中间对话区（填充剩余，底部留 1 行给输入框）
        _chatView = new ChatView
        {
            X = 0,
            Y = Pos.Bottom(_statusBarView),  // = 1
            Width = Dim.Fill(),
            Height = Dim.Fill(1)  // 底部预留 1 行
        };
        // 7c-1：放静态占位内容
        _chatView.AppendStaticMessage("ParrotCode.Net Terminal 模式（7c-1 骨架）");
        _chatView.AppendStaticMessage("输入框仅回显，不接 Agent（7c-2 接入）");

        // 底部输入框（固定 1 行）
        _inputFieldView = new InputFieldView
        {
            X = 0,
            Y = Pos.Bottom(_chatView),  // 贴在对话区下方
            Width = Dim.Fill(),
            Height = 1
        };
        _inputFieldView.Submit += OnInputSubmit;
        _inputFieldView.ExitRequested += () => Application.RequestStop(_top!);

        _top.Add(_statusBarView, _chatView, _inputFieldView);

        // 确保输入框获得焦点
        _inputFieldView.SetFocus();
    }

    /// <summary>
    /// 7c-1 输入提交处理：在主线程同步处理输入。
    /// 7c-1 只回显，不接 AgentLoop。
    /// </summary>
    private void OnInputSubmit(string line)
    {
        // 斜杠命令硬编码分发（保留 7a 的退出逻辑）
        if (line is "/exit" or "/quit")
        {
            Application.RequestStop(_top!);
            return;
        }
        if (line is "/clear")
        {
            _chatView!.ClearMessages();
            return;
        }

        // 7c-1：只回显，不接 Agent
        _chatView!.AppendStaticMessage($"❯ {line}");
        _chatView.AppendStaticMessage("⏺ （7c-1 骨架：Agent 未接入，输入仅回显）");
    }

    public void Dispose()
    {
        _top?.Dispose();
    }
}
