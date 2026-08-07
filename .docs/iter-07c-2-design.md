# 迭代 7c-2：事件流接入 + 流式渲染

> **状态**：[设计完成，待实现]
> **前置迭代**：7c-1 [待实现]
> **父文档**：iter-07c-design.md（保留追溯）
> **后续迭代**：7c-3（HITL 模态 + 收尾）
> **目标**：接入 AgentLoop + ChannelEventSink，实现流式文本 + 工具卡片渲染 + 线程模型验证。不做 HITL。

---

## 一、迭代目标

### 1.1 核心目标

在 7c-1 的静态布局骨架上，接入 Agent 层的事件流，实现 Claude Code 风格的流式渲染：

```
┌─────────────────────────────────────────────────────────────────┐
│  provider=deepseek model=deepseek-chat security=Normal ctx=...  │  ← 状态栏（round 实时更新）
├─────────────────────────────────────────────────────────────────┤
│  ❯ 写一份《赠汪伦》的赏析                                         │  ← 用户消息
│  ⏺ 我来写一份赏析并保存。                                         │  ← 助手流式回复
│    ⎿ → write_file({"path": "d:/zwl.md", ...})                    │  ← 工具调用卡片
│    ⎿ ✓ 已写入 2975 字节                                          │  ← 工具结果卡片
│  ⏺ 已为你写好赏析并保存到 d:/zwl.md。                            │  ← 助手总结
├─────────────────────────────────────────────────────────────────┤
│  > _                                                             │  ← 输入框
└─────────────────────────────────────────────────────────────────┘
```

### 1.2 本迭代要验证的关键问题

| 问题 | 验证方式 | 失败对策 |
|------|---------|---------|
| AgentLoop 在后台线程跑，Terminal.Gui UI 在主线程，如何安全更新 | 流式文本能实时显示 | 用 MainLoop.AddIdle 分帧 |
| Channel<AgentEvent> 的事件流如何不阻塞事件循环 | UI 不卡顿 | AddIdle 批量处理 |
| 流式 TextDelta 如何高效更新 ChatView（不每次重建全部） | 文本流畅追加 | 增量更新最后一条消息 |
| 工具调用/结果卡片的颜色和格式 | 视觉正确 | 调整 Attribute 设置 |

### 1.3 非目标（明确不做）

- ❌ 不做 HITL（enable_hitl=false，注入 NullHitlGate）
- ❌ 不做 Spinner 动画（7c-3）
- ❌ 不做 Tab 补全增强（7c-3）
- ❌ 不删除旧文件
- ❌ 不移除 Spectre.Console 依赖

### 1.4 HITL 处理策略

本迭代 `enable_hitl: false`，注入 `NullHitlGate`——所有工具直接执行，不弹确认。这样可以把"事件流 + 流式渲染"单独验证，不与 HITL 的复杂度耦合。

---

## 二、文件改动清单

### 2.1 新增文件（1 个）

```
Tui/
└── ChatMessage.cs            # 单条消息数据模型（类型 + 内容 + 颜色）
```

### 2.2 修改文件（2 个）

```
Tui/
├── TerminalApp.cs            # 接入 AgentLoop + 事件流消费
└── ChatView.cs               # RenderEvent 实现（替换 AppendStaticMessage）
```

### 2.3 不动的文件

```
Tui/StatusBarView.cs          # 不改（7c-1 已实现，本迭代只用 EstimatedTokens/CurrentRound 属性）
Tui/InputFieldView.cs         # 不改（7c-1 已实现基础输入）
Tui/HitlPrompt.cs             # 不改（本迭代不启用 HITL）
所有 Agent 层文件             # 不动
所有 7c-1 新增文件            # 继承使用
```

---

## 三、详细设计

### 3.1 ChatMessage——消息数据模型

**职责**：定义消息类型 + 格式化 + 颜色映射。

