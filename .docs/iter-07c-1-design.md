# 迭代 7c-1：Terminal.Gui 基础设施 + 三段式静态布局

> **状态**：[设计完成，待实现]
> **前置迭代**：7a [已完成]、7b [已完成]
> **父文档**：iter-07c-design.md（保留追溯）
> **后续迭代**：7c-2（事件流 + 流式渲染）、7c-3（HITL 模态 + 收尾）
> **目标**：验证 Terminal.Gui v2 API，搭出固定布局骨架，不接 AgentLoop。

---

## 一、迭代目标

### 1.1 核心目标

把 Terminal.Gui v2 引入项目，验证其 API 与设计文档结论是否一致，并搭出 Claude Code 风格的三段式固定布局骨架：

```
┌─────────────────────────────────────────────────────────────────┐
│  ParrotCode.Net | provider=deepseek model=deepseek-chat | ...    │  ← 固定顶部状态栏（1 行）
├─────────────────────────────────────────────────────────────────┤
│  （静态占位内容）                                                  │  ← 中间对话区（可滚动，本迭代放静态内容）
│  ...                                                             │
├─────────────────────────────────────────────────────────────────┤
│  > _                                                             │  ← 固定底部输入框（1 行）
└─────────────────────────────────────────────────────────────────┘
```

### 1.2 本迭代要验证的关键问题

| 问题 | 验证方式 | 失败对策 |
|------|---------|---------|
| Terminal.Gui v2 的 `Application.Create().Init()` 是否如文档所述 | 启动空白窗口 | 调整为 v1 兼容写法 |
| `Pos.Bottom` / `Dim.Fill(n)` 是否能实现三段式布局（含 2 条分割线） | 布局 5 个控件 | 用绝对坐标兜底 |
| 内置 `Label`/`TextField`/`ListView`/`LineView` 的属性与事件是否符合预期 | 装配后能显示/输入 | 退回更基础的内置控件或查阅 v2 API |
| `TextField` 的 IME 组字 / Backspace / 光标是否原生可用 | 输入中文测试 | 这是内置控件，不应出错；如异常查 v2 文档 |
| 顶层 `Attribute.Default` 能否让终端原生背景透出（不刷蓝） | 启动后观察背景 | 调整 `ColorScheme.Normal` |
| 终端 resize 时 Pos/Dim 是否自动重布局 | 手动调整窗口大小 | 监听 Resized 事件手动重算 |
| `MainLoop.Invoke` 跨线程是否工作 | 简单跨线程更新测试 | 用 AddIdle 替代 |

### 1.3 技术方案（与父文档一致）

**优先使用 Terminal.Gui 内置控件，不自绘**：
- `StatusBarView : Label`（设 `Text`）
- `ChatView : ListView`（+ `IListDataSource.Render` 钩子，7c-1 暂用静态字符串列表）
- `InputFieldView : TextField`（仅覆写 Enter/Esc）
- 分割线用内置 `LineView`
- 顶层 `ColorScheme.Normal = Attribute.Default` 让终端原生背景透出

### 1.4 非目标（明确不做）

- ❌ 不接入 AgentLoop（不调 provider，不跑 ReAct 循环）
- ❌ 不做流式渲染（ChatView 只放静态占位）
- ❌ 不做 HITL（输入框只回显，不触发工具）
- ❌ 不做 Spinner 动画
- ❌ 不删除旧文件（TuiApp 等保留，TerminalApp 与之并存）
- ❌ 不移除 Spectre.Console 依赖
- ❌ 不移除 IConsole 抽象

### 1.5 与旧代码并存策略

本迭代**新增** `TerminalApp`，**不修改** `TuiApp`。通过配置开关切换：

```yaml
# .parrotcode.yaml
tui:
  mode: "terminal"  # "terminal"=新 TerminalApp | "console"=旧 TuiApp（默认仍为 console）
```

`App.cs` 根据 `mode` 字段选择装配 `TerminalApp` 或 `TuiApp`。本迭代结束后默认仍是 `console`，手动切 `terminal` 验证。

---

## 二、文件改动清单

### 2.1 新增文件（4 个）

```
Tui/
├── TerminalApp.cs           # Terminal.Gui 主应用骨架（装配内置控件）
├── StatusBarView.cs          # 顶部状态栏 : Label（内置）+ 静态内容
├── ChatView.cs               # 对话区 : ListView（内置）+ 静态占位 + 滚动
└── InputFieldView.cs         # 底部输入框 : TextField（内置）+ Enter/Esc
```

