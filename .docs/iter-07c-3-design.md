# 迭代 7c-3：HITL 模态对话框 + Spinner + 收尾

> **状态**：[设计完成，待实现]
> **前置迭代**：7c-1 [待实现]、7c-2 [待实现]
> **父文档**：iter-07c-design.md（保留追溯）
> **后续迭代**：8（安全纵深防御）
> **目标**：实现模态 HITL 对话框，完成 Terminal.Gui 迁移收尾，删除旧文件。

---

## 一、迭代目标

### 1.1 核心目标

在 7c-2 的事件流 + 流式渲染基础上，实现 Claude Code 风格的 HITL 模态对话框，并完成迁移收尾：

```
┌─────────────────────────────────────────────────────────────────┐
│  provider=deepseek model=deepseek-chat security=Normal ctx=...  │
├─────────────────────────────────────────────────────────────────┤
│  ❯ 写一份赏析                                                    │
│  ⏺ 我来写一份赏析。                                              │
│    ⎿ → write_file({...})                                        │
│  ┌─ HITL 确认 ──────────────────────────────┐                   │
│  │ ⚠ 即将执行 write_file                     │                   │  ← 模态对话框
│  │ 参数: {"path":"d:/zwl.md",...}           │                   │
│  │                                          │                   │
│  │ 按 A=本次 S=会话 P=永久 D=拒绝           │                   │
│  └──────────────────────────────────────────┘                   │
├─────────────────────────────────────────────────────────────────┤
│  > _                                                             │
└─────────────────────────────────────────────────────────────────┘
```

### 1.2 本迭代要完成的工作

| 工作项 | 说明 |
|--------|------|
| HitlDialog 模态对话框 | 替代 7b 的内联渲染，用 Terminal.Gui Dialog |
| HitlPrompt 改为委托 HitlDialog | 保持 IHitlGate 接口不变 |
| SpinnerIndicator 动画 | 工具执行时显示盲文点动画 |
| InputFieldView 增强 | Tab 补全 + 历史导航 |
| 删除旧文件 | TuiApp/StatusBar/InputReader/ConsoleEventRenderer |
| 移除 IConsole | Terminal.Gui 自带抽象 |
| 移除 Spectre.Console 依赖 | 7c 迁移完成 |
| 测试适配 | HitlPromptTests 等适配新接口 |

### 1.3 非目标

- ❌ 不实现 Claude Code 的箭头键选项菜单（保留 A/S/P/D 四键）
- ❌ 不实现多行输入（Shift+Enter 等，留给后续迭代）
- ❌ 不实现 @路径补全（迭代 10）
- ❌ 不引入斜杠命令系统（迭代 10）

---

## 二、文件改动清单

### 2.1 新增文件（2 个）

```
Tui/
├── HitlDialog.cs             # HITL 模态对话框
└── SpinnerIndicator.cs       # 盲文点 spinner 动画
```

### 2.2 修改文件（4 个）

```
Tui/
├── HitlPrompt.cs             # 改为委托 HitlDialog（保持 IHitlGate）
├── InputFieldView.cs         # 增强 Tab 补全 + 历史导航
├── TerminalApp.cs            # 注入 HitlPrompt + Spinner 接入
└── ChatView.cs               # 接入 Spinner 显示（工具执行时）
App/
└── App.cs                    # 默认 mode 改为 "terminal"
Config/Models.cs              # 移除 mode 的 "console" 选项（可选）
ParrotCode.Net.csproj         # 移除 Spectre.Console 依赖
```

### 2.3 删除文件（5 个）

```
Tui/
├── TuiApp.cs                 # 被 TerminalApp 替代
├── StatusBar.cs              # 被 StatusBarView 替代
├── InputReader.cs            # 被 InputFieldView 替代
├── ConsoleEventRenderer.cs  # 被 ChatView 替代
└── IConsole.cs               # Terminal.Gui 自带抽象
```

### 2.4 保留文件（3 个，不动）

```
Tui/
├── IHitlGate.cs              # 接口不变（含 NullHitlGate）
├── HitlDecision.cs           # 数据模型不变
└── StatusBarView.cs          # 7c-1 已实现，不动
```

---

## 三、详细设计

### 3.1 HitlDialog——模态对话框

