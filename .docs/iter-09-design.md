# 迭代 9：上下文管理（工具结果截断 + 结构化摘要 + 压缩协调）— 详细设计

> **状态**：[设计完成，待实现]
> **前置迭代**：8a [已完成]、8b [已完成]、8c [已完成]（安全纵深防御全套）
> **后续迭代**：10（斜杠命令 + 会话持久化 + 项目指令）
> **对应 `plan.md` 第三章「迭代 9」**，本文档在其基础上补充实现级细节与可执行的验收清单。
> **参考**：MewCode `conversation/` 模块（`truncator.py` / `summarizer.py` / `compression.py`）。

---

## 一、概述

迭代 4 的 `ConversationHistory` 只做追加，不管理 token 上限。迭代 6 的 ReAct 循环每轮把完整工具结果灌入历史——`read_file` 读一个 10 万行日志就是 1MB+ 的 `Tool` 消息。几轮下来上下文窗口爆炸，LLM 返回 413 或静默截断。

迭代 9 构建两层 token 管理管线：

1. **层 1 — 工具结果截断（`ToolResultTruncator`）**：局部、轻量、无 LLM 调用。单条工具结果 > 50K 字符时写盘留 2K 预览；一轮内多条工具结果合计 > 200K 字符时截断最大的几条。在工具结果**入历史前**执行，历史始终保持紧凑。
2. **层 2 — 结构化摘要（`StructuredSummarizer`）**：全局、昂贵、需 LLM 调用。token > 70% 窗口发警告，> 90% 触发摘要——把旧消息压缩成 9 段结构化摘要 + 保留最近 4 条原文。摘要 Prompt **禁止工具调用**（首尾强调 + `tool_choice=none`），先 draft 再正式，避免摘要过程又触发工具调用导致死循环。含**熔断器**：连续失败 2 次停止自动触发，转人工。
3. **协调器（`ContextCompressor`）**：两层统一入口。`TruncateBatch` 供 AgentLoop 在工具执行后调用；`CheckAndCompressAsync` 供 AgentLoop 在每轮 LLM 调用前调用。

本迭代**刻意保持**：
- **不做斜杠命令 `/compress`**：迭代 10。本迭代仅自动触发 + 内部 API（`Compressor.CheckAndCompressAsync`），为迭代 10 的 `/compress` 手动触发预留接口。
- **不做摘要缓存持久化**：进阶练习，留作扩展。本迭代摘要仅存内存历史。
- **不升级 `TokenEstimator`**：仍用字符数 / 3 近似。精确分词器在后续迭代。
- **不改 `IBaseProvider` 接口**：摘要用现有 `ChatStreamAsync(messages, ct)`（返回 `IAsyncEnumerable<string>`，不传 tools），LLM 不会产出 tool_calls。
- **不改安全层**：截断/摘要与 `SecurityGuard` 无耦合。

> **拆分考量**：是否拆为 9a（Truncator + CircuitBreaker，纯逻辑）+ 9b（Summarizer + Compressor + 集成）？
> - 不拆理由：层 1 + 层 2 是同一管线的两个阶段，验收标准要求"截断 + 摘要 + 熔断"端到端跑通。分开验收 9a 只能验证截断逻辑，无法验证"历史变短后 AI 仍能继续工作"这一核心目标。
> - **结论**：本迭代不拆分，作为整体设计。实施时分 3 步（Step 1 纯逻辑 → Step 2 LLM 集成 → Step 3 端到端装配）。

---

## 二、学习目标

1. **上下文窗口管理**：长对话如何不爆 token。理解"局部截断（单条结果）"与"全局压缩（整段历史）"两层策略的分工——前者是 O(1) 的写盘替换，后者是 O(n) 的 LLM 调用。
2. **熔断器模式（Circuit Breaker）**：摘要依赖 LLM，LLM 可能连续报错（限流 / 超时 / 格式错）。连续失败 N 次后"打开"熔断器，停止自动触发，避免雪崩（每轮都失败 → 每轮都重试 → 无限消耗）。理解熔断器的三个状态：closed（正常）→ open（熔断，拒绝请求）→ reset（手动/超时恢复）。
3. **结构化摘要 Prompt 工程**：9 段固定结构让摘要可预测、可校验。draft + 正式两步走避免 LLM "边想边写"导致结构混乱。禁止工具调用（首尾强调 + `tool_choice=none`）防止摘要过程又触发工具调用形成死循环。
4. **边界消息设计**：摘要后插入"以下是被压缩的历史，文件内容请重新读取不要脑补"——明确告诉 LLM 摘要不等于原文，需要精确内容时用工具重新读。这是防止 LLM "幻觉"的关键。
5. **原地替换 vs 快照截断**：层 1 在工具结果**入历史前**截断（原地替换内容），历史永远紧凑；层 2 直接 `ReplaceMessages` 修改历史。对比 MewCode 的"快照截断"（每轮重新截断快照，历史保留全文）——体会两种策略的取舍。
6. **CancellationToken 在 LLM 摘要调用中的贯穿**：摘要可能耗时数秒，用户可能中途取消。`CancellationToken` 从 AgentLoop 传到 `SummarizeAsync` 再到 `provider.ChatStreamAsync`，取消时抛 `OperationCanceledException`。

---

## 三、范围

### 3.1 本迭代包含（In Scope）

| 项 | 说明 |
| --- | --- |
| `Conversation/CircuitBreaker.cs` | 通用熔断器：连续失败 N 次 open，成功/手动 reset 关闭 |
| `Conversation/Truncator.cs` | 层 1 工具结果截断：单条 > 50K 写盘留 2K 预览；一轮合计 > 200K 截断最大 |
| `Conversation/Summarizer.cs` | 层 2 结构化摘要：9 段 Prompt + draft + 熔断器 + 边界消息 |
| `Conversation/Compressor.cs` | 两层协调器：`TruncateBatch` + `CheckAndCompressAsync` |
| `Conversation/History.cs` | 扩展：`AddSystem` + `ReplaceMessages` 方法 |
| `Agent/AgentEvent.cs` | 扩展：3 种新事件 `TruncationEvent` / `ContextWarningEvent` / `ContextCompressedEvent` |
| `Agent/AgentLoop.cs` | 扩展：注入 `ContextCompressor`，工具结果截断 + 每轮压缩检查 |
| `Config/Models.cs` | 扩展：`ContextConfig` + `AppConfig.Context` 字段 |
| `App/App.cs` | 扩展：构造 `ContextCompressor` 传入 `TerminalApp` |
| `Tui/TerminalApp.cs` | 扩展：构造加 `ContextCompressor` 参数；`StartAgentRound` 传入 `AgentLoop` |
| `Tui/ChatView.cs` | 扩展：渲染 3 种新事件 |
| `Tui/StatusBarView.cs` | 扩展：显示压缩状态指示 |
| `example.parrotcode.yaml` | 加 `context:` 配置节示例 |
| 单元测试 | `CircuitBreakerTests` / `TruncatorTests` / `SummarizerTests` / `CompressorTests` + `ConversationHistoryTests`（补充）+ `AgentLoopTests`（补充压缩集成）|

### 3.2 本迭代不包含（Out of Scope）

- `/compress` 斜杠命令 → 迭代 10
- `/clear` 后重置熔断器/警告 → 迭代 10 命令系统（本迭代 `ConversationHistory.Clear` 不触发 compressor reset）
- 摘要缓存持久化到 `.parrotcode/summaries/` → 进阶练习
- 精确 token 分词器 → 后续迭代
- Anthropic Provider 的摘要适配 → 迭代 11/12（本迭代用 OpenAI 兼容协议的 `ChatStreamAsync`）
- 压缩后工具调用配对修复（`tool_use` 缺 `tool_result`）→ 迭代 10 JSONL 崩溃恢复处理同源问题

---

## 四、现状分析

### 4.1 已预留的接入点

| 位置 | 现状 | 迭代 9 利用方式 |
|------|------|---------------|
| `ConversationHistory.AddTool(content, toolCallId)` | 直接存原始 content | 在调用前截断 content，存截断后版本 |
| `ConversationHistory.EstimatedTokens` | 字符数 / 3 近似 | 摘要触发判断依据 |
| `AgentLoop.RunCoreAsync` | 每轮 `BuildMessagesWithSystem` → 调 LLM | 在 `BuildMessagesWithSystem` 前插入 `CheckAndCompressAsync` |
| `AgentLoop` 工具结果回灌 | `history.AddTool(result.Content, call.Id)` | 在 `AddTool` 前调用 `TruncateBatch` |
| `IBaseProvider.ChatStreamAsync(messages, ct)` | 返回 `IAsyncEnumerable<string>`，不传 tools | 摘要用此重载（LLM 不会产出 tool_calls） |
| `TuiConfig.ContextWindowTokens` | 默认 64000，状态栏占比分母 | 作为压缩器的上下文窗口大小 |
| `StatusBarView` | 显示 `ctx={pct}%` | 扩展显示压缩/熔断状态 |
| `AgentEvent` | 12 种，`abstract record` | 加 3 种派生（`TruncationEvent` / `ContextWarningEvent` / `ContextCompressedEvent`） |

