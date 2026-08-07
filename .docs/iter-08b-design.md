# 迭代 8b：安全管线编排 + Agent 集成（预扫描改造）

> **状态**：[设计完成，待实现]
> **前置迭代**：8a [已完成]
> **父文档**：iter-08-design.md（保留追溯）
> **后续迭代**：8c（配置与装配 + 端到端验收）
> **目标**：把 8a 的纯逻辑核心接入 Agent 层——实现 `SecurityGuard` 管线编排 + `SecureBatchToolExecutor` 子类 + `BatchToolExecutor.ExecuteAsync` 入口预扫描改造，让 Read 组也过安全层。**本迭代是回归风险核心**。

---

## 一、迭代目标

### 1.1 核心目标

把 8a 的 `Blacklist` / `PathSandbox` 组装为 `SecurityGuard` 管线，接入 `BatchToolExecutor`：

1. `SecurityPolicy`：三档模式策略评估（预留扩展点，本迭代默认放行）。
2. `SecurityGuard`：编排 黑名单 → 沙箱 → 策略，返回 `ToolResult?`（null=放行 / Fail=拦截）。
3. `SecureBatchToolExecutor : BatchToolExecutor`：子类，覆写 `OnBeforeExecuteAsync` 委托 `SecurityGuard`。
4. `BatchToolExecutor.ExecuteAsync` 改造：**入口预扫描**所有 calls，让 Read 组也过 `OnBeforeExecuteAsync`。

### 1.2 本迭代要验证的关键问题

| 问题 | 验证方式 | 失败对策 |
|------|---------|---------|
| 预扫描改造是否破坏 7b 的 `BatchToolExecutorHitlTests` | 跑全量测试 | 基类 `OnBeforeExecuteAsync` 默认 null 等价 7b；逐项核对断言 |
| 基类 `BatchToolExecutor`（非 Secure）预扫描是否全放行 | 单测 | 默认 `OnBeforeExecuteAsync` 返回 null，pending 包含全部索引 |
| Read 组过安全层后仍并发执行 | 集成测试 | 预扫描仅填充 results，Read 组从 pending 分组并发不变 |
| Write 组不重复调 `OnBeforeExecuteAsync` | 单测/代码审查 | 预扫描已覆盖，Write 组直接走 HITL |
| `SecurityGuard` 三层短路顺序正确 | 单测 | 黑名单命中不调沙箱；沙箱命中不调策略 |
| 拒绝原因含 `[黑名单]`/`[路径沙箱]` 前缀 | 单测 | 在 `SecurityGuard.CheckAsync` 拼接前缀 |

### 1.3 非目标（明确不做）

- ❌ 不扩展 `SecurityConfig`（8c）
- ❌ 不改 `App` / `TerminalApp` 装配（8c）
- ❌ 不做端到端 `dotnet run` 验证（8c 装配后）
- ❌ 不启用 `ToolBlockedEvent` emit（保留事件类型；拦截走 `ToolResult.Fail` → `ToolResultEvent`）
- ❌ 不做运行时 `/mode` 切换（迭代 10，但 `SecurityGuard.Level` 属性已预留可变）

### 1.4 回归风险控制策略

本迭代改动 `BatchToolExecutor.ExecuteAsync`，是迭代 8 最大回归风险点。控制策略：

1. **基类默认行为不变**：`BatchToolExecutor.OnBeforeExecuteAsync` 仍返回 null，预扫描全放行，`pending` = 全部索引，分组执行与 7b 等价。
2. **不删 Write 组的 `OnBeforeExecuteAsync` 调用注释**：保留说明"预扫描已覆盖，此处不重复调"。
3. **逐项核对 `BatchToolExecutorHitlTests`**：如有断言"Read 组不调 hook"或"Write 组调 hook 两次"，需更新为"预扫描调一次"。
4. **集成测试覆盖关键路径**：Read 组拦截、Write 组 HITL 顺序、全放行分组执行。

---

## 二、文件改动清单

### 2.1 新增文件（3 个）

```
Security/
├── SecurityPolicy.cs             # 三档模式策略评估（预留扩展点）
├── SecurityGuard.cs               # 管线编排：黑名单 → 沙箱 → 策略
└── SecureBatchToolExecutor.cs     # BatchToolExecutor 子类
```