```csharp
using Terminal.Gui.Drawing;

namespace ParrotCode;

/// <summary>
/// 对话消息类型。参照 Claude Code 的 5 种消息类型扩展。
/// </summary>
internal enum MessageType
{
    User,        // 用户消息（❯ 前缀）
    Assistant,   // 助手回复（⏺ 前缀）
    ToolCall,    // 工具调用（⎿ → 前缀）
    ToolResult,  // 工具结果成功（⎿ ✓ 前缀）
    ToolError,   // 工具失败（⎿ ✗ 前缀）
    System,      // 系统提示（── Round N ── 等）
    Warning,     // 警告（⚠ 前缀）
    Error        // 错误（✗ 前缀）
}

/// <summary>
/// 单条对话消息。包含类型 + 内容，提供格式化和颜色映射。
/// </summary>
internal sealed record ChatMessage(MessageType Type, string Content)
{
    /// <summary>
    /// 格式化为带前缀的显示字符串。
    /// </summary>
    public string Format() => Type switch
    {
        MessageType.User       => $"❯ {Content}",
        MessageType.Assistant  => $"⏺ {Content}",
        MessageType.ToolCall   => Content,   // 已含 ⎿ 前缀
        MessageType.ToolResult => Content,
        MessageType.ToolError  => Content,
        MessageType.System     => Content,
        MessageType.Warning    => Content,
        MessageType.Error      => Content,
        _ => Content
    };

    /// <summary>
    /// 获取该消息类型的前景色。
    /// </summary>
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

### 3.2 ChatView 改造——RenderEvent 实现

**职责**：在 7c-1 静态基础上，新增 `RenderEvent(AgentEvent)` 方法，消费事件流。

```csharp
using System.Text;
using System.Threading.Channels;
using Terminal.Gui.App;
using Terminal.Gui.Drawing;
using Terminal.Gui.ViewBase;

namespace ParrotCode;

/// <summary>
/// 对话区 View（迭代 7c-2：接入 AgentEvent 流式渲染）。
/// 消息列表模型 + 流式文本缓冲 + 自动滚动。
/// </summary>
internal sealed class ChatView : View
{
    private readonly List<ChatMessage> _messages = new();
    private readonly StringBuilder _currentText = new();  // 当前流式文本缓冲
    private bool _hasStreamingText;

    public ChatView()
    {
        CanFocus = true;
        ViewportSettings = ViewportSettingsFlags.HasVerticalScrollBar;
        SetContentSize(new Size(0, 0));
    }

    // ===== 7c-1 保留方法（兼容静态占位）=====

    /// <summary>追加静态消息（7c-1 用，7c-2 保留兼容）。</summary>
    public void AppendStaticMessage(string text)
    {
        _messages.Add(new ChatMessage(MessageType.System, text));
        RebuildContent();
    }

    /// <summary>清空对话区。</summary>
    public void Clear()
    {
        _messages.Clear();
        _currentText.Clear();
        _hasStreamingText = false;
        RebuildContent();
    }

    // ===== 7c-2 新增方法 =====

    /// <summary>追加用户消息。</summary>
    public void AppendUserMessage(string text)
    {
        FlushCurrentText();
        _messages.Add(new ChatMessage(MessageType.User, text));
        RebuildContent();
    }

    /// <summary>
    /// 渲染 Agent 事件（流式）。
    /// 由 TerminalApp 的事件消费循环调用（通过 MainLoop 调度到主线程）。
    /// </summary>
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
                // 流式追加到缓冲，不立即 flush
                _currentText.Append(text);
                _hasStreamingText = true;
                UpdateStreamingText();
                break;

            case AgentEvent.AssistantMessageEvent:
                // 文本已在 TextDelta 实时展示，此处不处理
                break;

            case AgentEvent.ToolCallStartEvent(var call):
                FlushCurrentText();
                _messages.Add(new ChatMessage(MessageType.ToolCall,
                    $"  ⎿ → {call.Name}({Truncate(call.Input.GetRawText(), 80)})"));
                RebuildContent();
                break;

            case AgentEvent.ToolResultEvent(_, var result):
                FlushCurrentText();
                var icon = result.Success ? "✓" : "✗";
                var content = result.Success
                    ? Truncate(result.Content, 200)
                    : (result.Error ?? "未知错误");
                _messages.Add(new ChatMessage(
                    result.Success ? MessageType.ToolResult : MessageType.ToolError,
                    $"  ⎿ {icon} {content}"));
                RebuildContent();
                break;