### 4.2 当前 AgentLoop 工具结果回灌流程（待改造）

```csharp
// 现状（AgentLoop.RunCoreAsync 第 139-159 行）
var results = await _batchExecutor.ExecuteAsync(toolCalls, cancellationToken);

for (var i = 0; i < toolCalls.Count; i++)
{
    var call = toolCalls[i];
    var result = results[i];
    // emit ToolResultEvent / ToolBlockedEvent
    history.AddTool(result.Success ? result.Content : $"错误：{result.Error}", call.Id);
}
```

**问题**：`result.Content` 可能是 10 万行日志（1MB+），直接入历史 → 几轮后 token 爆炸。

**改造后**：

```csharp
var results = await _batchExecutor.ExecuteAsync(toolCalls, cancellationToken);

// 层 1：批量截断（入历史前）
var (truncatedContents, truncInfos) = _compressor.TruncateBatch(
    results.Select((r, i) => r.Success ? r.Content : string.Empty).ToArray(),
    toolCalls.Select(c => c.Name).ToArray());

for (var i = 0; i < toolCalls.Count; i++)
{
    var call = toolCalls[i];
    var result = results[i];

    // 截断事件（如果有）
    if (truncInfos.FirstOrDefault(t => t.Index == i) is { } info)
        await sink.WriteAsync(new AgentEvent.TruncationEvent(call.Name, info.OriginalChars, info.FilePath), cancellationToken);

    // emit ToolResultEvent / ToolBlockedEvent（不变）
    // ...

    // 入历史的是截断后内容
    var contentToStore = result.Success ? truncatedContents[i] : $"错误：{result.Error}";
    history.AddTool(contentToStore, call.Id);
}
```

---

## 五、架构设计

### 5.1 两层管线总览

```
用户输入 → AgentLoop Round N
                │
                ▼
    ┌─── 层 2：CheckAndCompressAsync ───┐
    │  token > 70%? → emit Warning      │
    │  token > 90%? → 触发摘要          │
    │  熔断器 open? → 跳过              │
    │  摘要成功 → ReplaceMessages       │
    └────────────────────────────────────┘
                │
                ▼
         BuildMessagesWithSystem
                │
                ▼
         Provider.ChatStreamAsync
                │
                ▼
         解析 tool_calls
                │
                ▼
    ┌─── 层 1：TruncateBatch ──────────┐
    │  单条 > 50K → 写盘留 2K 预览      │
    │  一轮合计 > 200K → 截断最大       │
    └────────────────────────────────────┘
                │
                ▼
         history.AddTool(截断后内容)
                │
                ▼
         下一轮 Round N+1
```

### 5.2 层 1 截断策略

| 阈值 | 默认值 | 触发动作 |
|------|--------|---------|
| 单条工具结果 > `per_result_threshold` | 50,000 字符 | 全文写盘到 `.parrotcode/truncated/{timestamp}_{toolName}.txt`，历史中替换为 2K 预览 + 文件路径 |
| 一轮内所有工具结果合计 > `round_total_threshold` | 200,000 字符 | 从最大开始截断（写盘 + 预览），直到合计 < 阈值 |

**执行时机**：工具执行完毕、结果入历史**之前**。截断后的内容才入历史，历史永远紧凑。

**预览格式**：

```
[工具结果过大，完整内容已保存到磁盘]
文件: .parrotcode/truncated/20260808_153000_read_file.txt
预览（前 2000 字符）:
{前 2000 字符}
...（省略 {原始长度 - 2000} 字符）
```

**写盘安全性**：
- 目录不存在时自动创建（`Directory.CreateDirectory`）
- 文件名用时间戳 + 工具名（正则替换非法字符为 `_`）
- 写盘失败不阻断——降级为仅截断不写盘（预览 + "写盘失败"提示），记日志

### 5.3 层 2 摘要策略

| 阈值 | 默认值 | 触发动作 |
|------|--------|---------|
| token > `warning_fraction` × 窗口 | 0.7（70%） | emit `ContextWarningEvent`（每轮检查，但只发一次，压缩后/清空后重置） |
| token > `trigger_fraction` × 窗口 | 0.9（90%） | 触发自动摘要 |
| 消息数 < `keep_recent` + 4 | 4 + 4 = 8 | 不触发摘要（消息太少不值得压缩） |
| 熔断器 open | 连续失败 2 次 | 不触发自动摘要，emit 警告"自动压缩已禁用" |

**摘要流程**：

```
1. 分割消息：old = messages[:len-keep_recent], recent = messages[len-keep_recent:]
2. 构造摘要 Prompt（9 段结构 + draft + 禁止工具调用）
3. 调 Provider.ChatStreamAsync(messages, ct)（不传 tools → LLM 不会 tool_call）
4. 累积流式文本 → 去除 draft 块 → 提取正式摘要
5. 空摘要 → RecordFailure → 返回失败
6. 非空 → RecordSuccess → 构造新消息列表：
   [system: "[结构化摘要]\n{摘要}"] + [system: 边界提示] + recent
7. history.ReplaceMessages(新消息列表)
8. 返回 CompressionResult
```

**9 段摘要 Prompt 结构**：

```
## 主要请求        — 用户的核心需求
## 关键概念        — 涉及的技术栈/框架/API
## 文件与代码      — 已检查或修改的文件、关键代码位置
## 错误与修复      — 遇到的错误和修复方式
## 解决过程        — 问题解决的步骤时间线
## 用户原话        — 用户关键原话（> 引用，逐字保留）
## 待办事项        — 尚未完成的任务
## 当前工作        — 当前正在做的具体工作
## 下一步          — 建议的下一步操作
```

**边界消息**（摘要后插入）：

```
[对话上下文已压缩] 上方的结构化摘要替代了早期的详细对话。
如果你需要某个文件的完整内容或某段具体代码，请使用 read_file 或 grep
重新读取，不要根据摘要脑补不存在的细节。
```

### 5.4 熔断器状态机

```
         RecordSuccess              Reset()
    ┌──────────────────┐    ┌──────────────────┐
    │                  ▼    │                  │
    │            ┌─────────────┐         ┌─────────────┐
    │            │   Closed    │         │    Open     │
    │            │ (failure=0) │         │ (failure≥N) │
    │            └──────┬──────┘         └──────┬──────┘
    │                   │ RecordFailure          │
    │                   ▼ (failure < N)          │ Reset()
    │            ┌─────────────┐                 │
    │            │   Closed    │                 │
    │            │ (failure=k) │                 │
    │            └──────┬──────┘                 │
    │                   │ RecordFailure          │
    │                   ▼ (failure ≥ N)          │
    │            ┌─────────────┐                 │
    └────────────│    Open     │◄────────────────┘
                 └─────────────┘
```

- **Closed**：正常工作。`RecordFailure` 递增计数，达到 `maxFailures` 转 Open。`RecordSuccess` 清零计数。
- **Open**：拒绝自动触发。`Reset()` 手动关闭（迭代 10 `/compress` 命令或程序重启）。
- **线程安全**：AgentLoop 单线程驱动，无需锁。如后续多线程场景需加锁。

### 5.5 ContextCompressor 协调器

```csharp
// 组合层 1 + 层 2，对 AgentLoop 暴露两个方法
public sealed class ContextCompressor
{
    // 层 1：轻量，工具结果入历史前调用
    public (string[] TruncatedContents, IReadOnlyList<TruncationInfo> Infos)
        TruncateBatch(IReadOnlyList<string> contents, IReadOnlyList<string> toolNames);

    // 层 2：昂贵，每轮 LLM 调用前调用
    public Task<CompressionResult> CheckAndCompressAsync(
        ConversationHistory history, CancellationToken ct);
}
```

**AgentLoop 集成点**（伪代码）：

