# 迭代 7c：TUI 库迁移（Spectre.Console → Terminal.Gui v2）

> **状态**：[设计完成，待实现]
> **前置迭代**：7a [已完成]、7b [已完成]
> **后续迭代**：8（安全纵深防御）
> **目标**：用 Terminal.Gui v2 重写 TUI 层，实现 Claude Code 风格的固定布局（顶部状态栏 + 中间滚动对话区 + 底部输入框），彻底解决 Spectre.Console Live 的所有残影问题。

---

## 一、迭代目标

### 1.1 核心目标

把 7a/7b 基于 Spectre.Console 的 TUI 层**迁移到 Terminal.Gui v2**，实现 Claude Code 风格的终端 UI：

```
┌─────────────────────────────────────────────────────────────────┐
│  ParrotCode.Net | provider=deepseek model=deepseek-chat | ...    │  ← 固定顶部状态栏（1 行）
├─────────────────────────────────────────────────────────────────┤
│  ❯ 写一份《赠汪伦》的赏析                                         │  ← 用户消息
│  ⏺ 我来写一份赏析并保存。                                         │  ← 助手回复
│    ⎿ → write_file({"path": "d:/zwl.md", ...})                    │  ← 工具调用
│    ⎿ ✓ 已写入 2975 字节                                          │  ← 工具结果
│  ⏺ 已为你写好赏析并保存到 d:/zwl.md。                            │  ← 助手总结
│  ...                                                             │  ← 中间滚动对话区（可滚动）
├─────────────────────────────────────────────────────────────────┤
│  > _                                                             │  ← 固定底部输入框（1 行）
└─────────────────────────────────────────────────────────────────┘
```

### 1.2 必须解决的 7a/7b 遗留问题

| 问题 | 7a/7b 现状 | 7c 目标 |
|------|-----------|---------|
| 状态栏随内容滚动消失 | 每轮显示一次，滚屏后不见 | **固定顶部，始终可见** |
| 输入框非固定底部 | 临时读取，位置随内容变化 | **固定底部，始终可见** |
| Live 残影（off-by-one/跨轮/滚屏） | 方案 A 流式渲染已规避 | **彻底不存在**（不用 Live） |
| 流式文本原地增长 | 方案 A 追加输出，无法原地 | **对话区内追加，自动滚到底** |
| 多轮对话跨轮残影 | 方案 A 已规避 | **彻底不存在** |

### 1.3 非目标（明确不做）

- ❌ 不改 Agent 层（AgentLoop / BatchToolExecutor / ToolRegistry / Provider / Config / History）
- ❌ 不改 AgentEvent 事件流结构（Channel<AgentEvent> 不变）
- ❌ 不改 IHitlGate 接口（只换实现）
- ❌ 不实现 Claude Code 的全部高级特性（多行输入、Vim 模式、@路径补全、虚拟滚动、主题系统）——这些留给后续迭代
- ❌ 不引入斜杠命令系统（迭代 10）

---

## 二、Claude Code UI 特征参考

迁移设计参照 Claude Code 的核心 UI 特征（研究结论）：

### 2.1 布局

| 区域 | 位置 | 内容 |
|------|------|------|
| 状态栏 | 底部（Claude Code 默认）或顶部 | model/context/cwd/cost/权限模式 |
| 对话区 | 中间（占主体高度） | 5 种消息类型，虚拟滚动 |
| 输入框 | 底部（状态栏上方） | 多行输入 + Tab 补全 + 历史导航 |

> **ParrotCode.Net 调整**：状态栏放**顶部**（与 7a StatusBar 位置一致，用户已习惯），输入框放**底部**，对话区占中间。

### 2.2 消息类型视觉标记

| 类型 | Claude Code 标记 | ParrotCode.Net 采用 |
|------|-----------------|-------------------|
| 用户消息 | `❯` 前缀 | ✅ `❯` |
| 助手回复 | `⏺`（圆点）+ 2 空格缩进 | ✅ `⏺` |
| 工具调用 | `⎿` 树状连接符 | ✅ `⎿` |
| 工具结果 | `⎿` 树状连接符 | ✅ `⎿` |
| 系统/错误 | 独立样式 | ✅ 黄色/红色 |

### 2.3 HITL 交互

Claude Code 用**模态对话框**（编号选项菜单 + 箭头导航）。7c 采用**简化版**：对话区内联渲染 HITL 提示 + A/S/P/D 按键（保留 7b 的四键映射，不引入箭头导航，避免过度设计）。

### 2.4 流式输出

- 文本逐 token 追加到对话区
- 工具执行时显示 spinner 动画（`⠋⠙⠹⠸⠼⠴⠦⠧⠇⠏` 盲文点）
- 新内容自动滚动到可视区

---

## 三、架构设计

### 3.1 分层与不变量

```
┌─────────────────────────────────────────────────────┐
│  TUI 层（7c 重写）                                    │  ← 本迭代改动范围
│  Terminal.Gui v2 Views + 事件消费 + 输入循环           │
├─────────────────────────────────────────────────────┤
│  抽象接口（不变）                                     │  ← 7a/7b 已建，保持不变
│  IHitlGate / IConsole（可选保留）                     │
├─────────────────────────────────────────────────────┤
│  Agent 层（零改动）                                   │  ← 迭代 6 成果
│  AgentLoop / BatchToolExecutor / ToolRegistry        │
│  AgentEvent / IAgentEventSink / ChannelEventSink     │
├─────────────────────────────────────────────────────┤
│  基础设施（零改动）                                   │  ← 迭代 1-5 成果
│  Provider / Config / History / Tools                 │
└─────────────────────────────────────────────────────┘
```