### 2.2 修改文件（1 个，风险核心）

```
Agent/BatchToolExecutor.cs         # ExecuteAsync 入口预扫描改造
```

### 2.3 适配的测试（1 个）

```
ParrotCode.Net-xUnit/BatchToolExecutorHitlTests.cs  # 若有断言变化，更新
```

### 2.4 新增测试（2 个）

```
ParrotCode.Net-xUnit/Security/
├── SecurityGuardTests.cs          # 三层顺序 / 短路 / 原因前缀 / 非命令工具放行
└── SecureBatchToolExecutorTests.cs # 集成：Read 组过安全层 / Write 组 HITL 顺序 / 全放行
```

---

## 三、详细设计

> 完整代码见父文档 [iter-08-design.md](iter-08-design.md)。本节聚焦 8b 范围与改造细节。

### 3.1 SecurityPolicy（预留扩展点）

```csharp
// Security/SecurityPolicy.cs
namespace ParrotCode;

public sealed class SecurityPolicy
{
    private readonly PathSandbox _sandbox;
    public SecurityPolicy(PathSandbox sandbox) => _sandbox = sandbox;

    /// <summary>
    /// 评估是否拦截。null=放行；ToolResult.Fail=拦截。
    /// 本迭代默认放行（沙箱层已覆盖 Strict 白名单检查）。
    /// 预留：Strict 下禁止 run_command、二次确认等细粒度策略。
    /// </summary>
    public ToolResult? Evaluate(ToolCall call, SecurityLevel level) => null;
}
```

> **设计说明**：当前三档模式的核心拦截由黑名单（始终生效）+ 路径沙箱（按档位收紧）承担。`SecurityPolicy` 保持管线完整性，为迭代 10 `/mode` 和未来细粒度策略留接口，避免过度设计。

### 3.2 SecurityGuard（管线编排）

完整代码见父文档 §4.6。核心逻辑：

```csharp
public sealed class SecurityGuard
{
    private readonly Blacklist _blacklist;
    private readonly PathSandbox _sandbox;
    private readonly SecurityPolicy _policy;

    public SecurityLevel Level { get; set; }  // 可变，为 8c/迭代10 预留

    public SecurityGuard(SecurityContext context, SecurityLevel level, ILogger? logger = null)
    {
        _blacklist = new Blacklist(context.ExtraBlacklist);
        _sandbox = new PathSandbox(context);
        _policy = new SecurityPolicy(_sandbox);
        Level = level;
    }

    public Task<ToolResult?> CheckAsync(ToolCall call, CancellationToken ct)
    {
        ToolResult? blocked = null;

        // ① 黑名单（始终生效）
        var (cmd, args) = ExtractCommand(call);
        if (cmd is not null)
        {
            var hit = _blacklist.Match(cmd, args);
            if (hit is not null)
                blocked = ToolResult.Fail($"[黑名单] {hit.Reason}");
        }

        // ② 路径沙箱（按 Level 收紧）
        if (blocked is null)
        {
            var path = ExtractPath(call);
            if (path is not null)
            {
                var result = _sandbox.Check(path, Level);
                if (!result.IsAllowed)
                    blocked = ToolResult.Fail($"[路径沙箱] {result.Detail}");
            }
        }

        // ③ 策略（扩展点）
        blocked ??= _policy.Evaluate(call, Level);
        return Task.FromResult(blocked);
    }

    // ExtractCommand / ExtractPath 见父文档 §4.6
}
```

**关键点**：
- 三层顺序：黑名单（最廉价）→ 沙箱 → 策略，短路求值。
- 原因前缀：`[黑名单]` / `[路径沙箱]` / `[策略]`，便于 LLM 识别来源。
- `ExtractCommand` 仅对 `run_command` 工具提取 command+args；`ExtractPath` 对 `read_file`/`write_file`/`edit_file`/`glob`/`grep` 提取 path 或 cwd。

### 3.3 SecureBatchToolExecutor（子类）

