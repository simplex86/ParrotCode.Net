# 迭代 10c：项目指令（InstructionLoader + `@include` + AgentLoop 注入 + 端到端装配）

> **状态**：[设计完成，待实现]
> **前置迭代**：10a [已完成]（命令系统）、10b [已完成]（JSONL 持久化）
> **父文档**：[iter-10-design.md](iter-10-design.md)（保留追溯）
> **后续迭代**：11（MCP 协议客户端）
> **目标**：交付项目指令加载——`InstructionLoader`（三级目录扫描 + `@include` 嵌套限 3 层）+ 指令注入 `AgentLoop` system prompt（每轮重建，不受压缩/`/clear` 影响）+ `InstructionsConfig` 配置 + `App.cs` 端到端装配收尾。本迭代完成后，迭代 10 三大子系统全部就绪。

---

## 一、迭代目标

### 1.1 核心目标

让 Agent 知道项目约定（编码规范、测试命令、架构约束），类似 `.cursorrules` / `CLAUDE.md`：

1. **`InstructionLoader`**：三级目录扫描
   - 全局：`~/.parrocode/instructions.md`
   - 项目：`./PARROTCODE.md`
   - 本地：`./.parrotcode/instructions.md`（可 `.gitignore`）
   - 按优先级合并，后者追加

2. **`@include` 嵌套**：主指令文件支持 `@include path/to/file.md` 引用子文件
   - 限 3 层防无限递归
   - 支持双引号包裹含空格路径：`@include "path with spaces.md"`
   - 相对路径基于引用文件所在目录解析
   - 引用不存在文件时替换为提示文本（不崩溃）

3. **system prompt 注入**：项目指令拼接到 `AgentLoop._systemPrompt`
   - `TerminalApp` 构造时拼接：默认 prompt + `"\n\n## 项目指令\n"` + 指令内容
   - 每轮 `BuildMessagesWithSystem` 重新拼装（system prompt 在头部，历史在后）
   - **不受压缩影响**（压缩只动历史，不动 system prompt）
   - **不受 `/clear` 影响**（`/clear` 清历史，system prompt 每轮重建）

4. **`InstructionsConfig`**：配置项（`enable` / `max_include_depth` / `project_instructions_path`）

5. **`/status` 显示指令概要**：`StatusCommand` 的 `InstructionSummary` 字段填充

6. **端到端装配**：`App.cs` 构造 `InstructionLoader` 加载指令注入 `TerminalApp` → `AgentLoop`

### 1.2 本迭代要验证的关键问题

| 问题 | 验证方式 | 失败对策 |
|------|---------|---------|
| 项目指令是否真的注入 system prompt（每轮） | 日志/代码审查 | `BuildMessagesWithSystem` 拼装时 system 在首位 |
| 压缩后指令是否仍生效 | 手动（触发压缩后对话） | system prompt 不入历史，压缩不影响 |
| `/clear` 后指令是否仍生效 | 手动（/clear 后对话） | system prompt 每轮重建，/clear 只清历史 |
| `@include` 嵌套超过 3 层是否正确终止 | 单测 | `depth > maxIncludeDepth` 返回 null 记日志 |
| `@include` 循环引用（A 引用 B，B 引用 A）是否导致栈溢出 | 单测 | 深度限制兜底（即使不检测循环，3 层后终止） |
| `@include` 相对路径解析是否正确 | 单测 | 基于引用文件所在目录 `Path.GetDirectoryName` |
| 无任何指令文件时是否正常（不报错） | 单测 | `TryReadWithIncludes` 文件不存在返回 null |
| AI 是否真的遵守指令约定 | 端到端（PARROTCODE.md 写"用中文回复"） | 指令在 system prompt，LLM 应遵守 |

### 1.3 非目标（明确不做）

- ❌ 不做指令热重载（文件变化时自动重新加载）——后续迭代
- ❌ 不做 `@include` 通配符支持（`@include docs/*.md`）——后续迭代
- ❌ 不做指令的 YAML 结构化解析——本迭代是纯 Markdown 文本拼接
- ❌ 不做指令来源的 UI 展示（除 `/status` 概要外）——后续可加 `/instructions` 命令
- ❌ 不做 `@include` 路径遍历防护——项目指令是受信任内容（类似 `.cursorrules`）

### 1.4 与 10a/10b 的衔接策略

