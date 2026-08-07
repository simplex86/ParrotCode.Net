# 迭代 8c：配置扩展 + 装配 + 端到端验收

> **状态**：[设计完成，待实现]
> **前置迭代**：8a [已完成]、8b [已完成]
> **父文档**：iter-08-design.md（保留追溯）
> **后续迭代**：9（上下文管理）
> **目标**：扩展 `SecurityConfig`，在 `App`/`TerminalApp` 装配 `SecurityGuard` + `SecureBatchToolExecutor`，完成端到端三档模式真实拦截验收。

---

## 一、迭代目标

### 1.1 核心目标

把 8a/8b 的安全核心与管线接入应用装配：

1. `SecurityConfig` 扩展：`AllowPaths` / `DenyPaths` / `ExtraBlacklist` 字段。
2. `App.RunAsync` 构造 `SecurityContext` + `SecurityGuard`，传入 `TerminalApp`。
3. `TerminalApp` 构造加 `SecurityGuard` 参数；`StartAgentRound` 装配 `SecureBatchToolExecutor`。
4. 端到端验收：三档模式真实拦截 + 拒绝信息回灌 LLM + UI 展示。

### 1.2 本迭代要验证的关键问题

| 问题 | 验证方式 | 失败对策 |
|------|---------|---------|
| `.parrotcode.yaml` 的 `security` 段能否正确解析为 `SecurityConfig` | 单测 + 手动 | YamlDotNet 反序列化；检查 `IList<string>` 默认值 |
| 相对路径 `allow_paths`（如 `../sibling`）能否规范化为绝对 | 单测 | `Path.GetFullPath(p, projectRoot)` |
| `TerminalApp` 注入 `SecurityGuard` 后状态栏显示正确档位 | 手动 | 检查 `StatusBarView` 枚举名 |
| `SecureBatchToolExecutor` 装配后 Read 组真实过安全层 | 手动：Strict 下 read 越界被拦 | 核对 8b 集成测试在真实装配下成立 |
| 拦截后 AI 回复"无法执行，换个方式" | 手动多轮对话 | 检查 `ToolResult.Error` 回灌到 LLM |
| 无 `security` 配置时默认 Normal，行为与 7c 一致 | 手动 | `SecurityConfig` 为 null 时默认 Normal + 空 AllowPaths |
| 旧拼法 `permisive` 在 YAML 中仍可解析 | 单测 | `App.ParseSecurityLevel` 兼容（8a 已实现） |

### 1.3 非目标（明确不做）

- ❌ 不做运行时 `/mode` 切换命令（迭代 10 斜杠命令系统）
- ❌ 不做 `AllowPermanent` 持久化（迭代 10 JSONL）
- ❌ 不做黑名单 YAML 规则文件（进阶练习，留作扩展）
- ❌ 不启用 `ToolBlockedEvent` emit（保持 `ToolResult.Fail` → `ToolResultEvent` 路径）
- ❌ 不改 `HitlPrompt` / `IHitlGate`（8a/8b 已确认不变）

### 1.4 装配策略

- **有 `security` 配置**：构造 `SecurityContext` + `SecurityGuard`，`TerminalApp` 注入，`StartAgentRound` 用 `SecureBatchToolExecutor`。
- **无 `security` 配置**：`SecurityConfig` 为 null → 默认 Normal + 空 AllowPaths + 空黑名单扩展，仍构造 `SecurityGuard`（Level=Normal），装配 `SecureBatchToolExecutor`。**保证安全层始终生效**（至少黑名单 + Normal 路径检查）。
- **状态栏显示**：`StatusBarView` 显示 `SecurityLevel` 枚举名（`Strict`/`Normal`/`Permissive`）。

---

## 二、文件改动清单

### 2.1 修改文件（3 个）

```
Config/Models.cs       # SecurityConfig 扩展 AllowPaths/DenyPaths/ExtraBlacklist
App/App.cs             # 构造 SecurityContext + SecurityGuard 传入 TerminalApp
Tui/TerminalApp.cs     # 构造加 SecurityGuard 参数；StartAgentRound 装配 SecureBatchToolExecutor
```