**核心不变量**：
- `IHitlGate` 接口签名不变（`RequestAsync` / `IsAllowedThisSession`）
- `AgentEvent` 事件类型不变（12 种）
- `Channel<AgentEvent>` 事件流传输不变
- `BatchToolExecutor` 的 `IHitlGate` 注入方式不变
- `ToolRegistry` / `ToolExecutor` / `ToolCall` / `ToolResult` 不变

### 3.2 新增依赖

```xml
<!-- ParrotCode.Net.csproj -->
<PackageReference Include="Terminal.Gui" Version="2.0.0" />
<!-- Spectre.Console 保留（仅用于 Panel/Markup 的过渡期，7c 结束后可移除） -->
```

> **决策**：7c 期间 Spectre.Console 不立即移除，避免一次性删太多。7c 验收后单独清理。

### 3.3 文件改动清单

#### 新增文件（7 个）

```
Tui/
├── TerminalApp.cs           # Terminal.Gui 主应用（替代 TuiApp）
├── ChatView.cs               # 对话区视图（滚动 + 消息渲染）
├── ChatMessage.cs            # 单条消息数据模型（类型 + 内容 + 颜色）
├── StatusBarView.cs          # 顶部状态栏视图（替代 StatusBar.cs 的 Panel 渲染）
├── InputFieldView.cs         # 底部输入框视图（替代 InputReader，支持 Tab 补全）
├── HitlDialog.cs             # HITL 确认对话框（模态，替代 HitlPrompt）
└── SpinnerIndicator.cs       # 盲文点 spinner 动画（工具执行时）
```

#### 修改文件（3 个）

```
Tui/
├── HitlPrompt.cs             # 改为委托 HitlDialog 的薄封装（保持 IHitlGate 实现）
└── IConsole.cs               # 移除 IRenderable 相关方法，简化为纯 I/O（或保留兼容）
App/
└── App.cs                    # 装配 TerminalApp 替代 TuiApp
ParrotCode.Net.csproj          # 新增 Terminal.Gui 包引用
```

#### 删除文件（4 个）

```
Tui/
├── TuiApp.cs                 # 被 TerminalApp 替代
├── StatusBar.cs              # 被 StatusBarView 替代
├── InputReader.cs            # 被 InputFieldView 替代
└── ConsoleEventRenderer.cs   # 被 ChatView 内部渲染替代
```

#### 保留文件（3 个，不动）

```
Tui/
├── IHitlGate.cs              # 接口不变（含 NullHitlGate）
├── HitlDecision.cs           # 数据模型不变
└── TuiConfig.cs             # 配置不变（如保留；若在 Models.cs 中则不动）
```

---

## 四、详细设计

### 4.1 TerminalApp——主应用

**职责**：装配 Terminal.Gui v2 应用生命周期 + 三段式布局 + 事件循环。

```csharp
internal sealed class TerminalApp : IDisposable
{
    private readonly IBaseProvider _provider;
    private readonly ProviderConfig _providerConfig;
    private readonly AgentConfig _agentConfig;
    private readonly TuiConfig _tuiConfig;
    private readonly SecurityLevel _securityLevel;
    private readonly ILogger? _logger;
    private readonly CancellationToken _ct;

    // Terminal.Gui 视图
    private IApplication? _app;
    private Window? _top;
    private StatusBarView? _statusBarView;
    private ChatView? _chatView;
    private InputFieldView? _inputFieldView;

    // Agent 装配
    private ToolRegistry _registry = null!;
    private BatchToolExecutor _batchExecutor = null!;
    private HitlPrompt _hitlPrompt = null!;  // IHitlGate 实现
    private ConversationHistory _history = null!;

    public TerminalApp(/* 与 TuiApp 相同的构造参数 */);

    public async Task RunAsync()
    {
        // 1. 装配 Agent 层（与 7b 一致）
        AssembleAgentLayer();

        // 2. Terminal.Gui 必须在主线程初始化
        _app = Application.Create();
        _app.Init();

        // 3. 构建三段式布局
        BuildLayout();

        // 4. 启动输入循环（在事件循环中运行）
        // Terminal.Gui 的事件循环是阻塞的，用 MainLoop.Invoke 启动异步输入处理
        _app.MainLoop.Invoke(async () => await InputLoopAsync());

        // 5. 运行应用（阻塞直到 RequestStop）
        _app.Run(_top!);
    }

    private void BuildLayout()
    {
        _top = new Window { Title = "ParrotCode.Net" };

        // 顶部状态栏（固定 1 行）
        _statusBarView = new StatusBarView { X = 0, Y = 0, Width = Dim.Fill(), Height = 1 };
        _statusBarView.Update(_providerConfig, _securityLevel, _tuiConfig, _registry);

        // 中间对话区（填充剩余，底部留 1 行给输入框）
        _chatView = new ChatView
        {
            X = 0,
            Y = Pos.Bottom(_statusBarView),  // = 1
            Width = Dim.Fill(),
            Height = Dim.Fill(1)  // 底部预留 1 行
        };

        // 底部输入框（固定 1 行）
        _inputFieldView = new InputFieldView
        {
            X = 0,
            Y = Pos.Bottom(_chatView),  // 贴在对话区下方
            Width = Dim.Fill(),
            Height = 1
        };
        _inputFieldView.Submit += OnInputSubmit;  // Enter 提交
        _inputFieldView.ExitRequested += () => _app?.RequestStop();

        _top.Add(_statusBarView, _chatView, _inputFieldView);
    }

    private async Task InputLoopAsync()
    {
        while (!_ct.IsCancellationRequested)
        {
            // 等待用户提交输入（InputFieldView 通过 channel 通知）
            var line = await _inputFieldView.WaitForSubmitAsync(_ct);
            if (line is null) break;

            // 斜杠命令分发（保留 7a 逻辑）
            if (HandleCommand(line)) continue;

            // 显示用户消息
            _chatView.AppendUserMessage(line);
            _history.AddUser(line);

            // 更新状态栏
            _statusBarView!.CurrentRound = 0;
            _statusBarView.EstimatedTokens = _history.EstimatedTokens;

            // 启动 AgentLoop
            await RunAgentRoundAsync();
        }
        _app?.RequestStop();
    }

    private async Task RunAgentRoundAsync()
    {
        var sink = new ChannelEventSink();
        var agentLoop = new AgentLoop(_provider, _registry, _batchExecutor,
                                       _agentConfig.MaxRounds ?? 10,
                                       _agentConfig.ToolChoice ?? "auto",
                                       _agentConfig.SystemPrompt,
                                       logger: null);
        var agentTask = agentLoop.RunAsync(_history, sink, _ct);

        // 消费事件流，通过 App.Invoke 调度到主线程更新 UI
        await foreach (var evt in sink.Reader.ReadAllAsync(_ct))
        {
            _app!.Invoke(() =>
            {
                _statusBarView!.CurrentRound = evt is AgentEvent.RoundStartEvent(var r) ? r : _statusBarView.CurrentRound;
                _chatView!.RenderEvent(evt);
            });
        }

        await agentTask;
    }
}
```