### 2.2 修改文件（2 个）

```
Tui/
└── IConsole.cs               # 不改（保留兼容）
App/
└── App.cs                    # 根据 tui.mode 装配 TerminalApp 或 TuiApp
ParrotCode.Net.csproj          # 新增 Terminal.Gui v2 包引用
Config/Models.cs               # TuiConfig.Mode 增加 "terminal" 选项
```

### 2.3 不动的文件

```
Tui/TuiApp.cs                  # 保留，与 TerminalApp 并存
Tui/StatusBar.cs               # 保留
Tui/InputReader.cs             # 保留
Tui/ConsoleEventRenderer.cs    # 保留
Tui/HitlPrompt.cs              # 保留
Tui/IHitlGate.cs               # 保留
Tui/HitlDecision.cs            # 保留
所有 Agent 层文件               # 不动
```

---

## 三、详细设计

### 3.1 TerminalApp——主应用骨架

**职责**：装配 Terminal.Gui v2 生命周期 + 三段式布局。本迭代不接 AgentLoop，只做静态展示 + 输入回显。

```csharp
using Microsoft.Extensions.Logging;
using Terminal.Gui.App;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

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

    private IApplication? _app;
    private Window? _top;
    private StatusBarView? _statusBarView;
    private LineView? _statusDivider;   // 状态栏底部分割线（内置 LineView）
    private ChatView? _chatView;
    private LineView? _inputDivider;    // 输入框顶部分割线（内置 LineView）
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

    public async Task RunAsync()
    {
        // 1. 装配工具注册中心（供状态栏显示 toolCount，7c-2 才真正使用）
        _registry = new ToolRegistry();
        _registry.Register(new ReadFileTool());
        _registry.Register(new WriteFileTool());
        _registry.Register(new EditFileTool());
        _registry.Register(new GlobTool());
        _registry.Register(new GrepTool());
        _registry.Register(new RunCommandTool());

        // 2. Terminal.Gui 必须在主线程初始化
        _app = Application.Create();
        _app.Init();

        // 3. 构建三段式布局
        BuildLayout();

        // 4. 启动输入处理（7c-1 只回显，不接 Agent）
        // 用 MainLoop.Invoke 启动异步循环，不阻塞事件循环
        _app.MainLoop.Invoke(async () => await InputEchoLoopAsync());

        // 5. 运行应用（阻塞直到 RequestStop）
        _app.Run(_top!);
    }

    private void BuildLayout()
    {
        // 顶层不刷背景：用 Attribute.Default 让终端原生背景透出
        _top = new Window { Title = "ParrotCode.Net" };
        _top.ColorScheme = new ColorScheme { Normal = Attribute.Default };

        // 顶部状态栏（内置 Label 子类，固定 1 行）
        _statusBarView = new StatusBarView
        {
            X = 0, Y = 0, Width = Dim.Fill(), Height = 1
        };
        _statusBarView.Update(_providerConfig, _securityLevel, _tuiConfig, _registry!);

        // 状态栏底部分割线（内置 LineView）
        _statusDivider = new LineView(Orientation.Horizontal)
        {
            X = 0, Y = Pos.Bottom(_statusBarView), Width = Dim.Fill(), Height = 1
        };

        // 中间对话区（内置 ListView 子类，填充剩余，底部留 2 行给分割线+输入框）
        _chatView = new ChatView
        {
            X = 0,
            Y = Pos.Bottom(_statusDivider),   // = 2
            Width = Dim.Fill(),
            Height = Dim.Fill(2)              // 底部预留 2 行（分割线 + 输入框）
        };
        // 7c-1：放静态占位内容
        _chatView.AppendStaticMessage("ParrotCode.Net Terminal 模式（7c-1 骨架）");
        _chatView.AppendStaticMessage("输入框仅回显，不接 Agent（7c-2 接入）");

        // 输入框顶部分割线（内置 LineView）
        _inputDivider = new LineView(Orientation.Horizontal)
        {
            X = 0, Y = Pos.Bottom(_chatView), Width = Dim.Fill(), Height = 1
        };

        // 底部输入框（内置 TextField 子类，固定 1 行）
        _inputFieldView = new InputFieldView
        {
            X = 0,
            Y = Pos.Bottom(_inputDivider),    // 贴在分割线下方
            Width = Dim.Fill(),
            Height = 1
        };
        _inputFieldView.Submit += OnInputSubmit;
        _inputFieldView.ExitRequested += () => _app?.RequestStop();

        _top.Add(_statusBarView, _statusDivider, _chatView, _inputDivider, _inputFieldView);
        _inputFieldView.SetFocus();  // 焦点给输入框
    }

    /// <summary>
    /// 7c-1 输入回显循环：读输入 → 显示到对话区 → 继续等待。
    /// 不接 AgentLoop。
    /// </summary>
    private async Task InputEchoLoopAsync()
    {
        while (!_ct.IsCancellationRequested)
        {
            var line = await _inputFieldView!.WaitForSubmitAsync(_ct);
            if (line is null) break;

            // 斜杠命令硬编码分发（保留 7a 的退出逻辑）
            if (line is "/exit" or "/quit")
            {
                _app?.RequestStop();
                break;
            }
            if (line is "/clear")
            {
                _chatView!.Clear();
                continue;
            }

            // 7c-1：只回显，不接 Agent
            _chatView!.AppendStaticMessage($"❯ {line}");
            _chatView.AppendStaticMessage("⏺ （7c-1 骨架：Agent 未接入，输入仅回显）");
        }
        _app?.RequestStop();
    }

    private void OnInputSubmit(string line)
    {
        // Submit 事件由 InputFieldView 触发，实际处理在 InputEchoLoopAsync 中通过 Channel
        // 这里不需要做任何事，事件已通过 _submitChannel 传递
    }

    public void Dispose()
    {
        _app?.Dispose();
    }
}
```