### 2.2 新增测试（2 个）

```
ParrotCode.Net-xUnit/
├── SecurityConfigTests.cs     # SecurityConfig 新字段 YAML 解析
└── SecurityAssemblyTests.cs   # App 装配 SecurityGuard 的集成测试（若可行）
```

### 2.3 不变文件

```
Security/*              # 8a/8b 已完成，本迭代不改动
Tui/HitlPrompt.cs       # 不变
Tui/IHitlGate.cs         # 不变
Agent/BatchToolExecutor.cs  # 8b 已改造，本迭代不改动
Tools/*                  # 不变
```

---

## 三、详细设计

> 完整代码见父文档 [iter-08-design.md](iter-08-design.md) §4.9 / §4.10 / §4.11。本节聚焦 8c 装配细节。

### 3.1 SecurityConfig 扩展

```csharp
// Config/Models.cs
public sealed record SecurityConfig
{
    /// <summary>安全等级："strict" | "normal"（默认）| "permissive"。大小写不敏感。</summary>
    public string? Level { get; init; }

    /// <summary>额外允许的路径白名单（绝对或相对项目根）。Strict 模式下只允许这些 + 项目根的读写。</summary>
    public IList<string> AllowPaths { get; init; } = Array.Empty<string>();

    /// <summary>显式拒绝的路径（最高优先级，所有非 Permissive 档位生效）。</summary>
    public IList<string> DenyPaths { get; init; } = Array.Empty<string>();

    /// <summary>额外黑名单命令正则模式（与硬编码黑名单合并）。</summary>
    public IList<string> ExtraBlacklist { get; init; } = Array.Empty<string>();
}
```

**示例 YAML**：

```yaml
security:
  level: strict
  allow_paths:
    - d:/projects/shared-libs
    - ../sibling-project
  deny_paths:
    - d:/secrets
  extra_blacklist:
    - "\\bkubectl\\s+delete\\b"
    - "\\bdocker\\s+rm\\s+-f"
```

> **注**：YAML 中正则的反斜杠需转义（`\\b`），或在单引号字符串中写 `\b`。测试用例覆盖两种写法。

### 3.2 App.RunAsync 装配

```csharp
// App/App.cs
public async Task RunAsync()
{
    var tuiConfig = _config.Tui ?? new TuiConfig();
    var securityLevel = ParseSecurityLevel(_config.Security?.Level);

    // 构造安全上下文（项目根 = 当前工作目录）
    var projectRoot = Directory.GetCurrentDirectory();
    var secCtx = new SecurityContext
    {
        ProjectRoot = projectRoot,
        AllowPaths = NormalizePaths(_config.Security?.AllowPaths, projectRoot),
        DenyPaths = NormalizePaths(_config.Security?.DenyPaths, projectRoot),
        ExtraBlacklist = _config.Security?.ExtraBlacklist ?? Array.Empty<string>()
    };
    var securityGuard = new SecurityGuard(secCtx, securityLevel, _logger);

    using var terminalApp = new TerminalApp(
        _provider, _providerConfig, _config.Agent, tuiConfig,
        securityLevel, securityGuard, _logger, _ct);
    await terminalApp.RunAsync();
}

/// <summary>规范化路径列表（相对→绝对，基于 projectRoot）。</summary>
private static IReadOnlyList<string> NormalizePaths(IList<string>? paths, string projectRoot)
{
    if (paths is null || paths.Count == 0) return Array.Empty<string>();
    var result = new List<string>(paths.Count);
    foreach (var p in paths)
    {
        try { result.Add(Path.GetFullPath(p, projectRoot)); }
        catch { /* 非法路径忽略，记日志 */ }
    }
    return result;
}
```

**关键点**：
- `_config.Security` 为 null 时，`AllowPaths`/`DenyPaths`/`ExtraBlacklist` 取空数组，`securityLevel` 默认 Normal。
- `NormalizePaths` 把相对路径（如 `../sibling`）解析为绝对路径，基于 `projectRoot`。
- 非法路径（含非法字符）忽略，不抛异常（避免启动失败）。

### 3.3 TerminalApp 装配改动