**关键设计点**：
- **事件循环集成**：Terminal.Gui 的 `Application.Run` 阻塞主线程。异步逻辑用 `MainLoop.Invoke` 调度到事件循环中执行。
- **跨线程 UI 更新**：AgentLoop 在后台线程跑，事件流读取在主线程的 Invoke 回调中更新 UI。
- **不阻塞事件循环**：`await foreach` 在 Invoke 内会阻塞事件循环，需要用 `MainLoop.AddIdle` 分帧处理（见 4.8 线程模型）。

### 4.2 ChatView——对话区

**职责**：渲染消息历史 + 流式追加 + 自动滚动。

```csharp
internal sealed class ChatView : View
{
    private readonly List<ChatMessage> _messages = new();
    private readonly StringBuilder _currentText = new();  // 当前流式文本缓冲
    private bool _hasTextOnLine;

    public ChatView()
    {
        CanFocus = true;
        ViewportSettings = ViewportSettingsFlags.HasVerticalScrollBar;
        SetContentSize(new Size(0, 0));
    }

    /// <summary>追加用户消息。</summary>
    public void AppendUserMessage(string text)
    {
        FlushCurrentText();
        _messages.Add(new ChatMessage(MessageType.User, text));
        RebuildContent();
    }

    /// <summary>渲染 Agent 事件（流式）。</summary>
    public void RenderEvent(AgentEvent evt)
    {
        switch (evt)
        {
            case AgentEvent.RoundStartEvent(var round):
                FlushCurrentText();
                _messages.Add(new ChatMessage(MessageType.System, $"── Round {round} ──"));
                RebuildContent();
                break;

            case AgentEvent.TextDeltaEvent(var text):
                _currentText.Append(text);
                _hasTextOnLine = true;
                UpdateStreamingText();
                break;

            case AgentEvent.ToolCallStartEvent(var call):
                FlushCurrentText();
                _messages.Add(new ChatMessage(MessageType.ToolCall,
                    $"⎿ → {call.Name}({Truncate(call.Input.GetRawText(), 80)})"));
                RebuildContent();
                break;

            case AgentEvent.ToolResultEvent(_, var result):
                var icon = result.Success ? "✓" : "✗";
                var content = result.Success ? Truncate(result.Content, 200) : (result.Error ?? "未知错误");
                _messages.Add(new ChatMessage(result.Success ? MessageType.ToolResult : MessageType.ToolError,
                    $"⎿ {icon} {content}"));
                RebuildContent();
                break;

            case AgentEvent.ToolBlockedEvent(var call, var reason):
                _messages.Add(new ChatMessage(MessageType.System, $"⎿ ⛔ 拦截 {call.Name}: {reason}"));
                RebuildContent();
                break;

            case AgentEvent.AgentDoneEvent:
                FlushCurrentText();
                break;

            case AgentEvent.MaxRoundsReachedEvent(var rounds):
                FlushCurrentText();
                _messages.Add(new ChatMessage(MessageType.Warning, $"⚠ 已达最大轮次 {rounds}"));
                RebuildContent();
                break;

            case AgentEvent.ErrorEvent(var msg, _):
                FlushCurrentText();
                _messages.Add(new ChatMessage(MessageType.Error, $"✗ 错误：{msg}"));
                RebuildContent();
                break;

            case AgentEvent.CancelledEvent:
                FlushCurrentText();
                _messages.Add(new ChatMessage(MessageType.System, "── 已取消 ──"));
                RebuildContent();
                break;
        }
    }

    private void FlushCurrentText()
    {
        if (_hasTextOnLine && _currentText.Length > 0)
        {
            _messages.Add(new ChatMessage(MessageType.Assistant, _currentText.ToString()));
            _currentText.Clear();
            _hasTextOnLine = false;
            RebuildContent();
        }
    }

    /// <summary>重建内容并自动滚到底部。</summary>
    private void RebuildContent()
    {
        var lines = new List<string>();
        foreach (var msg in _messages)
        {
            var formatted = msg.Format();
            lines.AddRange(formatted.Split('\n'));
        }
        Text = string.Join(Environment.NewLine, lines);
        SetContentSize(new Size(Viewport.Width, lines.Count));
        ScrollToBottom();
    }

    private void UpdateStreamingText()
    {
        // 流式更新最后一条 assistant 消息（不重建全部，性能优化）
        // 简单实现：重建（消息少时足够快）
        RebuildContent();
    }

    private void ScrollToBottom()
    {
        var contentHeight = GetContentSize().Height;
        Viewport = Viewport with { Location = new Point(0, Math.Max(0, contentHeight - Viewport.Height)) };
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..(max - 3)] + "...";
}
```