**关键设计点**：
- **不接 Agent**：`InputEchoLoopAsync` 只回显，验证输入框 + 对话区 + 布局正常工作。
- **并存策略**：不修改 TuiApp，App.cs 根据 config.mode 选择装配。
- **生命周期**：`Application.Create().Init()` → `BuildLayout()` → `Run(top)` → `Dispose()`。

### 3.2 StatusBarView——顶部状态栏（内置 Label）

**职责**：固定顶部 1 行，显示 Provider/Model/Security/Context/Round/Tools。**不自绘**——继承内置 `Label`，只格式化字符串并设 `Text`。

```csharp
using Terminal.Gui.Drawing;
using Terminal.Gui.Views;

namespace ParrotCode;

/// <summary>
/// 顶部状态栏（迭代 7c-1：继承内置 Label，静态内容）。
/// 7c-1 不实时更新（不接 Agent），7c-2 接入 RoundStartEvent 后实时更新 round。
/// </summary>
internal sealed class StatusBarView : Label
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
        set { _estimatedTokens = value; RefreshText(); }
    }

    public int CurrentRound
    {
        get => _currentRound;
        set { _currentRound = value; RefreshText(); }
    }

    public StatusBarView()
    {
        CanFocus = false;  // 状态栏不获取焦点
        // 背景透明继承顶层；仅设前景色
        ColorScheme = new ColorScheme { Normal = new Attribute(Color.White, Color.Black) };
    }

    /// <summary>初始化状态栏数据。</summary>
    public void Update(ProviderConfig config, SecurityLevel level, TuiConfig tui, ToolRegistry registry)
    {
        _providerConfig = config;
        _securityLevel = level;
        _contextWindowTokens = tui.ContextWindowTokens ?? 64000;
        _toolCount = registry.GetAll().Count;
        RefreshText();
    }

    /// <summary>格式化并刷新 Text（Label 设 Text 即自动重绘，无需 OnDrawingContent）。</summary>
    private void RefreshText()
    {
        if (_providerConfig is null) return;
        var pct = _contextWindowTokens > 0 ? (int)((double)_estimatedTokens / _contextWindowTokens * 100) : 0;
        Text = $"provider={_providerConfig.Name} model={_providerConfig.Model} " +
               $"security={_securityLevel} ctx={pct}%({_estimatedTokens}/{_contextWindowTokens}) " +
               $"round={_currentRound} tools={_toolCount}";
    }
}
```

> **不再有 `OnDrawingContent` / `DrawText`**：Label 自带文本渲染，设 `Text` 即重绘。ctx 占比的阈值颜色如需分段，7c-3 用 `TextFormatter` markup 处理。

### 3.3 ChatView——对话区（内置 ListView，静态占位）

**职责**：中间滚动区，7c-1 只放静态占位内容，验证滚动能力。**不自绘滚动**——继承内置 `ListView`，滚动/滚轮/resize 由控件原生处理。