```csharp
// Security/SecureBatchToolExecutor.cs
namespace ParrotCode;

public sealed class SecureBatchToolExecutor : BatchToolExecutor
{
    private readonly SecurityGuard _guard;

    public SecureBatchToolExecutor(
        ToolExecutor executor, ToolRegistry registry, SecurityGuard guard,
        int maxParallelism = 5, IHitlGate? hitlGate = null, ILogger? logger = null)
        : base(executor, registry, maxParallelism, hitlGate, logger)
    {
        _guard = guard ?? throw new ArgumentNullException(nameof(guard));
    }

    protected override async Task<ToolResult?> OnBeforeExecuteAsync(ToolCall call, CancellationToken ct) =>
        await _guard.CheckAsync(call, ct);
}
```

### 3.4 BatchToolExecutor.ExecuteAsync 改造（风险核心）

**改造点**：入口预扫描所有 calls，拒绝的填 results 不进分组，放行的进 pending 再分组执行。Write 组去掉重复的 `OnBeforeExecuteAsync` 调用。

```csharp
// Agent/BatchToolExecutor.cs
public async Task<IReadOnlyList<ToolResult>> ExecuteAsync(IReadOnlyList<ToolCall> calls, CancellationToken cancellationToken)
{
    ArgumentNullException.ThrowIfNull(calls);
    if (calls.Count == 0) return Array.Empty<ToolResult>();
    cancellationToken.ThrowIfCancellationRequested();

    var results = new ToolResult[calls.Count];
    var pending = new List<int>(calls.Count);

    // 【迭代 8b 改造】入口预扫描：对所有 calls 调 OnBeforeExecuteAsync（安全层）
    for (var i = 0; i < calls.Count; i++)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var blocked = await OnBeforeExecuteAsync(calls[i], cancellationToken);
        if (blocked is not null)
        {
            results[i] = blocked;
            _logger?.LogInformation("工具 {Name} 被安全层拦截", calls[i].Name);
        }
        else
        {
            pending.Add(i);
        }
    }

    if (pending.Count == 0)
        return results;

    // 分组（只对 pending）
    var readIndices = new List<int>();
    var writeIndices = new List<int>();
    foreach (var i in pending)
    {
        var tool = _registry.Get(calls[i].Name);
        if (tool is null || tool.Category != ToolCategory.Read)
            writeIndices.Add(i);
        else
            readIndices.Add(i);
    }

    // Read 组并发（分批限流）——无 HITL（安全层已在预扫描跑过）
    foreach (var batch in readIndices.Chunk(_maxParallelism))
    {
        cancellationToken.ThrowIfCancellationRequested();
        var tasks = batch.Select(i => _executor.ExecuteAsync(calls[i], cancellationToken)).ToArray();
        var batchResults = await Task.WhenAll(tasks);
        for (var j = 0; j < batch.Length; j++)
            results[batch[j]] = batchResults[j];
    }

    // Write 组串行 + HITL（迭代 8b：OnBeforeExecuteAsync 已在预扫描跑过，此处不再调）
    foreach (var i in writeIndices)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var call = calls[i];

        if (_hitlGate is not null)
        {
            var decision = await _hitlGate.RequestAsync(call, cancellationToken);
            if (decision is { IsAllowed: false })
            {
                results[i] = ToolResult.Fail(decision.Reason ?? "用户拒绝执行");
                _logger?.LogInformation("HITL 拒绝工具 {Name}", call.Name);
                continue;
            }
        }

        results[i] = await _executor.ExecuteAsync(call, cancellationToken);
    }

    return results;
}
```

**关键改动**：
1. 新增入口 `for` 循环预扫描，调 `OnBeforeExecuteAsync`，拒绝填 `results[i]`，放行进 `pending`。
2. 分组从 `pending` 而非 `calls` 全量。
3. Write 组循环去掉 `OnBeforeExecuteAsync` 调用（预扫描已覆盖），直接走 HITL → 执行。
4. `OnBeforeExecuteAsync` 虚方法签名不变（`protected virtual Task<ToolResult?>`），默认实现仍 `Task.FromResult<ToolResult?>(null)`。

**对 7b 测试的影响**：
- 基类 `BatchToolExecutor`（非 Secure）：`OnBeforeExecuteAsync` 返回 null，预扫描全放行，`pending` = 全部索引，分组执行与 7b 等价。
- `BatchToolExecutorHitlTests`：若断言"Write 组调 `OnBeforeExecuteAsync` 一次"或"Read 组不调"，需更新为"预扫描对所有 calls 调一次"。
- 预扫描是顺序 await，Read 组的并发执行性能不受影响（预扫描是微秒级内存操作）。