```csharp
for (var round = 1; round <= _maxRounds; round++)
{
    // ── 层 2：压缩检查（每轮 LLM 调用前）──
    var compression = await _compressor.CheckAndCompressAsync(history, cancellationToken);
    if (compression.WarningIssued)
        await sink.WriteAsync(new AgentEvent.ContextWarningEvent(compression.WarningMessage!), ct);
    if (compression.WasCompressed)
        await sink.WriteAsync(new AgentEvent.ContextCompressedEvent(
            compression.MessagesCompressed, compression.EstimatedTokensSaved), ct);

    // ── 调 LLM ──
    var messages = BuildMessagesWithSystem(history);
    await foreach (var chunk in _provider.ChatStreamAsync(...)) { ... }

    // ── 工具执行 ──
    var results = await _batchExecutor.ExecuteAsync(toolCalls, ct);

    // ── 层 1：截断（入历史前）──
    var (truncated, infos) = _compressor.TruncateBatch(
        results.Select(r => r.Success ? r.Content : "").ToArray(),
        toolCalls.Select(c => c.Name).ToArray());

    // ── 入历史 ──
    for (var i = 0; i < toolCalls.Count; i++)
    {
        if (infos.FirstOrDefault(t => t.Index == i) is { } info)
            await sink.WriteAsync(new AgentEvent.TruncationEvent(
                info.ToolName, info.OriginalChars, info.FilePath), ct);
        // ... emit ToolResultEvent ...
        history.AddTool(truncated[i], toolCalls[i].Id);  // 截断后内容
    }
}
```

---

## 六、详细设计

### 6.1 CircuitBreaker（通用熔断器）

```csharp
// Conversation/CircuitBreaker.cs
namespace ParrotCode;

/// <summary>
/// 通用熔断器：连续失败 maxFailures 次后打开，停止自动触发。
/// 成功或手动 Reset 后关闭。非线程安全（AgentLoop 单线程驱动）。
/// </summary>
public sealed class CircuitBreaker
{
    private readonly int _maxFailures;
    private int _failureCount;
    private bool _isOpen;

    public CircuitBreaker(int maxFailures = 2)
    {
        if (maxFailures < 1) throw new ArgumentOutOfRangeException(nameof(maxFailures));
        _maxFailures = maxFailures;
    }

    public bool IsOpen => _isOpen;
    public int FailureCount => _failureCount;
    public int MaxFailures => _maxFailures;

    /// <summary>记录一次失败。达到阈值时打开熔断器。</summary>
    public void RecordFailure()
    {
        _failureCount++;
        if (_failureCount >= _maxFailures)
            _isOpen = true;
    }

    /// <summary>记录一次成功。清零计数，关闭熔断器。</summary>
    public void RecordSuccess()
    {
        _failureCount = 0;
        _isOpen = false;
    }

    /// <summary>手动重置（如 /compress 命令或程序重启）。</summary>
    public void Reset()
    {
        _failureCount = 0;
        _isOpen = false;
    }
}
```

### 6.2 ToolResultTruncator（层 1 截断）

```csharp
// Conversation/Truncator.cs
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace ParrotCode;

/// <summary>截断配置。所有阈值可经 ContextConfig 覆盖。</summary>
public sealed record TruncateConfig
{
    /// <summary>单条工具结果截断阈值（字符数）。默认 50_000。</summary>
    public int PerResultThreshold { get; init; } = 50_000;

    /// <summary>一轮内所有工具结果合计截断阈值（字符数）。默认 200_000。</summary>
    public int RoundTotalThreshold { get; init; } = 200_000;

    /// <summary>截断后保留的预览长度（字符数）。默认 2_000。</summary>
    public int PreviewLength { get; init; } = 2_000;

    /// <summary>截断文件存储目录。默认 ".parrotcode/truncated"（项目根下）。</summary>
    public string StorageDir { get; init; } = ".parrotcode/truncated";
}

/// <summary>单条截断结果信息。</summary>
public sealed record TruncationInfo(
    int Index,
    string ToolName,
    int OriginalChars,
    string? FilePath);

/// <summary>
/// 层 1：工具结果截断器。轻量、无 LLM 调用。
/// 单条 > PerResultThreshold → 写盘留 PreviewLength 预览。
/// 一轮合计 > RoundTotalThreshold → 从最大开始截断。
/// 在工具结果入历史前执行，历史始终保持紧凑。
/// </summary>
public sealed class ToolResultTruncator
{
    private readonly TruncateConfig _config;
    private readonly string _storageDir;

    public ToolResultTruncator(TruncateConfig? config = null, string? projectRoot = null)
    {
        _config = config ?? new TruncateConfig();
        // 存储目录：projectRoot/.parrotcode/truncated（绝对路径）
        var root = projectRoot ?? Directory.GetCurrentDirectory();
        _storageDir = Path.IsPathRooted(_config.StorageDir)
            ? _config.StorageDir
            : Path.GetFullPath(_config.StorageDir, root);
    }

    /// <summary>
    /// 批量截断工具结果。返回 (截断后内容数组, 被截断的 TruncationInfo 列表)。
    /// 先处理单条超长，再处理合计超长。
    /// </summary>
    public (string[] TruncatedContents, IReadOnlyList<TruncationInfo> Infos)
        TruncateBatch(IReadOnlyList<string> contents, IReadOnlyList<string> toolNames)
    {
        if (contents.Count != toolNames.Count)
            throw new ArgumentException("contents 和 toolNames 长度不一致");

        var result = new string[contents.Count];
        var infos = new List<TruncationInfo>();

        // Pass 1：单条截断
        var sizes = new int[contents.Count];
        for (var i = 0; i < contents.Count; i++)
        {
            var content = contents[i];
            sizes[i] = content.Length;
            if (content.Length > _config.PerResultThreshold)
            {
                var (truncated, filePath) = TruncateToDisk(content, toolNames[i]);
                result[i] = truncated;
                infos.Add(new TruncationInfo(i, toolNames[i], content.Length, filePath));
                sizes[i] = truncated.Length; // 更新为截断后大小
            }
            else
            {
                result[i] = content;
            }
        }

        // Pass 2：合计截断（从最大开始截断直到合计 < 阈值）
        var total = sizes.Sum();
        if (total <= _config.RoundTotalThreshold)
            return (result, infos);

        // 按大小降序排列索引（跳过已截断的）
        var candidates = Enumerable.Range(0, contents.Count)
            .Where(i => !infos.Any(info => info.Index == i))
            .OrderByDescending(i => sizes[i])
            .ToList();

        foreach (var i in candidates)
        {
            if (total <= _config.RoundTotalThreshold)
                break;
            if (result[i].Length <= _config.PreviewLength + 200)
                continue; // 已经够小，不值得再截断

            var original = result[i];
            var (truncated, filePath) = TruncateToDisk(original, toolNames[i]);
            total -= original.Length - truncated.Length;
            result[i] = truncated;
            infos.Add(new TruncationInfo(i, toolNames[i], original.Length, filePath));
        }

        return (result, infos);
    }

    /// <summary>截换单条内容到磁盘，返回 (预览文本, 文件路径)。</summary>
    private (string Preview, string FilePath) TruncateToDisk(string content, string toolName)
    {
        try
        {
            Directory.CreateDirectory(_storageDir);
            var ts = DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
            var safeName = Regex.Replace(toolName, @"[^a-zA-Z0-9_-]", "_");
            var filePath = Path.Combine(_storageDir, $"{ts}_{safeName}.txt");
            File.WriteAllText(filePath, content, Encoding.UTF8);

            var preview = content.AsSpan(0, Math.Min(_config.PreviewLength, content.Length)).ToString();
            var omitted = content.Length - _config.PreviewLength;
            var text = new StringBuilder()
                .AppendLine("[工具结果过大，完整内容已保存到磁盘]")
                .AppendLine($"文件: {filePath}")
                .AppendLine($"预览（前 {_config.PreviewLength} 字符）:")
                .AppendLine(preview)
                .Append($"...（省略 {omitted} 字符）")
                .ToString();
            return (text, filePath);
        }
        catch (Exception)
        {
            // 写盘失败：降级为仅截断不写盘
            var preview = content.AsSpan(0, Math.Min(_config.PreviewLength, content.Length)).ToString();
            var text = new StringBuilder()
                .AppendLine("[工具结果过大（写盘失败，未保存完整内容）]")
                .AppendLine($"预览（前 {_config.PreviewLength} 字符）:")
                .AppendLine(preview)
                .Append($"...（省略 {content.Length - _config.PreviewLength} 字符）")
                .ToString();
            return (text, null);
        }
    }
}
```

### 6.3 StructuredSummarizer（层 2 摘要）