```csharp
// Tui/TerminalApp.cs
internal sealed class TerminalApp : IDisposable
{
    private readonly SecurityGuard _securityGuard;  // 新增
    // ... 其他字段不变 ...

    public TerminalApp(IBaseProvider provider,
                       ProviderConfig providerConfig,
                       AgentConfig? agentConfig,
                       TuiConfig? tuiConfig,
                       SecurityLevel securityLevel,
                       SecurityGuard securityGuard,  // 新增
                       ILogger? logger,
                       CancellationToken ct)
    {
        // ... 其他赋值不变 ...
        _securityGuard = securityGuard ?? throw new ArgumentNullException(nameof(securityGuard));
    }

    private void StartAgentRound()
    {
        var executor = new ToolExecutor(_registry!, TimeSpan.FromSeconds(_agentConfig.ToolTimeoutSeconds ?? 30), _logger);
        IHitlGate? hitlGate = _hitlPrompt is null ? new NullHitlGate() : (IHitlGate)_hitlPrompt;

        // 【迭代 8c】装配 SecureBatchToolExecutor（注入 SecurityGuard）
        _securityGuard.Level = _securityLevel;  // 同步当前档位（为运行时切换预留）
        var batchExecutor = new SecureBatchToolExecutor(
            executor, _registry!, _securityGuard,
            _agentConfig.MaxParallelism ?? 5, hitlGate, _logger);

        _sink = new ChannelEventSink();
        var agentLoop = new AgentLoop(_provider, _registry!, batchExecutor,
                                       _agentConfig.MaxRounds ?? 10,
                                       _agentConfig.ToolChoice ?? "auto",
                                       _agentConfig.SystemPrompt, logger: null);
        _agentTask = agentLoop.RunAsync(_history!, _sink, _ct);
    }
}
```

**关键点**：
- 构造强制 `SecurityGuard` 非空（App 总会构造，即使无配置也是 Normal + 空白名单）。
- `StartAgentRound` 每轮 new `SecureBatchToolExecutor`，`SecurityGuard` 是长生命周期对象（跨轮保留，8b 设计）。
- `_securityGuard.Level = _securityLevel` 同步档位，为迭代 10 `/mode` 运行时切换预留（`_securityLevel` 当前是构造时固定，迭代 10 改为可变）。

### 3.4 拒绝信息回灌与 UI 展示

拦截走 `ToolResult.Fail` → `AgentLoop` emit `ToolResultEvent(Success=false, Error=reason)` → `ChatView` 渲染为红色 `⎿ ✗ [黑名单] 递归删除根目录（rm -rf /）`。

LLM 收到的 `tool_result` 消息 `content` 为拒绝原因（含 `[黑名单]`/`[路径沙箱]` 前缀），据此调整策略。

**ChatView 渲染不变**：7c 的 `RenderEvent` 已处理 `ToolResultEvent(Success=false)` 为红色 `✗` + Error 内容。迭代 8c 无需改 ChatView。

---

## 四、验收标准

### 4.1 编译与测试

| 编号 | 标准 | 验证方式 |
|------|------|---------|
| 08c-01 | `dotnet build` 0 错误 0 警告 | 编译 |
| 08c-02 | 全量测试全绿（8a + 8b + 现有） | `dotnet test` |
| 08c-03 | `SecurityConfigTests` 新字段解析全绿 | `dotnet test` |

### 4.2 配置解析

| 编号 | 标准 | 验证方式 |
|------|------|---------|
| 08c-04 | `security.level: strict` 解析为 `SecurityLevel.Strict` | 单测 |
| 08c-05 | `allow_paths` 相对路径规范化为绝对 | 单测 |
| 08c-06 | `deny_paths` 解析为 `SecurityContext.DenyPaths` | 单测 |
| 08c-07 | `extra_blacklist` 正则数组解析为 `SecurityContext.ExtraBlacklist` | 单测 |
| 08c-08 | 无 `security` 段时默认 Normal + 空白名单 | 单测 |
| 08c-09 | 旧拼法 `level: permisive` 解析为 `Permissive`（兼容） | 单测 |
| 08c-10 | 非法路径（如 `:`）在 `allow_paths` 中被忽略不抛异常 | 单测 |