---

## 四、验收标准

### 4.1 编译与测试

| 编号 | 标准 | 验证方式 |
|------|------|---------|
| 08b-01 | `dotnet build` 0 错误 0 警告 | 编译 |
| 08b-02 | 现有测试全绿（7b/7c/8a 不受影响） | `dotnet test` |
| 08b-03 | 新增 Security 管线测试全绿 | `dotnet test` |
| 08b-04 | `BatchToolExecutorHitlTests` 适配后全绿 | `dotnet test` |

### 4.2 SecurityGuard 管线

| 编号 | 标准 | 验证方式 |
|------|------|---------|
| 08b-05 | 黑名单命中时不调沙箱（短路） | 单测（spy 沙箱） |
| 08b-06 | 沙箱命中时不调策略（短路） | 单测 |
| 08b-07 | 全放行返回 null | 单测 |
| 08b-08 | 黑名单拦截原因含 `[黑名单]` 前缀 | 单测 |
| 08b-09 | 沙箱拦截原因含 `[路径沙箱]` 前缀 | 单测 |
| 08b-10 | `run_command` 工具调黑名单 + 沙箱（无 path 跳过沙箱） | 单测 |
| 08b-11 | `read_file` 工具跳过黑名单，调沙箱 | 单测 |
| 08b-12 | `glob`/`grep` 提取 cwd 参数过沙箱 | 单测 |
| 08b-13 | 未知工具（无 command 无 path）全放行 | 单测 |
| 08b-14 | `Level` 属性可运行时 set | 单测 |

### 4.3 BatchToolExecutor 预扫描

| 编号 | 标准 | 验证方式 |
|------|------|---------|
| 08b-15 | 基类（非 Secure）预扫描全放行，行为等价 7b | 单测：全放行 + 分组执行 |
| 08b-16 | Secure 子类预扫描调 `SecurityGuard.CheckAsync` 对每个 call | 单测 |
| 08b-17 | 拦截的 call 不进 Read/Write 分组 | 单测：拦截后 results[i]=Fail，pending 不含 i |
| 08b-18 | Read 组拦截后不执行（results[i]=Fail） | 单测 |
| 08b-19 | Write 组拦截后不调 HITL（安全层先于 HITL） | 单测 |
| 08b-20 | Write 组放行后仍调 HITL | 单测 |
| 08b-21 | 全部拦截时 `pending.Count == 0` 直接返回 | 单测 |
| 08b-22 | 预扫描顺序：call[0] → call[1] → ... | 单测 |

### 4.4 SecureBatchToolExecutor 集成

| 编号 | 标准 | 验证方式 |
|------|------|---------|
| 08b-23 | 注入 `SecurityGuard` 后 Read 组也过安全层 | 集成测试 |
| 08b-24 | Strict 模式 Read 越界路径被拦，不执行 | 集成测试 |
| 08b-25 | Normal 模式 Read 项目根内放行并发执行 | 集成测试 |
| 08b-26 | Normal 模式 Write 放行后走 HITL | 集成测试 |
| 08b-27 | Permissive 模式仅黑名单拦，路径不查 | 集成测试 |
| 08b-28 | 拦截原因作为 `ToolResult.Error` 回灌 | 集成测试 |
| 08b-29 | `CancellationToken` 取消时预扫描中断 | 单测 |

---

## 五、测试计划

| 测试文件 | 覆盖范围 | 用例数 |
|---------|---------|--------|
| `SecurityGuardTests.cs` | 三层顺序、短路、原因前缀、工具类型分发、Level 切换 | ~10 |
| `SecureBatchToolExecutorTests.cs` | 预扫描、Read 组安全层、Write 组 HITL 顺序、全放行、全拦截 | ~8 |
| `BatchToolExecutorHitlTests.cs`（适配） | 断言更新（预扫描调一次而非 Write 组调） | 适配现有 |