```csharp
// Conversation/Summarizer.cs
using System.Text;
using Microsoft.Extensions.Logging;

namespace ParrotCode;

/// <summary>摘要结果。</summary>
public sealed record SummaryResult
{
    public bool Success { get; init; }
    public string? SummaryText { get; init; }
    public int MessagesCompressed { get; init; }
    public int EstimatedTokensSaved { get; init; }
    public string? Error { get; init; }
}

/// <summary>
/// 层 2：结构化摘要器。昂贵，需 LLM 调用。
/// token > triggerFraction × 窗口时触发，把旧消息压缩成 9 段摘要 + 保留最近 N 条原文。
/// 含熔断器：连续失败 maxFailures 次停止自动触发。
/// </summary>
internal sealed class StructuredSummarizer
{
    private readonly IBaseProvider _provider;
    private readonly int _contextWindow;
    private readonly int _warningThreshold;
    private readonly int _triggerThreshold;
    private readonly int _keepRecent;
    private readonly CircuitBreaker _breaker;
    private readonly ILogger? _logger;

    // 9 段结构化摘要 Prompt（首尾强调禁止工具调用 + draft 两步走）
    private const string SummaryPrompt = """
        你是一个对话摘要生成器。**只生成摘要，不要调用任何工具。**

        请分析以下对话，按指定结构生成摘要。每个部分用 ## 标题分隔：

        ## 主要请求
        用户的核心需求——他们想完成什么

        ## 关键概念
        涉及的技术栈、框架、API、库

        ## 文件与代码
        已检查或修改的文件、关键代码片段及其位置

        ## 错误与修复
        遇到的错误信息和修复方式

        ## 解决过程
        问题解决的步骤顺序和时间线

        ## 用户原话
        用户的关键原话（用 > 引用，逐字保留，不要改写）

        ## 待办事项
        尚未完成的任务

        ## 当前工作
        当前正在进行的具体工作

        ## 下一步
        建议的下一步操作

        ---

        先将你的分析写成草稿，用 ```draft ... ``` 包裹。草稿写完后再输出正式摘要。

        **再次强调：不要调用任何工具，只输出摘要文本。**
        """;

    private const string BoundaryMessage =
        "[对话上下文已压缩] 上方的结构化摘要替代了早期的详细对话。" +
        "如果你需要某个文件的完整内容或某段具体代码，请使用 read_file 或 grep " +
        "重新读取，不要根据摘要脑补不存在的细节。";

    public StructuredSummarizer(
        IBaseProvider provider,
        int contextWindowTokens,
        double warningFraction = 0.7,
        double triggerFraction = 0.9,
        int keepRecent = 4,
        int maxCircuitFailures = 2,
        ILogger? logger = null)
    {
        _provider = provider ?? throw new ArgumentNullException(nameof(provider));
        _contextWindow = contextWindowTokens;
        _warningThreshold = (int)(contextWindowTokens * warningFraction);
        _triggerThreshold = (int)(contextWindowTokens * triggerFraction);
        _keepRecent = keepRecent;
        _breaker = new CircuitBreaker(maxCircuitFailures);
        _logger = logger;
    }

    public int ContextWindow => _contextWindow;
    public int WarningThreshold => _warningThreshold;
    public int TriggerThreshold => _triggerThreshold;
    public bool CircuitOpen => _breaker.IsOpen;
    public int CircuitFailures => _breaker.FailureCount;

    public void ResetCircuit() => _breaker.Reset();

    /// <summary>是否需要发警告（token > 70%）。</summary>
    public bool NeedsWarning(IReadOnlyList<Message> messages) =>
        TokenEstimator.Estimate(messages) > _warningThreshold;

    /// <summary>是否需要触发摘要（token > 90%）。</summary>
    public bool NeedsCompression(IReadOnlyList<Message> messages) =>
        TokenEstimator.Estimate(messages) > _triggerThreshold;

    /// <summary>
    /// 生成结构化摘要，替换历史中的旧消息。
    /// 返回 SummaryResult。失败时熔断器递增，历史不变。
    /// </summary>
    public async Task<SummaryResult> SummarizeAsync(
        ConversationHistory history, CancellationToken cancellationToken)
    {
        var messages = history.ToProviderMessages();

        // 消息太少不值得压缩
        if (messages.Count < _keepRecent + 4)
            return new SummaryResult { Success = false, Error = "消息太少，不值得压缩" };

        // 熔断器检查
        if (_breaker.IsOpen)
            return new SummaryResult { Success = false, Error = "熔断器已打开，自动压缩已禁用" };

        // 分割：旧消息摘要 + 近期消息保留
        var split = messages.Count - _keepRecent;
        var old = messages.Take(split).ToList();
        var recent = messages.Skip(split).ToList();

        // 构造摘要请求
        var summaryInput = SummaryPrompt + "\n\n---\n对话内容:\n" + FormatForSummary(old);
        var summaryMessages = new List<Message>
        {
            new(MessageRole.User, summaryInput)
        };

        try
        {
            // 调 LLM（不传 tools → LLM 不会 tool_call）
            var sb = new StringBuilder();
            await foreach (var token in _provider.ChatStreamAsync(summaryMessages, cancellationToken))
            {
                sb.Append(token);
            }

            var raw = sb.ToString();

            // 去除 draft 块，提取正式摘要
            var summaryText = ExtractFormalSummary(raw);

            if (string.IsNullOrWhiteSpace(summaryText))
                throw new InvalidOperationException("摘要生成返回空内容");

            // 成功
            _breaker.RecordSuccess();

            // 计算节省的 token
            var oldTokens = TokenEstimator.Estimate(old);
            var summaryTokens = TokenEstimator.Estimate(summaryText);
            var saved = Math.Max(0, oldTokens - summaryTokens);

            // 构造新消息列表：[system: 摘要] + [system: 边界提示] + recent
            var newMessages = new List<Message>
            {
                new(MessageRole.System, $"[结构化摘要]\n{summaryText}"),
                new(MessageRole.System, BoundaryMessage)
            };
            newMessages.AddRange(recent);

            history.ReplaceMessages(newMessages);

            return new SummaryResult
            {
                Success = true,
                SummaryText = summaryText,
                MessagesCompressed = old.Count,
                EstimatedTokensSaved = saved
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw; // 取消不记失败
        }
        catch (Exception ex)
        {
            _breaker.RecordFailure();
            _logger?.LogWarning(ex, "摘要生成失败，熔断器计数 {Count}/{Max}",
                _breaker.FailureCount, _breaker.MaxFailures);
            return new SummaryResult { Success = false, Error = ex.Message };
        }
    }

    /// <summary>从 LLM 输出中去除 draft 块，提取正式摘要。</summary>
    private static string ExtractFormalSummary(string raw)
    {
        // 查找 ```draft ... ``` 块，取其后的内容
        var draftStart = raw.IndexOf("```draft", StringComparison.OrdinalIgnoreCase);
        if (draftStart == -1)
        {
            // 无 draft 标记，整体作为摘要
            return raw.Trim();
        }

        var draftEnd = raw.IndexOf("```", draftStart + 8, StringComparison.OrdinalIgnoreCase);
        if (draftEnd == -1)
        {
            // draft 未闭合，取 draftStart 之前的内容
            return raw[..draftStart].Trim();
        }

        // 取 draft 闭合后的内容
        var afterDraft = raw[(draftEnd + 3)..];
        return afterDraft.Trim();
    }

    /// <summary>格式化消息列表供摘要 Prompt 使用（每条截断 3000 字符）。</summary>
    private static string FormatForSummary(IReadOnlyList<Message> messages)
    {
        var parts = new List<string>(messages.Count);
        foreach (var msg in messages)
        {
            var role = msg.Role.ToString().ToLowerInvariant();
            var content = msg.Content;
            if (content.Length > 3000)
                content = content[..3000] + "...(截断)";
            parts.Add($"[{role}]: {content}");
        }
        return string.Join("\n\n", parts);
    }
}
```

### 6.4 ContextCompressor（协调器）

```csharp
// Conversation/Compressor.cs
using Microsoft.Extensions.Logging;

namespace ParrotCode;

/// <summary>压缩结果。</summary>
public sealed record CompressionResult
{
    public bool WasCompressed { get; init; }
    public int MessagesCompressed { get; init; }
    public int EstimatedTokensSaved { get; init; }
    public bool WarningIssued { get; init; }
    public string? WarningMessage { get; init; }
    public bool CircuitOpen { get; init; }
}

/// <summary>
/// 两层 token 管理协调器。
/// 层 1（TruncateBatch）：轻量，工具结果入历史前调用。
/// 层 2（CheckAndCompressAsync）：昂贵，每轮 LLM 调用前调用。
/// </summary>
public sealed class ContextCompressor
{
    private readonly ToolResultTruncator _truncator;
    private readonly StructuredSummarizer _summarizer;
    private readonly bool _enableAutoCompress;  // false 时仅截断不摘要
    private bool _warningEmitted;
    private readonly ILogger? _logger;

    public ContextCompressor(
        IBaseProvider provider,
        int contextWindowTokens,
        TruncateConfig? truncateConfig = null,
        double warningFraction = 0.7,
        double triggerFraction = 0.9,
        int keepRecent = 4,
        int maxCircuitFailures = 2,
        bool enableAutoCompress = true,
        string? projectRoot = null,
        ILogger? logger = null)
    {
        _truncator = new ToolResultTruncator(truncateConfig, projectRoot);
        _summarizer = new StructuredSummarizer(
            provider, contextWindowTokens,
            warningFraction, triggerFraction,
            keepRecent, maxCircuitFailures, logger);
        _enableAutoCompress = enableAutoCompress;
        _logger = logger;
    }