**职责**：模态 Dialog，显示工具调用信息 + A/S/P/D 按键。弹出时暂停主循环，用户选择后关闭。

```csharp
using System.Threading.Tasks;
using Terminal.Gui.App;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace ParrotCode;

/// <summary>
/// HITL 确认模态对话框（迭代 7c-3）。
/// 替代 7b 的内联渲染（HitlPrompt + IConsole）。
///
/// 工作原理：
/// 1. HitlPrompt.RequestAsync 在 agentLoop 线程调用
/// 2. 通过 MainLoop.Invoke 在主线程弹出 HitlDialog
/// 3. Application.Run(dialog) 阻塞（嵌套事件循环），等待用户按键
/// 4. 用户按 A/S/P/D/Esc，设置 TaskCompletionSource 结果
/// 5. dialog.RequestStop() 关闭，返回到 HitlPrompt.RequestAsync
/// 6. HitlPrompt 返回决策
/// </summary>
internal sealed class HitlDialog : Dialog
{
    private readonly TaskCompletionSource<HitlDecision> _tcs = new();

    /// <summary>用户决策的 Task。HitlPrompt await 此 Task。</summary>
    public Task<HitlDecision> Result => _tcs.Task;

    public HitlDialog(ToolCall call)
    {
        Title = "HITL 确认";
        X = Pos.Center();
        Y = Pos.Center();
        Width = 60;
        Height = 8;

        // 对话框内容
        var argsText = Truncate(call.Input.GetRawText(), 50);
        var label = new Label
        {
            X = 1,
            Y = 1,
            Width = Dim.Fill(),
            Height = 4,
            Text = $"⚠ 即将执行 {call.Name}\n" +
                   $"参数: {argsText}\n\n" +
                   $"按 A=本次  S=会话  P=永久  D=拒绝"
        };
        Add(label);

        // 确保对话框获得焦点
        CanFocus = true;
    }

    protected override bool OnKeyDown(Key key)
    {
        var choice = key.KeyCode switch
        {
            KeyCode.A => HitlChoice.AllowOnce,
            KeyCode.S => HitlChoice.AllowSession,
            KeyCode.P => HitlChoice.AllowPermanent,
            KeyCode.D => HitlChoice.Deny,
            KeyCode.Esc => HitlChoice.Deny,  // Esc 默认拒绝（安全）
            _ => (HitlChoice?)null
        };

        if (choice.HasValue)
        {
            var decision = choice == HitlChoice.Deny
                ? HitlDecision.Deny("用户拒绝执行")
                : new HitlDecision(choice.Value);

            _tcs.TrySetResult(decision);
            RequestStop();  // 关闭对话框
            return true;
        }

        return false;  // 未处理的键交给基类
    }

    private static string Truncate(string s, int max) =>
        s.Length <= max ? s : s[..(max - 3)] + "...";
}
```

**关键设计点**：
- **模态**：`Dialog` 继承自 `Window`，`Application.Run(dialog)` 会启动嵌套事件循环，暂停外层循环。
- **TaskCompletionSource**：用户按键后设置结果，`HitlPrompt` 的 `await` 完成。
- **Esc 默认拒绝**：安全设计，与 7b 一致。

### 3.2 HitlPrompt 改造——委托 HitlDialog

**职责**：保持 `IHitlGate` 接口，内部委托 `HitlDialog` 弹模态框。