**关键设计点**：
- **消息列表模型**：`List<ChatMessage>` 存储所有消息，流式文本用 `_currentText` 缓冲，完成时 flush 到列表。
- **自动滚动**：每次 RebuildContent 后调 ScrollToBottom，确保新内容可见。
- **性能**：简单实现用重建全部文本；消息量大时优化为增量更新（迭代 9+ 考虑）。

### 4.3 ChatMessage——消息数据模型

```csharp
internal enum MessageType
{
    User,        // 用户消息
    Assistant,   // 助手回复
    ToolCall,    // 工具调用
    ToolResult,  // 工具结果（成功）
    ToolError,   // 工具失败
    System,      // 系统提示
    Warning,     // 警告
    Error        // 错误
}

internal sealed record ChatMessage(MessageType Type, string Content)
{
    /// <summary>格式化为带前缀和颜色的字符串。</summary>
    public string Format() => Type switch
    {
        MessageType.User      => $"❯ {Content}",
        MessageType.Assistant => $"⏺ {Content}",
        MessageType.ToolCall  => Content,  // 已含 ⎿ 前缀
        MessageType.ToolResult => Content,
        MessageType.ToolError  => Content,
        MessageType.System    => Content,
        MessageType.Warning   => Content,
        MessageType.Error      => Content,
        _ => Content
    };

    /// <summary>获取该消息类型的颜色。</summary>
    public Color GetColor() => Type switch
    {
        MessageType.User       => Color.White,
        MessageType.Assistant  => Color.BrightCyan,
        MessageType.ToolCall   => Color.Cyan,
        MessageType.ToolResult => Color.Green,
        MessageType.ToolError  => Color.Red,
        MessageType.System     => Color.DarkGray,
        MessageType.Warning    => Color.Yellow,
        MessageType.Error      => Color.Red,
        _ => Color.White
    };
}
```

### 4.4 StatusBarView——顶部状态栏

**职责**：固定顶部，显示 Provider/Model/Security/Context/Round/Tools。

```csharp
internal sealed class StatusBarView : View
{
    private ProviderConfig? _providerConfig;
    private SecurityLevel _securityLevel;
    private int _contextWindowTokens = 64000;
    private int _toolCount;
    private int _estimatedTokens;
    private int _currentRound;

    public int EstimatedTokens
    {
        get => _estimatedTokens;
        set { _estimatedTokens = value; SetNeedsDraw(); }
    }

    public int CurrentRound
    {
        get => _currentRound;
        set { _currentRound = value; SetNeedsDraw(); }
    }

    public void Update(ProviderConfig config, SecurityLevel level, TuiConfig tui, ToolRegistry registry)
    {
        _providerConfig = config;
        _securityLevel = level;
        _contextWindowTokens = tui.ContextWindowTokens ?? 64000;
        _toolCount = registry.GetAll().Count;
        SetNeedsDraw();
    }

    protected override void OnDrawingContent()
    {
        // 直接绘制到 Viewport（Terminal.Gui v2 的绘制方式）
        var ratio = _contextWindowTokens > 0 ? (double)_estimatedTokens / _contextWindowTokens : 0;
        var ratioColor = ratio >= 0.9 ? Color.Red : ratio >= 0.7 ? Color.Yellow : Color.Green;
        var pct = (int)(ratio * 100);
        var securityColor = _securityLevel switch
        {
            SecurityLevel.Strict => Color.Red,
            SecurityLevel.Normal => Color.Yellow,
            SecurityLevel.Permisive => Color.Green,
            _ => Color.DarkGray
        };

        var text = $"provider={_providerConfig?.Name} model={_providerConfig?.Model} " +
                   $"security={_securityLevel} ctx={pct}%({_estimatedTokens}/{_contextWindowTokens}) " +
                   $"round={_currentRound} tools={_toolCount}";

        // 用 Attribute 设置颜色
        SetAttribute(new Attribute(Color.White, Color.Black));
        DrawText(0, 0, text);
    }
}
```

> **注**：Terminal.Gui v2 的绘制 API 细节在实现时确认。可以用 `Label` 子视图简化，或直接重写 `OnDrawingContent`。

### 4.5 InputFieldView——底部输入框

**职责**：固定底部，支持文本输入 + Tab 补全 + 历史导航 + Enter 提交。