            case AgentEvent.ToolBlockedEvent(var call, var reason):
                FlushCurrentText();
                _messages.Add(new ChatMessage(MessageType.System,
                    $"  ⎿ ⛔ 拦截 {call.Name}: {reason}"));
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

            case AgentEvent.WarningEvent(var msg):
                FlushCurrentText();
                _messages.Add(new ChatMessage(MessageType.Warning, $"⚠ {msg}"));
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

            case AgentEvent.RoundEndEvent:
                // 不渲染
                break;
        }
    }

    /// <summary>
    /// 把流式缓冲的文本 flush 到消息列表。
    /// 在工具调用/结果/轮次结束等边界点调用。
    /// </summary>
    private void FlushCurrentText()
    {
        if (_hasStreamingText && _currentText.Length > 0)
        {
            _messages.Add(new ChatMessage(MessageType.Assistant, _currentText.ToString()));
            _currentText.Clear();
            _hasStreamingText = false;
            RebuildContent();
        }
    }

    /// <summary>
    /// 重建全部内容并自动滚到底部。
    /// 简单实现：每次重建全部文本。消息量大时优化为增量更新（迭代 9+）。
    /// </summary>
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

    /// <summary>
    /// 流式更新当前文本（不重建全部，性能优化）。
    /// 7c-2 简单实现：直接 RebuildContent。消息少时足够快。
    /// </summary>
    private void UpdateStreamingText()
    {
        // TODO: 性能优化——只更新最后一条消息的文本，不重建全部
        // 7c-2 先用简单实现，迭代 9+ 优化
        RebuildContent();
    }

    /// <summary>自动滚动到底部。</summary>
    private void ScrollToBottom()
    {
        var contentHeight = GetContentSize().Height;
        Viewport = Viewport with
        {
            Location = new Point(0, Math.Max(0, contentHeight - Viewport.Height))
        };
    }

    private static string Truncate(string s, int max) =>
        s.Length <= max ? s : s[..(max - 3)] + "...";
}
```

### 3.3 TerminalApp 改造——接入 AgentLoop

**职责**：把 7c-1 的 `InputEchoLoopAsync` 改为 `InputLoopAsync`，接入 AgentLoop + 事件流消费。

```csharp
// TerminalApp.cs 的关键改动（7c-2）

/// <summary>
/// 输入循环：读输入 → 启动 AgentLoop → 消费事件流 → 继续等待。
/// 7c-2 接入 Agent，enable_hitl=false（NullHitlGate）。
/// </summary>
private async Task InputLoopAsync()
{
    var history = new ConversationHistory();

    while (!_ct.IsCancellationRequested)
    {
        var line = await _inputFieldView!.WaitForSubmitAsync(_ct);
        if (line is null) break;

        // 斜杠命令硬编码分发（保留 7c-1 逻辑）
        if (line is "/exit" or "/quit")
        {
            _app?.RequestStop();
            break;
        }
        if (line is "/clear")
        {
            _chatView!.Clear();
            history.Clear();
            continue;
        }
        if (line is "/help")
        {
            _chatView!.AppendStaticMessage("可用命令：/clear /exit /help /status");
            continue;
        }
        if (string.IsNullOrWhiteSpace(line)) continue;

        // 显示用户消息
        _chatView!.AppendUserMessage(line);
        history.AddUser(line);

        // 更新状态栏
        _statusBarView!.CurrentRound = 0;
        _statusBarView.EstimatedTokens = history.EstimatedTokens;

        // 启动 AgentLoop + 消费事件流
        await RunAgentRoundAsync(history);
    }

    _app?.RequestStop();
}