- 10a：`TerminalApp._instructionSummary = null`，`StatusCommand` 显示"未加载"
- 10c：`App.cs` 构造 `InstructionLoader` 加载指令，注入 `TerminalApp`；`StatusCommand` 显示概要
- **`AgentLoop` 无代码改动**：`systemPrompt` 参数本就支持自定义。10c 只改 `TerminalApp.StartAgentRound` 传入的 prompt（从 `_agentConfig.SystemPrompt` 改为含指令的 `_systemPromptWithInstructions`）

---

## 二、文件改动清单

### 2.1 新增文件（2 个）

```
Instructions/
├── InstructionLoader.cs     # 三级目录扫描 + @include 嵌套
└── InstructionResult.cs     # 加载结果 record（Content + Sources）
```

### 2.2 修改文件（4 个）

| 文件 | 改动 |
|------|------|
| `Tui/TerminalApp.cs` | 构造函数加 `InstructionResult` 参数；拼接 `_systemPromptWithInstructions`；`StartAgentRound` 传含指令的 prompt；`_instructionSummary` 填充 |
| `Agent/AgentLoop.cs` | **无代码改动**（`systemPrompt` 参数已支持；仅调用方传值变化） |
| `App/App.cs` | 构造 `InstructionLoader` 加载指令注入 `TerminalApp` |
| `Config/Models.cs` | 新增 `InstructionsConfig` + `AppConfig.Instructions` |
| `example.parrotcode.yaml` | 新增 `instructions:` 配置节示例 |

### 2.3 不变文件

- `Commands/` 骨架与内置命令——10a 已完成
- `Storage/SessionStore`——10b 已完成
- `StatusCommand`——10a 已支持 `InstructionSummary`（10a 为 null，10c 填充）

---

## 三、详细设计

### 3.1 项目指令加载流程

```
1. 三级目录扫描（按优先级合并，后者追加）：
   a. ~/.parrocode/instructions.md     （全局用户指令）
   b. ./PARROTCODE.md                  （项目根指令，类似 CLAUDE.md）
   c. ./.parrotcode/instructions.md    （项目本地指令，可被 .gitignore 忽略）

2. @include 嵌套处理（限 3 层）：
   主指令文件内容 → 扫描 @include path/to/file.md → 递归加载子文件内容替换
   
3. 合并结果：
   [全局指令]\n\n[项目指令（含 @include 展开）]\n\n[本地指令（含 @include 展开）]

4. 注入 system prompt：
   TerminalApp 构造时拼接：默认 prompt + "\n\n## 项目指令\n" + 指令内容
   每轮 BuildMessagesWithSystem 重新拼装，不受压缩影响
```

### 3.2 InstructionResult

```csharp
// Instructions/InstructionResult.cs
namespace ParrotCode;

/// <summary>
/// 指令加载结果。
/// </summary>
public sealed record InstructionResult
{
    /// <summary>合并后的指令文本（注入 system prompt）。</summary>
    public string Content { get; init; } = string.Empty;

    /// <summary>来源文件列表（含全局/项目/本地 + @include 展开）。</summary>
    public IReadOnlyList<string> Sources { get; init; } = Array.Empty<string>();

    /// <summary>是否有任何指令被加载。</summary>
    public bool HasInstructions => !string.IsNullOrWhiteSpace(Content);
}
```

### 3.3 InstructionLoader（核心加载器）