```csharp
internal sealed class InputFieldView : View
{
    private readonly StringBuilder _buffer = new();
    private readonly Channel<string> _submitChannel = Channel.CreateUnbounded<string>();
    private readonly string[] _commands = { "/clear", "/exit", "/quit", "/help", "/status" };
    private readonly List<string> _history = new();
    private int _historyIndex = -1;

    public ChannelReader<string> Submits => _submitChannel.Reader;
    public event Action<string>? Submit;
    public event Action? ExitRequested;

    public InputFieldView()
    {
        CanFocus = true;
        // 绑定按键
        KeyBindings.Add(Key.Enter, Command.Accept);
        KeyBindings.Add(Key.Backspace, Command.DeleteCharLeft);
        KeyBindings.Add(Key.Tab, Command.Tab);
        KeyBindings.Add(Key.Esc, Command.Quit);
    }

    public async Task<string?> WaitForSubmitAsync(CancellationToken ct)
    {
        try { return await _submitChannel.Reader.ReadAsync(ct); }
        catch (OperationCanceledException) { return null; }
    }

    protected override bool OnKeyDown(Key key)
    {
        if (key.KeyCode == KeyCode.Enter)
        {
            var line = _buffer.ToString();
            _buffer.Clear();
            if (!string.IsNullOrEmpty(line)) _history.Add(line);
            _historyIndex = -1;
            _submitChannel.Writer.TryWrite(line);
            SetNeedsDraw();
            return true;
        }
        if (key.KeyCode == KeyCode.Esc)
        {
            ExitRequested?.Invoke();
            return true;
        }
        if (key.KeyCode == KeyCode.Backspace && _buffer.Length > 0)
        {
            _buffer.Remove(_buffer.Length - 1, 1);
            SetNeedsDraw();
            return true;
        }
        if (key.KeyCode == KeyCode.Tab && _buffer.Length > 0 && _buffer[0] == '/')
        {
            CompleteCommand();
            SetNeedsDraw();
            return true;
        }
        if (key.KeyCode == KeyCode.Up && _history.Count > 0)
        {
            NavigateHistory(1);
            SetNeedsDraw();
            return true;
        }
        if (key.KeyCode == KeyCode.Down && _historyIndex >= 0)
        {
            NavigateHistory(-1);
            SetNeedsDraw();
            return true;
        }
        if (!char.IsControl(key.AsRune))
        {
            _buffer.Append(key.AsRune);
            SetNeedsDraw();
            return true;
        }
        return false;
    }

    private void CompleteCommand()
    {
        var prefix = _buffer.ToString();
        var matches = _commands.Where(c => c.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (matches.Length == 1) { _buffer.Clear(); _buffer.Append(matches[0]); }
    }

    private void NavigateHistory(int direction) { /* 略 */ }

    protected override void OnDrawingContent()
    {
        SetAttribute(new Attribute(Color.BrightBlue, Color.Black));
        DrawText(0, 0, "> ");
        var color = _buffer.Length > 0 && _buffer[0] == '/' ? Color.Cyan : Color.White;
        SetAttribute(new Attribute(color, Color.Black));
        DrawText(2, 0, _buffer.ToString());
    }
}
```

### 4.6 HitlDialog——HITL 确认对话框

**职责**：模态对话框，显示工具调用信息 + A/S/P/D 选项。

```csharp
internal sealed class HitlDialog : Dialog
{
    private readonly TaskCompletionSource<HitlDecision> _tcs = new();

    public Task<HitlDecision> Result => _tcs.Task;

    public HitlDialog(ToolCall call)
    {
        Title = "HITL 确认";
        X = Pos.Center();
        Y = Pos.Center();
        Width = 60;
        Height = 8;

        var label = new Label
        {
            X = 1, Y = 1, Width = Dim.Fill(), Height = 3,
            Text = $"⚠ 即将执行 {call.Name}\n参数: {Truncate(call.Input.GetRawText(), 50)}\n\n按 A=本次 S=会话 P=永久 D=拒绝"
        };
        Add(label);

        KeyBindings.Add(Key.A, Command.Custom);
        KeyBindings.Add(Key.S, Command.Custom);
        KeyBindings.Add(Key.P, Command.Custom);
        KeyBindings.Add(Key.D, Command.Custom);
        KeyBindings.Add(Key.Esc, Command.Quit);
    }

    protected override bool OnKeyDown(Key key)
    {
        var choice = key.KeyCode switch
        {
            KeyCode.A => HitlChoice.AllowOnce,
            KeyCode.S => HitlChoice.AllowSession,
            KeyCode.P => HitlChoice.AllowPermanent,
            KeyCode.D => HitlChoice.Deny,
            KeyCode.Esc => HitlChoice.Deny,
            _ => (HitlChoice?)null
        };
        if (choice.HasValue)
        {
            var decision = choice == HitlChoice.Deny
                ? HitlDecision.Deny("用户拒绝")
                : new HitlDecision(choice.Value);
            _tcs.TrySetResult(decision);
            RequestStop();
            return true;
        }
        return false;
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..(max - 3)] + "...";
}
```

### 4.7 HitlPrompt——薄封装（保持 IHitlGate 实现）

**职责**：保持 `IHitlGate` 接口实现，内部委托 `HitlDialog`。

```csharp
public sealed class HitlPrompt : IHitlGate
{
    private readonly ConcurrentDictionary<string, byte> _sessionCache = new();
    private readonly Func<HitlDialog> _dialogFactory;  // 由 TerminalApp 注入

    public HitlPrompt(Func<HitlDialog> dialogFactory)
    {
        _dialogFactory = dialogFactory;
    }

    public async Task<HitlDecision?> RequestAsync(ToolCall call, CancellationToken ct)
    {
        if (_sessionCache.ContainsKey(call.Name))
            return new HitlDecision(HitlChoice.AllowSession);
        if (ct.IsCancellationRequested)
            return HitlDecision.Deny("已取消");

        // 在主线程弹出模态对话框
        HitlDecision? decision = null;
        await Task.Factory.StartNew(() =>
        {
            var dialog = _dialogFactory();
            Application.Run(dialog);  // 阻塞直到用户选择
            decision = dialog.Result.GetAwaiter().GetResult();
        }, CancellationToken.None, TaskCreationOptions.None, TaskScheduler.FromCurrentSynchronizationContext());

        if (decision?.ShouldCache == true)
            _sessionCache[call.Name] = 0;

        return decision;
    }

    public bool IsAllowedThisSession(string toolName) => _sessionCache.ContainsKey(toolName);
}
```

> **注**：HITL 对话框的实现细节（如何在事件循环中弹出模态 + 等待结果）在实现时确认。Terminal.Gui v2 的 `Application.Run(dialog)` 支持嵌套运行（SessionStack），可实现"暂停当前循环，运行对话框，返回结果"。

### 4.8 SpinnerIndicator——盲文点动画

**职责**：工具执行时显示思考动画。