/// <summary>
/// 启动一轮 AgentLoop，消费事件流，通过 MainLoop 调度到主线程更新 UI。
/// </summary>
private async Task RunAgentRoundAsync(ConversationHistory history)
{
    // 7c-2：enable_hitl=false，注入 NullHitlGate
    var executor = new ToolExecutor(_registry!, TimeSpan.FromSeconds(_agentConfig.ToolTimeoutSeconds ?? 30), _logger);
    var hitlGate = new NullHitlGate();  // 7c-2 不启用 HITL
    var batchExecutor = new BatchToolExecutor(executor, _registry!,
                                               _agentConfig.MaxParallelism ?? 5,
                                               hitlGate: hitlGate,
                                               _logger);

    var sink = new ChannelEventSink();
    var agentLoop = new AgentLoop(_provider!,
                                  _registry!,
                                  batchExecutor,
                                  _agentConfig.MaxRounds ?? 10,
                                  _agentConfig.ToolChoice ?? "auto",
                                  _agentConfig.SystemPrompt,
                                  logger: null);  // 不给 logger，避免 stderr 交错

    var agentTask = agentLoop.RunAsync(history, sink, _ct);

    // 消费事件流——用 AddIdle 分帧处理，不阻塞事件循环
    var idleToken = _app!.MainLoop.AddIdle(() =>
    {
        // 每帧批量处理最多 20 个事件
        for (int i = 0; i < 20; i++)
        {
            if (!sink.Reader.TryRead(out var evt))
            {
                // 暂无事件
                // 如果 agentTask 已完成且 Channel 已关闭，返回 false 停止 Idle
                return !agentTask.IsCompleted;
            }

            // 处理事件（在主线程，因为 AddIdle 回调在主线程执行）
            ProcessEvent(evt);
        }
        return !agentTask.IsCompleted;  // agentTask 完成后停止 Idle
    });

    await agentTask;  // 等待 AgentLoop 完成

    // 确保 Idle 停止
    _app.MainLoop.RemoveIdle(idleToken);

    // 消费剩余事件
    while (sink.Reader.TryRead(out var evt))
    {
        ProcessEvent(evt);
    }

    // 更新状态栏的 token 估算
    _statusBarView!.EstimatedTokens = history.EstimatedTokens;
}

/// <summary>处理单个 Agent 事件（在主线程执行）。</summary>
private void ProcessEvent(AgentEvent evt)
{
    // 更新状态栏轮次
    if (evt is AgentEvent.RoundStartEvent(var r))
        _statusBarView!.CurrentRound = r;

    // 渲染到对话区
    _chatView!.RenderEvent(evt);
}
```

### 3.4 TerminalApp 装配补充

```csharp
// TerminalApp.RunAsync 改动（7c-2）
public async Task RunAsync()
{
    // 装配工具注册中心
    _registry = new ToolRegistry();
    _registry.Register(new ReadFileTool());
    _registry.Register(new WriteFileTool());
    _registry.Register(new EditFileTool());
    _registry.Register(new GlobTool());
    _registry.Register(new GrepTool());
    _registry.Register(new RunCommandTool());

    // Terminal.Gui 初始化
    _app = Application.Create();
    _app.Init();

    // 构建布局
    BuildLayout();

    // 启动输入循环（7c-2 接入 Agent）
    _app.MainLoop.Invoke(async () => await InputLoopAsync());

    // 运行应用
    _app.Run(_top!);
}
```

> **注**：`_provider` 字段在 7c-1 中没有（因为不接 Agent）。7c-2 需要在 `TerminalApp` 构造函数中新增 `IBaseProvider provider` 参数，由 `App.cs` 传入。

---

## 四、线程模型详解

### 4.1 线程分工

```
主线程（Terminal.Gui 事件循环）          后台线程（AgentLoop）
─────────────────────────────          ──────────────────────
Application.Run() 阻塞                  agentLoop.RunAsync() 开始
  │                                     │
  ├─ MainLoop.Invoke(InputLoopAsync)    │
  │     │                               │
  │     ├─ 等待用户输入（Channel）       │
  │     ├─ AppendUserMessage            │
  │     ├─ agentTask = agentLoop.Run ──→│  调 LLM → 解析 → 执行工具
  │     │                               │  → 写事件到 Channel
  │     ├─ AddIdle(ProcessEvent) ←──────│  事件流
  │     │   │                           │
  │     │   ├─ TryRead(evt)             │
  │     │   ├─ ChatView.RenderEvent     │
  │     │   └─ return !agentTask.Done   │
  │     │                               │
  │     └─ await agentTask              │
  │                                     │
  │  继续等待输入                        │
  └─ RequestStop()                      │