    // ── 层 1：截断 ──

    public (string[] TruncatedContents, IReadOnlyList<TruncationInfo> Infos)
        TruncateBatch(IReadOnlyList<string> contents, IReadOnlyList<string> toolNames)
        => _truncator.TruncateBatch(contents, toolNames);

    // ── 层 2：压缩 ──

    public int ContextWindow => _summarizer.ContextWindow;
    public int WarningThreshold => _summarizer.WarningThreshold;
    public int TriggerThreshold => _summarizer.TriggerThreshold;
    public bool CircuitOpen => _summarizer.CircuitOpen;
    public int CircuitFailures => _summarizer.CircuitFailures;

    public void ResetCircuit() => _summarizer.ResetCircuit();

    /// <summary>重置警告标志（/clear 或压缩成功后调用）。</summary>
    public void ResetWarning() => _warningEmitted = false;

    /// <summary>
    /// 检查并执行压缩。在每轮 LLM 调用前调用。
    /// 1. token > 70% → 发警告（仅一次）
    /// 2. 熔断器 open → 跳过
    /// 3. token > 90% → 触发摘要
    /// </summary>
    public async Task<CompressionResult> CheckAndCompressAsync(
        ConversationHistory history, CancellationToken cancellationToken)
    {
        // enable_auto_compress: false → 跳过层 2（层 1 截断不受影响）
        if (!_enableAutoCompress)
            return new CompressionResult();

        var result = new CompressionResult();
        var messages = history.ToProviderMessages();

        // 1. 警告检查（仅一次）
        if (!_warningEmitted && _summarizer.NeedsWarning(messages))
        {
            _warningEmitted = true;
            result = result with { WarningIssued = true, WarningMessage = "上下文即将不足，建议保存当前会话并开启新对话" };
        }

        // 2. 熔断器检查
        if (_summarizer.CircuitOpen)
        {
            if (!_warningEmitted || result.WarningIssued)
            {
                result = result with
                {
                    CircuitOpen = true,
                    WarningMessage = "自动压缩已禁用（摘要连续失败），请手动 /compress 或开启新会话"
                };
            }
            return result;
        }

        // 3. 触发摘要
        if (!_summarizer.NeedsCompression(messages))
            return result;

        var summary = await _summarizer.SummarizeAsync(history, cancellationToken);

        if (!summary.Success)
        {
            // 摘要失败（熔断器已递增）
            if (_summarizer.CircuitOpen)
            {
                result = result with
                {
                    CircuitOpen = true,
                    WarningMessage = "自动压缩已禁用（摘要连续失败 2 次），请手动 /compress 或开启新会话"
                };
            }
            return result;
        }

        // 摘要成功 → 压缩后 token 降下来，重置警告
        _warningEmitted = false;

        return result with
        {
            WasCompressed = true,
            MessagesCompressed = summary.MessagesCompressed,
            EstimatedTokensSaved = summary.EstimatedTokensSaved
        };
    }
}
```

### 6.5 ConversationHistory 扩展

```csharp
// Conversation/History.cs — 新增方法

/// <summary>追加 system 消息（压缩摘要/边界提示用）。</summary>
public void AddSystem(string content)
{
    ArgumentNullException.ThrowIfNull(content);
    _messages.Add(new Message(MessageRole.System, content));
}

/// <summary>
/// 替换全部消息（压缩后用）。供 StructuredSummarizer.SummarizeAsync 调用。
/// </summary>
public void ReplaceMessages(IReadOnlyList<Message> messages)
{
    ArgumentNullException.ThrowIfNull(messages);
    _messages.Clear();
    _messages.AddRange(messages);
}
```

### 6.6 AgentEvent 扩展

```csharp
// Agent/AgentEvent.cs — 新增 3 种事件类型（接在 CancelledEvent 后）

/// <summary>
/// 工具结果被截断（迭代 9 层 1）。
/// 单条工具结果超过 50K 字符时写盘留预览，此事件通知 UI 展示截断指示。
/// </summary>
public sealed record TruncationEvent(string ToolName, int OriginalChars, string? FilePath) : AgentEvent;

/// <summary>
/// 上下文警告（迭代 9 层 2）。
/// token > 70% 窗口或熔断器打开时触发，提示用户上下文即将不足。
/// </summary>
public sealed record ContextWarningEvent(string Message) : AgentEvent;

/// <summary>
/// 上下文已压缩（迭代 9 层 2）。
/// 摘要完成后触发，通知 UI 展示压缩结果。
/// </summary>
public sealed record ContextCompressedEvent(int MessagesCompressed, int EstimatedTokensSaved) : AgentEvent;
```

### 6.7 AgentLoop 扩展

```csharp
// Agent/AgentLoop.cs — 构造函数加 compressor 参数

internal sealed class AgentLoop
{
    private readonly ContextCompressor? _compressor;  // 新增（null = 不做上下文管理，测试用；生产环境始终非 null）

    public AgentLoop(IBaseProvider provider,
                     ToolRegistry registry,
                     BatchToolExecutor batchExecutor,
                     int maxRounds = 10,
                     string toolChoice = "auto",
                     string? systemPrompt = null,
                     ContextCompressor? compressor = null,  // 新增
                     ILogger? logger = null)
    {
        // ... 既有赋值 ...
        _compressor = compressor;
    }

    private async Task RunCoreAsync(ConversationHistory history, IAgentEventSink sink, CancellationToken cancellationToken)
    {
        var tools = _registry.GetAll().Count > 0 ? _registry.ToOpenAiSchemas() : (JsonElement?)null;

        for (var round = 1; round <= _maxRounds; round++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await sink.WriteAsync(new AgentEvent.RoundStartEvent(round), cancellationToken);

            // ── 迭代 9：层 2 压缩检查（每轮 LLM 调用前）──
            if (_compressor is not null)
            {
                var compression = await _compressor.CheckAndCompressAsync(history, cancellationToken);
                if (compression.WarningIssued && compression.WarningMessage is not null)
                    await sink.WriteAsync(new AgentEvent.ContextWarningEvent(compression.WarningMessage), cancellationToken);
                if (compression.WasCompressed)
                    await sink.WriteAsync(new AgentEvent.ContextCompressedEvent(
                        compression.MessagesCompressed, compression.EstimatedTokensSaved), cancellationToken);
            }

            // 构造消息：system prompt + 历史快照
            var messages = BuildMessagesWithSystem(history);

            // ... 既有流式调用 + 解析 tool_calls ...

            // 无工具调用 → Agent 完成（不变）
            if (toolCalls.Count == 0) { ... }

            // 有工具调用 → 分批执行
            foreach (var call in toolCalls)
                await sink.WriteAsync(new AgentEvent.ToolCallStartEvent(call), cancellationToken);

            var results = await _batchExecutor.ExecuteAsync(toolCalls, cancellationToken);

            // ── 迭代 9：层 1 截断（入历史前）──
            string[] truncatedContents;
            IReadOnlyList<TruncationInfo> truncInfos = Array.Empty<TruncationInfo>();
            if (_compressor is not null)
            {
                var (tc, ti) = _compressor.TruncateBatch(
                    results.Select(r => r.Success ? r.Content : string.Empty).ToArray(),
                    toolCalls.Select(c => c.Name).ToArray());
                truncatedContents = tc;
                truncInfos = ti;
            }
            else
            {
                truncatedContents = results.Select(r => r.Content).ToArray();
            }

            for (var i = 0; i < toolCalls.Count; i++)
            {
                var call = toolCalls[i];
                var result = results[i];

                // 截断事件（如果有）
                if (truncInfos.FirstOrDefault(t => t.Index == i) is { } truncInfo)
                    await sink.WriteAsync(new AgentEvent.TruncationEvent(
                        truncInfo.ToolName, truncInfo.OriginalChars, truncInfo.FilePath), cancellationToken);

                // 既有 ToolResultEvent / ToolBlockedEvent 逻辑不变
                if (!result.Success && IsHitlDenial(result))
                    await sink.WriteAsync(new AgentEvent.ToolBlockedEvent(call, result.Error ?? "被拦截"), cancellationToken);
                else
                    await sink.WriteAsync(new AgentEvent.ToolResultEvent(call, result), cancellationToken);

                // 入历史的是截断后内容
                var contentToStore = result.Success
                    ? truncatedContents[i]
                    : $"错误：{result.Error}";
                history.AddTool(contentToStore, call.Id);
            }

            await sink.WriteAsync(new AgentEvent.RoundEndEvent(round), cancellationToken);
        }

        // 达到最大轮次（不变）
        await sink.WriteAsync(new AgentEvent.MaxRoundsReachedEvent(_maxRounds), cancellationToken);
    }
}
```

### 6.8 Config 扩展

```csharp
// Config/Models.cs — 新增 ContextConfig + AppConfig.Context