```csharp
internal sealed class SpinnerIndicator : View
{
    private static readonly string[] Frames = { "⠋", "⠙", "⠹", "⠸", "⠼", "⠴", "⠦", "⠧", "⠇", "⠏" };
    private int _frame;
    private object? _timeoutToken;

    public string Verb { get; set; } = "Thinking";

    public void Start()
    {
        _frame = 0;
        _timeoutToken = Application.MainLoop.AddTimeout(TimeSpan.FromMilliseconds(100), () =>
        {
            _frame = (_frame + 1) % Frames.Length;
            SetNeedsDraw();
            return true;  // 继续
        });
        Visible = true;
    }

    public void Stop()
    {
        if (_timeoutToken != null)
            Application.MainLoop.RemoveTimeout(_timeoutToken);
        Visible = false;
    }

    protected override void OnDrawingContent()
    {
        SetAttribute(new Attribute(Color.BrightCyan, Color.Black));
        DrawText(0, 0, $"{Verb}{Frames[_frame]}");
    }
}
```

### 4.9 线程模型

**关键挑战**：AgentLoop 在后台线程跑，Terminal.Gui UI 必须在主线程更新。

```
主线程（Terminal.Gui 事件循环）          后台线程（AgentLoop）
─────────────────────────────          ──────────────────────
Application.Run() 阻塞                  agentLoop.RunAsync() 开始
  │                                     │
  ├─ MainLoop.Invoke(async () => {      │
  │     await InputLoopAsync()          │
  │  })                                 │
  │                                     │
  │  等待用户输入（不阻塞事件循环）       │
  │  ← InputFieldView 提交              │
  │                                     │
  │  await RunAgentRoundAsync()         │
  │     ├─ agentTask = agentLoop.Run    │
  │     │                               │
  │     │   await foreach (evt) {       │← 事件流写入 Channel
  │     │     App.Invoke(() => {        │
  │     │       UpdateChatView(evt)     │
  │     │     })                        │
  │     │   }                           │
  │     └─ await agentTask              │
  │                                     │
  │  继续等待输入                        │
  └─ RequestStop()                      │
```

**问题**：`await foreach` 在 Invoke 回调内会阻塞事件循环，导致 UI 无响应。

**解决方案**：用 `MainLoop.AddIdle` 分帧处理事件流：

```csharp
private async Task RunAgentRoundAsync()
{
    var sink = new ChannelEventSink();
    var agentTask = agentLoop.RunAsync(_history, sink, _ct);

    // 用 Idle 分帧处理，不阻塞事件循环
    bool ProcessNextEvent()
    {
        if (sink.Reader.TryRead(out var evt))
        {
            _statusBarView!.CurrentRound = evt is AgentEvent.RoundStartEvent(var r) ? r : _statusBarView.CurrentRound;
            _chatView!.RenderEvent(evt);
            return true;  // 还有事件，继续
        }
        return false;  // 暂无事件，停止 Idle
    }

    _app!.MainLoop.AddIdle(() =>
    {
        // 每帧处理多个事件（批量）
        for (int i = 0; i < 10 && ProcessNextEvent(); i++) { }
        return !agentTask.IsCompleted;  // agentTask 完成后停止 Idle
    });

    await agentTask;  // 等待 AgentLoop 完成
}
```

> **实现时确认**：Terminal.Gui v2 的 async/await 集成程度。如果 `await` 后能自动回到主线程，可以简化为直接 `await foreach` + `App.Invoke`。

---

## 五、迁移映射表

| 7a/7b（Spectre.Console） | 7c（Terminal.Gui v2） | 说明 |
|--------------------------|----------------------|------|
| `TuiApp` | `TerminalApp` | 主应用，装配布局 + 事件循环 |
| `StatusBar.Render()` 返回 IRenderable | `StatusBarView : View` | 固定顶部，重写 OnDrawingContent |
| `InputReader.ReadLineWithCompletionAsync` | `InputFieldView : View` | 固定底部，按键处理 + Channel 提交 |
| `ConsoleEventRenderer.RenderEvent` | `ChatView.RenderEvent` | 消息列表 + 流式追加 + 自动滚动 |
| `HitlPrompt`（IConsole + ReadKey） | `HitlPrompt`（委托 `HitlDialog`） | 模态对话框 |
| `AnsiConsole.Write(IRenderable)` | View 的 OnDrawingContent | Terminal.Gui 绘制 |
| `IConsole` 抽象 | **移除** | Terminal.Gui 自带抽象层 |
| `EventRenderer`（已删） | 不需要 | ChatView 直接渲染 |
| Panel/Markup 颜色 | Scheme/Attribute | 颜色系统迁移 |

---

## 六、保留的抽象接口

### 6.1 IHitlGate（不变）

```csharp
public interface IHitlGate
{
    Task<HitlDecision?> RequestAsync(ToolCall call, CancellationToken cancellationToken);
    bool IsAllowedThisSession(string toolName);
}
```

**不变**。`HitlPrompt` 仍实现此接口，内部委托 `HitlDialog`。

### 6.2 IConsole（移除或简化）

7a/7b 的 `IConsole` 是为 Spectre.Console 抽象的。Terminal.Gui 自带 View 抽象，不需要 `IConsole`。

**决策**：移除 `IConsole`，测试改用 Terminal.Gui 的 `FakeDriver`（无头测试）。

> 如果迁移成本太高，可临时保留 `IConsole` 作为过渡。但最终目标是移除。

---

## 七、验收标准

### 7.1 编译与测试

| 编号 | 标准 | 验证方式 |
|------|------|---------|
| 7c-01 | `dotnet build` 0 错误 0 警告 | 编译 |
| 7c-02 | Agent 层测试全绿（迭代 5/6 测试不受影响） | `dotnet test` |
| 7c-03 | HITL 逻辑测试全绿（HitlPromptTests 适配新接口） | `dotnet test` |
| 7c-04 | 新增 ChatView/StatusBarView/InputFieldView 单元测试 | `dotnet test` |