```csharp
using System.Collections.Concurrent;
using Terminal.Gui.App;

namespace ParrotCode;

/// <summary>
/// IHitlGate 实现（迭代 7c-3：委托 HitlDialog 模态对话框）。
/// 保持 IHitlGate 接口不变——BatchToolExecutor 的注入方式不变。
///
/// 工作流程：
/// 1. RequestAsync 检查缓存命中 → 直接返回 AllowSession
/// 2. 通过 MainLoop.Invoke 在主线程弹出 HitlDialog
/// 3. Application.Run(dialog) 阻塞等待用户按键
/// 4. 用户选择后 dialog 关闭，返回决策
/// 5. 缓存会话级允许
/// </summary>
public sealed class HitlPrompt : IHitlGate
{
    private readonly ConcurrentDictionary<string, byte> _sessionCache = new();
    private readonly IApplication _app;
    private readonly Func<ToolCall, HitlDialog> _dialogFactory;

    /// <summary>
    /// 构造函数。
    /// </summary>
    /// <param name="app">Terminal.Gui IApplication 实例（用于 MainLoop.Invoke）。</param>
    /// <param name="dialogFactory">创建 HitlDialog 的工厂（注入 ToolCall）。</param>
    public HitlPrompt(IApplication app, Func<ToolCall, HitlDialog> dialogFactory)
    {
        _app = app ?? throw new ArgumentNullException(nameof(app));
        _dialogFactory = dialogFactory ?? throw new ArgumentNullException(nameof(dialogFactory));
    }

    public async Task<HitlDecision?> RequestAsync(ToolCall call, CancellationToken cancellationToken)
    {
        // 1. 会话缓存命中——直接返回 AllowSession（不弹框）
        if (_sessionCache.ContainsKey(call.Name))
            return new HitlDecision(HitlChoice.AllowSession);

        // 2. 取消时立即返回 Deny
        if (cancellationToken.IsCancellationRequested)
            return HitlDecision.Deny("已取消");

        // 3. 在主线程弹出模态对话框
        // RequestAsync 可能在 agentLoop 后台线程调用，需要调度到主线程
        HitlDecision? decision = null;
        _app.Invoke(() =>
        {
            var dialog = _dialogFactory(call);
            _app.Run(dialog);  // 嵌套事件循环，阻塞直到用户选择
            decision = dialog.Result.GetAwaiter().GetResult();
        });

        // 4. 缓存会话级允许
        if (decision?.ShouldCache == true)
            _sessionCache[call.Name] = 0;

        return await Task.FromResult(decision);
    }

    public bool IsAllowedThisSession(string toolName) =>
        _sessionCache.ContainsKey(toolName);
}
```

**关键设计点**：
- **IHitlGate 接口不变**：`RequestAsync` / `IsAllowedThisSession` 签名不变。
- **MainLoop.Invoke**：`RequestAsync` 可能在后台线程调用（BatchToolExecutor），用 `_app.Invoke` 调度到主线程弹对话框。
- **嵌套事件循环**：`_app.Run(dialog)` 启动嵌套循环，阻塞当前 `Invoke` 回调，直到用户选择。这个阻塞是在主线程的 `Invoke` 回调中，不影响后台的 agentLoop 线程（它在等待 `RequestAsync` 的 Task 完成）。

### 3.3 SpinnerIndicator——盲文点动画

**职责**：工具执行时显示思考动画。

```csharp
using Terminal.Gui.App;
using Terminal.Gui.Drawing;
using Terminal.Gui.ViewBase;

namespace ParrotCode;

/// <summary>
/// 盲文点 spinner 动画（迭代 7c-3）。
/// 工具执行时显示 Thinking⠋ → Thinking⠙ → ... 循环。
/// 参照 Claude Code 的 spinner 设计。
/// </summary>
internal sealed class SpinnerIndicator : View
{
    private static readonly string[] Frames = { "⠋", "⠙", "⠹", "⠸", "⠼", "⠴", "⠦", "⠧", "⠇", "⠏" };

    private readonly IApplication _app;
    private int _frame;
    private object? _timeoutToken;

    /// <summary>动词（如 "Thinking"、"Working"）。</summary>
    public string Verb { get; set; } = "Thinking";

    public SpinnerIndicator(IApplication app)
    {
        _app = app;
        Width = 20;
        Height = 1;
        Visible = false;  // 默认隐藏
    }

    /// <summary>开始动画。</summary>
    public void Start()
    {
        _frame = 0;
        Visible = true;
        _timeoutToken = _app.MainLoop.AddTimeout(TimeSpan.FromMilliseconds(100), () =>
        {
            _frame = (_frame + 1) % Frames.Length;
            SetNeedsDraw();
            return true;  // 继续
        });
    }

    /// <summary>停止动画。</summary>
    public void Stop()
    {
        if (_timeoutToken != null)
        {
            _app.MainLoop.RemoveTimeout(_timeoutToken);
            _timeoutToken = null;
        }
        Visible = false;
    }

    protected override void OnDrawingContent()
    {
        SetAttribute(new Attribute(Color.BrightCyan, Color.Black));
        DrawText(0, 0, $"{Verb}{Frames[_frame]}");
    }
}
```