```csharp
// Instructions/InstructionLoader.cs
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace ParrotCode;

/// <summary>
/// 项目指令加载器：三级目录扫描 + @include 嵌套（限 3 层）。
/// 加载顺序：
/// 1. ~/.parrocode/instructions.md（全局用户指令）
/// 2. ./PARROTCODE.md（项目根指令）
/// 3. ./.parrotcode/instructions.md（项目本地指令）
/// 每个文件支持 @include path/to/file.md 嵌套引用，限 maxIncludeDepth 层防无限递归。
/// </summary>
public sealed class InstructionLoader
{
    private readonly string _projectRoot;
    private readonly string _userHome;
    private readonly int _maxIncludeDepth;
    private readonly string _projectInstructionsPath;
    private readonly ILogger? _logger;

    // @include path/to/file.md 或 @include "path with spaces.md"
    private static readonly Regex IncludeRegex = new(
        @"@include\s+(?:""([^""]+)""|(\S+))",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public InstructionLoader(
        string? projectRoot = null,
        string? userHome = null,
        int maxIncludeDepth = 3,
        string? projectInstructionsPath = null,
        ILogger? logger = null)
    {
        _projectRoot = projectRoot ?? Directory.GetCurrentDirectory();
        _userHome = userHome ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        _maxIncludeDepth = maxIncludeDepth;
        _projectInstructionsPath = projectInstructionsPath ?? "PARROTCODE.md";
        _logger = logger;
    }

    /// <summary>
    /// 加载所有指令（三级目录扫描 + @include 展开）。
    /// </summary>
    public InstructionResult Load()
    {
        var sources = new List<string>();
        var sections = new List<string>();

        // 1. 全局用户指令
        var globalPath = Path.Combine(_userHome, ".parrocode", "instructions.md");
        var globalContent = TryReadWithIncludes(globalPath, depth: 0);
        if (globalContent is not null)
        {
            sections.Add("## 全局指令\n" + globalContent.Value.Content);
            sources.AddRange(globalContent.Value.Sources);
        }

        // 2. 项目根指令
        var projectPath = Path.IsPathRooted(_projectInstructionsPath)
            ? _projectInstructionsPath
            : Path.Combine(_projectRoot, _projectInstructionsPath);
        var projectContent = TryReadWithIncludes(projectPath, depth: 0);
        if (projectContent is not null)
        {
            sections.Add("## 项目指令\n" + projectContent.Value.Content);
            sources.AddRange(projectContent.Value.Sources);
        }

        // 3. 项目本地指令
        var localPath = Path.Combine(_projectRoot, ".parrotcode", "instructions.md");
        var localContent = TryReadWithIncludes(localPath, depth: 0);
        if (localContent is not null)
        {
            sections.Add("## 本地指令\n" + localContent.Value.Content);
            sources.AddRange(localContent.Value.Sources);
        }

        return new InstructionResult
        {
            Content = string.Join("\n\n", sections),
            Sources = sources.Distinct().ToList()
        };
    }

    /// <summary>
    /// 读取文件并展开 @include 指令（递归，限 maxIncludeDepth 层）。
    /// 返回 (展开后内容, 来源文件列表)；文件不存在返回 null。
    /// </summary>
    private (string Content, List<string> Sources)? TryReadWithIncludes(string filePath, int depth)
    {
        if (!File.Exists(filePath))
            return null;

        if (depth > _maxIncludeDepth)
        {
            _logger?.LogWarning("@include 嵌套超过 {Max} 层，跳过 {File}", _maxIncludeDepth, filePath);
            return null;
        }

        string raw;
        try
        {
            raw = File.ReadAllText(filePath);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning("读取指令文件失败 {File}：{Error}", filePath, ex.Message);
            return null;
        }

        var sources = new List<string> { filePath };
        var content = new StringBuilder(raw);

        // 查找所有 @include 指令
        var matches = IncludeRegex.Matches(raw);
        // 从后往前替换（避免索引偏移）
        for (var i = matches.Count - 1; i >= 0; i--)
        {
            var match = matches[i];
            var includePath = match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Value;

            // 解析路径（相对当前文件所在目录）
            var basePath = Path.GetDirectoryName(filePath) ?? _projectRoot;
            var resolvedPath = Path.IsPathRooted(includePath)
                ? includePath
                : Path.GetFullPath(includePath, basePath);

            var included = TryReadWithIncludes(resolvedPath, depth + 1);
            if (included is not null)
            {
                content.Remove(match.Index, match.Length);
                content.Insert(match.Index, included.Value.Content);
                sources.AddRange(included.Value.Sources);
            }
            else
            {
                // @include 文件不存在——替换为提示
                var warning = $"[指令引用失败：{includePath}]";
                content.Remove(match.Index, match.Length);
                content.Insert(match.Index, warning);
                _logger?.LogWarning("@include 文件不存在：{Path}（引用自 {File}）", resolvedPath, filePath);
            }
        }

        return (content.ToString(), sources);
    }

    /// <summary>生成指令加载概要（/status 用）。</summary>
    public string GetSummary(InstructionResult result)
    {
        if (!result.HasInstructions) return "未加载";
        return $"{result.Sources.Count} 个文件：{string.Join(", ", result.Sources.Select(Path.GetFileName))}";
    }
}
```

### 3.4 InstructionsConfig 配置

```csharp
// Config/Models.cs — 新增 InstructionsConfig

/// <summary>
/// 项目指令配置（迭代 10c 新增）。null 时用默认值。
/// </summary>
public sealed record InstructionsConfig
{
    /// <summary>是否启用项目指令加载。默认 true。false 时不扫描任何指令文件。</summary>
    public bool? Enable { get; init; }

    /// <summary>@include 最大嵌套深度。默认 3。</summary>
    public int? MaxIncludeDepth { get; init; }

    /// <summary>自定义项目指令文件路径（覆盖默认的 ./PARROTCODE.md）。</summary>
    public string? ProjectInstructionsPath { get; init; }
}

// AppConfig 扩展
public sealed record AppConfig
{
    // ... 既有字段（含 10b 的 Session）...
    /// <summary>项目指令配置（迭代 10c 新增）。null 时用默认值。</summary>
    public InstructionsConfig? Instructions { get; init; }
}
```