### 4.3 装配

| 编号 | 标准 | 验证方式 |
|------|------|---------|
| 08c-11 | `TerminalApp` 构造接受 `SecurityGuard` 参数 | 编译 |
| 08c-12 | `StartAgentRound` 装配 `SecureBatchToolExecutor`（非基类 `BatchToolExecutor`） | 代码审查 |
| 08c-13 | `SecurityGuard` 跨轮保留（不每轮重建） | 代码审查 |
| 08c-14 | 无 `security` 配置时仍构造 `SecurityGuard`（Normal + 空白名单） | 代码审查 |

### 4.4 端到端三档模式

| 编号 | 标准 | 验证方式 |
|------|------|---------|
| 08c-15 | Strict：`read_file` 越界路径被拦，AI 收到 `[路径沙箱]` 原因 | 手动 |
| 08c-16 | Strict：`write_file` 白名单内弹 HITL，白名单外被拦 | 手动 |
| 08c-17 | Normal：`read_file` 项目根内放行不弹 HITL | 手动 |
| 08c-18 | Normal：`write_file` 弹 HITL 确认 | 手动 |
| 08c-19 | Normal：`run_command` 黑名单命令被拦不弹 HITL | 手动 |
| 08c-20 | Permissive：`write_file` 不弹 HITL（无安全配置时） | 手动 |
| 08c-21 | Permissive：`rm -rf /` 仍被黑名单拦 | 手动 |
| 08c-22 | 安全层拒绝时不弹 HITL（避免打扰已拦截操作） | 手动 |

### 4.5 拒绝信息回灌

| 编号 | 标准 | 验证方式 |
|------|------|---------|
| 08c-23 | 拦截后 AI 收到含原因的 `ToolResult.Error` | 手动：让 AI 执行 `rm -rf /`，看 AI 回复"无法执行" |
| 08c-24 | 拒绝原因含来源前缀（`[黑名单]`/`[路径沙箱]`） | 手动 |
| 08c-25 | ChatView 显示拦截结果（红色 `✗` + 原因） | 手动 |
| 08c-26 | 拦截后 AI 能调整策略（如改用更安全命令） | 手动多轮 |

### 4.6 状态栏与兼容性

| 编号 | 标准 | 验证方式 |
|------|------|---------|
| 08c-27 | 状态栏显示修正后的 `Permissive`（非 `Permisive`） | 手动 |
| 08c-28 | 无 `security` 配置时状态栏显示 `Normal` | 手动 |
| 08c-29 | 现有 7c 功能不受影响（流式渲染/HITL/Spinner/输入） | 手动回归 |

---

## 五、测试计划

| 测试文件 | 覆盖范围 | 用例数 |
|---------|---------|--------|
| `SecurityConfigTests.cs` | YAML 解析、相对路径规范化、非法路径忽略、默认值、兼容拼法 | ~8 |
| `SecurityAssemblyTests.cs`（可选） | App 构造 SecurityGuard 的装配断言 | ~3 |

**端到端手动测试清单**（对照 08c-15 到 08c-29）：

1. **Strict 模式**：配置 `security.level: strict`，让 AI 读写项目根外文件，验证被拦。
2. **Normal 模式**：无配置（默认 Normal），多轮对话，验证 read 不弹 HITL、write 弹 HITL。
3. **Permissive 模式**：配置 `security.level: permissive`，让 AI 执行 `rm -rf /tmp`，验证被黑名单拦。
4. **黑名单回灌**：让 AI 执行 `rm -rf /`，观察 AI 回复"无法执行这个命令，换个方式"。
5. **路径沙箱回灌**：Strict 下让 AI 读 `/etc/passwd`，观察 AI 回复"路径不在白名单"。
6. **自定义黑名单**：配置 `extra_blacklist: ["\\bkubectl\\s+delete\\b"]`，让 AI 执行 `kubectl delete pod`，验证被拦。
7. **allow_paths**：Strict 下配置 `allow_paths: ["../sibling"]`，让 AI 读写该目录，验证放行。