### 3.4 InputFieldView 增强——Tab 补全 + 历史导航

```csharp
// InputFieldView.cs 7c-3 增强（在 7c-1 基础上）

/// <summary>
/// 底部输入框 View（迭代 7c-3：增强 Tab 补全 + 历史导航）。
/// </summary>
internal sealed class InputFieldView : View
{
    private readonly StringBuilder _buffer = new();
    private readonly Channel<string> _submitChannel = Channel.CreateUnbounded<string>();
    private readonly string[] _commands = { "/clear", "/exit", "/quit", "/help", "/status" };
    private readonly List<string> _history = new();
    private int _historyIndex = -1;
    private string? _savedBuffer;  // 历史导航时保存当前输入

    // ... 7c-1 的字段和方法保留 ...

    protected override bool OnKeyDown(Key key)
    {
        // ===== 7c-1 已有：Enter / Esc / Backspace / 普通字符 =====
        // （略，见 7c-1 设计）

        // ===== 7c-3 新增：Tab 补全 =====
        if (key.KeyCode == KeyCode.Tab && _buffer.Length > 0 && _buffer[0] == '/')
        {
            CompleteCommand();
            SetNeedsDraw();
            return true;
        }

        // ===== 7c-3 新增：历史导航 =====
        if (key.KeyCode == KeyCode.CursorUp && _history.Count > 0)
        {
            NavigateHistory(direction: 1);  // 向上（更早）
            SetNeedsDraw();
            return true;
        }
        if (key.KeyCode == KeyCode.CursorDown && _historyIndex >= 0)
        {
            NavigateHistory(direction: -1);  // 向下（更新）
            SetNeedsDraw();
            return true;
        }

        // ... 7c-1 的其他处理 ...
    }

    /// <summary>
    /// Tab 补全 / 开头的命令。
    /// 唯一匹配→填充；多匹配→不填充（7c-3 简化，不显示候选列表）。
    /// </summary>
    private void CompleteCommand()
    {
        var prefix = _buffer.ToString();
        var matches = _commands
            .Where(c => c.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (matches.Length == 1)
        {
            _buffer.Clear();
            _buffer.Append(matches[0]);
        }
        // 多匹配或无匹配——不做任何事（7c-3 简化）
    }

    /// <summary>
    /// 历史导航。
    /// direction=1 向上（更早的历史），direction=-1 向下（更新的历史）。
    /// </summary>
    private void NavigateHistory(int direction)
    {
        if (_history.Count == 0) return;

        // 第一次按 Up——保存当前输入，跳到最新一条历史
        if (_historyIndex == -1)
        {
            _savedBuffer = _buffer.ToString();
            _historyIndex = _history.Count - 1;
        }
        else
        {
            _historyIndex += direction;
            // 越界检查
            if (_historyIndex < 0)
            {
                // 超出最早历史——恢复保存的输入
                _buffer.Clear();
                if (_savedBuffer != null) _buffer.Append(_savedBuffer);
                _historyIndex = -1;
                return;
            }
            if (_historyIndex >= _history.Count)
            {
                _historyIndex = _history.Count - 1;
                return;
            }
        }

        // 加载历史条目
        _buffer.Clear();
        _buffer.Append(_history[_historyIndex]);
    }

    // Enter 提交时记录历史
    // （在 7c-1 的 Enter 处理中新增）
    // if (!string.IsNullOrEmpty(line)) _history.Add(line);
    // _historyIndex = -1;
    // _savedBuffer = null;
}
```

### 3.5 TerminalApp 改造——注入 HitlPrompt + Spinner