/// <summary>
/// 上下文管理配置（迭代 9 新增）。null 时用默认值。
/// </summary>
public sealed record ContextConfig
{
    /// <summary>上下文窗口 token 数。null 时回退 TuiConfig.ContextWindowTokens ?? 64000。</summary>
    public int? ContextWindowTokens { get; init; }

    /// <summary>警告阈值（占窗口比例）。默认 0.7。</summary>
    public double? WarningFraction { get; init; }

    /// <summary>触发摘要阈值（占窗口比例）。默认 0.9。</summary>
    public double? TriggerFraction { get; init; }

    /// <summary>单条工具结果截断阈值（字符数）。默认 50_000。</summary>
    public int? PerResultThreshold { get; init; }

    /// <summary>一轮内工具结果合计截断阈值（字符数）。默认 200_000。</summary>
    public int? RoundTotalThreshold { get; init; }

    /// <summary>截断后保留预览长度（字符数）。默认 2_000。</summary>
    public int? PreviewLength { get; init; }

    /// <summary>摘要时保留的最近消息数。默认 4。</summary>
    public int? KeepRecentMessages { get; init; }

    /// <summary>熔断器最大连续失败次数。默认 2。</summary>
    public int? MaxCircuitFailures { get; init; }

    /// <summary>是否启用自动压缩。默认 true。false 时仅截断不摘要。</summary>
    public bool? EnableAutoCompress { get; init; }
}

// AppConfig 扩展
public sealed record AppConfig
{
    // ... 既有字段 ...
    /// <summary>上下文管理配置（迭代 9 新增）。null 时用默认值。</summary>
    public ContextConfig? Context { get; init; }
}
```

**示例 YAML**（`example.parrotcode.yaml` 追加）：

```yaml
# 迭代 9 新增：上下文管理配置（全部可选，省略时用默认值）
context:
  context_window_tokens: 64000    # 上下文窗口大小（省略时回退 tui.context_window_tokens）
  warning_fraction: 0.7           # token 占比超过此值发警告
  trigger_fraction: 0.9           # token 占比超过此值触发摘要
  per_result_threshold: 50000     # 单条工具结果截断阈值（字符数）
  round_total_threshold: 200000   # 一轮内工具结果合计截断阈值（字符数）
  preview_length: 2000            # 截断后保留预览长度（字符数）
  keep_recent_messages: 4         # 摘要时保留的最近消息数
  max_circuit_failures: 2         # 熔断器最大连续失败次数
  enable_auto_compress: true      # 是否启用自动压缩（false 时仅截断不摘要）
```

### 6.9 App + TerminalApp 装配

```csharp
// App/App.cs — RunAsync 构造 ContextCompressor

public async Task RunAsync()
{
    var tuiConfig = _config.Tui ?? new TuiConfig();
    var contextConfig = _config.Context ?? new ContextConfig();
    var contextWindow = contextConfig.ContextWindowTokens
                        ?? tuiConfig.ContextWindowTokens
                        ?? 64000;

    // 构造安全上下文 + SecurityGuard（8c，不变）
    // ...

    // 构造上下文压缩器（迭代 9 新增）
    // 始终创建——层 1 截断始终生效；enable_auto_compress: false 仅禁用层 2 摘要
    var truncateConfig = new TruncateConfig
    {
        PerResultThreshold = contextConfig.PerResultThreshold ?? 50_000,
        RoundTotalThreshold = contextConfig.RoundTotalThreshold ?? 200_000,
        PreviewLength = contextConfig.PreviewLength ?? 2_000,
    };
    var compressor = new ContextCompressor(
        _provider, contextWindow,
        truncateConfig,
        contextConfig.WarningFraction ?? 0.7,
        contextConfig.TriggerFraction ?? 0.9,
        contextConfig.KeepRecentMessages ?? 4,
        contextConfig.MaxCircuitFailures ?? 2,
        enableAutoCompress: contextConfig.EnableAutoCompress ?? true,
        projectRoot: Directory.GetCurrentDirectory(),
        _logger);

    using var terminalApp = new TerminalApp(
        _provider, _providerConfig, _config.Agent, tuiConfig,
        _securityLevel, securityGuard, compressor, _logger, _ct);
    await terminalApp.RunAsync();
}
```

```csharp
// Tui/TerminalApp.cs — 构造加 compressor 参数

internal sealed class TerminalApp : IDisposable
{
    private readonly ContextCompressor _compressor;  // 新增（App 始终构造，不为 null）

    public TerminalApp(IBaseProvider provider,
                       ProviderConfig providerConfig,
                       AgentConfig? agentConfig,
                       TuiConfig? tuiConfig,
                       SecurityLevel securityLevel,
                       SecurityGuard securityGuard,
                       ContextCompressor compressor,  // 新增
                       ILogger? logger,
                       CancellationToken ct)
    {
        // ... 既有赋值 ...
        _compressor = compressor;
    }

    private void StartAgentRound()
    {
        // ... 既有 executor / hitlGate / batchExecutor 装配（8c 不变）...

        _sink = new ChannelEventSink();
        var agentLoop = new AgentLoop(_provider,
                                      _registry!,
                                      batchExecutor,
                                      _agentConfig.MaxRounds ?? 10,
                                      _agentConfig.ToolChoice ?? "auto",
                                      _agentConfig.SystemPrompt,
                                      compressor: _compressor,  // 始终非 null
                                      logger: null);
        _agentTask = agentLoop.RunAsync(_history!, _sink, _ct);
    }

    // ProcessEvent 扩展：渲染新事件
    private void ProcessEvent(AgentEvent evt)
    {
        // 既有逻辑不变...

        // 迭代 9：压缩相关事件
        if (evt is AgentEvent.TruncationEvent(var toolName, var origChars, var filePath))
        {
            var location = filePath is not null
                ? $"完整内容已保存到 {filePath}"
                : "写盘失败，未保存完整内容";
            _chatView!.AppendStaticMessage(
                $"[截断] {toolName} 结果过大（{origChars} 字符），{location}");
        }
        else if (evt is AgentEvent.ContextWarningEvent(var msg))
        {
            _chatView!.AppendStaticMessage($"[!] {msg}");
            // 熔断器打开时更新状态栏
            if (msg.Contains("自动压缩已禁用"))
                _statusBarView!.CircuitOpen = true;
        }
        else if (evt is AgentEvent.ContextCompressedEvent(var compressed, var saved))
        {
            _chatView!.AppendStaticMessage(
                $"[压缩] 已压缩 {compressed} 条消息，节省约 {saved} tokens");
            // 压缩后更新状态栏
            _statusBarView!.EstimatedTokens = _history!.EstimatedTokens;
            _statusBarView!.Compressed = true;
        }
    }
}
```

### 6.10 StatusBarView 扩展

```csharp
// Tui/StatusBarView.cs — 显示压缩/熔断状态

internal sealed class StatusBarView : Label
{
    private bool _circuitOpen;
    private bool _compressed;

    public bool CircuitOpen
    {
        get => _circuitOpen;
        set { _circuitOpen = value; RefreshText(); }
    }

    public bool Compressed
    {
        get => _compressed;
        set { _compressed = value; RefreshText(); }
    }