### 7.2 布局与渲染

| 编号 | 标准 | 验证方式 |
|------|------|---------|
| 7c-05 | 状态栏固定顶部，1 行，始终可见 | 手动：多轮对话后状态栏仍在顶部 |
| 7c-06 | 输入框固定底部，1 行，始终可见 | 手动：对话区滚动时输入框不动 |
| 7c-07 | 对话区占中间，可滚动（键盘上下或鼠标滚轮） | 手动：长对话能滚动查看历史 |
| 7c-08 | 新内容自动滚动到底部 | 手动：AI 回复时自动滚到最新 |
| 7c-09 | 终端 resize 时三段布局自动适配 | 手动：调整窗口大小，布局不乱 |

### 7.3 消息渲染

| 编号 | 标准 | 验证方式 |
|------|------|---------|
| 7c-10 | 用户消息以 `❯` 前缀显示 | 手动 |
| 7c-11 | 助手回复以 `⏺` 前缀显示，流式追加 | 手动 |
| 7c-12 | 工具调用以 `⎿ → 工具名(参数)` 显示 | 手动 |
| 7c-13 | 工具结果以 `⎿ ✓/✗ 内容` 显示 | 手动 |
| 7c-14 | 错误以红色显示 | 手动 |
| 7c-15 | 无任何残影/重叠/错位 | 手动多轮对话 |

### 7.4 HITL 交互

| 编号 | 标准 | 验证方式 |
|------|------|---------|
| 7c-16 | HITL 弹出模态对话框，显示工具名 + 参数 | 手动 |
| 7c-17 | A/S/P/D 四键映射正确 | 手动 |
| 7c-18 | Esc 默认拒绝 | 手动 |
| 7c-19 | 会话缓存命中时不弹框 | 手动：第二次 write_file 不弹 |
| 7c-20 | HITL 决策后对话框关闭，对话继续 | 手动 |
| 7c-21 | HITL 期间对话区不残影 | 手动 |

### 7.5 输入框

| 编号 | 标准 | 验证方式 |
|------|------|---------|
| 7c-22 | 输入文本实时显示 | 手动 |
| 7c-23 | Backspace 删除正确（含中文） | 手动 |
| 7c-24 | Tab 补全 `/` 命令 | 手动：输入 `/c` + Tab → `/clear` |
| 7c-25 | Enter 提交，清空输入框 | 手动 |
| 7c-26 | Esc 退出程序 | 手动 |
| 7c-27 | 历史导航（Up/Down）切换历史输入 | 手动 |

### 7.6 状态栏

| 编号 | 标准 | 验证方式 |
|------|------|---------|
| 7c-28 | 显示 provider/model/security/ctx/round/tools | 手动 |
| 7c-29 | ctx 占比颜色：绿(<70%)/黄(70-90%)/红(>90%) | 手动：构造长对话触发颜色变化 |
| 7c-30 | round 实时更新 | 手动：多轮对话时 round 递增 |

### 7.7 跨轮稳定性

| 编号 | 标准 | 验证方式 |
|------|------|---------|
| 7c-31 | 多轮对话无残影 | 手动：3 轮以上对话 |
| 7c-32 | 长对话滚屏后状态栏/输入框不乱 | 手动：对话超过终端高度 |
| 7c-33 | HITL 后继续对话不残影 | 手动 |

---

## 八、测试计划

### 8.1 单元测试（新增）

| 测试文件 | 覆盖范围 | 用例数 |
|---------|---------|--------|
| `ChatViewTests.cs` | 消息追加、流式缓冲、滚动 | 6 |
| `ChatMessageTests.cs` | 消息格式化、颜色映射 | 5 |
| `StatusBarViewTests.cs` | 状态栏内容、颜色阈值 | 4 |
| `InputFieldViewTests.cs` | 输入、Backspace、Tab、历史 | 6 |
| `HitlDialogTests.cs` | A/S/P/D 映射、Esc 拒绝、缓存 | 5 |

> **注**：Terminal.Gui 的 View 测试需要 `FakeDriver`（无头驱动）。测试初始化时用 `Application.Create(driver: new FakeDriver())`。

### 8.2 保留的测试

| 测试文件 | 改动 |
|---------|------|
| `HitlPromptTests.cs` | 适配新 `HitlPrompt`（注入 dialogFactory） |
| `BatchToolExecutorHitlTests.cs` | 不变（IHitlGate 接口未变） |
| `TuiAppIntegrationTests.cs` | 改名为 `TerminalAppIntegrationTests.cs`，适配新布局 |
| `TuiAppHitlIntegrationTests.cs` | 同上 |
| 所有 Agent 层测试 | **不变** |

### 8.3 测试策略

- **Agent 层测试**：零改动，验证迁移未影响核心逻辑
- **UI 层测试**：用 `FakeDriver` 无头测试，验证渲染逻辑
- **集成测试**：端到端验证输入 → AgentLoop → 渲染闭环

---

## 九、实施步骤

### 步骤 1：新增依赖 + 骨架

- `ParrotCode.Net.csproj` 添加 `Terminal.Gui` v2 包引用
- 创建 `TerminalApp.cs` 骨架（空布局 + Application.Init/Run/Shutdown）
- `App.cs` 装配 `TerminalApp`（与 `TuiApp` 并存，配置开关切换）
- 验证：`dotnet run` 能启动空白 Terminal.Gui 窗口

### 步骤 2：三段式布局

- 实现 `StatusBarView`（固定顶部）
- 实现 `ChatView`（中间，静态内容）
- 实现 `InputFieldView`（固定底部，Enter 提交到 Channel）
- `TerminalApp.BuildLayout` 装配三个 View
- 验证：能看到三段布局，输入框可输入