```csharp
// TerminalApp.cs 7c-3 关键改动

private HitlPrompt? _hitlPrompt;
private SpinnerIndicator? _spinner;

private void BuildLayout()
{
    _top = new Window { Title = "ParrotCode.Net" };

    // 状态栏、对话区、输入框（7c-1/7c-2 已实现）
    _statusBarView = new StatusBarView { /* ... */ };
    _chatView = new ChatView { /* ... */ };
    _inputFieldView = new InputFieldView { /* ... */ };

    // 7c-3 新增：Spinner（叠加在对话区底部，工具执行时显示）
    _spinner = new SpinnerIndicator(_app!)
    {
        X = 0,
        Y = Pos.Bottom(_chatView!) - 1,  // 对话区最后一行
        Width = 20,
        Height = 1,
        Visible = false
    };

    _top.Add(_statusBarView, _chatView, _inputFieldView, _spinner);
}

/// <summary>
/// 7c-3：装配 HitlPrompt（替代 NullHitlGate）。
/// </summary>
private void AssembleHitl()
{
    var hitlEnabled = _tuiConfig.EnableHitl ?? true;
    if (hitlEnabled)
    {
        _hitlPrompt = new HitlPrompt(_app!, call => new HitlDialog(call));
    }
    // else: NullHitlGate（7c-2 行为）
}

/// <summary>
/// 7c-3：RunAgentRoundAsync 注入 HitlPrompt + Spinner 控制。
/// </summary>
private async Task RunAgentRoundAsync(ConversationHistory history)
{
    var executor = new ToolExecutor(_registry!, TimeSpan.FromSeconds(_agentConfig.ToolTimeoutSeconds ?? 30), _logger);

    // 7c-3：注入 HitlPrompt（替代 NullHitlGate）
    IHitlGate hitlGate = _hitlPrompt ?? new NullHitlGate();

    var batchExecutor = new BatchToolExecutor(executor, _registry!,
                                               _agentConfig.MaxParallelism ?? 5,
                                               hitlGate: hitlGate,
                                               _logger);

    var sink = new ChannelEventSink();
    var agentLoop = new AgentLoop(_provider!, _registry!, batchExecutor,
                                   _agentConfig.MaxRounds ?? 10,
                                   _agentConfig.ToolChoice ?? "auto",
                                   _agentConfig.SystemPrompt,
                                   logger: null);
    var agentTask = agentLoop.RunAsync(history, sink, _ct);

    // 事件流消费（7c-2 的 AddIdle 逻辑，新增 Spinner 控制）
    var idleToken = _app!.MainLoop.AddIdle(() =>
    {
        for (int i = 0; i < 20; i++)
        {
            if (!sink.Reader.TryRead(out var evt))
                return !agentTask.IsCompleted;

            ProcessEvent(evt);
        }
        return !agentTask.IsCompleted;
    });

    await agentTask;
    _app.MainLoop.RemoveIdle(idleToken);

    // 消费剩余事件
    while (sink.Reader.TryRead(out var evt))
        ProcessEvent(evt);

    // 确保 Spinner 停止
    _spinner?.Stop();

    _statusBarView!.EstimatedTokens = history.EstimatedTokens;
}

/// <summary>处理事件（7c-3 新增 Spinner 控制）。</summary>
private void ProcessEvent(AgentEvent evt)
{
    if (evt is AgentEvent.RoundStartEvent(var r))
        _statusBarView!.CurrentRound = r;

    _chatView!.RenderEvent(evt);

    // 7c-3：工具调用时启动 Spinner，结果时停止
    if (evt is AgentEvent.ToolCallStartEvent)
        _spinner?.Start();
    else if (evt is AgentEvent.ToolResultEvent or AgentEvent.ToolBlockedEvent)
        _spinner?.Stop();
}
```

### 3.6 App.cs——默认 mode 改为 "terminal"

```csharp
// App.cs 7c-3 改动
public async Task RunAsync()
{
    var tuiConfig = _config.Tui ?? new TuiConfig();
    var securityLevel = ParseSecurityLevel(_config.Security?.Level);

    // 7c-3：默认 mode 改为 "terminal"
    var mode = tuiConfig.Mode ?? "terminal";

    if (mode == "terminal")
    {
        using var terminalApp = new TerminalApp(
            _provider,                    // 7c-2 新增
            _providerConfig,
            _config.Agent,
            tuiConfig,
            securityLevel,
            _logger,
            _ct);
        await terminalApp.RunAsync();
    }
    else
    {
        // 旧 TuiApp 已在 7c-3 删除，此分支不再可达
        // 保留分支用于平滑过渡（如果 TuiApp 未删则走此）
        throw new InvalidOperationException("旧 TuiApp 已移除，请使用 terminal 模式");
    }
}
```