    private void RefreshText()
    {
        if (_providerConfig is null) return;
        var pct = _contextWindowTokens > 0
            ? (int)((double)_estimatedTokens / _contextWindowTokens * 100) : 0;
        var compressFlag = _compressed ? "[Z]" : "";      // Z = 已压缩
        var circuitFlag = _circuitOpen ? "[!CB]" : "";     // CB = 熔断器打开
        Text = $"provider={_providerConfig.Name} model={_providerConfig.Model} " +
               $"security={_securityLevel} ctx={pct}%({_estimatedTokens}/{_contextWindowTokens}) " +
               $"{compressFlag}{circuitFlag} round={_currentRound} tools={_toolCount}";
    }
}
```

> **注**：`[Z]` 表示历史已被压缩过（本次会话），`[!CB]` 表示熔断器打开（自动压缩已禁用）。两者可同时出现。

---

## 七、验收标准

### 7.1 编译与测试

| 编号 | 标准 | 验证方式 |
|------|------|---------|
| 09-01 | `dotnet build` 0 错误 0 警告 | 编译 |
| 09-02 | 全量测试全绿（8a + 8b + 8c + 现有 + 9 新增） | `dotnet test` |
| 09-03 | `CircuitBreakerTests` 全绿 | `dotnet test` |
| 09-04 | `TruncatorTests` 全绿 | `dotnet test` |
| 09-05 | `SummarizerTests` 全绿（用 MockProvider） | `dotnet test` |
| 09-06 | `CompressorTests` 全绿 | `dotnet test` |
| 09-07 | 现有 `AgentLoopTests` / `ConversationHistoryTests` 不回归 | `dotnet test` |

### 7.2 CircuitBreaker

| 编号 | 标准 | 验证方式 |
|------|------|---------|
| 09-10 | `RecordFailure` 递增 `FailureCount` | 单测 |
| 09-11 | 连续 `maxFailures` 次失败后 `IsOpen == true` | 单测 |
| 09-12 | `RecordSuccess` 清零计数并关闭熔断器 | 单测 |
| 09-13 | `Reset` 手动关闭熔断器 | 单测 |
| 09-14 | 熔断器 open 时不再自动触发摘要 | 单测（Compressor 集成） |

### 7.3 Truncator（层 1）

| 编号 | 标准 | 验证方式 |
|------|------|---------|
| 09-20 | 单条结果 < 50K 时不截断，原样返回 | 单测 |
| 09-21 | 单条结果 > 50K 时写盘 + 返回 2K 预览 + 文件路径 | 单测 |
| 09-22 | 截断后内容含 `[工具结果过大，完整内容已保存到磁盘]` + 文件路径 | 单测 |
| 09-23 | 一轮内多条合计 > 200K 时从最大开始截断 | 单测 |
| 09-24 | 已被单条截断的结果不再被合计截断重复处理 | 单测 |
| 09-25 | 写盘失败时降级为仅截断不写盘（不抛异常） | 单测（mock 写盘失败） |
| 09-26 | 截断目录不存在时自动创建 | 单测 |
| 09-27 | 文件名含时间戳 + 工具名（非法字符替换为 `_`） | 单测 |

### 7.4 Summarizer（层 2）

| 编号 | 标准 | 验证方式 |
|------|------|---------|
| 09-30 | token > 70% 时 `NeedsWarning` 返回 true | 单测 |
| 09-31 | token > 90% 时 `NeedsCompression` 返回 true | 单测 |
| 09-32 | 消息数 < `keepRecent + 4` 时不触发摘要 | 单测 |
| 09-33 | 摘要成功后历史变短（`ReplaceMessages` 生效） | 单测（MockProvider） |
| 09-34 | 摘要后历史含 `[结构化摘要]` system 消息 + 边界消息 | 单测 |
| 09-35 | 摘要后保留最近 4 条原文消息 | 单测 |
| 09-36 | 摘要 Prompt 含 9 段 `##` 标题 + 禁止工具调用首尾强调 | 代码审查 |
| 09-37 | 摘要 Prompt 含 draft 步骤（` ```draft ... ``` `） | 代码审查 |
| 09-38 | `ExtractFormalSummary` 正确去除 draft 块 | 单测 |
| 09-39 | 摘要返回空内容时记为失败，熔断器递增 | 单测（MockProvider 返回空） |
| 09-40 | 摘要 API 异常时记为失败，熔断器递增，历史不变 | 单测（MockProvider 抛异常） |
| 09-41 | 连续失败 2 次后熔断器打开，第 3 次不再调用 LLM | 单测 |
| 09-42 | `ResetCircuit` 后可重新触发摘要 | 单测 |
| 09-43 | 取消（CancellationToken）时不记为失败 | 单测 |

### 7.5 Compressor（协调器）

| 编号 | 标准 | 验证方式 |
|------|------|---------|
| 09-50 | `TruncateBatch` 委托 `ToolResultTruncator` | 单测 |
| 09-51 | token > 70% 时 `CheckAndCompressAsync` 返回 `WarningIssued=true` | 单测 |
| 09-52 | 警告只发一次（第二次检查同一阈值不再发） | 单测 |
| 09-53 | token > 90% 时 `CheckAndCompressAsync` 返回 `WasCompressed=true` | 单测 |
| 09-54 | 压缩成功后 `WarningEmitted` 重置 | 单测 |
| 09-55 | 熔断器 open 时 `CheckAndCompressAsync` 返回 `CircuitOpen=true` 不调 LLM | 单测 |
| 09-56 | token < 70% 时 `CheckAndCompressAsync` 返回全 false（无操作） | 单测 |
| 09-57 | `enableAutoCompress=false` 时 `CheckAndCompressAsync` 直接返回空结果，`TruncateBatch` 仍正常截断 | 单测 |

### 7.6 ConversationHistory 扩展

| 编号 | 标准 | 验证方式 |
|------|------|---------|
| 09-60 | `AddSystem` 追加 `MessageRole.System` 消息 | 单测 |
| 09-61 | `ReplaceMessages` 清空旧消息 + 添加新消息 | 单测 |
| 09-62 | `ReplaceMessages(null)` 抛 `ArgumentNullException` | 单测 |
| 09-63 | `EstimatedTokens` 在 `ReplaceMessages` 后反映新消息的 token 数 | 单测 |

### 7.7 AgentLoop 集成

| 编号 | 标准 | 验证方式 |
|------|------|---------|
| 09-70 | `compressor=null` 时 AgentLoop 行为与迭代 8 完全一致（不截断不压缩） | 单测 |
| 09-71 | 工具结果 > 50K 时入历史的是截断后内容（2K 预览） | 单测（MockProvider + 大结果） |
| 09-72 | 截断后 emit `TruncationEvent` | 单测 |
| 09-73 | token > 90% 时每轮 LLM 调用前触发摘要 | 单测（MockProvider 脚本） |
| 09-74 | 摘要后 emit `ContextCompressedEvent` | 单测 |
| 09-75 | token > 70% 时 emit `ContextWarningEvent`（仅一次） | 单测 |
| 09-76 | 熔断器 open 后不再调用 LLM 做摘要 | 单测 |

### 7.8 配置解析

| 编号 | 标准 | 验证方式 |
|------|------|---------|
| 09-80 | `context:` 段正确解析为 `ContextConfig` | 单测 |
| 09-81 | 无 `context:` 段时用默认值（50K / 200K / 2K / 0.7 / 0.9 / 4 / 2） | 单测 |
| 09-82 | `enable_auto_compress: false` 时 `CheckAndCompressAsync` 直接返回空结果（层 1 截断仍生效） | 单测 |
| 09-83 | `context_window_tokens` 省略时回退 `tui.context_window_tokens` | 单测 |

### 7.9 端到端

| 编号 | 标准 | 验证方式 |
|------|------|---------|
| 09-90 | 让 AI 读一个 10 万行日志文件，工具结果被截断且 AI 知道去哪看全文 | 手动 |
| 09-91 | 截断文件实际写入 `.parrotcode/truncated/` 目录 | 手动：检查文件存在 |
| 09-92 | 人为构造超长对话（多轮 + 大结果），触发摘要后历史变短、AI 仍能继续工作 | 手动 |
| 09-93 | 状态栏显示 `[Z]` 标记（已压缩） | 手动 |
| 09-94 | 故意让摘要 API 报错 2 次（断网/错 key），第 3 次不再自动触发 | 手动 |
| 09-95 | 熔断器打开后状态栏显示 `[!CB]` | 手动 |
| 09-96 | ChatView 显示截断/警告/压缩 3 种静态消息（`AppendStaticMessage`） | 手动 |
| 09-97 | 现有 8c 功能不受影响（安全层/HITL/三档模式） | 手动回归 |
| 09-98 | 现有 7c 功能不受影响（流式渲染/Spinner/输入） | 手动回归 |

---

## 八、测试计划

| 测试文件 | 覆盖范围 | 预估用例数 |
|---------|---------|-----------|
| `CircuitBreakerTests.cs` | 记录失败/成功、打开/关闭/重置、阈值边界 | ~6 |
| `TruncatorTests.cs` | 单条截断、合计截断、写盘、降级、文件名、目录创建 | ~10 |
| `SummarizerTests.cs` | 阈值判断、摘要成功/失败/空内容、draft 提取、熔断器、取消 | ~12 |
| `CompressorTests.cs` | 警告一次、压缩触发、熔断器跳过、重置、`enableAutoCompress=false` 仅截断 | ~9 |
| `ConversationHistoryTests.cs`（补充） | `AddSystem` / `ReplaceMessages` | ~3 |
| `AgentLoopTests.cs`（补充） | compressor=null 回归、截断 emit、压缩 emit | ~5 |
| `ContextConfigTests.cs` | YAML 解析、默认值、回退 | ~5 |

**端到端手动测试清单**（对照 09-90 到 09-98）：

1. **截断验证**：
   - 准备一个 10 万行的日志文件（如 `Generate-Content -Count 100000 | Set-Content big.log`）
   - 让 AI `read_file big.log`
   - 验证：ChatView 显示截断提示 + 文件路径；`.parrotcode/truncated/` 下有文件；AI 回复知道去哪看全文