```csharp
using System.Collections;
using Terminal.Gui.ConsoleDrivers;
using Terminal.Gui.Views;

namespace ParrotCode;

/// <summary>
/// 对话区（迭代 7c-1：继承内置 ListView，静态占位 + 滚动能力验证）。
/// 7c-2 将扩展为 RenderEvent(AgentEvent) + DrawItem 上色。
/// </summary>
internal sealed class ChatView : ListView
{
    private readonly List<string> _lines = new();

    public ChatView()
    {
        CanFocus = false;  // 不抢焦点（焦点给输入框）；鼠标滚轮仍可滚动
        SetSource(_lines);
    }

    /// <summary>追加静态消息（7c-1 用，7c-2 改为 RenderEvent）。</summary>
    public void AppendStaticMessage(string text)
    {
        _lines.Add(text);
        SetSource(_lines);  // 通知 ListView 数据变化
        ScrollToBottom();
    }

    /// <summary>清空对话区。</summary>
    public void Clear()
    {
        _lines.Clear();
        SetSource(_lines);
    }

    /// <summary>滚动到底部（ListView 原生：选中最后一项即滚入可视）。</summary>
    private void ScrollToBottom()
    {
        if (_lines.Count > 0)
            SelectedIndex = _lines.Count - 1;
    }
}
```

> **不再有 `SetContentSize` / `Viewport` / `ScrollToBottom` 自实现**：ListView 原生支持滚动条与滚轮。7c-2 用 `IListDataSource.Render` 钩子按消息类型上色，7c-1 暂用纯字符串列表。

### 3.4 InputFieldView——底部输入框（继承 TextField）

**职责**：固定底部 1 行，支持文本输入 + Enter 提交 + Esc 退出。**不自绘、不自处理 Backspace/IME/光标**——继承内置 `TextField`，7c-1 只覆写 Enter/Esc。

```csharp
using System.Threading.Channels;
using Terminal.Gui.Drawing;
using Terminal.Gui.Views;

namespace ParrotCode;

/// <summary>
/// 底部输入框（迭代 7c-1：继承内置 TextField）。
/// TextField 原生处理：普通字符/Backspace/方向键/IME 组字/光标/鼠标选区。
/// 本迭代只覆写 Enter（提交）+ Esc（退出）。7c-3 加 Tab 补全 + 历史导航。
/// </summary>
internal sealed class InputFieldView : TextField
{
    private readonly Channel<string> _submitChannel = Channel.CreateUnbounded<string>();

    /// <summary>提交事件的 ChannelReader。主循环用 ReadAllAsync 等待。</summary>
    public ChannelReader<string> Submits => _submitChannel.Reader;

    /// <summary>提交事件（兼容旧代码风格）。</summary>
    public event Action<string>? Submit;

    /// <summary>退出请求事件（Esc 按下）。</summary>
    public event Action? ExitRequested;

    public InputFieldView()
    {
        CanFocus = true;
        // 提示符用 Caption（占位），输入有内容时自动隐藏
        Caption = "> ";
        CaptionColor = new Attribute(Color.BrightBlue, Color.Black);
        // 背景透明继承顶层；前景白
        ColorScheme = new ColorScheme { Normal = new Attribute(Color.White, Color.Black) };
    }

    /// <summary>等待用户提交输入。</summary>
    public async Task<string?> WaitForSubmitAsync(CancellationToken ct)
    {
        try { return await _submitChannel.Reader.ReadAsync(ct); }
        catch (OperationCanceledException) { return null; }
    }

    protected override bool OnKeyDown(Key key)
    {
        // Enter——提交（直接读 TextField.Text）
        if (key.KeyCode == KeyCode.Enter)
        {
            var line = Text?.ToString() ?? "";
            Text = "";  // TextField 原生清空 + 重绘
            _submitChannel.Writer.TryWrite(line);
            Submit?.Invoke(line);
            return true;
        }

        // Esc——退出
        if (key.KeyCode == KeyCode.Esc)
        {
            ExitRequested?.Invoke();
            return true;
        }

        // 其余按键（含中文 IME 组字、Backspace、左右、Home/End）交给 TextField 基类
        return base.OnKeyDown(key);
    }
}
```

> **不再有 `StringBuilder _buffer` / `OnDrawingContent`**：缓冲即 `TextField.Text`，提示符即 `Caption`，输入/Backspace/IME 光标全部原生。7c-1 验证中文输入法光标定位是否正确（应不再出现临时字母漂到 `>` 左侧的问题）。