**示例 YAML**（`example.parrotcode.yaml` 追加）：

```yaml
# 迭代 10c 新增：项目指令配置（全部可选，省略时用默认值）
instructions:
  enable: true                        # 是否启用项目指令加载
  max_include_depth: 3                # @include 最大嵌套深度
  # project_instructions_path: ./PARROTCODE.md  # 自定义项目指令路径（默认 ./PARROTCODE.md）
```

### 3.5 TerminalApp 扩展（system prompt 注入）

```csharp
// Tui/TerminalApp.cs — 10c 扩展要点

internal sealed class TerminalApp : IUiControl, IDisposable
{
    private readonly SessionStore? _sessionStore;          // 10b 注入
    private readonly InstructionResult _instructions;      // 10c 新增
    private readonly string _instructionSummary;           // 10c 新增
    private readonly string _systemPromptWithInstructions; // 10c 新增：含指令的 system prompt

    public TerminalApp(/* 既有参数 */,
                       SessionStore? sessionStore,          // 10b
                       InstructionResult instructions,      // 10c 新增
                       ILogger? logger,
                       CancellationToken ct)
    {
        // ... 既有赋值 ...
        _sessionStore = sessionStore;
        _instructions = instructions;

        // 拼接 system prompt：默认 + 项目指令
        var basePrompt = _agentConfig?.SystemPrompt ?? DefaultSystemPrompt;
        _systemPromptWithInstructions = instructions.HasInstructions
            ? basePrompt + "\n\n## 项目指令\n" + instructions.Content
            : basePrompt;

        _instructionSummary = new InstructionLoader().GetSummary(instructions);

        // 命令系统装配（10a 已完成，不变）
        _commandRegistry = new CommandRegistry(logger);
        _commandRegistry.Register(new HelpCommand(_commandRegistry));
        _commandRegistry.AutoRegisterFromAssembly();
        _commandDispatcher = new CommandDispatcher(_commandRegistry);
    }

    private static string DefaultSystemPrompt =>
        "你是 ParrotCode.Net 的 AI 编程助手。你可以调用工具读写文件、执行命令、搜索代码。" +
        "每次只调用必要的工具，拿到结果后用简洁中文回复用户。";

    private void StartAgentRound()
    {
        // ... 既有装配（不变）...

        var agentLoop = new AgentLoop(_provider,
                                      _registry!,
                                      batchExecutor,
                                      _agentConfig.MaxRounds ?? 10,
                                      _agentConfig.ToolChoice ?? "auto",
                                      _systemPromptWithInstructions,  // 10c 改：用含指令的 prompt
                                      compressor: _compressor,
                                      logger: null);

        _agentTask = agentLoop.RunAsync(_history!, _sink, _ct);
    }

    private CommandContext BuildCommandContext() => new(
        History: _history!,
        Compressor: _compressor,
        SecurityGuard: _securityGuard,
        Ui: this,
        SessionStore: _sessionStore,
        Ct: _ct)
    {
        ProviderConfig = _providerConfig,
        TuiConfig = _tuiConfig,
        AgentConfig = _agentConfig,
        InstructionSummary = _instructionSummary,  // 10c 填充
    };
}
```

### 3.6 AgentLoop（无代码改动）

```csharp
// Agent/AgentLoop.cs — 无改动，systemPrompt 参数已支持

internal sealed class AgentLoop
{
    // ... 既有构造函数（systemPrompt 参数本就支持）...

    // BuildMessagesWithSystem 不变——每轮重新拼装 system prompt + 历史
    private IReadOnlyList<Message> BuildMessagesWithSystem(ConversationHistory history)
    {
        var snapshot = history.ToProviderMessages();
        if (string.IsNullOrEmpty(_systemPrompt))
            return snapshot;
        var withSystem = new List<Message>(snapshot.Count + 1)
        {
            new(MessageRole.System, _systemPrompt)
        };
        withSystem.AddRange(snapshot);
        return withSystem;
    }
}
```

> **关键**：`AgentLoop` 无需改动——`systemPrompt` 参数本就支持自定义。改造在 `TerminalApp.StartAgentRound`：传入 `_systemPromptWithInstructions` 而非 `_agentConfig.SystemPrompt`。每轮 `BuildMessagesWithSystem` 重新拼装，项目指令始终在 system prompt 头部。