### 3.7 csproj——移除 Spectre.Console

```xml
<!-- 7c-3：移除 Spectre.Console -->
<!-- <PackageReference Include="Spectre.Console" Version="0.49.1" /> -->  <!-- 已移除 -->
<PackageReference Include="Terminal.Gui" Version="2.0.0" />
<PackageReference Include="Microsoft.Extensions.Logging.Console" Version="8.0.0" />
<PackageReference Include="YamlDotNet" Version="16.2.1" />
```

> **注意**：移除 Spectre.Console 前，确认没有其他文件引用 `Spectre.Console` 命名空间。删除 TuiApp/StatusBar/InputReader/ConsoleEventRenderer/IConsole 后，应该没有引用了。

---

## 四、HITL 交互流程详解

### 4.1 时序图

```
AgentLoop 线程                     主线程（Terminal.Gui）          用户
────────────                      ──────────────────              ────
  │                                 │
  │ BatchToolExecutor 调            │
  │ hitlGate.RequestAsync(call)     │
  │ ├─ 缓存未命中                   │
  │ ├─ _app.Invoke(() => {         │
  │ │    创建 HitlDialog            │
  │ │    _app.Run(dialog)          │
  │ │    └─ 嵌套事件循环开始 ──────→│                                │
  │ │                               │    显示 HITL 对话框            │
  │ │                               │    等待按键  ←─────────────────│ 按 A
  │ │                               │    OnKeyDown(A)                 │
  │ │                               │    _tcs.SetResult(AllowOnce)    │
  │ │                               │    RequestStop()                │
  │ │    ← 嵌套循环结束              │                                │
  │ │    decision = AllowOnce        │                                │
  │ │  })                            │                                │
  │ ├─ 缓存 AllowSession（如果 S）  │                                │
  │ └─ return decision               │                                │
  │                                 │                                │
  │ 继续执行工具                     │                                │
```

### 4.2 嵌套事件循环

`_app.Run(dialog)` 是**嵌套事件循环**：
- 外层 `Application.Run(_top)` 暂停
- 内层 `Application.Run(dialog)` 开始
- 用户按键后 `dialog.RequestStop()` 关闭内层
- 控制权返回到外层

**关键**：嵌套循环期间，外层的 UI 仍然可见（对话框叠加在对话区上方），但外层不处理输入。这是 Terminal.Gui 的标准模态行为。

### 4.3 线程安全

`RequestAsync` 在 agentLoop 后台线程调用，但 `_app.Invoke` 把弹对话框的逻辑调度到主线程执行。`_app.Invoke` 是同步的——它等待主线程执行完回调后才返回。

> **实现时确认**：`_app.Invoke` 是否同步阻塞。如果是异步（`Invoke` 返回 Task），需要 `await`。根据 Terminal.Gui v2 文档，`Invoke` 应该是同步的。

---

## 五、验收标准

### 5.1 编译与测试

| 编号 | 标准 | 验证方式 |
|------|------|---------|
| 7c3-01 | `dotnet build` 0 错误 0 警告 | 编译 |
| 7c3-02 | 移除 Spectre.Console 后无引用残留 | 编译通过 |
| 7c3-03 | Agent 层测试全绿 | `dotnet test` |
| 7c3-04 | HitlDialog 单元测试通过 | `dotnet test` |
| 7c3-05 | HitlPromptTests 适配新接口 | `dotnet test` |
| 7c3-06 | InputFieldView Tab/历史测试通过 | `dotnet test` |

### 5.2 HITL 交互

| 编号 | 标准 | 验证方式 |
|------|------|---------|
| 7c3-07 | write_file 触发 HITL 弹出模态对话框 | 手动 |
| 7c3-08 | 对话框显示工具名 + 参数 + A/S/P/D 提示 | 手动 |
| 7c3-09 | 按 A → 允许本次，对话框关闭，工具执行 | 手动 |
| 7c3-10 | 按 S → 会话允许，对话框关闭，后续不弹 | 手动：第二次 write_file 不弹 |
| 7c3-11 | 按 P → 永久允许（7c 退化为会话级） | 手动 |
| 7c3-12 | 按 D → 拒绝，工具不执行，显示拒绝原因 | 手动 |
| 7c3-13 | 按 Esc → 默认拒绝 | 手动 |
| 7c3-14 | HITL 期间对话区不残影 | 手动 |
| 7c3-15 | HITL 决策后对话继续，无残影 | 手动 |