```

### 4.2 AddIdle 的工作原理

`MainLoop.AddIdle(Func<bool>)` 注册一个空闲回调：
- 事件循环每轮检查是否有空闲任务
- 有空闲任务时执行，返回 `true` 继续下一轮，返回 `false` 移除
- **在主线程执行**，所以可以安全更新 UI

**关键**：`AddIdle` 回调不阻塞事件循环——每帧只处理少量事件（批量 20 个），然后返回，让事件循环处理其他事件（如键盘输入、重绘）。

### 4.3 为何不用 await foreach + App.Invoke

```csharp
// ❌ 会阻塞事件循环
_app.MainLoop.Invoke(async () =>
{
    await foreach (var evt in sink.Reader.ReadAllAsync(_ct))  // 阻塞！
    {
        _chatView.RenderEvent(evt);  // 在主线程，但事件循环被卡住
    }
});

// ✅ 用 AddIdle 分帧
_app.MainLoop.AddIdle(() =>
{
    for (int i = 0; i < 20 && sink.Reader.TryRead(out var evt); i++)
    {
        _chatView.RenderEvent(evt);
    }
    return !agentTask.IsCompleted;
});
```

`await foreach` 在 `MainLoop.Invoke` 内会阻塞事件循环，导致 UI 无响应（无法处理键盘/鼠标/重绘）。`AddIdle` 每帧只处理少量事件，然后返回控制权给事件循环。

---

## 五、验收标准

### 5.1 编译与测试

| 编号 | 标准 | 验证方式 |
|------|------|---------|
| 7c2-01 | `dotnet build` 0 错误 0 警告 | 编译 |
| 7c2-02 | Agent 层测试全绿（不受影响） | `dotnet test` |
| 7c2-03 | ChatMessage 单元测试通过 | `dotnet test` |
| 7c2-04 | ChatView RenderEvent 单元测试通过 | `dotnet test` |

### 5.2 流式渲染

| 编号 | 标准 | 验证方式 |
|------|------|---------|
| 7c2-05 | 用户输入后显示 `❯` 前缀消息 | 手动 |
| 7c2-06 | AI 回复以 `⏺` 前缀流式追加 | 手动：能看到逐字显示 |
| 7c2-07 | 工具调用显示 `⎿ → 工具名(参数)` | 手动 |
| 7c2-08 | 工具结果显示 `⎿ ✓/✗ 内容` | 手动 |
| 7c2-09 | 错误以红色显示 | 手动：构造错误场景 |
| 7c2-10 | Round 分隔线显示 `── Round N ──` | 手动 |

### 5.3 状态栏实时更新

| 编号 | 标准 | 验证方式 |
|------|------|---------|
| 7c2-11 | round 随轮次递增 | 手动：多轮工具调用 |
| 7c2-12 | ctx token 估算更新 | 手动 |

### 5.4 自动滚动

| 编号 | 标准 | 验证方式 |
|------|------|---------|
| 7c2-13 | AI 回复时自动滚动到底部 | 手动 |
| 7c2-14 | 长对话能滚动查看历史 | 手动：键盘上下或鼠标 |

### 5.5 UI 响应性

| 编号 | 标准 | 验证方式 |
|------|------|---------|
| 7c2-15 | 流式输出期间 UI 不卡顿 | 手动：流式期间能滚动 |
| 7c2-16 | Esc 能中断当前对话 | 手动 |

### 5.6 多轮稳定性

| 编号 | 标准 | 验证方式 |
|------|------|---------|
| 7c2-17 | 连续 3 轮对话无残影 | 手动 |
| 7c2-18 | 状态栏始终在顶部 | 手动 |
| 7c2-19 | 输入框始终在底部 | 手动 |

### 5.7 无 HITL（本迭代不启用）

| 编号 | 标准 | 验证方式 |
|------|------|---------|
| 7c2-20 | write_file 直接执行，不弹确认 | 手动 |

---

## 六、测试计划

### 6.1 新增单元测试

| 测试文件 | 覆盖范围 | 用例数 |
|---------|---------|--------|
| `ChatMessageTests.cs` | 消息格式化、颜色映射 | 5 |
| `ChatViewEventTests.cs` | RenderEvent 各事件类型、流式缓冲 flush | 6 |

### 6.2 测试策略

**ChatMessage 测试**（纯 C#，不依赖 Terminal.Gui）：
```csharp
[Fact]
public void Format_User_ReturnsWithArrowPrefix()
{
    var msg = new ChatMessage(MessageType.User, "hello");
    msg.Format().Should().Be("❯ hello");
}