---

## 六、实施步骤

### 步骤 1：扩展 SecurityConfig + 单测

- `Config/Models.cs` 的 `SecurityConfig` 加 `AllowPaths` / `DenyPaths` / `ExtraBlacklist`
- 新建 `SecurityConfigTests.cs`（覆盖 08c-04 到 08c-10）
- 验证：单测全绿

### 步骤 2：App 装配 SecurityGuard

- `App.RunAsync` 构造 `SecurityContext` + `SecurityGuard`，传入 `TerminalApp`
- 实现 `NormalizePaths` 辅助方法
- 验证：`dotnet build` 通过

### 步骤 3：TerminalApp 装配 SecureBatchToolExecutor

- `TerminalApp` 构造加 `SecurityGuard` 参数
- `StartAgentRound` 装配 `SecureBatchToolExecutor`（替换基类 `BatchToolExecutor`）
- 验证：`dotnet build` + `dotnet test` 全绿

### 步骤 4：端到端手动验收

- 配置三档模式分别测试（对照 08c-15 到 08c-29）
- 验证拦截原因回灌 LLM，AI 能调整策略
- 验证状态栏显示正确档位
- 回归 7c 功能（流式/HITL/Spinner/输入）

### 步骤 5：最终回归

- `dotnet build` 0 错误 0 警告
- `dotnet test` 全量全绿（8a + 8b + 8c + 现有）
- 对照父文档 [iter-08-design.md](iter-08-design.md) 验收标准 08-01 到 08-41 逐项确认
- 标记迭代 8 [已完成]

---

## 七、风险与对策

| 风险 | 概率 | 影响 | 对策 |
|------|------|------|------|
| YamlDotNet 反序列化 `IList<string>` 默认值异常 | 中 | 中 | 用 `Array.Empty<string>()` 默认值；单测覆盖无配置场景 |
| 相对路径规范化基准目录不正确 | 中 | 中 | 基准用 `Directory.GetCurrentDirectory()`（项目根）；单测验证 `../sibling` 解析 |
| `TerminalApp` 构造参数变更破坏现有调用 | 低 | 低 | 仅 `App.RunAsync` 一处调用，同步更新 |
| 端到端拦截未生效（装配遗漏） | 低 | 高 | 代码审查确认 `SecureBatchToolExecutor` 装配；手动测试 08c-21（Permissive 下 rm -rf / 被拦） |
| Strict 模式过严影响正常使用 | 中 | 中 | 默认 Normal；Strict 需显式配置；`allow_paths` 提供白名单扩展 |
| 状态栏仍显示旧拼法 `Permisive` | 低 | 低 | 8a 已迁移枚举，状态栏显示枚举名自动更新 |

---

## 八、与父文档及后续迭代的关系

### 8.1 完成父文档全部范围

本子迭代完成后，父文档 [iter-08-design.md](iter-08-design.md) 的全部范围交付：
- §4.1-4.4（8a）：SecurityLevel / Models / Blacklist / PathSandbox
- §4.5-4.8（8b）：SecurityPolicy / SecurityGuard / SecureBatchToolExecutor / BatchToolExecutor 改造
- §4.9-4.11（8c）：SecurityConfig 扩展 / TerminalApp 装配 / 拒绝信息回灌

### 8.2 后续迭代衔接

- **迭代 9（上下文管理）**：安全层与压缩无耦合，不影响。
- **迭代 10（斜杠命令 + 持久化）**：
  - `/mode strict|normal|permissive` 运行时切换 `SecurityGuard.Level`（8b 已预留可变属性）。
  - `AllowPermanent` 持久化到 JSONL，跨会话加载。
  - `PARROTCODE.md` 项目指令可声明默认安全等级。
- **迭代 11（MCP）**：MCP 工具调用同样过 `SecurityGuard`；MCP 的 `run_command` 类工具受黑名单约束。
- **迭代 12（Hook + 子 Agent）**：Hook 的 `tool_pre_exec` 在 `SecurityGuard` 之后；子 Agent 工具调用同样过安全层。

---

**文档结束**。状态：[设计完成，待实现]