### 3.7 App.cs 端到端装配

```csharp
// App/App.cs — 10c 扩展：加载项目指令 + 完整装配

public async Task RunAsync()
{
    var tuiConfig = _config.Tui ?? new TuiConfig();
    var securityLevel = ParseSecurityLevel(_config.Security?.Level);
    var projectRoot = Directory.GetCurrentDirectory();

    // ... 既有 SecurityGuard + ContextCompressor 构造（不变）...

    // 【迭代 10b】SessionStore 装配（已完成）
    var sessionConfig = _config.Session ?? new SessionConfig();
    SessionStore? sessionStore = null;
    if (sessionConfig.Enable ?? true)
    {
        sessionStore = new SessionStore(
            storageDir: sessionConfig.StorageDir ?? ".parrotcode/sessions",
            _logger);
    }

    // 【迭代 10c】加载项目指令
    var instructionsConfig = _config.Instructions ?? new InstructionsConfig();
    InstructionResult instructions = new();
    if (instructionsConfig.Enable ?? true)
    {
        var loader = new InstructionLoader(
            projectRoot: projectRoot,
            maxIncludeDepth: instructionsConfig.MaxIncludeDepth ?? 3,
            projectInstructionsPath: instructionsConfig.ProjectInstructionsPath,
            _logger);
        instructions = loader.Load();
        if (instructions.HasInstructions)
            _logger.LogInformation("已加载项目指令：{Count} 个文件", instructions.Sources.Count);
    }

    using var terminalApp = new TerminalApp(_provider,
                                            _providerConfig,
                                            _config.Agent,
                                            tuiConfig,
                                            securityLevel,
                                            securityGuard,
                                            compressor,
                                            sessionStore,        // 10b
                                            instructions,        // 10c 新增
                                            _logger,
                                            _ct);
    await terminalApp.RunAsync();
}
```

### 3.8 @include 嵌套展开流程

```
TryReadWithIncludes(filePath, depth=0)
    │
    ▼ 文件存在？→ 否 → return null
    │ 是
    ▼ depth > maxIncludeDepth(3)？→ 是 → 记日志，return null
    │ 否
    ▼ 读取文件全部内容 raw
    │
    ▼ IncludeRegex.Matches(raw) 找所有 @include
    │
    ▼ 从后往前替换（避免索引偏移）：
    │   每个 @include path
    │     ├─ 解析 path（相对 filePath 所在目录）
    │     ├─ 递归 TryReadWithIncludes(resolvedPath, depth+1)
    │     │   ├─ 成功 → 替换为子文件内容，sources.AddRange
    │     │   └─ 失败 → 替换为 "[指令引用失败：path]"
    │
    ▼ return (展开后内容, sources)
```

**示例**：

`./PARROTCODE.md`：
```markdown
# 项目约定
- 用中文回复
- 测试命令：dotnet test
@include docs/coding-standards.md
```

`./docs/coding-standards.md`：
```markdown
# 编码规范
- 使用 var 声明
- 方法名 PascalCase
```

**展开后**注入 system prompt：
```markdown
## 项目指令
# 项目约定
- 用中文回复
- 测试命令：dotnet test
# 编码规范
- 使用 var 声明
- 方法名 PascalCase
```

---

## 四、验收标准

### 4.1 编译与测试

| 编号 | 标准 | 验证方式 |
|------|------|---------|
| 10c-01 | `dotnet build` 0 错误 0 警告 | 编译 |
| 10c-02 | 全量测试全绿（10a + 10b + 10c 新增） | `dotnet test` |
| 10c-03 | `InstructionLoaderTests` 全绿 | `dotnet test` |
| 10c-04 | `InstructionsConfigTests` 全绿 | `dotnet test` |
| 10c-05 | 10a/10b 测试不回归 | `dotnet test` |

### 4.2 InstructionLoader