### 步骤 3：事件流接入

- `TerminalApp.RunAgentRoundAsync` 接入 AgentLoop + ChannelEventSink
- `ChatView.RenderEvent` 实现所有事件类型渲染
- 线程模型：`MainLoop.AddIdle` 分帧处理事件流
- 验证：能进行单轮对话（用户输入 → AI 流式回复 → 完成）

### 步骤 4：HITL 模态对话框

- 实现 `HitlDialog`（模态 + A/S/P/D 按键）
- `HitlPrompt` 改为委托 `HitlDialog`
- 验证：write_file 触发 HITL，选择后继续

### 步骤 5：状态栏 + 输入增强

- `StatusBarView` 完整实现（所有字段 + 颜色阈值）
- `InputFieldView` 增强：Tab 补全 + 历史导航 + Backspace 中文
- 验证：状态栏实时更新，输入框功能完整

### 步骤 6：Spinner + 收尾

- 实现 `SpinnerIndicator`（工具执行时动画）
- 删除旧文件（TuiApp/StatusBar/InputReader/ConsoleEventRenderer）
- 移除 `IConsole`（或保留过渡）
- 更新所有测试
- 全量 `dotnet test` 验证

### 步骤 7：端到端验收

- 手动多轮对话验证
- HITL 交互验证
- 长对话滚屏验证
- 终端 resize 验证
- 对照验收标准 7c-01 到 7c-33 逐项确认

---

## 十、风险与对策

| 风险 | 概率 | 影响 | 对策 |
|------|------|------|------|
| Terminal.Gui v2 API 与研究结论有差异 | 中 | 高 | 步骤 1 先做骨架验证 API，发现问题及时调整设计 |
| 线程模型复杂导致 UI 卡顿 | 中 | 高 | 用 `MainLoop.AddIdle` 分帧，每帧限制处理事件数 |
| FakeDriver 测试支持不完善 | 低 | 中 | 关键逻辑用纯 C# 测试（不依赖 View 渲染），UI 渲染用集成测试 |
| 中文/宽字符渲染问题 | 低 | 中 | Terminal.Gui v2 支持 Unicode，步骤 2 验证中文输入 |
| 迁移过程中 Spectre.Console 依赖清理不彻底 | 低 | 低 | 7c 期间 Spectre.Console 保留，验收后单独清理 |

---

## 十一、与后续迭代的衔接

### 迭代 8（安全纵深防御）

- `SecurityGuard` 通过 `BatchToolExecutor.OnBeforeExecuteAsync` hook 接入，与 UI 无关
- `SecurityLevel` 已在 `StatusBarView` 显示，迭代 8 接入真实拦截逻辑
- HITL 对话框可复用（`HitlDialog`），迭代 8 的权限模式切换可扩展

### 迭代 9（上下文管理）

- `ChatView` 的消息列表是上下文压缩的展示基础
- 摘要后可在 `ChatView` 插入"── 上下文已压缩 ──"系统消息

### 迭代 10（斜杠命令 + 持久化）

- `InputFieldView` 已支持 `/` 前缀 Tab 补全，迭代 10 完善命令注册中心
- `IUiControl` 抽象可基于 `InputFieldView` 设计

### 迭代 11（MCP）

- MCP 工具调用通过现有 `ToolCallStartEvent` / `ToolResultEvent` 渲染，UI 无需改动

### 迭代 12（Skill + Hook + 子 Agent）

- Hook 的 `tool_pre_exec` 类似 HITL，可复用 `HitlDialog` 模式
- 子 Agent 的后台任务结果可通过 `ChatView` 插入消息

---

## 附录 A：Terminal.Gui v2 关键 API 速查

```csharp
// 应用生命周期
using IApplication app = Application.Create();
app.Init();
app.Run(window);
app.RequestStop();
// app.Dispose() 自动调用

// 布局
view.X = Pos.Center();
view.Y = Pos.Bottom(otherView);
view.Width = Dim.Fill();
view.Height = Dim.Fill(1);  // 底部留 1 行

// 滚动
view.SetContentSize(new Size(w, h));
view.ViewportSettings = ViewportSettingsFlags.HasVerticalScrollBar;
view.Viewport = view.Viewport with { Location = new Point(0, scrollY) };

// 跨线程
app.Invoke(() => { /* UI 更新 */ });

// 定时器（spinner）
var token = app.MainLoop.AddTimeout(TimeSpan.FromMilliseconds(100), () => { return true; });
app.MainLoop.RemoveTimeout(token);

// 空闲处理（事件流）
app.MainLoop.AddIdle(() => { return shouldContinue; });

// 颜色
view.SetAttribute(new Attribute(foreground, background));

// 模态对话框
app.Run(dialog);  // 阻塞直到 dialog.RequestStop()
```

---

## 附录 B：消息类型视觉规范

| 类型 | 前缀 | 颜色 | 示例 |
|------|------|------|------|
| User | `❯ ` | White | `❯ 写一份赏析` |
| Assistant | `⏺ ` | BrightCyan | `⏺ 我来写一份赏析` |
| ToolCall | `  ⎿ → ` | Cyan | `  ⎿ → write_file({"path":"d:/zwl.md"})` |
| ToolResult | `  ⎿ ✓ ` | Green | `  ⎿ ✓ 已写入 2975 字节` |
| ToolError | `  ⎿ ✗ ` | Red | `  ⎿ ✗ 文件不存在` |
| System | 无 | DarkGray | `── Round 2 ──` |
| Warning | `⚠ ` | Yellow | `⚠ 已达最大轮次` |
| Error | `✗ ` | Red | `✗ 错误：网络超时` |

---

**文档结束**。状态：[设计完成，待实现]
