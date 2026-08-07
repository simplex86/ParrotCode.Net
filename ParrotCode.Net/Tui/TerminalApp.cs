using Microsoft.Extensions.Logging;
using Terminal.Gui;
using Attribute = Terminal.Gui.Attribute;

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

    private Toplevel? _top;
    private StatusBarView? _statusBarView;
    private ChatView? _chatView;
    private InputFieldView? _inputFieldView;
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

        // 用 Attribute.Default 覆盖全局 TopLevel 配色——空属性不发送颜色转义码，
        // 让终端原生前景/背景色透出来，避免 Terminal.Gui 默认的蓝底
        var defaultAttr = Attribute.Default;
        Colors.ColorSchemes["TopLevel"] = new ColorScheme(defaultAttr, defaultAttr, defaultAttr, defaultAttr, defaultAttr);

        // 3. 构建三段式布局
        BuildLayout();

        // 4. 注册 AddIdle 状态机——分帧处理输入，不阻塞事件循环
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
        // 7c-1：放静态占位内容
        _chatView.AppendStaticMessage("ParrotCode.Net Terminal 模式（7c-1 骨架）");
        _chatView.AppendStaticMessage("输入框仅回显，不接 Agent（7c-2 接入）");
        _chatView.AppendStaticMessage("输入 /exit 退出，/clear 清空对话区");

        // 分割线：输入框上方（内置 LineView）
        var sep2 = new LineView
        {
            X = 0,
            Y = Pos.Bottom(_chatView),
            Width = Dim.Fill(),
            Height = 1
        };

        // 底部输入框（内置 TextField 子类，固定 1 行）
        _inputFieldView = new InputFieldView
        {
            X = 0,
            Y = Pos.Bottom(sep2),
            Width = Dim.Fill(),
            Height = 1
        };
        _inputFieldView.ExitRequested += () => Application.RequestStop(_top!);

        _top.Add(_statusBarView, sep1, _chatView, sep2, _inputFieldView);
        _inputFieldView.SetFocus();
    }

    /// <summary>
    /// AddIdle 回调：轮询输入 Channel，不阻塞事件循环。
    /// 返回 true 保持 Idle 活跃，false 停止（应用退出时）。
    /// </summary>
    private bool IdleCallback()
    {
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
            return;
        }
        if (line is "/help")
        {
            _chatView!.AppendStaticMessage("可用命令：/clear /exit /help");
            return;
        }
        if (string.IsNullOrWhiteSpace(line)) return;

        // 7c-1：只回显，不接 Agent
        _chatView!.AppendStaticMessage($"❯ {line}");
        _chatView.AppendStaticMessage("⏺ （7c-1 骨架：Agent 未接入，输入仅回显）");
    }

    public void Dispose()
    {
        _top?.Dispose();
    }
}