| 编号 | 标准 | 验证方式 |
|------|------|---------|
| 10c-10 | 无指令文件时返回 `HasInstructions=false` | 单测（临时目录） |
| 10c-11 | 项目根有 `PARROTCODE.md` 时加载其内容 | 单测 |
| 10c-12 | 全局 `~/.parrocode/instructions.md` 被加载 | 单测 |
| 10c-13 | 本地 `.parrotcode/instructions.md` 被加载 | 单测 |
| 10c-14 | 三级指令合并后 Content 含全部三段 | 单测 |
| 10c-15 | 合并顺序：全局 → 项目 → 本地 | 单测 |
| 10c-16 | `@include path/to/file.md` 正确展开子文件内容 | 单测 |
| 10c-17 | `@include "path with spaces.md"` 支持带空格路径 | 单测 |
| 10c-18 | `@include` 嵌套超过 3 层时跳过并记日志 | 单测 |
| 10c-19 | `@include` 引用不存在的文件时替换为提示文本 | 单测 |
| 10c-20 | `@include` 相对路径基于引用文件所在目录解析 | 单测 |
| 10c-21 | `@include` 绝对路径直接使用 | 单测 |
| 10c-22 | `@include` 嵌套内容正确合并到主文件位置 | 单测 |
| 10c-23 | `Sources` 列表含所有加载的文件路径（含 @include 展开） | 单测 |
| 10c-24 | `Sources` 去重（同一文件被多次 include 只记一次） | 单测 |
| 10c-25 | 自定义 `projectInstructionsPath` 生效 | 单测 |
| 10c-26 | 读取文件失败（权限等）记日志返回 null 不崩溃 | 单测 |
| 10c-27 | `GetSummary` 无指令返回"未加载" | 单测 |
| 10c-28 | `GetSummary` 有指令返回"N 个文件：..." | 单测 |

### 4.3 system prompt 注入

| 编号 | 标准 | 验证方式 |
|------|------|---------|
| 10c-35 | 项目指令内容出现在 system prompt（每轮） | 代码审查 / 日志 |
| 10c-36 | 无指令时 system prompt 为默认 prompt | 代码审查 |
| 10c-37 | 压缩后指令仍生效（system prompt 不受压缩影响） | 手动 |
| 10c-38 | `/clear` 后指令仍生效（system prompt 每轮重建） | 手动 |
| 10c-39 | `@include` 展开的子文件内容出现在 system prompt | 手动 |
| 10c-40 | system prompt 在消息列表首位（history 之前） | 代码审查 |

### 4.4 InstructionsConfig 配置

| 编号 | 标准 | 验证方式 |
|------|------|---------|
| 10c-45 | `instructions:` 段正确解析为 `InstructionsConfig` | 单测 |
| 10c-46 | 无 `instructions:` 段时用默认值（enable=true / depth=3） | 单测 |
| 10c-47 | `instructions.enable: false` 时不加载任何指令 | 单测（App 装配） |
| 10c-48 | `instructions.max_include_depth: 5` 自定义深度生效 | 单测 |
| 10c-49 | `instructions.project_instructions_path` 自定义路径生效 | 单测 |

### 4.5 /status 指令概要

| 编号 | 标准 | 验证方式 |
|------|------|---------|
| 10c-55 | `/status` 显示指令概要（非"未加载"） | 手动 |
| 10c-56 | 无指令时 `/status` 显示"未加载" | 手动 |

### 4.6 端到端（迭代 10 收尾）

| 编号 | 标准 | 验证方式 |
|------|------|---------|
| 10c-65 | 项目根放 `PARROTCODE.md`，AI 回复体现对约定的遵守 | 手动 |
| 10c-66 | `@include` 引用的子文件约定被 AI 遵守 | 手动 |
| 10c-67 | 全局指令 `~/.parrocode/instructions.md` 被 AI 遵守 | 手动 |
| 10c-68 | 本地指令 `.parrotcode/instructions.md` 被 AI 遵守 | 手动 |
| 10c-69 | `/clear` 后指令仍生效（对话验证） | 手动 |
| 10c-70 | 触发压缩后指令仍生效（对话验证） | 手动 |
| 10c-71 | `/session save` + `/session load` 后指令仍生效（指令来自 prompt 不来自历史） | 手动 |
| 10c-72 | `/mode permissive` 后 write_file 不弹 HITL（10a 命令 + 8c 安全） | 手动 |
| 10c-73 | `/mode strict` 后 read 项目外文件被拦（10a 命令 + 8c 安全） | 手动 |
| 10c-74 | 现有 9 功能不受影响（截断/摘要/熔断） | 手动回归 |
| 10c-75 | 现有 8c 功能不受影响（安全层/HITL） | 手动回归 |
| 10c-76 | 现有 7c 功能不受影响（流式渲染/Spinner） | 手动回归 |
| 10c-77 | 10a 命令系统正常（/help /clear /mode /compress /status /exit） | 手动回归 |
| 10c-78 | 10b 会话持久化正常（/session save/load/list） | 手动回归 |

---

## 五、测试计划