### 5.3 Spinner 动画

| 编号 | 标准 | 验证方式 |
|------|------|---------|
| 7c3-16 | 工具执行时显示 spinner 动画 | 手动 |
| 7c3-17 | 工具结果返回后 spinner 停止 | 手动 |
| 7c3-18 | spinner 不遮挡对话内容 | 手动 |

### 5.4 输入框增强

| 编号 | 标准 | 验证方式 |
|------|------|---------|
| 7c3-19 | Tab 补全 / 命令（唯一匹配填充） | 手动：/c + Tab → /clear |
| 7c3-20 | Up 键切换到上一条历史输入 | 手动 |
| 7c3-21 | Down 键切换到下一条历史输入 | 手动 |
| 7c3-22 | 历史导航后能正常编辑 | 手动 |

### 5.5 迁移收尾

| 编号 | 标准 | 验证方式 |
|------|------|---------|
| 7c3-23 | 旧文件已删除（TuiApp/StatusBar/InputReader/ConsoleEventRenderer/IConsole） | 检查文件系统 |
| 7c3-24 | Spectre.Console 依赖已移除 | 检查 csproj |
| 7c3-25 | 旧测试已适配或删除 | `dotnet test` 全绿 |
| 7c3-26 | 默认 mode 为 "terminal" | 手动：不配置 mode 也能启动 TerminalApp |

### 5.6 端到端综合

| 编号 | 标准 | 验证方式 |
|------|------|---------|
| 7c3-27 | 多轮对话（3 轮+）+ HITL 无残影 | 手动 |
| 7c3-28 | 长对话滚屏后 HITL 对话框位置正确 | 手动 |
| 7c3-29 | 终端 resize 后 HITL 对话框居中 | 手动 |
| 7c3-30 | 所有 7c 验收标准（7c1 + 7c2 + 7c3）通过 | 逐项确认 |

---

## 六、测试计划

### 6.1 新增/修改的测试

| 测试文件 | 类型 | 用例数 |
|---------|------|--------|
| `HitlDialogTests.cs` | 新增 | 5（A/S/P/D/Esc 映射 + 缓存） |
| `HitlPromptTests.cs` | 修改 | 8（适配 dialogFactory 注入） |
| `InputFieldViewTests.cs` | 修改 | 8（新增 Tab/历史导航用例） |
| `SpinnerIndicatorTests.cs` | 新增 | 2（Start/Stop） |

### 6.2 删除的测试

| 测试文件 | 原因 |
|---------|------|
| `TuiAppIntegrationTests.cs` | TuiApp 已删除 |
| `TuiAppHitlIntegrationTests.cs` | TuiApp 已删除 |
| `ConsoleEventRendererTests.cs` | ConsoleEventRenderer 已删除 |
| `InputReaderTests.cs` | InputReader 已删除 |

### 6.3 新增的集成测试

| 测试文件 | 覆盖范围 |
|---------|---------|
| `TerminalAppIntegrationTests.cs` | 端到端：输入 → AgentLoop → 渲染 |
| `TerminalAppHitlIntegrationTests.cs` | 端到端：HITL 弹框 → 决策 → 继续 |

### 6.4 BatchToolExecutorHitlTests

**不变**——`IHitlGate` 接口未变，这些测试验证的是 Agent 层的 HITL 逻辑，与 UI 无关。

---

## 七、实施步骤

### 步骤 1：HitlDialog + HitlPrompt 改造

- 实现 `HitlDialog`（模态 + A/S/P/D 按键）
- `HitlPrompt` 改为委托 `HitlDialog`（注入 IApplication + dialogFactory）
- 编写 `HitlDialogTests` + 适配 `HitlPromptTests`
- **验证**：`dotnet test` HITL 测试通过

### 步骤 2：TerminalApp 注入 HitlPrompt

- `TerminalApp.AssembleHitl` 装配 HitlPrompt
- `RunAgentRoundAsync` 注入 HitlPrompt（替代 NullHitlGate）
- **验证**：write_file 触发 HITL 弹框，选择后继续