### 3.5 App.cs 修改——并存装配

```csharp
// App.cs 修改后的 RunAsync
public async Task RunAsync()
{
    var tuiConfig = _config.Tui ?? new TuiConfig();
    var securityLevel = ParseSecurityLevel(_config.Security?.Level);

    // 根据 tui.mode 选择装配
    if (tuiConfig.Mode == "terminal")
    {
        // 7c-1：新 TerminalApp（Terminal.Gui v2）
        using var terminalApp = new TerminalApp(
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
        // 旧 TuiApp（Spectre.Console 流式）
        var tuiApp = new TuiApp(_provider, _providerConfig, _config.Agent, tuiConfig,
                                securityLevel, _logger, _ct);
        await tuiApp.RunAsync();
    }
}
```

### 3.6 csproj 修改

```xml
<PackageReference Include="Terminal.Gui" Version="2.0.0" />
```

---

## 四、验收标准

### 4.1 编译

| 编号 | 标准 | 验证方式 |
|------|------|---------|
| 7c1-01 | `dotnet build` 0 错误 0 警告 | 编译 |
| 7c1-02 | Terminal.Gui v2 包正确引用 | 编译通过 |

### 4.2 启动与布局

| 编号 | 标准 | 验证方式 |
|------|------|---------|
| 7c1-03 | 配置 `tui.mode: "terminal"` 能启动 Terminal.Gui 窗口 | 手动运行 |
| 7c1-04 | 窗口标题为 "ParrotCode.Net" | 手动 |
| 7c1-05 | 顶部状态栏固定 1 行，显示 provider/model/security/ctx/round/tools | 手动 |
| 7c1-06 | 底部输入框固定 1 行，显示 "> " 提示符（Caption） | 手动 |
| 7c1-07 | 中间对话区占满剩余空间 | 手动 |
| 7c1-08 | 三段布局无重叠 | 手动 |
| 7c1-08a | 状态栏下方与输入框上方各有一条分割线（LineView） | 手动 |
| 7c1-08b | 背景为终端原生色，不被刷成蓝色 | 手动 |

### 4.3 输入与回显

| 编号 | 标准 | 验证方式 |
|------|------|---------|
| 7c1-09 | 输入框能打字，实时回显（TextField 原生） | 手动 |
| 7c1-10 | Backspace 删除正确（TextField 原生） | 手动 |
| 7c1-11 | 中文输入正确显示，IME 组字时光标在正确位置（不在 `>` 左侧） | 手动：输入"你好" |
| 7c1-12 | Enter 提交后，输入出现在对话区 | 手动 |
| 7c1-13 | `/exit` 命令能退出程序 | 手动 |
| 7c1-14 | Esc 能退出程序 | 手动 |
| 7c1-15 | `/clear` 能清空对话区 | 手动 |

### 4.4 布局稳定性

| 编号 | 标准 | 验证方式 |
|------|------|---------|
| 7c1-16 | 终端 resize 后三段布局自动适配，不重叠 | 手动：调整窗口大小 |
| 7c1-17 | 状态栏始终在顶部，不随内容滚动消失 | 手动：追加多条消息后 |
| 7c1-18 | 输入框始终在底部，不随内容滚动消失 | 手动：追加多条消息后 |

### 4.5 滚动验证

| 编号 | 标准 | 验证方式 |
|------|------|---------|
| 7c1-19 | 对话区内容超出可视区时出现滚动条 | 手动：追加 50 条消息 |
| 7c1-20 | 新内容追加后自动滚动到底部 | 手动 |

### 4.6 旧代码不受影响

| 编号 | 标准 | 验证方式 |
|------|------|---------|
| 7c1-21 | 配置 `tui.mode: "console"`（或不配置）仍走旧 TuiApp | 手动 |
| 7c1-22 | 旧 TuiApp 的所有测试仍全绿 | `dotnet test` |
| 7c1-23 | Agent 层测试不受影响 | `dotnet test` |

---

## 五、测试计划

### 5.1 新增单元测试

| 测试文件 | 覆盖范围 | 用例数 |
|---------|---------|--------|
| `StatusBarViewTests.cs` | 状态栏数据更新、内容格式化 | 3 |
| `ChatViewTests.cs` | 消息追加、清空、滚动 | 3 |
| `InputFieldViewTests.cs` | 输入、Backspace、Enter 提交、Esc 退出 | 4 |