| 测试文件 | 覆盖范围 | 预估用例数 |
|---------|---------|-----------|
| `InstructionLoaderTests.cs` | 三级扫描/@include/嵌套深度/路径解析/Sources/GetSummary | ~19 |
| `InstructionsConfigTests.cs` | YAML 解析/默认值/enable=false/自定义路径 | ~5 |

**端到端手动测试清单**（对照 10c-65 ~ 10c-78）：

1. **项目指令验证**：
   - 项目根创建 `PARROTCODE.md`，写"所有回复以'收到'结尾"
   - 启动程序，对话，验证 AI 回复以"收到"结尾
   - `@include docs/coding-standards.md` 引用子文件，验证子文件约定生效

2. **指令持久性验证**：
   - `/clear` 后对话，验证指令仍生效
   - 触发压缩（或 `/compress`）后对话，验证指令仍生效
   - `/session save` + `/session load` 后对话，验证指令仍生效（指令来自 prompt 不来自历史）

3. **三级指令验证**：
   - 全局 `~/.parrocode/instructions.md` 写"用中文回复"
   - 项目 `./PARROTCODE.md` 写"测试命令：dotnet test"
   - 本地 `.parrotcode/instructions.md` 写"用 var 声明"
   - 对话验证三者都生效

4. **端到端综合回归**（迭代 10 收尾）：
   - 10a 命令：/help /clear /mode /compress /status /exit
   - 10b 持久化：/session save/load/list
   - 10c 指令：PARROTCODE.md 生效
   - 9 功能：截断/摘要/熔断
   - 8c 功能：安全层/HITL
   - 7c 功能：流式输出/Spinner

---

## 六、实施步骤

1. 新建 `Instructions/InstructionLoader.cs` / `InstructionResult.cs`
2. 新建 `InstructionLoaderTests.cs`（用临时目录构造三级指令文件 + @include）
3. `Config/Models.cs` 加 `InstructionsConfig` + `AppConfig.Instructions`
4. `example.parrotcode.yaml` 加 `instructions:` 段
5. 新建 `InstructionsConfigTests.cs`
6. `Tui/TerminalApp.cs` 扩展：构造函数加 `InstructionResult` 参数；拼接 `_systemPromptWithInstructions`；`StartAgentRound` 传含指令的 prompt；`_instructionSummary` 填充
7. `App/App.cs` 扩展：构造 `InstructionLoader` 加载指令注入 `TerminalApp`
8. 验证：`dotnet build` 0 警告 + `dotnet test` 全绿
9. 端到端手动验收（10c-65 ~ 10c-78，含迭代 10 整体回归）
10. 标记迭代 10c [已完成] + 标记迭代 10 [已完成]

---

## 七、风险与对策

| 风险 | 概率 | 影响 | 对策 |
|------|------|------|------|
| `@include` 循环引用导致无限递归 | 中 | 高 | `maxIncludeDepth=3` 限制；超出记日志并跳过（即使不显式检测循环，深度兜底） |
| `@include` 路径遍历（`../../../etc/passwd`） | 低 | 中 | 项目指令是受信任内容（类似 `.cursorrules`）；安全敏感场景可加路径白名单 |
| 指令文件编码非 UTF-8 导致乱码 | 低 | 中 | `File.ReadAllText` 默认 UTF-8；非 UTF-8 文件记日志 |
| 指令内容过长撑爆 context window | 低 | 中 | 指令通常 < 2K tokens；超长时 LLM 会自行截断关注；后续可加指令长度警告 |
| `InstructionLoader` 在 `TerminalApp` 构造时同步加载阻塞启动 | 低 | 低 | 文件读取快（< 10ms）；本迭代接受同步加载 |
| 指令注入后 LLM 不遵守 | 中 | 中 | system prompt 权重高，LLM 通常遵守；不遵守是对话问题非技术问题 |
| 指令与 `AgentConfig.SystemPrompt` 冲突 | 低 | 低 | 指令追加在默认 prompt 之后（`basePrompt + 指令`），后者覆盖前者语义 |
| 热重载需求（文件变化时重新加载） | — | — | 本迭代不支持；需重建 `AgentLoop`（`_systemPrompt` 构造时固定）。留作进阶练习 |

---

## 八、关键设计决策

### Q1：为什么项目指令注入 system prompt 而非历史？

**注入历史方案**：把指令作为第一条 `MessageRole.System` 消息存入 `ConversationHistory`。

**问题**：
- 压缩时 system 消息会被摘要（迭代 9 的 `StructuredSummarizer` 对所有消息摘要）——指令丢失
- `/clear` 清空历史后指令丢失
- 每轮 `BuildMessagesWithSystem` 重复拼装——指令在历史中重复出现