2. **摘要验证**：
   - 配置 `context_window_tokens: 8000`（故意调小，容易触发）
   - 多轮对话 + 多次读大文件，让 token 超过 90%
   - 验证：ChatView 显示 `[压缩]` 消息；状态栏 token 数下降；AI 仍能继续回答

3. **熔断验证**：
   - 故意配错 API key（让摘要 LLM 调用失败）
   - 多轮对话触发摘要 → 失败 2 次
   - 验证：第 3 次不再自动触发；状态栏显示 `[!CB]`；ChatView 显示"自动压缩已禁用"

4. **警告验证**：
   - 配置 `context_window_tokens: 8000`
   - 对话让 token 超过 70%（但不到 90%）
   - 验证：ChatView 显示 `[!] 上下文即将不足` 警告（仅一次）

5. **回归验证**：
   - 8c 安全层：`write_file` 仍弹 HITL；黑名单命令仍被拦
   - 7c UI：流式输出不闪烁；Spinner 正常；输入框正常

---

## 九、实施步骤

### Step 1：纯逻辑层（CircuitBreaker + Truncator + History 扩展）

- 新建 `Conversation/CircuitBreaker.cs`
- 新建 `Conversation/Truncator.cs`
- `Conversation/History.cs` 加 `AddSystem` + `ReplaceMessages`
- 新建 `CircuitBreakerTests.cs` / `TruncatorTests.cs`
- `ConversationHistoryTests.cs` 补充新方法测试
- 验证：`dotnet build` + `dotnet test` 全绿

### Step 2：LLM 集成层（Summarizer + Compressor + Config）

- 新建 `Conversation/Summarizer.cs`
- 新建 `Conversation/Compressor.cs`
- `Config/Models.cs` 加 `ContextConfig` + `AppConfig.Context`
- `example.parrotcode.yaml` 加 `context:` 段
- 新建 `SummarizerTests.cs` / `CompressorTests.cs` / `ContextConfigTests.cs`
- 验证：`dotnet build` + `dotnet test` 全绿

### Step 3：Agent + UI 集成 + 端到端

- `Agent/AgentEvent.cs` 加 3 种新事件
- `Agent/AgentLoop.cs` 加 `compressor` 参数 + 截断/压缩逻辑
- `Tui/TerminalApp.cs` 加 `compressor` 参数 + `ProcessEvent` 渲染
- `Tui/StatusBarView.cs` 加压缩/熔断状态
- `App/App.cs` 构造 `ContextCompressor`
- `AgentLoopTests.cs` 补充压缩集成测试
- 验证：`dotnet build` 0 警告 + `dotnet test` 全绿
- 端到端手动验收（对照 09-90 到 09-98）
- 标记迭代 9 [已完成]

---

## 十、风险与对策

| 风险 | 概率 | 影响 | 对策 |
|------|------|------|------|
| 摘要 LLM 调用忽略 `tool_choice=none` 仍产出 tool_calls | 低 | 高 | 用 `ChatStreamAsync(messages, ct)` 重载（不传 tools 参数），LLM 无法 tool_call；Prompt 首尾强调禁止 |
| 摘要后 `tool_use` / `tool_result` 配对断裂 | 中 | 中 | `KEEP_RECENT=4` 保留近期完整对话；分割点选在 `tool_result` 之后（非 `tool_use` 之后）；迭代 10 JSONL 崩溃恢复处理同源问题 |
| 截断写盘权限不足 | 低 | 低 | 降级为仅截断不写盘（try-catch），记日志，不阻断 |
| 截断文件无限增长 | 中 | 低 | 文件名含时间戳不覆盖；后续可加清理策略（进阶练习） |
| `ReplaceMessages` 破坏历史迭代器 | 低 | 中 | `ToProviderMessages` 返回数组快照，不存在 live 迭代器问题 |
| 摘要在 AgentLoop 线程阻塞 | 中 | 中 | `SummarizeAsync` 是 async，不阻塞；但耗时数秒，用户可能取消 → `CancellationToken` 贯穿 |
| 熔断器状态不跨会话 | 低 | 低 | 本迭代内存版；迭代 10 JSONL 持久化时可扩展 |
| `TokenEstimator` 不精确导致过早/过晚触发 | 中 | 低 | 70% 警告 + 90% 触发有 20% 缓冲；估算用字符数 / 3 偏保守（高估） |

---

## 十一、与后续迭代的关系

### 11.1 迭代 10（斜杠命令 + 会话持久化 + 项目指令）

- **`/compress` 命令**：手动触发 `ContextCompressor.CheckAndCompressAsync`。即使熔断器 open 也允许手动触发（手动触发成功后 `ResetCircuit`）。
- **`/clear` 命令**：清空历史后调 `compressor.ResetWarning()` 重置警告标志。
- **JSONL 持久化**：压缩后的历史（含 `[结构化摘要]` system 消息）直接序列化为 JSONL。恢复时正常加载。
- **`PARROTCODE.md` 项目指令**：项目指令作为 system prompt 注入，不受压缩影响（AgentLoop 每轮重新拼装 system prompt）。
- **崩溃恢复**：JSONL 读取时遇到 `tool_use` 缺 `tool_result`（可能因压缩分割点不当）→ 截断到最后一个完整状态。与本迭代的 `KEEP_RECENT` 策略配合。

### 11.2 迭代 11（MCP 协议客户端）

- MCP 工具结果同样过 `TruncateBatch` 截断——MCP server 可能返回超大 JSON。
- MCP 工具名含 `{server_name}/{tool_name}` 前缀，截断文件名中 `/` 被替换为 `_`（`Truncator` 的正则已处理）。

### 11.3 迭代 12（Skill + Hook + 子 Agent）

- **子 Agent**：Fork 式子 Agent 继承父历史 → 继承压缩后历史。子 Agent 有独立 `ContextCompressor`。
- **Hook `tool_post_exec`**：可在工具结果入历史前插入自定义截断逻辑（在 `TruncateBatch` 之后）。
- **Skill SOP**：Skill 正文注入的 system 消息不受摘要影响（每轮重新注入）。

---

## 十二、关键设计决策记录

### Q1：为什么截断在入历史前，而不是快照上？

**MewCode 方案**：每轮 LLM 调用前对历史快照跑 `process_round`，返回截断后的快照发给 LLM，历史不变。

**问题**：历史中仍存 100K+ 原文，每轮重新扫描 + 写盘（时间戳不同 → 新文件），内存不省、磁盘浪费。

**ParrotCode.Net 方案**：截断在 `history.AddTool` 之前执行，截断后内容才入历史。历史永远紧凑，不重复截断。

**取舍**：失去"历史保留原文"的能力（原文只在磁盘）。但这对 Agent 不是问题——LLM 需要原文时可用 `read_file` 重新读，且预览 + 文件路径已足够告知 LLM 去哪看全文。

### Q2：为什么警告 70% 但触发 90%，不合并？

MewCode 用单一 0.7 阈值（既警告又触发）。ParrotCode.Net 分两档：

- **70% 警告**：给用户"上下文快满了"的预警，可主动 `/compress` 或开新会话。
- **90% 触发**：自动摘要。不在 70% 就自动摘要——太激进，很多对话在 70%-90% 之间就结束了，不值得花 LLM 调用做摘要。

20% 的缓冲区让用户有机会自主决策，减少不必要的摘要开销。

### Q3：为什么摘要用 `ChatStreamAsync(messages, ct)` 而非带 tools 的重载？

带 tools 的重载会传 `tools` schema 给 LLM，LLM 可能产出 `tool_calls`。即使 Prompt 说"不要调工具"，部分模型（尤其小模型）仍会调。

用 `ChatStreamAsync(messages, ct)`（返回 `IAsyncEnumerable<string>`）的重载——不传 `tools` 参数，LLM 协议层面无法产出 `tool_calls`。双保险。

### Q4：为什么熔断器是 2 次而非 3 次或 5 次？

- 2 次足够判断"不是偶发"——连续 2 次失败大概率是系统性问题（key 错、限流、模型不支持）。
- 3 次以上太宽容——每次失败都消耗数秒 + token，用户等待体验差。
- 可经 `ContextConfig.MaxCircuitFailures` 配置。

### Q5：为什么不做摘要缓存持久化（进阶练习）？

本迭代聚焦"运行时上下文管理"。摘要缓存持久化涉及：
- 跨会话匹配（同一会话 ID 的摘要缓存）
- 失效策略（历史变化后缓存失效）
- 存储格式（`.parrotcode/summaries/{session_id}.json`）

这些与迭代 10 JSONL 持久化深度耦合，放迭代 10 之后做更合适。

---

**文档结束**。状态：[设计完成，待实现]