**测试策略**：
- `SecurityGuardTests`：用假 `SecurityContext` 构造 `SecurityGuard`，直接调 `CheckAsync` 断言。
- `SecureBatchToolExecutorTests`：用假 `ToolRegistry`（注册假 Read/Write 工具）+ 假 `ToolExecutor`（记录调用）+ 假 `IHitlGate`（返回预设决策），验证预扫描 → 分组 → HITL → 执行顺序。
- **短路验证**：用 spy 模式包装 `PathSandbox`/`Blacklist`，计数调用次数，验证黑名单命中后沙箱不调。

**关键集成测试用例**：
```
[Fact] Read 组过安全层_Strict 越界被拦不执行
  - 注册假 ReadFileTool（记录调用）
  - SecurityGuard Level=Strict, ProjectRoot=/proj
  - calls = [read_file(path=/etc/passwd)]
  - 执行后：假工具未被调用，results[0].Success=false, Error 含 [路径沙箱]
```

---

## 六、实施步骤

### 步骤 1：实现 SecurityPolicy + SecurityGuard + 单测

- 新建 `Security/SecurityPolicy.cs`（默认放行）
- 新建 `Security/SecurityGuard.cs`（三层编排 + ExtractCommand/ExtractPath）
- 新建 `SecurityGuardTests.cs`（覆盖 08b-05 到 08b-14）
- 验证：单测全绿

### 步骤 2：改造 BatchToolExecutor.ExecuteAsync 预扫描

- 改 `ExecuteAsync`：入口 for 循环预扫描，pending 分组，Write 组去重
- 保留 `OnBeforeExecuteAsync` 虚方法默认实现（返回 null）
- 验证：`dotnet build` + 跑现有 `BatchToolExecutorHitlTests`

### 步骤 3：适配 BatchToolExecutorHitlTests

- 逐项核对断言：如有"Read 组不调 hook"/"Write 组调两次"等断言，更新为"预扫描调一次"
- 验证：`dotnet test` 全绿（现有 + 8a）

### 步骤 4：实现 SecureBatchToolExecutor + 集成测试

- 新建 `Security/SecureBatchToolExecutor.cs`（子类，覆写 OnBeforeExecuteAsync）
- 新建 `SecureBatchToolExecutorTests.cs`（覆盖 08b-23 到 08b-29）
- 验证：集成测试全绿

### 步骤 5：回归与验收

- `dotnet build` 0 错误 0 警告
- `dotnet test` 全绿（8a + 8b + 现有）
- 对照 08b-01 到 08b-29 逐项确认
- **重点核对**：7b 的 `BatchToolExecutorHitlTests` 全绿，预扫描未破坏 HITL 逻辑

---

## 七、风险与对策

| 风险 | 概率 | 影响 | 对策 |
|------|------|------|------|
| 预扫描改造破坏 7b HITL 测试 | **中** | **中** | 基类默认 null 等价 7b；逐项核对断言；保留 Write 组 HITL 逻辑不变 |
| `OnBeforeExecuteAsync` 顺序 await 降低 Read 并发 | 低 | 低 | 预扫描是微秒级内存操作；实际工具执行的 Task.WhenAll 并发不变 |
| `ExtractCommand`/`ExtractPath` 对未知工具漏检 | 低 | 低 | 未知工具无 command/path，全放行交工具自身处理 |
| 短路逻辑错误导致沙箱在黑名单命中后仍调 | 低 | 中 | spy 计数单测验证调用次数 |
| `CancellationToken` 在预扫描中未传播 | 低 | 中 | 循环内 `cancellationToken.ThrowIfCancellationRequested()` |

---

## 八、与父文档的关系

本子迭代交付父文档 [iter-08-design.md](iter-08-design.md) 的：
- §4.5 SecurityPolicy
- §4.6 SecurityGuard
- §4.7 SecureBatchToolExecutor
- §4.8 BatchToolExecutor.ExecuteAsync 改造

**未覆盖**（留给 8c）：
- §4.9 SecurityConfig 扩展
- §4.10 TerminalApp 装配改动
- §4.11 拒绝信息回灌的端到端验证（UI 展示）

**本迭代不接入 `App`/`TerminalApp`**：`SecureBatchToolExecutor` 已就绪但未被装配，端到端拦截在 8c 验证。

---

**文档结束**。状态：[设计完成，待实现]