**注入 system prompt 方案**：指令拼接到 `_systemPrompt`，每轮 `BuildMessagesWithSystem` 重新构造 `system prompt + 历史`。指令始终在头部，不受压缩/清空影响。

**取舍**：`AgentLoop._systemPrompt` 在构造时固定，指令在 `TerminalApp` 构造时加载拼接。如需热重载（文件变化时重新加载），需重建 `AgentLoop`——本迭代不支持，留作进阶练习。

### Q2：为什么 @include 限制 3 层而非更深？

- 3 层足够覆盖常见场景：主指令 → 通用规范 → 具体条目
- 更深嵌套增加调试难度（循环引用检测靠深度限制）
- 可经 `InstructionsConfig.MaxIncludeDepth` 配置

### Q3：为什么三级目录而非单一级别？

| 级别 | 路径 | 用途 | gitignore |
|------|------|------|-----------|
| 全局 | `~/.parrocode/instructions.md` | 用户跨项目通用偏好（如"始终用中文"） | N/A |
| 项目 | `./PARROTCODE.md` | 项目团队共享约定（提交到 git） | 否 |
| 本地 | `./.parrotcode/instructions.md` | 个人本地覆盖（不提交） | 是 |

**合并顺序**：全局 → 项目 → 本地。后者追加，语义上后者可补充或覆盖前者。

### Q4：为什么 `AgentLoop` 无需改动？

`AgentLoop` 的 `systemPrompt` 参数本就支持自定义（迭代 4 已有）。10c 的改造仅在 `TerminalApp.StartAgentRound`：传入 `_systemPromptWithInstructions`（含项目指令）而非 `_agentConfig.SystemPrompt`（纯配置）。

`BuildMessagesWithSystem` 每轮重新拼装 `system prompt + 历史`，项目指令始终在头部。这是迭代 4 已有的机制，10c 只是改变了传入的 prompt 内容。

### Q5：为什么指令是纯 Markdown 文本而非结构化 YAML？

**结构化方案**：`instructions.yml` 解析为 `{"coding_standards": "...", "test_commands": [...]}`。

**问题**：
- 增加解析复杂度
- LLM 对自然语言 Markdown 的理解优于结构化字段
- `@include` 在结构化格式中处理复杂

**纯文本方案**：指令就是 Markdown，直接拼接注入。LLM 按自然语言理解。`@include` 是简单的文本替换。

**取舍**：保持简单。指令是给 LLM 看的"人话"，不是给程序解析的结构化数据。

---

## 九、与后续迭代的关系

### 9.1 迭代 11（MCP 协议客户端）

- MCP 工具调用同样产生 `tool_use` / `tool_result`——JSONL 持久化无需改动（`MessageDto` 协议中性，10b 已完成）
- MCP 工具名含 `{server_name}/{tool_name}` 前缀——`MessageDto` 存原始字符串，不影响
- `/status` 命令可扩展显示 MCP server 连接状态

### 9.2 迭代 12（Skill + Hook + 子 Agent）

- **Skill 系统**：新增 `/skill` 命令（`CommandType.System`），自动注册到 Registry（10a 已支持反射扫描）。Skill 的 SOP 作为 system prompt 注入——与项目指令注入机制一致（10c 已建立模式）
- **Hook 引擎**：`tool_pre_exec` / `tool_post_exec` Hook 可在命令执行前/后触发。`/clear` `/mode` 等命令可配 Hook（如 `/clear` 前自动 `/session save`）
- **子 Agent**：Fork 式子 Agent 继承父历史——可 `/session save` 父会话后 Fork
- **`AllowPermanent` 持久化**：HITL 的永久允许可持久化到 `.parrotcode/permissions.json`，跨会话加载。与 SessionStore 同级目录

### 9.3 进阶练习（迭代 10 之后）

- **指令热重载**：`FileSystemWatcher` 监听指令文件变化，自动重新加载并重建 `AgentLoop`
- **会话自动恢复**：启动时加载上次会话（需持久化"上次会话 ID"到 `.parrotcode/last_session.json`）
- **会话导出**：`/session export <id> markdown` 导出为 Markdown 对话记录
- **会话搜索**：`/session search <keyword>` 在历史会话中搜索
- **摘要缓存持久化**：迭代 9 的摘要结果缓存到 `.parrotcode/summaries/`
- **`@include` 通配符**：`@include docs/*.md` 批量引用

---

**文档结束**。状态：[设计完成，待实现]