[Fact]
public void GetColor_Assistant_ReturnsBrightCyan()
{
    var msg = new ChatMessage(MessageType.Assistant, "text");
    msg.GetColor().Should().Be(Color.BrightCyan);
}
```

**ChatView RenderEvent 测试**（需要 FakeDriver）：
```csharp
[Fact]
public void RenderEvent_TextDelta_BuffersText()
{
    using IApplication app = Application.Create(driver: new FakeDriver());
    app.Init();
    var view = new ChatView();
    view.RenderEvent(new AgentEvent.TextDeltaEvent("Hello"));
    view.RenderEvent(new AgentEvent.TextDeltaEvent(" World"));
    view.RenderEvent(new AgentEvent.AgentDoneEvent(null));
    // 验证消息列表含一条 assistant 消息 "Hello World"
    app.Dispose();
}
```

---

## 七、实施步骤

### 步骤 1：新增 ChatMessage 数据模型

- 创建 `ChatMessage.cs`（MessageType 枚举 + record）
- 编写 `ChatMessageTests.cs`
- **验证**：`dotnet test` ChatMessage 测试通过

### 步骤 2：ChatView RenderEvent 实现

- `ChatView` 新增 `RenderEvent(AgentEvent)` 方法
- 实现所有 12 种事件类型的渲染
- 实现流式缓冲（`_currentText` + `FlushCurrentText`）
- **验证**：单元测试通过

### 步骤 3：TerminalApp 接入 AgentLoop

- `TerminalApp` 构造函数新增 `IBaseProvider provider` 参数
- 实现 `InputLoopAsync`（替换 7c-1 的 `InputEchoLoopAsync`）
- 实现 `RunAgentRoundAsync` + `ProcessEvent`
- `App.cs` 传入 provider
- **验证**：能进行单轮对话（输入 → AI 回复 → 完成）

### 步骤 4：线程模型验证

- 用 `AddIdle` 分帧处理事件流
- 验证流式期间 UI 不卡顿
- 验证 Esc 中断
- **验证**：流式输出流畅，UI 响应正常

### 步骤 5：多轮对话 + 验收

- 多轮对话测试（3 轮以上）
- 长对话滚屏测试
- 对照验收标准 7c2-01 到 7c2-20 逐项确认

---

## 八、风险与对策

| 风险 | 概率 | 影响 | 对策 |
|------|------|------|------|
| AddIdle 分帧导致流式不流畅 | 中 | 中 | 调整批量大小（20→50），或用定时器替代 |
| 跨线程访问 View 导致崩溃 | 低 | 高 | 确保 ProcessEvent 只在 AddIdle 回调（主线程）执行 |
| 流式文本重建性能差 | 中 | 低 | 7c-2 先用简单实现，迭代 9+ 优化增量更新 |
| AgentLoop 异常未捕获导致 UI 卡死 | 低 | 中 | RunAgentRoundAsync 包 try-catch，异常显示到 ChatView |

---

## 九、与后续迭代的衔接

### 7c-3（HITL 模态 + 收尾）

- 把 `NullHitlGate` 替换为 `HitlPrompt`（委托 `HitlDialog`）
- `RunAgentRoundAsync` 的 `hitlGate` 参数改为注入 `HitlPrompt`
- 新增 `SpinnerIndicator`（工具执行时动画）
- `InputFieldView` 增强 Tab 补全 + 历史导航
- 删除旧文件，移除 Spectre.Console 依赖

---

**文档结束**。状态：[设计完成，待实现]