> **注**：Terminal.Gui View 测试需要 `FakeDriver`（无头驱动）。测试初始化时：
> ```csharp
> using IApplication app = Application.Create(driver: new FakeDriver());
> app.Init();
> // 测试代码
> app.Dispose();
> ```

### 5.2 保留的测试

所有 7a/7b 的测试**不变**。旧 TuiApp 仍存在，其测试仍有效。

---

## 六、实施步骤

### 步骤 1：新增依赖 + 空骨架

- `ParrotCode.Net.csproj` 添加 `Terminal.Gui` v2 包引用
- 创建 `TerminalApp.cs` 空骨架（只有 `Application.Create().Init()` + `Run(new Window())`）
- `App.cs` 加 `mode == "terminal"` 分支装配 TerminalApp
- **验证**：`dotnet run` + 配置 `mode: terminal` 能启动空白窗口

### 步骤 2：三段式布局（内置控件）

- 实现 `StatusBarView : Label`（固定顶部，静态内容）
- 实现 `ChatView : ListView`（中间，静态占位，原生滚动）
- 实现 `InputFieldView : TextField`（固定底部，Caption 提示符）
- 加 2 条内置 `LineView` 分割线
- 顶层 `ColorScheme.Normal = Attribute.Default`（背景透出）
- `TerminalApp.BuildLayout` 装配 5 个控件，焦点给输入框
- **验证**：能看到三段布局 + 分割线，背景为终端原生色，输入框能打字

### 步骤 3：输入回显循环

- `InputFieldView` 仅覆写 Enter/Esc（Backspace/IME 交给 TextField 基类）
- `TerminalApp.InputEchoLoopAsync` 读取输入 → 显示到 ChatView
- 斜杠命令（/exit /clear）分发
- **验证**：输入 → 回显 → 继续等待；中文输入法光标位置正确

### 步骤 4：滚动 + resize 验证

- `ChatView` 用 ListView 原生滚动（`SelectedIndex` 滚到底）
- 追加多条消息测试滚动 + 鼠标滚轮
- 终端 resize 测试布局自适应
- **验证**：滚动正常，resize 不乱

### 步骤 5：测试 + 验收

- 编写 StatusBarView/ChatView/InputFieldView 单元测试
- 全量 `dotnet test`
- 对照验收标准 7c1-01 到 7c1-23 逐项确认

---

## 七、风险与对策

| 风险 | 概率 | 影响 | 对策 |
|------|------|------|------|
| Terminal.Gui v2 内置控件 API 与文档结论有差异 | 中 | 高 | 步骤 1 先做空骨架验证 Label/TextField/ListView/LineView |
| 顶层 `Attribute.Default` 仍刷蓝背景 | 中 | 高 | 改用 `ColorScheme` 全局覆盖 `Colors.Base`，或显式设 `Normal = new Attribute(Color.White, Color.Black)` 兜底 |
| `TextField.Caption` 不符合提示符预期 | 中 | 低 | 退回在输入框左侧放一个独立 `Label`（"> "）作为提示符 |
| FakeDriver 测试支持不完善 | 中 | 中 | 关键逻辑用纯 C# 测试，渲染逻辑用集成测试 |
| 中文/宽字符渲染问题 | 低 | 中 | TextField 原生支持，步骤 3 验证中文输入 |
| `MainLoop.Invoke` 跨线程行为不符 | 低 | 中 | 用 `AddIdle` 替代 |

---

## 八、与后续迭代的衔接

### 7c-2（事件流 + 流式渲染）

- `TerminalApp` 的 `InputEchoLoopAsync` 改为 `InputLoopAsync`，接入 AgentLoop
- `ChatView` 新增 `RenderEvent(AgentEvent)`，替换 `AppendStaticMessage`
- `StatusBarView` 接入 RoundStartEvent 实时更新 round
- 新增 `ChatMessage` 数据模型

### 7c-3（HITL 模态 + 收尾）

- 新增 `HitlDialog` 模态对话框
- `HitlPrompt` 改为委托 `HitlDialog`
- 新增 `SpinnerIndicator`
- `InputFieldView` 增强 Tab 补全 + 历史导航
- 删除旧文件（TuiApp/StatusBar/InputReader/ConsoleEventRenderer）
- 移除 IConsole（可选）

---

**文档结束**。状态：[设计完成，待实现]