### 步骤 3：SpinnerIndicator

- 实现 `SpinnerIndicator`
- `TerminalApp` 装配 Spinner
- `ProcessEvent` 接入 Spinner 控制（ToolCallStart→Start，ToolResult→Stop）
- **验证**：工具执行时显示动画

### 步骤 4：InputFieldView 增强

- Tab 补全（/ 前缀）
- 历史导航（Up/Down）
- 编写测试
- **验证**：Tab 补全和历史导航正常

### 步骤 5：删除旧文件 + 移除依赖

- 删除 TuiApp/StatusBar/InputReader/ConsoleEventRenderer/IConsole
- 删除对应的旧测试
- csproj 移除 Spectre.Console
- 适配 `App.cs`（默认 mode=terminal）
- **验证**：`dotnet build` 0 错误

### 步骤 6：集成测试 + 端到端验收

- 编写 TerminalAppIntegrationTests
- 编写 TerminalAppHitlIntegrationTests
- 全量 `dotnet test`
- 手动多轮对话 + HITL 验证
- 对照验收标准 7c3-01 到 7c3-30 逐项确认

---

## 八、风险与对策

| 风险 | 概率 | 影响 | 对策 |
|------|------|------|------|
| 嵌套事件循环导致死锁 | 中 | 高 | 步骤 1 先单独验证 HitlDialog 弹框/关闭，不接 Agent |
| `_app.Invoke` 同步阻塞行为不符预期 | 中 | 高 | 用 `MainLoop.Invoke` 替代，或用 Channel 通信 |
| HITL 弹框期间后台线程继续写 Channel | 低 | 中 | BatchToolExecutor await RequestAsync，期间不产事件 |
| 移除 Spectre.Console 后有遗漏引用 | 中 | 低 | 编译检查，逐个修复 |
| Spinner 动画闪烁 | 低 | 低 | 调整 100ms 间隔 |

---

## 九、与后续迭代的衔接

### 迭代 8（安全纵深防御）

- `SecurityGuard` 通过 `BatchToolExecutor.OnBeforeExecuteAsync` hook 接入
- `SecurityLevel` 已在 StatusBarView 显示
- `HitlDialog` 可复用——迭代 8 的权限模式切换可扩展对话框选项
- `IHitlGate` 接口不变，SecurityGuard 作为前置拦截器（在 IHitlGate 之前）

### 迭代 9（上下文管理）

- `ChatView` 的消息列表是上下文压缩的展示基础
- 摘要后可在 ChatView 插入"── 上下文已压缩 ──"系统消息

### 迭代 10（斜杠命令 + 持久化）

- `InputFieldView` 已支持 Tab 补全，迭代 10 完善命令注册中心
- `IUiControl` 抽象可基于 InputFieldView 设计

### 迭代 11-12

- MCP 工具调用通过现有事件流渲染，UI 无需改动
- Hook 的 `tool_pre_exec` 可复用 HitlDialog 模式
- 子 Agent 结果可通过 ChatView 插入消息

---

## 十、7c 迁移总结

### 10.1 三阶段回顾

| 阶段 | 目标 | 风险 | 价值 |
|------|------|------|------|
| 7c-1 | 库 API 验证 + 静态布局 | 库 API 不符 | 提前消化最大不确定性 |
| 7c-2 | 事件流 + 流式渲染 | 线程模型 | 验证主循环集成，不耦合 HITL |
| 7c-3 | HITL 模态 + 收尾 | 嵌套事件循环 | 在已稳定基础上加最复杂交互 |

### 10.2 迁移成果

- ✅ 固定顶部状态栏（始终可见）
- ✅ 固定底部输入框（始终可见）
- ✅ 中间滚动对话区（自动滚到底）
- ✅ 流式文本渲染（逐字追加）
- ✅ 工具调用/结果卡片
- ✅ HITL 模态对话框
- ✅ Spinner 动画
- ✅ Tab 补全 + 历史导航
- ✅ 无残影（不用 Live）
- ✅ 终端 resize 自适应
- ✅ 移除 Spectre.Console 依赖

---

**文档结束**。状态：[设计完成，待实现]
