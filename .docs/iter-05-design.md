# 迭代 5：工具系统骨架 + 三个文件工具 — 详细设计

> 状态：[已完成]
> 对应 `plan.md` 第三章「迭代 5」，本文档在其基础上补充实现级细节与可执行的验收清单。
> 前置：迭代 4 已交付 `ConversationHistory`（含 `AddUser` / `AddAssistant` / `AddTool` / `ToProviderMessages`）/ `TokenEstimator` / `MessageExtensions` / `Message` + `MessageRole` + `ToolCall(string Id, string Name, JsonElement Input)` 类型（来自迭代 2a，本迭代启用 `ToolCall` 字段）。本迭代引入工具系统骨架与三个内置文件工具，让"LLM → 工具调用 → 结果"的闭环可在无 LLM 环境下跑通。

## 一、概述

迭代 4 让 AI 能跨轮次记住上下文，但 AI 还只能"说话"不能"做事"——它无法读文件、写文件、改文件。本迭代引入工具系统骨架，把文件操作包装成 LLM 可调用的结构化接口：

1. **`IBaseTool` 抽象 + `ToolBase` 基类**：定义工具的统一契约——`Name` / `Description` / `Category` / `Parameters` / `ExecuteAsync`，以及把工具元数据转换为 OpenAI / Anthropic 协议 schema 的方法。`ToolBase` 把 schema 转换的样板代码集中到一处，具体工具只关心执行逻辑。
2. **`ToolResult` / `ToolCategory` / `ToolParameter`**：工具系统的核心数据类型。`ToolResult` 携带成功标志 + 内容 + 错误信息，让 LLM 能根据失败原因调整策略；`ToolCategory` 区分 Read（可并发）与 Write（需串行），为迭代 6 的分批执行打基础。
3. **`ToolRegistry`**：按名查找工具的注册中心 + 协议 schema 批量转换。AgentLoop（迭代 6）在调用 LLM 前向其注入 `ToolRegistry.ToOpenAiSchemas()`，让 LLM 知道有哪些工具可用。
4. **`ToolExecutor`**：超时 + 错误捕获的执行器。把工具执行中的所有异常（包括超时、IO、权限）转译为 `ToolResult(Error=...)`，**不让异常逃逸到 AgentLoop**——这是 Agent 自我修正能力的基础（失败原因回灌给 LLM，它再调整策略）。
5. **三个内置工具**：`ReadFileTool` / `WriteFileTool` / `EditFileTool`，覆盖文件读 / 写 / 改三类操作。`EditFileTool` 严格按 MewCode 语义——原文必须**唯一匹配**，0 次或多次都报错并附上下文，这是后续 Agent 自我修正能力的关键。
6. **闭环 demo**：一段不接 LLM 的代码模拟"tool_call → 执行 → 拿到结果"的完整流程，作为本迭代的可验证验收物。

本迭代**刻意保持**：
- **不接 LLM**：本迭代的工具系统是"骨架 + 内置工具 + 执行器"，工具调用由测试或 demo 手工构造，不来自 LLM 响应解析。LLM 响应中的 `tool_calls` 字段解析在迭代 6。
- **不接 App 主循环**：`App.cs` 行为与迭代 4 保持一致（多轮对话 + 流式输出 + `/clear`）。工具系统作为独立模块开发，迭代 6 的 AgentLoop 才把它与 Provider / History 串起来。
- **不做安全层**：路径校验 / 沙箱 / 黑名单 / 权限模式全部在迭代 8。本迭代工具接受任意路径，文件操作权限交给操作系统。
- **不做上下文截断**：`ReadFileTool` 读整个文件返回。超长结果的截断（50K 字符阈值）是迭代 9 Truncator 的职责。
- **不做并发分批执行**：`ToolCategory` 枚举存在以承载"Read 并发 / Write 串行"的设计语义，但 `ToolExecutor` 只做单次执行。批量分批执行在迭代 6 AgentLoop 实现。
- **不做 RunCommandTool**：plan.md 进阶练习明确为本迭代外内容。本迭代只交付三个文件工具。

> **拆分考量**：迭代 5 是否拆为 5a（工具抽象 + 注册中心 + 执行器）+ 5b（三个文件工具）？
> - 不拆理由：工具抽象脱离具体工具是空壳——`IBaseTool` 的设计直接由三个文件工具的共性驱动，schema 转换的样板代码也是为它们服务的。骨架 + 三个工具一起设计与验收，能形成"闭环 demo"这一可验证交付物。
> - **结论**：本迭代不拆分，作为整体设计。

## 二、学习目标

1. **工具即函数**：理解"工具"的本质是把一个有副作用的函数（如 `ReadFile(path) → string`）包装成 LLM 可调用的结构化接口——`Name` + `Description` + `Parameters`（JSON Schema）让 LLM 知道工具能做什么、需要什么参数；`ExecuteAsync(JsonElement input)` 在宿主端执行真实操作。
2. **JSON Schema 作为契约**：参数用 JSON Schema 描述，LLM 据此生成结构化 JSON 参数。理解 schema 是 LLM 与宿主之间的"类型契约"——宿主负责校验、LLM 负责生成。本迭代手写 schema（基于 `ToolParameter` 列表构造），体会它与 .NET 反射式 schema 生成的差异。
3. **工具分类的设计动机**：`ToolCategory.Read`（幂等、无副作用、可并发）vs `ToolCategory.Write`（有副作用、可能冲突、需串行）。这是 Agent 性能与正确性权衡的基础——并发读提升速度，串行写避免竞态。本迭代只设计枚举，分批执行在迭代 6。
4. **错误回灌而非异常逃逸**：`ToolExecutor` 把所有异常转译为 `ToolResult(Success=false, Error=...)`，**不让异常逃逸到调用方**。错误原因作为字符串返回 LLM，让它调整策略——这是 ReAct 范式"自我修正"的物质基础。
5. **EditFileTool 的唯一性约束**：原文必须**精确匹配且唯一**，0 次（找不到）或多次（歧义）都报错。这是 Agent 在编辑代码时不破坏文件的强约束——多次匹配意味着 LLM 的描述不够精确，应让它重试或提供更多上下文。
6. **协议无关的 schema 转换**：`ToOpenAiSchema()` / `ToAnthropicSchema()` 把工具元数据转成两种 wire format。本迭代实现 OpenAI 版本（迭代 6 直接用），Anthropic 版本作为预留接口（实际用 Anthropic 协议时再补字段细节）。体会"工具层定义抽象、Provider 层解释协议"的解耦收益。
7. **超时与取消的边界**：`ToolExecutor` 用 `CancellationTokenSource.CancelAfter` + `Task.WhenAny` 实现超时。理解超时不是"杀掉工具"——工具任务仍在跑（除非它自己响应取消令牌），超时只是"不再等待结果"。这是异步超时的本质限制。

## 三、范围

### 3.1 本迭代包含（In Scope）

| 项 | 说明 |
| --- | --- |
| `Tools/IBaseTool.cs` | 工具抽象接口：`Name` / `Description` / `Category` / `Parameters` / `ExecuteAsync` / `ToOpenAiSchema` / `ToAnthropicSchema` |
| `Tools/ToolBase.cs` | 抽象基类：实现 `ToOpenAiSchema` / `ToAnthropicSchema`（基于 `Name` / `Description` / `Parameters` 拼装），提供参数提取辅助方法 `GetRequiredString` / `GetOptionalString` |
| `Tools/ToolResult.cs` | `record ToolResult(bool Success, string Content, string? Error)` |
| `Tools/ToolCategory.cs` | `enum ToolCategory { Read, Write }` |
| `Tools/ToolParameter.cs` | `record ToolParameter(string Name, string Type, string Description, bool Required)` |
| `Tools/ToolRegistry.cs` | 注册中心：`Register` / `Get` / `GetAll` / `ToOpenAiSchemas` / `ToAnthropicSchemas` |
| `Tools/ToolExecutor.cs` | 单次执行器：`ExecuteAsync(ToolCall)` → 超时 + 异常捕获 → `ToolResult` |
| `Tools/ReadFileTool.cs` | 读文件：参数 `path`，行为读整个文件返回 Content |
| `Tools/WriteFileTool.cs` | 写文件：参数 `path` + `content`，行为创建或覆盖写入 |
| `Tools/EditFileTool.cs` | 改文件：参数 `path` + `old_text` + `new_text`，要求 old_text 唯一匹配 |
| 闭环 demo | `Tools/ClosedLoopDemo.cs`：构造 ToolCall → ToolExecutor 执行 → 打印 ToolResult，不接 LLM |
| 单元测试 | `Tools/` 每个工具 ≥ 3 用例（正常 / 边界 / 错误）+ `ToolRegistryTests` + `ToolExecutorTests` + `ToolClosedLoopTests`（集成） |

### 3.2 本迭代不包含（Out of Scope）

- LLM 响应中 `tool_calls` 字段的解析与调度 → 迭代 6 AgentLoop
- 工具系统接入 `App` 主循环 / `ConversationHistory.AddTool` 真实使用 → 迭代 6
- `Read` 工具并发 + `Write` 工具串行的分批执行 → 迭代 6 AgentLoop
- 安全层（路径沙箱 / 黑名单 / 三档权限模式）→ 迭代 8
- 工具结果超长截断（50K 字符阈值）→ 迭代 9 Truncator
- `RunCommandTool` / `GlobTool` / `GrepTool` → 迭代 6（plan.md 列入 iter 6 影响文件）
- `AnthropicProvider` 实际使用 schema（本迭代只提供 `ToAnthropicSchema` 接口实现，wire format 按 Anthropic 文档预设，实际接入在 Anthropic 实现时）
- `sub_agent` 工具 → 迭代 12
- MCP 工具适配（外部工具服务器）→ 迭代 11

### 3.3 与迭代 6 的边界

`ToolCategory` 在本迭代与迭代 6 都涉及，边界如下：

| 本迭代（迭代 5） | 迭代 6 |
| --- | --- |
| 定义 `enum ToolCategory { Read, Write }` | 不变 |
| 工具声明自己的 `Category`（ReadFileTool=Read，WriteFileTool/EditFileTool=Write） | AgentLoop 按 Category 分批 |
| `ToolExecutor` 单次执行（无并发语义） | AgentLoop 把 Read 工具用 `Task.WhenAll` 并发，Write 工具顺序 `await` |
| 不关心调用顺序 / 批大小 | 单批最大并行度 / 失败时是否跳过同批后续 |

> 本迭代的 `ToolCategory` 是迭代 6 分批执行的基础设施。设计时确保枚举稳定且工具如实声明自己的类别，迭代 6 直接消费。

### 3.4 与迭代 8 的边界

| 本迭代（迭代 5） | 迭代 8 |
| --- | --- |
| 工具接受任意路径（绝对 / 相对 / `..` 遍历均不拦） | `PathSandbox` 拒绝绝对路径、`..` 遍历、项目目录边界 |
| 文件操作权限交给 OS（`UnauthorizedAccessException` 会被 ToolExecutor 捕获） | 三档权限模式（Strict / Normal / Permissive）在工具执行前评估 |
| 不识别危险操作（如覆盖 `.git/` 下文件） | `SecurityGuard` 管线：黑名单 → 沙箱 → 策略 → HITL |
| 错误以 `ToolResult.Error` 字符串返回 LLM | 拒绝原因同样以 `ToolResult.Error` 返回 LLM（接口不变，只是上游多了一层） |

> 本迭代故意不引入安全层——工具系统的核心抽象（IBaseTool / ToolRegistry / ToolExecutor）应与安全策略正交。迭代 8 的 SecurityGuard 作为 ToolExecutor 的前置过滤器接入，工具自身无需感知。

## 四、架构设计

### 4.1 模块结构（迭代 5 增量）

```
ParrotCode.Net/
├── Program.cs                 # 不变
├── App/
│   └── App.cs                 # 不变（迭代 4 行为保持，工具系统未接入主循环）
├── Config/                    # 不变
├── Conversation/              # 不变
├── Providers/
│   ├── IBaseProvider.cs       # 不变（迭代 6 扩展为带 tools 的重载）
│   ├── MessageTypes.cs        # 不变（ToolCall 已在 2a 定义，本迭代开始使用）
│   └── ...                    # 其他 Provider 文件不变
└── Tools/                     # 新增目录
    ├── IBaseTool.cs           # 新增：工具抽象接口
    ├── ToolBase.cs            # 新增：抽象基类，实现 schema 转换 + 参数提取辅助
    ├── ToolResult.cs          # 新增：record ToolResult
    ├── ToolCategory.cs        # 新增：enum { Read, Write }
    ├── ToolParameter.cs       # 新增：record ToolParameter
    ├── ToolRegistry.cs        # 新增：按名查找 + 批量 schema 转换
    ├── ToolExecutor.cs        # 新增：单次执行 + 超时 + 异常捕获
    ├── ReadFileTool.cs        # 新增：读文件
    ├── WriteFileTool.cs       # 新增：写文件
    ├── EditFileTool.cs        # 新增：精确匹配替换
    └── ClosedLoopDemo.cs      # 新增：不接 LLM 的闭环演示（供手测 / 集成测试调用）
```

> 命名空间约定沿用迭代 1/2/3/4：所有源文件统一 `namespace ParrotCode`，文件夹仅作物理组织。

### 4.2 调用流程（闭环 demo）

```
┌─────────────────────────┐
│  ClosedLoopDemo.Run()   │  手工构造 ToolCall，不接 LLM
└────────┬────────────────┘
         │
         │  1. 装配 ToolRegistry（注册三个工具）
         ▼
┌─────────────────────────┐
│      ToolRegistry       │  Register(ReadFileTool)
│                         │  Register(WriteFileTool)
│                         │  Register(EditFileTool)
└────────┬────────────────┘
         │
         │  2. 构造 ToolExecutor（注入 registry + 超时）
         ▼
┌─────────────────────────┐
│      ToolExecutor       │
└────────┬────────────────┘
         │
         │  3. 模拟 LLM 返回的 ToolCall
         │     var call = new ToolCall(
         │       Id: "demo-1",
         │       Name: "write_file",
         │       Input: JsonDocument.Parse("""{"path":"hello.txt","content":"你好"}""").RootElement
         │     );
         ▼
┌─────────────────────────┐
│  ToolExecutor.ExecuteAsync(call)  ──────────────┐
└────────┬────────────────┘                      │
         │                                        │  3a. registry.Get("write_file")
         │                                        │  3b. 构造 CancellationTokenSource.CancelAfter(timeout)
         │                                        │  3c. await Task.WhenAny(tool.ExecuteAsync, Task.Delay)
         ▼                                        │
┌─────────────────────────┐                      │
│   WriteFileTool         │  ExecuteAsync(input) │
│   - GetRequiredString   │                      │
│       ("path")         │                      │
│   - GetRequiredString   │                      │
│       ("content")       │                      │
│   - File.WriteAllTextAsync                     │
│   - return ToolResult(true, "已写入 N 字节")  │
└────────┬────────────────┘                      │
         │                                        │
         │  ← ToolResult                          │
         ▼                                        │
┌─────────────────────────┐                      │
│  ToolExecutor 包装       │  异常 → ToolResult(false, "", ex.Message)  ←─┘
│  - 超时 → ToolResult    │  超时 → ToolResult(false, "", "工具执行超时（30s）")
│    (false, "", "超时") │  正常 → 透传 ToolResult
└────────┬────────────────┘
         │
         ▼
┌─────────────────────────┐
│  Console.WriteLine(...) │  打印 Success / Content / Error
│  打印结果给用户看        │
└─────────────────────────┘
```

### 4.3 关键类型设计

#### 4.3.1 `ToolCategory` + `ToolParameter` + `ToolResult`（数据类型）

```csharp
namespace ParrotCode;

/// <summary>
/// 工具分类：决定执行策略。
/// Read：幂等、无副作用、可并发（迭代 6 AgentLoop 用 Task.WhenAll 批量执行）。
/// Write：有副作用、可能冲突、需串行（迭代 6 顺序 await）。
/// 本迭代 ToolExecutor 单次执行，分类仅作元信息；分批执行在迭代 6。
/// </summary>
public enum ToolCategory
{
    Read,
    Write
}

/// <summary>
/// 工具参数元数据。用于生成 JSON Schema（OpenAI / Anthropic 的 tools 字段）。
/// Type 用 JSON Schema 类型字符串："string" / "number" / "integer" / "boolean" / "array" / "object"。
/// 本迭代三个文件工具的参数全部为 string。
/// </summary>
public sealed record ToolParameter(string Name, string Type, string Description, bool Required);

/// <summary>
/// 工具执行结果。无论成功失败都返回 ToolResult，不抛异常（异常由 ToolExecutor 捕获转译）。
/// Success=true 时 Content 含结果文本，Error 为 null。
/// Success=false 时 Error 含人类可读错误原因（会回灌给 LLM），Content 通常为空。
/// </summary>
public sealed record ToolResult(bool Success, string Content, string? Error = null)
{
    /// <summary>便捷构造：成功结果。</summary>
    public static ToolResult Ok(string content) => new(true, content, null);

    /// <summary>便捷构造：失败结果。</summary>
    public static ToolResult Fail(string error) => new(false, string.Empty, error);
}
```

> **设计要点**：
> - `ToolResult` 是 `record`：不可变 + 值相等。`Success` / `Content` / `Error` 三字段足够表达工具结果语义。后续迭代如需扩展（如 `IsTruncated` 标志、附件路径），用 `init` 属性补充。
> - `Error` 为 `string?`：成功时为 null，失败时必填。静态工厂 `Ok` / `Fail` 让构造意图明确，避免 `new ToolResult(false, "", "错误")` 这种参数顺序混淆。
> - `ToolParameter.Type` 用字符串而非枚举：JSON Schema 类型有限（6 种），但用字符串避免枚举的 `(ToolParameterType)999` 兜底问题，且与 JSON Schema wire format 直接对应。
> - `ToolCategory` 不带 `Unknown` 兜底：工具必须明确声明类别，不声明无法注册。

#### 4.3.2 `IBaseTool` 接口 + `ToolBase` 抽象基类

```csharp
using System.Text.Json;

namespace ParrotCode;

/// <summary>
/// 工具抽象接口：定义工具的统一契约。
/// LLM 通过 Name + Description + Parameters(JSON Schema) 知道工具能做什么、需要什么参数。
/// 宿主通过 ExecuteAsync(JsonElement input) 执行真实操作，返回 ToolResult。
/// ToOpenAiSchema / ToAnthropicSchema 把工具元数据转成 Provider 协议的 wire format。
/// </summary>
public interface IBaseTool
{
    /// <summary>工具名（LLM 调用时使用）。snake_case，跨工具唯一。</summary>
    string Name { get; }

    /// <summary>工具描述（LLM 据此判断何时调用）。应说明用途、参数语义、副作用。</summary>
    string Description { get; }

    /// <summary>工具分类：Read 可并发、Write 需串行。</summary>
    ToolCategory Category { get; }

    /// <summary>参数列表（用于生成 JSON Schema）。</summary>
    IReadOnlyList<ToolParameter> Parameters { get; }

    /// <summary>执行工具。input 是 LLM 生成的 JSON 参数。失败应返回 ToolResult.Fail，不抛异常。</summary>
    Task<ToolResult> ExecuteAsync(JsonElement input, CancellationToken cancellationToken);

    /// <summary>转 OpenAI 协议的 tools 数组元素（含 type/function 包裹层）。</summary>
    JsonElement ToOpenAiSchema();

    /// <summary>转 Anthropic 协议的 tools 数组元素（含 input_schema）。</summary>
    JsonElement ToAnthropicSchema();
}

/// <summary>
/// 工具基类：集中实现 schema 转换的样板代码，具体工具只关心 ExecuteAsync。
/// 三个文件工具的 schema 转换逻辑完全一致（基于 Name + Description + Parameters 拼装），
/// 提取到基类避免每个工具重复实现。
/// 同时提供参数提取辅助方法，统一参数校验的错误格式。
/// </summary>
public abstract class ToolBase : IBaseTool
{
    public abstract string Name { get; }
    public abstract string Description { get; }
    public abstract ToolCategory Category { get; }
    public abstract IReadOnlyList<ToolParameter> Parameters { get; }
    public abstract Task<ToolResult> ExecuteAsync(JsonElement input, CancellationToken cancellationToken);

    public JsonElement ToOpenAiSchema()
    {
        // OpenAI tools 数组元素格式：
        // {"type":"function","function":{"name":...,"description":...,"parameters":{...}}}
        var schema = new
        {
            type = "function",
            function = new
            {
                name = Name,
                description = Description,
                parameters = BuildParametersSchema(Parameters)
            }
        };
        return JsonSerializer.SerializeToElement(schema);
    }

    public JsonElement ToAnthropicSchema()
    {
        // Anthropic tools 数组元素格式：
        // {"name":...,"description":...,"input_schema":{...}}
        var schema = new
        {
            name = Name,
            description = Description,
            input_schema = BuildParametersSchema(Parameters)
        };
        return JsonSerializer.SerializeToElement(schema);
    }

    /// <summary>
    /// 基于 Parameters 列表构造 JSON Schema 的 parameters / input_schema 对象。
    /// {"type":"object","properties":{name:{"type":...,"description":...}},"required":[...]}
    /// </summary>
    private static JsonElement BuildParametersSchema(IReadOnlyList<ToolParameter> parameters)
    {
        var properties = parameters.ToDictionary(
            p => p.Name,
            p => (object)new { type = p.Type, description = p.Description }
        );
        var required = parameters.Where(p => p.Required).Select(p => p.Name).ToArray();
        var schema = new
        {
            type = "object",
            properties,
            required
        };
        return JsonSerializer.SerializeToElement(schema);
    }

    // —— 参数提取辅助方法：统一参数校验的错误格式 ——

    /// <summary>
    /// 提取必需的 string 参数。缺失或类型错误时返回 string.Empty 并设置 error。
    /// 用 out 模式而非 (Value, Error) 元组——让编译器能正确推断返回值为非空，
    /// 避免 nullable warning。调用方应立即判断 error 是否非空，非空则 return ToolResult.Fail(err)。
    /// </summary>
    protected static string GetRequiredString(JsonElement input, string name, out string? error)
    {
        if (!input.TryGetProperty(name, out var el))
        {
            error = $"缺少必需参数：{name}";
            return string.Empty;
        }
        if (el.ValueKind != JsonValueKind.String)
        {
            error = $"参数 {name} 类型错误：期望 string，实际 {el.ValueKind}";
            return string.Empty;
        }
        error = null;
        return el.GetString() ?? string.Empty;
    }

    /// <summary>
    /// 提取可选的 string 参数。缺失返回 defaultValue，类型错误返回 string.Empty 并设置 error。
    /// </summary>
    protected static string GetOptionalString(
        JsonElement input, string name, out string? error, string defaultValue = "")
    {
        if (!input.TryGetProperty(name, out var el))
        {
            error = null;
            return defaultValue;
        }
        if (el.ValueKind != JsonValueKind.String)
        {
            error = $"参数 {name} 类型错误：期望 string，实际 {el.ValueKind}";
            return string.Empty;
        }
        error = null;
        return el.GetString() ?? string.Empty;
    }
}
```

> **设计要点**：
>
> - **`ToolBase` 而非 `IBaseTool` 直接实现 schema**：抽象类 vs 接口默认方法（C# 8+）的取舍——选抽象类因为 schema 转换需要访问实例的 `Name` / `Description` / `Parameters`（抽象属性），且未来可能加共享的辅助字段（如 `Timeout` 默认值）。接口默认方法不能访问实例状态，不适合此处。
> - **`BuildParametersSchema` 是 `private static`**：纯函数，不依赖实例状态（入参显式传 `Parameters`），便于单测独立验证。
> - **参数提取用 `out string? error` 模式而非 `(Value, Error)` 元组**：工具的 `ExecuteAsync` 不应抛 `ArgumentException`——参数错误是工具调用的常态（LLM 可能生成错参数），应转成 `ToolResult.Fail` 让 LLM 看到。`out` 模式让编译器能正确推断返回值 `string` 非空（vs 元组的 `string?`），避免 nullable warning。调用方代码形如 `var path = GetRequiredString(input, "path", out var err); if (err is not null) return ToolResult.Fail(err);`。
> - **`JsonSerializer.SerializeToElement`**：把匿名对象序列化为 `JsonElement`。匿名对象的属性名直接对应 JSON 字段名（C# 匿名对象属性名按 PascalCase，但 `JsonSerializer` 默认用属性名原样输出，因此需用 `JsonNamingPolicy.SnakeCaseLower`（.NET 8+）或字段名手写为 camelCase）。
>   - **决策**：手写匿名对象时属性名用 camelCase（如 `type` / `function` / `input_schema`），与协议 wire format 完全一致。避免全局 `JsonSerializerOptions` 配置影响其他序列化路径。
> - **`ToAnthropicSchema` 预留但未实测**：本迭代没有 AnthropicProvider，无法端到端验证 Anthropic schema。代码按 Anthropic 官方文档实现 `input_schema` 字段，待 Anthropic Provider 接入时回归。

#### 4.3.3 `ToolRegistry`（注册中心）

```csharp
using System.Text.Json;

namespace ParrotCode;

/// <summary>
/// 工具注册中心：按名查找 + 批量 schema 转换。
/// AgentLoop（迭代 6）在调用 LLM 前注入 ToolRegistry.ToOpenAiSchemas()，
/// 让 LLM 知道有哪些工具可用。
/// 本迭代由 ClosedLoopDemo 与单测使用，迭代 6 才在 App 主循环接入。
/// </summary>
public sealed class ToolRegistry
{
    private readonly Dictionary<string, IBaseTool> _tools = new(StringComparer.Ordinal);

    /// <summary>注册工具。重名抛 ArgumentException（工具名应跨工具唯一）。</summary>
    public void Register(IBaseTool tool)
    {
        ArgumentNullException.ThrowIfNull(tool);
        if (string.IsNullOrWhiteSpace(tool.Name))
            throw new ArgumentException("工具名不能为空", nameof(tool));
        if (!_tools.TryAdd(tool.Name, tool))
            throw new ArgumentException($"工具名 '{tool.Name}' 已注册", nameof(tool));
    }

    /// <summary>按名查找。未注册返回 null，调用方决定是否抛错。</summary>
    public IBaseTool? Get(string name) =>
        _tools.TryGetValue(name, out var tool) ? tool : null;

    /// <summary>按名查找。未注册抛 ArgumentException（用于"工具必须存在"的场景）。</summary>
    public IBaseTool Require(string name) =>
        Get(name) ?? throw new ArgumentException($"未注册工具：{name}");

    /// <summary>所有已注册工具（顺序不保证，按需排序）。</summary>
    public IReadOnlyList<IBaseTool> GetAll() => _tools.Values.ToArray();

    /// <summary>批量转 OpenAI tools 数组（用于 ChatRequest 的 tools 字段）。</summary>
    public JsonElement ToOpenAiSchemas() =>
        JsonSerializer.SerializeToElement(_tools.Values.Select(t => t.ToOpenAiSchema()).ToArray());

    /// <summary>批量转 Anthropic tools 数组。</summary>
    public JsonElement ToAnthropicSchemas() =>
        JsonSerializer.SerializeToElement(_tools.Values.Select(t => t.ToAnthropicSchema()).ToArray());
}
```

> **设计要点**：
>
> - **`StringComparer.Ordinal`**：工具名大小写敏感。`read_file` 与 `Read_File` 视为不同工具——避免 LLM 大小写混淆导致误调用。LLM 生成的工具名应与 `Name` 完全一致。
> - **`Get` vs `Require` 双 API**：`Get` 返回 nullable，调用方自行处理（如 ToolExecutor 找不到工具时构造 `ToolResult.Fail`）；`Require` 抛异常，用于初始化时断言（如"启动时必须注册 read_file"）。两种语义都常见，都提供。
> - **重名抛 `ArgumentException`**：注册期是程序启动的装配阶段，错误应 fail-fast 而非静默覆盖。`ArgumentException` 而非自定义异常——这是装配错误，调用方不应捕获。
> - **`GetAll()` 返回 `ToArray()` 快照**：避免外部修改内部字典。本迭代不要求快照语义（注册完成后一般不再修改），但返回新数组是廉价且安全的默认。
> - **`ToOpenAiSchemas()` 返回 `JsonElement`**：直接序列化为 JSON 数组元素，迭代 6 AgentLoop 把它放进 ChatRequest body。返回 `JsonElement` 而非 `string`——避免双重序列化（已序列化一次，再拼到 body 又序列化一次）。

#### 4.3.4 `ToolExecutor`（单次执行器）

```csharp
using System.Diagnostics;
using System.Text.Json;

namespace ParrotCode;

/// <summary>
/// 工具单次执行器：超时 + 异常捕获，把所有失败转译为 ToolResult。
/// 不让异常逃逸到调用方——这是 Agent 自我修正能力的物质基础：
/// 失败原因作为 ToolResult.Error 回灌给 LLM，让它调整策略。
/// 本迭代只做单次执行；Read 并发 / Write 串行的分批执行在迭代 6 AgentLoop。
/// </summary>
public sealed class ToolExecutor
{
    private readonly ToolRegistry _registry;
    private readonly TimeSpan _timeout;
    private readonly ILogger? _logger;

    /// <summary>
    /// 构造执行器。
    /// timeout：单次工具执行的最大时长，默认 30 秒。超时不杀工具任务（除非工具响应取消令牌），
    /// 只是不再等待结果——返回 ToolResult.Fail("工具执行超时")。
    /// </summary>
    public ToolExecutor(ToolRegistry registry, TimeSpan? timeout = null, ILogger? logger = null)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _timeout = timeout ?? TimeSpan.FromSeconds(30);
        _logger = logger;
    }

    /// <summary>
    /// 执行单个 ToolCall。
    /// 流程：查找工具 → 构造取消令牌 → 执行（带超时）→ 异常捕获 → 返回 ToolResult。
    /// 任何异常（包括超时、IO、权限、参数错误）都转为 ToolResult.Fail，不抛异常。
    /// </summary>
    public async Task<ToolResult> ExecuteAsync(ToolCall call, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(call);
        // 外部取消优先——已取消的 token 立即抛 OCE，不进入工具查找 / 执行
        cancellationToken.ThrowIfCancellationRequested();

        // 1. 查找工具
        var tool = _registry.Get(call.Name);
        if (tool is null)
            return ToolResult.Fail($"未注册工具：{call.Name}");

        // 2. 构造带超时的取消令牌：外部的 cancellationToken + 内部的超时取并集
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(_timeout);

        // 3. 执行：Task.WhenAny 等待工具任务或超时
        var sw = Stopwatch.StartNew();
        Task<ToolResult> executeTask;
        try
        {
            executeTask = Task.Run(() => tool.ExecuteAsync(call.Input, timeoutCts.Token), timeoutCts.Token);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // 外部取消（用户 Ctrl+C）——透传
            throw;
        }
        catch (Exception ex)
        {
            // Task.Run 同步抛（如工具 ExecuteAsync 内部 sync throw before first await）
            _logger?.LogWarning(ex, "工具 {Name} 启动失败", call.Name);
            return ToolResult.Fail($"工具 {call.Name} 启动失败：{ex.Message}");
        }

        // 等待工具完成或超时（Task.Delay 用外部 ct，外部取消时立即结束等待）
        var delayTask = Task.Delay(_timeout, cancellationToken);
        var completed = await Task.WhenAny(executeTask, delayTask);

        // 优先检查外部取消：即使 delayTask 先完成（被 ct 取消），若是外部取消则透传 OCE
        // 这避免"外部取消 + delay 先完成"被误判为超时
        if (cancellationToken.IsCancellationRequested)
            throw new OperationCanceledException(cancellationToken);

        if (completed == executeTask)
        {
            // 工具任务完成（成功或抛异常）
            try
            {
                var result = await executeTask;
                _logger?.LogInformation("工具 {Name} 执行完成，耗时 {Ms}ms，成功={Success}",
                    call.Name, sw.ElapsedMilliseconds, result.Success);
                return result;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw; // 外部取消透传
            }
            catch (OperationCanceledException)
            {
                // 超时触发的取消
                _logger?.LogWarning("工具 {Name} 执行超时（{Timeout}s）", call.Name, _timeout.TotalSeconds);
                return ToolResult.Fail($"工具 {call.Name} 执行超时（{_timeout.TotalSeconds}s）");
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "工具 {Name} 执行抛异常", call.Name);
                return ToolResult.Fail($"工具 {call.Name} 执行失败：{ex.Message}");
            }
        }
        else
        {
            // Task.Delay 先完成且外部未取消——纯超时
            _logger?.LogWarning("工具 {Name} 执行超时（{Timeout}s）", call.Name, _timeout.TotalSeconds);
            return ToolResult.Fail($"工具 {call.Name} 执行超时（{_timeout.TotalSeconds}s）");
        }
    }
}
```

> **设计要点**：
>
> - **`CancellationTokenSource.CreateLinkedTokenSource`**：把外部 cancellationToken（用户 Ctrl+C）与内部超时令牌链接——任一触发都取消工具。外部取消应让整个流程终止（透传 `OperationCanceledException`），超时则转为 `ToolResult.Fail`。
> - **`Task.Run` 包裹工具执行**：避免工具同步阻塞（如 `File.ReadAllText` 在大文件时阻塞调用线程）。代价是失去了工具的同步上下文——本迭代的文件工具无同步上下文依赖，安全。
> - **超时不"杀"工具**：`Task.WhenAny` 超时后工具任务仍在后台跑（除非工具响应 `cancellationToken` 取消）。这是异步超时的本质限制——C# 没有强制取消线程的安全方式。文件 IO 通常响应取消（`FileStream` 异步 API 接受 `cancellationToken`），但仍有泄漏可能。日志记录便于追踪。
> - **异常分类**：
>   - `OperationCanceledException` + `cancellationToken.IsCancellationRequested` → 外部取消，透传（让 AgentLoop 决定是否终止整个会话）。
>   - `OperationCanceledException` 不带外部取消标志 → 超时触发，转 `ToolResult.Fail`。
>   - 其他 `Exception` → 工具内部异常，转 `ToolResult.Fail(ex.Message)`。
> - **不重试**：本迭代 ToolExecutor 不做重试——失败原因回灌给 LLM 让它决策。重试逻辑（如网络抖动自动重试）在迭代 6 AgentLoop 或更高层处理。
> - **ILogger 可选**：生产环境注入 logger 记录工具调用耗时与失败，测试环境传 null 不记日志。`ILogger?` 而非 `ILogger`——测试构造执行器时不强制 mock logger。

#### 4.3.5 `ReadFileTool`（读文件）

```csharp
using System.Text.Json;

namespace ParrotCode;

/// <summary>
/// 读文件工具：读取指定路径的文件内容，返回完整文本。
/// Category=Read（幂等、无副作用、可并发）。
/// 本迭代读整个文件不做截断——超长结果的截断（50K 字符阈值）在迭代 9 Truncator。
/// 路径校验（沙箱、.. 遍历拦截）在迭代 8 SecurityGuard。
/// </summary>
public sealed class ReadFileTool : ToolBase
{
    public override string Name => "read_file";
    public override string Description =>
        "读取指定路径的文件内容，返回完整文本。路径可以是相对或绝对路径。" +
        "不支持读取目录；文件不存在或无权限会返回错误。";
    public override ToolCategory Category => ToolCategory.Read;

    public override IReadOnlyList<ToolParameter> Parameters { get; } = new[]
    {
        new ToolParameter("path", "string", "要读取的文件路径（相对或绝对）", Required: true)
    };

    public override async Task<ToolResult> ExecuteAsync(JsonElement input, CancellationToken cancellationToken)
    {
        var (path, err) = GetRequiredString(input, "path");
        if (err is not null) return ToolResult.Fail(err);
        if (string.IsNullOrWhiteSpace(path))
            return ToolResult.Fail("参数 path 不能为空");

        // 目录检测：File.ReadAllText 对目录抛 UnauthorizedAccessException，错误信息不友好
        if (Directory.Exists(path))
            return ToolResult.Fail($"路径是目录而非文件：{path}");

        try
        {
            var content = await File.ReadAllTextAsync(path, cancellationToken);
            return ToolResult.Ok(content);
        }
        catch (FileNotFoundException)
        {
            return ToolResult.Fail($"文件不存在：{path}");
        }
        catch (DirectoryNotFoundException)
        {
            return ToolResult.Fail($"路径不存在：{path}");
        }
        catch (IOException ex)
        {
            return ToolResult.Fail($"读取文件失败：{ex.Message}");
        }
        // UnauthorizedAccessException 等其他异常由 ToolExecutor 兜底捕获
    }
}
```

> **设计要点**：
> - **`File.ReadAllTextAsync`**：自动检测编码（BOM / UTF-8 / 默认）。本迭代不显式指定编码——多数源码文件是 UTF-8 或 UTF-8 BOM，BCL 默认行为正确。
> - **目录检测**：`File.ReadAllTextAsync` 对目录抛 `UnauthorizedAccessException`，错误信息不直观（"对路径的访问被拒绝"）。提前检测目录返回友好错误。
> - **错误分类**：`FileNotFoundException` / `DirectoryNotFoundException` 给具体错误（"文件不存在" / "路径不存在"），其他 `IOException` 透传 `ex.Message`。`UnauthorizedAccessException` 不专门 catch——让 ToolExecutor 兜底捕获（错误信息可读）。
> - **不做大小限制**：本迭代不限制文件大小。读 1GB 文件可能 OOM，但这是迭代 9 Truncator 的职责（单条 >50K 字符截断）。本迭代文档明确"接受任意大小，OOM 风险记录在风险表"。
> - **返回完整内容**：`Content` 字段含文件全文。空文件返回 `ToolResult.Ok("")`——空文件是合法的"读成功"。

#### 4.3.6 `WriteFileTool`（写文件）

```csharp
using System.Text.Json;

namespace ParrotCode;

/// <summary>
/// 写文件工具：创建或覆盖写入指定路径。
/// Category=Write（有副作用、需串行）。
/// 父目录不存在时自动创建（与 mkdir -p 语义一致）。
/// 不弹 HITL 确认——本迭代无安全层，HITL 在迭代 7 TUI + 迭代 8 SecurityGuard 接入。
/// </summary>
public sealed class WriteFileTool : ToolBase
{
    public override string Name => "write_file";
    public override string Description =>
        "创建或覆盖写入文件。若父目录不存在会自动创建。" +
        "已存在的文件会被覆盖——如需保留原内容请先 read_file。" +
        "返回写入的字节数（UTF-8 编码）。";
    public override ToolCategory Category => ToolCategory.Write;

    public override IReadOnlyList<ToolParameter> Parameters { get; } = new[]
    {
        new ToolParameter("path", "string", "要写入的文件路径（相对或绝对）", Required: true),
        new ToolParameter("content", "string", "文件内容（完整覆盖）", Required: true)
    };

    public override async Task<ToolResult> ExecuteAsync(JsonElement input, CancellationToken cancellationToken)
    {
        var (path, err1) = GetRequiredString(input, "path");
        if (err1 is not null) return ToolResult.Fail(err1);
        var (content, err2) = GetRequiredString(input, "content");
        if (err2 is not null) return ToolResult.Fail(err2);

        if (string.IsNullOrWhiteSpace(path))
            return ToolResult.Fail("参数 path 不能为空");

        try
        {
            // 父目录不存在则创建（与 mkdir -p 语义一致）
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            // UTF-8 无 BOM 写入（与源码文件惯例一致）
            var bytes = System.Text.Encoding.UTF8.GetBytes(content);
            await using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None,
                bufferSize: 4096, useAsync: true);
            await fs.WriteAsync(bytes.AsMemory(0, bytes.Length), cancellationToken);

            return ToolResult.Ok($"已写入 {bytes.Length} 字节到 {path}");
        }
        catch (UnauthorizedAccessException ex)
        {
            return ToolResult.Fail($"无写入权限：{ex.Message}");
        }
        catch (IOException ex)
        {
            return ToolResult.Fail($"写入文件失败：{ex.Message}");
        }
    }
}
```

> **设计要点**：
> - **`FileMode.Create`**：文件存在则覆盖，不存在则创建。这是 LLM 写文件的典型语义（"写一个 hello.txt 内容为 X"——期望覆盖或新建）。如需追加语义，未来加 `append` 可选参数或独立 `append_file` 工具。
> - **自动创建父目录**：`Directory.CreateDirectory` 递归创建。LLM 经常会写 `subdir/file.txt` 这种带目录的路径——不自动创建会失败，让 LLM 困惑。MewCode 同语义。
> - **UTF-8 无 BOM**：源码文件惯例。`new UTF8Encoding(false)` 显式不带 BOM；`FileStream` + 手动 `GetBytes` 比 `File.WriteAllTextAsync`（默认 UTF-8 BOM 在 .NET 8 已改为无 BOM，但显式控制更安全）更明确。
> - **返回字节数**：让 LLM 知道实际写入大小，便于校验（如"我让你写 100 字，结果写了 97 字节——可能编码转换"）。返回字节数而非字符数——字节是文件实际大小。
> - **`content` 允许空字符串**：`write_file(path, "")` 创建空文件是合法操作。`GetRequiredString` 不要求非空——只要求存在且为 string。如需非空校验，工具内显式加。

#### 4.3.7 `EditFileTool`（精确匹配替换）

```csharp
using System.Text;
using System.Text.Json;

namespace ParrotCode;

/// <summary>
/// 改文件工具：精确匹配替换。old_text 必须在文件中唯一匹配，否则报错。
/// 0 次匹配：报"未找到"，附文件前 200 字符作为上下文。
/// 多次匹配：报"找到 N 处"，附前 3 处匹配的行号 + 上下文。
/// 这是 Agent 自我修正能力的关键约束——多次匹配意味着 LLM 的描述不够精确，
/// 应让它重试或提供更多上下文（如带行号或更多周边代码）。
/// Category=Write（有副作用、需串行）。
/// </summary>
public sealed class EditFileTool : ToolBase
{
    private const int ContextPreviewLength = 200;
    private const int MaxMatchContextsToShow = 3;

    public override string Name => "edit_file";
    public override string Description =>
        "在文件中精确替换文本。old_text 必须在文件中唯一匹配——" +
        "0 次或多次匹配都报错，附上下文帮助修正。" +
        "匹配区分大小写、保留所有空白字符（包括缩进）。" +
        "用于精确编辑文件中已存在的代码片段。";
    public override ToolCategory Category => ToolCategory.Write;

    public override IReadOnlyList<ToolParameter> Parameters { get; } = new[]
    {
        new ToolParameter("path", "string", "要编辑的文件路径", Required: true),
        new ToolParameter("old_text", "string", "要被替换的原文（必须在文件中唯一匹配）", Required: true),
        new ToolParameter("new_text", "string", "替换后的新文本", Required: true)
    };

    public override async Task<ToolResult> ExecuteAsync(JsonElement input, CancellationToken cancellationToken)
    {
        var (path, err1) = GetRequiredString(input, "path");
        if (err1 is not null) return ToolResult.Fail(err1);
        var (oldText, err2) = GetRequiredString(input, "old_text");
        if (err2 is not null) return ToolResult.Fail(err2);
        var (newText, err3) = GetRequiredString(input, "new_text");
        if (err3 is not null) return ToolResult.Fail(err3);

        if (string.IsNullOrWhiteSpace(path))
            return ToolResult.Fail("参数 path 不能为空");
        if (oldText.Length == 0)
            return ToolResult.Fail("参数 old_text 不能为空（如需清空文件请用 write_file）");

        // 先检查目录（File.Exists 对目录返回 false，会误报"文件不存在"）
        if (Directory.Exists(path))
            return ToolResult.Fail($"路径是目录而非文件：{path}");
        if (!File.Exists(path))
            return ToolResult.Fail($"文件不存在：{path}");

        string content;
        try
        {
            content = await File.ReadAllTextAsync(path, cancellationToken);
        }
        catch (IOException ex)
        {
            return ToolResult.Fail($"读取文件失败：{ex.Message}");
        }

        // 查找所有匹配（非重叠）
        var matches = FindAllMatches(content, oldText);

        if (matches.Count == 0)
        {
            var preview = content.Length <= ContextPreviewLength
                ? content
                : content[..ContextPreviewLength] + "...（截断）";
            return ToolResult.Fail(
                $"未在 {path} 中找到匹配的 old_text。\n" +
                $"文件前 {ContextPreviewLength} 字符预览：\n{preview}");
        }

        if (matches.Count > 1)
        {
            var contexts = new StringBuilder();
            for (var i = 0; i < Math.Min(matches.Count, MaxMatchContextsToShow); i++)
            {
                var (lineNo, lineContext) = GetLineContext(content, matches[i]);
                contexts.AppendLine($"  第 {i + 1} 处：行 {lineNo}，上下文：{lineContext}");
            }
            if (matches.Count > MaxMatchContextsToShow)
                contexts.AppendLine($"  ...（共 {matches.Count} 处，仅显示前 {MaxMatchContextsToShow} 处）");

            return ToolResult.Fail(
                $"在 {path} 中找到 {matches.Count} 处匹配的 old_text，无法确定替换哪一处。\n" +
                $"请提供更精确的 old_text（如包含更多周边代码）：\n{contexts}");
        }

        // 唯一匹配——执行替换
        var newContent = content.Remove(matches[0], oldText.Length).Insert(matches[0], newText);
        try
        {
            await File.WriteAllTextAsync(path, newContent, new UTF8Encoding(false), cancellationToken);
        }
        catch (IOException ex)
        {
            return ToolResult.Fail($"写入文件失败：{ex.Message}");
        }

        return ToolResult.Ok($"已在 {path} 中替换 1 处（{oldText.Length} 字符 → {newText.Length} 字符）");
    }

    /// <summary>
    /// 查找所有非重叠匹配的位置。
    /// 非重叠：从当前位置开始找下一个匹配，找到后跳过整个匹配长度继续找。
    /// 这与 Python str.count / str.replace 的语义一致。
    /// </summary>
    private static List<int> FindAllMatches(string content, string needle)
    {
        var matches = new List<int>();
        var pos = 0;
        while (pos <= content.Length - needle.Length)
        {
            var idx = content.IndexOf(needle, pos, StringComparison.Ordinal);
            if (idx < 0) break;
            matches.Add(idx);
            pos = idx + needle.Length; // 非重叠
        }
        return matches;
    }

    /// <summary>
    /// 获取匹配位置所在的行号（1-based）+ 当前行内容。
    /// 用于错误信息中的上下文展示，帮助 LLM 定位歧义位置。
    /// </summary>
    private static (int LineNo, string LineContext) GetLineContext(string content, int matchPos)
    {
        var lineNo = 1;
        var lineStart = 0;
        for (var i = 0; i < matchPos; i++)
        {
            if (content[i] == '\n')
            {
                lineNo++;
                lineStart = i + 1;
            }
        }
        // 找行尾
        var lineEnd = content.IndexOf('\n', matchPos);
        if (lineEnd < 0) lineEnd = content.Length;
        var line = content[lineStart..lineEnd].Trim();
        // 截断过长的行
        if (line.Length > 80) line = line[..77] + "...";
        return (lineNo, line);
    }
}
```

> **设计要点**：
>
> - **唯一匹配约束**：0 次或多次都报错并附上下文。这是 Agent 编辑代码的强约束——多次匹配意味着 LLM 描述不够精确，强约束让它修正。MewCode 同语义，是 plan.md 明确要求。
> - **非重叠匹配**：`pos = idx + needle.Length` 跳过整个匹配。与 Python `str.count` / `str.replace` 语义一致。重叠匹配（如 `"aaa".IndexOf("aa")` 找到 0 后找 2）在本场景无意义——LLM 不会期望重叠替换。
> - **`StringComparison.Ordinal`**：区分大小写、不文化敏感。代码编辑必须精确匹配——`Foo` 与 `foo` 是不同标识符。
> - **错误信息附上下文**：
>   - 0 次匹配：附文件前 200 字符预览。让 LLM 知道文件大概内容，便于修正 old_text。
>   - 多次匹配：附前 3 处匹配的行号 + 行内容。让 LLM 知道歧义在哪，便于提供更精确的 old_text（如带行号或更多周边代码）。
> - **空 `old_text` 拒绝**：空字符串匹配处处都是（无限匹配），拒绝并提示用 `write_file` 清空文件。
> - **保留所有空白**：`IndexOf` 不裁剪、不规范化。LLM 必须精确提供缩进——这是代码编辑的正确语义（缩进错就替换不到，强约束 LLM 注意缩进）。
> - **`new_text` 允许空字符串**：`edit_file(path, "old", "")` 把 "old" 替换为空——即删除 "old"。合法操作。
> - **`old_text == new_text`**：本迭代不专门拒绝——会写入相同内容（无害但浪费 IO）。如需优化，加显式校验。

#### 4.3.8 `ClosedLoopDemo`（不接 LLM 的闭环演示）

```csharp
using System.Text.Json;
using Spectre.Console;

namespace ParrotCode;

/// <summary>
/// 不接 LLM 的工具系统闭环演示。
/// 模拟"LLM 返回 tool_call → 执行 → 拿到结果"的完整流程，
/// 验证 ToolRegistry + ToolExecutor + 三个工具的端到端集成。
/// 调用方：手测时在 Program.cs 临时调用，或集成测试 ToolClosedLoopTests 直接验证。
/// </summary>
internal static class ClosedLoopDemo
{
    public static async Task RunAsync(CancellationToken cancellationToken)
    {
        // 1. 装配 ToolRegistry
        var registry = new ToolRegistry();
        registry.Register(new ReadFileTool());
        registry.Register(new WriteFileTool());
        registry.Register(new EditFileTool());

        // 2. 构造 ToolExecutor
        var executor = new ToolExecutor(registry, timeout: TimeSpan.FromSeconds(10));

        AnsiConsole.MarkupLine("[grey]=== 工具系统闭环 demo ===[/]");

        // 3. 模拟 LLM 返回的 ToolCall：write_file
        var writeFileCall = new ToolCall(
            Id: "demo-write-1",
            Name: "write_file",
            Input: JsonDocument.Parse("""{"path":"demo_output.txt","content":"hello\nworld\n"}""").RootElement
        );
        AnsiConsole.MarkupLine($"[cyan]→ tool_call:[/] {writeFileCall.Name} (id={writeFileCall.Id})");
        var writeResult = await executor.ExecuteAsync(writeFileCall, cancellationToken);
        PrintResult(writeResult);

        // 4. 模拟 LLM 返回的 ToolCall：read_file（读回刚写的）
        var readFileCall = new ToolCall(
            Id: "demo-read-1",
            Name: "read_file",
            Input: JsonDocument.Parse("""{"path":"demo_output.txt"}""").RootElement
        );
        AnsiConsole.MarkupLine($"[cyan]→ tool_call:[/] {readFileCall.Name} (id={readFileCall.Id})");
        var readResult = await executor.ExecuteAsync(readFileCall, cancellationToken);
        PrintResult(readResult);

        // 5. 模拟 LLM 返回的 ToolCall：edit_file（把 hello 改成 hi）
        var editFileCall = new ToolCall(
            Id: "demo-edit-1",
            Name: "edit_file",
            Input: JsonDocument.Parse("""{"path":"demo_output.txt","old_text":"hello","new_text":"hi"}""").RootElement
        );
        AnsiConsole.MarkupLine($"[cyan]→ tool_call:[/] {editFileCall.Name} (id={editFileCall.Id})");
        var editResult = await executor.ExecuteAsync(editFileCall, cancellationToken);
        PrintResult(editResult);

        // 6. 模拟 LLM 返回的 ToolCall：edit_file 唯一性失败（world 在文件中只出现一次但重复 read 后）
        var ambiguousCall = new ToolCall(
            Id: "demo-edit-2",
            Name: "edit_file",
            Input: JsonDocument.Parse("""{"path":"demo_output.txt","old_text":"o","new_text":"0"}""").RootElement
        );
        AnsiConsole.MarkupLine($"[cyan]→ tool_call:[/] {ambiguousCall.Name} (id={ambiguousCall.Id})");
        var ambiguousResult = await executor.ExecuteAsync(ambiguousCall, cancellationToken);
        PrintResult(ambiguousResult);

        // 7. 模拟 LLM 返回的 ToolCall：未注册工具
        var unknownCall = new ToolCall(
            Id: "demo-unknown-1",
            Name: "nonexistent_tool",
            Input: JsonDocument.Parse("{}").RootElement
        );
        AnsiConsole.MarkupLine($"[cyan]→ tool_call:[/] {unknownCall.Name} (id={unknownCall.Id})");
        var unknownResult = await executor.ExecuteAsync(unknownCall, cancellationToken);
        PrintResult(unknownResult);

        AnsiConsole.MarkupLine("[grey]=== demo 结束 ===[/]");
    }

    private static void PrintResult(ToolResult result)
    {
        if (result.Success)
        {
            AnsiConsole.MarkupLine($"[green]✓ 成功:[/] {Markup.Escape(result.Content)}");
        }
        else
        {
            AnsiConsole.MarkupLine($"[red]✗ 失败:[/] {Markup.Escape(result.Error ?? "(无错误信息)")}");
        }
    }
}
```

> **设计要点**：
> - **`internal static`**：demo 不对外暴露，仅供手测与集成测试调用。如需对外，改 `public`。
> - **覆盖 5 个场景**：正常 write / 正常 read / 正常 edit（唯一匹配）/ edit 歧义失败 / 未注册工具。覆盖核心路径与错误路径。
> - **不接 LLM**：`ToolCall` 手工构造（`JsonDocument.Parse` 从字符串生成 `JsonElement`），不依赖 Provider 响应解析。这把"工具系统"与"LLM 集成"解耦——本迭代只验证前者。
> - **`Markup.Escape`**：文件内容 / 错误信息可能含 Spectre 标记字符（如 `[`），必须转义避免 Markup 解析错误。

### 4.4 工具名命名规范

LLM 可见的工具名（`IBaseTool.Name`）与参数名（`ToolParameter.Name`）遵循：

| 规范 | 示例 | 理由 |
| --- | --- | --- |
| snake_case | `read_file` / `write_file` / `edit_file` | OpenAI / Anthropic / MewCode 惯例；LLM 训练数据中多数工具用 snake_case |
| 全小写 | `read_file`（非 `ReadFile`） | 与 LLM 训练分布一致，降低生成大小写错误 |
| 动词_名词 | `read_file` / `write_file` | 工具是动作，动词在前更直观 |
| 参数名同 snake_case | `path` / `old_text` / `new_text` | 与工具名风格一致 |

> C# 类名保持 PascalCase（`ReadFileTool`），LLM 看到的 `Name` 是 snake_case 字符串。两者解耦——C# 命名遵循 .NET 惯例，LLM-facing 名字遵循生态惯例。

### 4.5 工具系统与现有类型的衔接

| 现有类型（迭代 2a/4 已定义） | 本迭代使用方式 |
| --- | --- |
| `Message(MessageRole.Role, string Content)` + `ToolCalls` 字段 | 本迭代不产生带 ToolCalls 的 Message（无 LLM 集成）；迭代 6 由 AgentLoop 填充 |
| `ToolCall(string Id, string Name, JsonElement Input)` | 本迭代直接消费——ClosedLoopDemo 与测试手工构造 ToolCall 传给 ToolExecutor |
| `ConversationHistory.AddTool(string content)` | 本迭代不使用（无工具结果入历史）；迭代 6 由 AgentLoop 把 ToolResult.Content 通过 AddTool 入历史 |
| `MessageRole.Tool` | 本迭代不产生 Tool 角色消息；迭代 6 AgentLoop 用 |

> 本迭代**复用**迭代 2a 已定义的 `ToolCall` 类型——它的 `JsonElement Input` 字段正是为承载 LLM 生成的 JSON 参数而设计。`ToolExecutor.ExecuteAsync(ToolCall call)` 直接读 `call.Name` 查找工具、`call.Input` 传给工具的 `ExecuteAsync`。

## 五、依赖变更

**无新增 NuGet 依赖。**

- `JsonSerializer` / `JsonDocument` / `JsonElement` 来自 `System.Text.Json`（BCL 内置）。
- `File` / `FileStream` / `Directory` / `Path` 来自 `System.IO`（BCL 内置）。
- `Stopwatch` 来自 `System.Diagnostics`（BCL 内置）。
- `StringBuilder` 来自 `System.Text`（BCL 内置）。
- `Spectre.Console` / `Microsoft.Extensions.Logging` 已在迭代 1/2b 引入。

`ParrotCode.Net.csproj` / `ParrotCode.Net-xUnit.csproj`：**不变**。

> 与迭代 3/4 一致，零新依赖，纯代码实现。

## 六、配置文件

**无变化。** `example.parrotcode.yaml` / `.parrotcode.yaml` / `Config/Models.cs` 均不改。

- 工具系统不需要配置——三个内置工具硬编码注册到 ToolRegistry。
- 工具超时默认 30 秒，不暴露为配置（迭代 6 AgentLoop 集成时再考虑）。
- 工具的 Name / Description / Parameters 硬编码在工具类中——LLM-facing 元数据应稳定，避免配置漂移。

> 后续迭代可能引入的配置项：
> - 工具超时（`tools.timeout`）→ 迭代 6 或独立小迭代。
> - 启用 / 禁用工具列表（`tools.enabled` / `tools.disabled`）→ 迭代 8 安全层。
> - MCP 工具服务器列表（`mcp.servers`）→ 迭代 11。

## 七、迁移说明（迭代 4 → 迭代 5）

| 迭代 4 | 迭代 5 | 处理 |
| --- | --- | --- |
| 无 `Tools/` 目录 | 新增 `Tools/` 目录 + 10 个文件 | 新模块 |
| `ToolCall` 类型已定义但不使用 | `ToolExecutor.ExecuteAsync(ToolCall)` 直接消费 | 启用既有类型 |
| `ConversationHistory.AddTool(string)` 定义但不使用 | 仍不使用（迭代 6 才用） | 保持预留 |
| `App.cs` 多轮对话 + `/clear` | 不变 | 工具系统未接入主循环 |
| 无闭环 demo | `Tools/ClosedLoopDemo.cs` 新增 | 手测验收物 |
| `IBaseProvider.ChatStreamAsync` 返回 `IAsyncEnumerable<string>` | 不变（迭代 6 扩展为带 tools 的重载） | 保持迭代 4 签名 |

迁移后回归不变式：
- `dotnet run`（mock）行为与迭代 4 完全一致——多轮对话 + 流式输出 + `/clear` 全部保持。
- 工具系统作为独立模块存在，对 App / Provider / History 透明。
- `dotnet test`（迭代 1-4 既有测试）全绿，无回归。

> **回归保护**：迭代 5 不修改任何现有文件，只新增 `Tools/` 目录。`Program.cs` / `App.cs` / `Providers/` / `Conversation/` / `Config/` 全部不变——这是把工具系统作为"纯增量"的设计选择。迭代 6 才把这些与 App / Provider 串联。

## 八、单元测试

### 8.1 `ReadFileToolTests`（新增）

| 用例 | 操作 | 期望 |
| --- | --- | --- |
| `ReadFile_ExistingFile_ReturnsContent` | 写临时文件 + read_file | `Success=true`，`Content` = 文件内容 |
| `ReadFile_EmptyFile_ReturnsEmptyContent` | 写空文件 + read_file | `Success=true`，`Content == ""` |
| `ReadFile_NonExistentFile_ReturnsError` | 读不存在的路径 | `Success=false`，`Error` 含"文件不存在" |
| `ReadFile_DirectoryPath_ReturnsError` | 传目录路径 | `Success=false`，`Error` 含"目录而非文件" |
| `ReadFile_MissingPathParameter_ReturnsError` | `{"content":"x"}` | `Success=false`，`Error` 含"缺少必需参数：path" |
| `ReadFile_PathWrongType_ReturnsError` | `{"path":123}` | `Success=false`，`Error` 含"类型错误" |
| `ReadFile_NullPath_ReturnsError` | `{"path":""}` | `Success=false`，`Error` 含"不能为空" |
| `ReadFile_ChineseContent_ReturnsCorrectly` | 写中文文件 + read_file | `Content` 正确解码（UTF-8） |
| `ReadFile_CategoryIsRead` | 检查 `Category` | `ToolCategory.Read` |
| `ReadFile_NameIsReadFile` | 检查 `Name` | `"read_file"` |
| `ReadFile_ToOpenAiSchema_HasCorrectStructure` | 调 `ToOpenAiSchema()` | 含 `type=function` / `function.name` / `function.parameters.properties.path` |

### 8.2 `WriteFileToolTests`（新增）

| 用例 | 操作 | 期望 |
| --- | --- | --- |
| `WriteFile_NewFile_CreatesAndWrites` | write_file 到不存在的路径 | `Success=true`，文件被创建，内容正确 |
| `WriteFile_ExistingFile_Overwrites` | 先写"旧内容"再 write_file"新内容" | `Success=true`，文件内容为"新内容"（覆盖） |
| `WriteFile_EmptyContent_CreatesEmptyFile` | write_file 内容为 `""` | `Success=true`，文件被创建，大小 0 字节 |
| `WriteFile_PathWithMissingParentDir_CreatesDir` | write_file 到 `subdir/file.txt` | `Success=true`，子目录自动创建 |
| `WriteFile_MissingContentParameter_ReturnsError` | `{"path":"x"}` | `Success=false`，`Error` 含"缺少必需参数：content" |
| `WriteFile_MissingPathParameter_ReturnsError` | `{"content":"x"}` | `Success=false`，`Error` 含"缺少必需参数：path" |
| `WriteFile_NullPath_ReturnsError` | `{"path":""}` | `Success=false`，`Error` 含"不能为空" |
| `WriteFile_ReadOnlyFile_ReturnsError` | 写只读文件 | `Success=false`，`Error` 含"无写入权限"或"写入失败" |
| `WriteFile_CategoryIsWrite` | 检查 `Category` | `ToolCategory.Write` |
| `WriteFile_ReturnsByteCount` | 写"你好"（UTF-8 = 6 字节） | `Content` 含"6 字节" |
| `WriteFile_ReturnsPathInContent` | write_file 到 `foo.txt` | `Content` 含 `foo.txt` |

### 8.3 `EditFileToolTests`（新增）

| 用例 | 操作 | 期望 |
| --- | --- | --- |
| `EditFile_UniqueMatch_ReplacesAndReturnsSuccess` | 文件含 1 处 old_text，edit_file | `Success=true`，文件被修改，Content 含"替换 1 处" |
| `EditFile_ZeroMatch_ReturnsErrorWithPreview` | 文件不含 old_text | `Success=false`，`Error` 含"未找到" + 文件预览 |
| `EditFile_MultipleMatches_ReturnsErrorWithContexts` | 文件含 3 处 old_text | `Success=false`，`Error` 含"找到 3 处" + 行号 |
| `EditFile_MultipleMatches_ShowsAtMost3Contexts` | 文件含 5 处 old_text | `Error` 含"共 5 处，仅显示前 3 处" |
| `EditFile_NonExistentFile_ReturnsError` | edit 不存在的文件 | `Success=false`，`Error` 含"文件不存在" |
| `EditFile_DirectoryPath_ReturnsError` | edit 目录路径 | `Success=false`，`Error` 含"目录而非文件" |
| `EditFile_EmptyOldText_ReturnsError` | `old_text=""` | `Success=false`，`Error` 含"不能为空" + 提示用 write_file |
| `EditFile_EmptyNewText_DeletesOldText` | `new_text=""` 替换"old" | `Success=true`，文件中"old"被删除 |
| `EditFile_CaseSensitive_DoesNotMatchDifferentCase` | 文件含"Hello"，old_text="hello" | `Success=false`，`Error` 含"未找到" |
| `EditFile_PreservesWhitespace` | 文件含"  indented\n"，old_text 含缩进 | 唯一匹配则成功 |
| `EditFile_OldTextEqualsNewText_WritesSameContent` | `old_text==new_text` | `Success=true`（无害但写入相同内容） |
| `EditFile_MissingParameter_ReturnsError` | 缺 `path` / `old_text` / `new_text` 任一 | `Success=false`，`Error` 含"缺少必需参数" |
| `EditFile_CategoryIsWrite` | 检查 `Category` | `ToolCategory.Write` |
| `EditFile_LineContext_CorrectLineNumbers` | 多次匹配的行号正确 | 验证 `GetLineContext` 算法 |

### 8.4 `ToolRegistryTests`（新增）

| 用例 | 操作 | 期望 |
| --- | --- | --- |
| `Register_AddsTool` | `Register(new ReadFileTool())` | `Get("read_file")` 返回该实例 |
| `Register_DuplicateName_Throws` | 注册两个同名工具 | 抛 `ArgumentException` |
| `Register_NullTool_Throws` | `Register(null!)` | 抛 `ArgumentNullException` |
| `Register_EmptyName_Throws` | 注册 Name="" 的工具 | 抛 `ArgumentException` |
| `Get_UnknownName_ReturnsNull` | `Get("nonexistent")` | 返回 null |
| `Require_UnknownName_Throws` | `Require("nonexistent")` | 抛 `ArgumentException` |
| `GetAll_ReturnsAllRegistered` | 注册 3 个工具 | `GetAll().Count == 3` |
| `GetAll_AfterRegisterReflectsNewTools` | 注册 1 个 → 取 GetAll → 再注册 1 个 → 取 GetAll | 第二次返回 2 个 |
| `ToOpenAiSchemas_ReturnsJsonArray` | 注册 3 个工具 → `ToOpenAiSchemas()` | JsonElement 是数组，含 3 个元素，每个含 `type=function` |
| `ToOpenAiSchemas_EmptyRegistry_ReturnsEmptyArray` | 空 registry | 返回空 JSON 数组 |
| `ToAnthropicSchemas_ReturnsJsonArray` | 注册 1 个工具 → `ToAnthropicSchemas()` | 数组含 1 元素，含 `input_schema` 字段 |
| `Register_MultipleTools_AllGettable` | 注册 read/write/edit 三个 | 三个都能 `Get` 到 |
| `Get_CaseSensitive` | 注册 `read_file`，`Get("Read_File")` | 返回 null（大小写敏感） |

### 8.5 `ToolExecutorTests`（新增）

| 用例 | 操作 | 期望 |
| --- | --- | --- |
| `ExecuteAsync_ValidCall_ReturnsResult` | 注册 read_file + 执行 ToolCall(read_file, {path: existing}) | 返回 `ToolResult(Success=true)` |
| `ExecuteAsync_UnknownTool_ReturnsError` | 执行未注册工具 | 返回 `ToolResult.Fail("未注册工具：...")` |
| `ExecuteAsync_ToolThrowsException_ReturnsError` | 工具内抛 `InvalidOperationException` | 返回 `ToolResult.Fail("...InvalidOperationException...")` |
| `ExecuteAsync_ToolCancels_ReturnsTimeoutError` | 工具内 `Task.Delay(10s)` + 超时 100ms | 返回 `ToolResult.Fail("...超时...")` |
| `ExecuteAsync_ExternalCancellation_Throws` | 外部 cancellationToken 已取消 | 抛 `OperationCanceledException`（透传） |
| `ExecuteAsync_NullCall_Throws` | `ExecuteAsync(null!)` | 抛 `ArgumentNullException` |
| `ExecuteAsync_ToolRespectsCancellationToken` | 工具内 `await Task.Delay(... ct)` | 超时后工具被取消（ct.IsCancellationRequested） |
| `ExecuteAsync_NullRegistry_Throws` | `new ToolExecutor(null!)` | 抛 `ArgumentNullException` |
| `ExecuteAsync_DefaultTimeoutIs30Seconds` | 检查默认 timeout | 30s（通过反射或行为验证） |
| `ExecuteAsync_CustomTimeoutRespected` | timeout=100ms + 慢工具 | 超时返回 |

> **超时测试注意**：测试超时行为时，`timeout` 设小（如 100ms）+ 工具 `Task.Delay(5s)`，避免测试慢。同时验证外部取消与超时取消的不同处理（外部取消透传 OCE，超时取消转 ToolResult.Fail）。

### 8.6 `ToolSchemaTests`（新增，覆盖 ToolBase schema 转换）

| 用例 | 操作 | 期望 |
| --- | --- | --- |
| `ToOpenAiSchema_ContainsTypeFunction` | `readFileTool.ToOpenAiSchema()` | 顶层含 `type=function` |
| `ToOpenAiSchema_ContainsFunctionField` | 同上 | 含 `function` 字段 |
| `ToOpenAiSchema_FunctionContainsName` | 同上 | `function.name == "read_file"` |
| `ToOpenAiSchema_FunctionContainsDescription` | 同上 | `function.description` 非空 |
| `ToOpenAiSchema_FunctionContainsParameters` | 同上 | `function.parameters` 是 object |
| `ToOpenAiSchema_ParametersContainsPropertiesPath` | 同上 | `function.parameters.properties.path` 存在 |
| `ToOpenAiSchema_ParametersContainsRequiredArray` | 同上 | `function.parameters.required` 含 "path" |
| `ToOpenAiSchema_ParametersTypeIsObject` | 同上 | `function.parameters.type == "object"` |
| `ToAnthropicSchema_ContainsName` | `readFileTool.ToAnthropicSchema()` | 顶层含 `name == "read_file"` |
| `ToAnthropicSchema_ContainsInputSchema` | 同上 | 含 `input_schema` 字段 |
| `ToAnthropicSchema_InputSchemaContainsProperties` | 同上 | `input_schema.properties.path` 存在 |
| `WriteFileTool_SchemaHasPathAndContent` | `writeFileTool.ToOpenAiSchema()` | `properties` 含 `path` 与 `content`，`required` 含两者 |
| `EditFileTool_SchemaHasThreeRequiredParams` | `editFileTool.ToOpenAiSchema()` | `required` 数组长度 == 3 |

### 8.7 `ToolClosedLoopTests`（新增，集成测试）

| 用例 | 操作 | 期望 |
| --- | --- | --- |
| `ClosedLoop_WriteReadEdit_Succeeds` | write_file 创建 → read_file 读回 → edit_file 修改 | 三步均 `Success=true`，最终文件内容正确 |
| `ClosedLoop_EditFileAmbiguousMatch_ReturnsError` | write_file 含重复 old_text → edit_file | edit 返回 `Success=false`，错误含"找到 N 处" |
| `ClosedLoop_EditFileAfterRead_PreservesContent` | write "abc" → read 回 → edit "abc"→"xyz" → read 回 | 第二次 read 内容为 "xyz" |
| `ClosedLoop_UnknownTool_ReturnsError` | 执行未注册工具 | 返回 `ToolResult.Fail`，错误含"未注册" |
| `ClosedLoop_MissingParameter_ReturnsError` | write_file 缺 content | 返回 `ToolResult.Fail`，错误含"缺少必需参数" |
| `ClosedLoop_DemoRun_CompletesWithoutException` | 调用 `ClosedLoopDemo.RunAsync()` | 不抛异常（输出可不验证） |

### 8.8 回归

- `dotnet test` 全绿（含迭代 1/2a/2b/3/4 既有 + 迭代 5 新增）。
- `dotnet run`（mock）行为与迭代 4 完全一致——多轮对话 + 流式 + `/clear`。
- 工具系统对 App 透明——`App.cs` 未引用 `Tools/`，无回归风险。

## 九、验收标准

> 全部满足方可标记 `[已完成]`。每条尽量可被命令或肉眼验证。

### 9.1 构建与运行

- [ ] `ParrotCode.Net.csproj` 无改动（零新依赖）。
- [ ] `dotnet build` 无 error、无 warning。
- [ ] `dotnet test` 全绿（含迭代 1-4 既有 + 迭代 5 新增的 7 个测试文件）。
- [ ] `dotnet run`（`active_provider: mock`）行为与迭代 4 一致——多轮对话 + 流式 + `/clear` 全部正常。

### 9.2 工具抽象层

- [ ] `Tools/IBaseTool.cs` 定义 `IBaseTool` 接口，含 `Name` / `Description` / `Category` / `Parameters` / `ExecuteAsync` / `ToOpenAiSchema` / `ToAnthropicSchema`。
- [ ] `Tools/ToolBase.cs` 定义 `ToolBase` 抽象类，实现 `ToOpenAiSchema` / `ToAnthropicSchema`，提供 `GetRequiredString` / `GetOptionalString` 辅助方法。
- [ ] `Tools/ToolResult.cs` 定义 `record ToolResult(bool Success, string Content, string? Error)` + 静态 `Ok` / `Fail` 工厂。
- [ ] `Tools/ToolCategory.cs` 定义 `enum ToolCategory { Read, Write }`。
- [ ] `Tools/ToolParameter.cs` 定义 `record ToolParameter(string Name, string Type, string Description, bool Required)`。

### 9.3 ToolRegistry

- [ ] `Tools/ToolRegistry.cs` 定义 `ToolRegistry` 类。
- [ ] `Register(tool)` 添加工具，重名抛 `ArgumentException`。
- [ ] `Get(name)` 返回 `IBaseTool?`，未注册返回 null。
- [ ] `Require(name)` 返回 `IBaseTool`，未注册抛 `ArgumentException`。
- [ ] `GetAll()` 返回所有已注册工具的快照。
- [ ] `ToOpenAiSchemas()` 返回 `JsonElement`（JSON 数组）。
- [ ] `ToAnthropicSchemas()` 返回 `JsonElement`（JSON 数组）。
- [ ] 工具名大小写敏感（`StringComparer.Ordinal`）。

### 9.4 ToolExecutor

- [ ] `Tools/ToolExecutor.cs` 定义 `ToolExecutor` 类。
- [ ] 构造接受 `ToolRegistry` + 可选 `timeout`（默认 30s）+ 可选 `ILogger`。
- [ ] `ExecuteAsync(ToolCall, ct)` 返回 `Task<ToolResult>`。
- [ ] 未注册工具返回 `ToolResult.Fail("未注册工具：...")`。
- [ ] 工具内异常被捕获，转为 `ToolResult.Fail(ex.Message)`。
- [ ] 超时返回 `ToolResult.Fail("工具执行超时...")`。
- [ ] 外部取消（`cancellationToken` 已取消）透传 `OperationCanceledException`。
- [ ] 不让任何异常逃逸到调用方（除外部取消）。
- [ ] `ExecuteAsync(null!)` 抛 `ArgumentNullException`。

### 9.5 ReadFileTool

- [ ] `Tools/ReadFileTool.cs` 定义 `ReadFileTool : ToolBase`。
- [ ] `Name == "read_file"`，`Category == Read`。
- [ ] `Parameters` 含 1 个必需参数 `path` (string)。
- [ ] 读存在的文件返回 `ToolResult.Ok(content)`。
- [ ] 读空文件返回 `ToolResult.Ok("")`。
- [ ] 读不存在的文件返回 `ToolResult.Fail("文件不存在：...")`。
- [ ] 读目录返回 `ToolResult.Fail("目录而非文件：...")`。
- [ ] 缺 `path` 参数返回 `ToolResult.Fail("缺少必需参数：path")`。
- [ ] `path` 类型错误（非 string）返回 `ToolResult.Fail("类型错误...")`。
- [ ] 中文内容正确读取（UTF-8 解码）。

### 9.6 WriteFileTool

- [ ] `Tools/WriteFileTool.cs` 定义 `WriteFileTool : ToolBase`。
- [ ] `Name == "write_file"`，`Category == Write`。
- [ ] `Parameters` 含 2 个必需参数 `path` + `content`。
- [ ] 写新文件：文件被创建，内容正确，返回 `Success=true`。
- [ ] 写已存在文件：覆盖原内容。
- [ ] 写空内容：创建空文件（0 字节）。
- [ ] 父目录不存在：自动创建（递归 `mkdir -p` 语义）。
- [ ] 缺 `path` / `content` 任一参数返回 `ToolResult.Fail`。
- [ ] 写只读文件返回 `ToolResult.Fail("无写入权限...")`。
- [ ] 返回 Content 含写入字节数（UTF-8 编码字节数）。
- [ ] 返回 Content 含文件路径。

### 9.7 EditFileTool

- [ ] `Tools/EditFileTool.cs` 定义 `EditFileTool : ToolBase`。
- [ ] `Name == "edit_file"`，`Category == Write`。
- [ ] `Parameters` 含 3 个必需参数 `path` + `old_text` + `new_text`。
- [ ] 唯一匹配：替换成功，返回 `Success=true`，Content 含"替换 1 处"。
- [ ] 0 次匹配：返回 `ToolResult.Fail("未找到...")` + 文件前 200 字符预览。
- [ ] 多次匹配：返回 `ToolResult.Fail("找到 N 处...")` + 前 3 处行号 + 上下文。
- [ ] 多次匹配超过 3 处：错误信息含"共 N 处，仅显示前 3 处"。
- [ ] 文件不存在：返回 `ToolResult.Fail("文件不存在...")`。
- [ ] 目录路径：返回 `ToolResult.Fail("目录而非文件...")`。
- [ ] 空 `old_text`：返回 `ToolResult.Fail("不能为空...")` + 提示用 write_file。
- [ ] 空 `new_text`：合法（删除 old_text）。
- [ ] 大小写敏感：`Hello` 与 `hello` 视为不同。
- [ ] 保留空白：缩进 / 换行必须精确匹配。
- [ ] 缺任一参数返回 `ToolResult.Fail("缺少必需参数...")`。
- [ ] 行号上下文正确（`GetLineContext` 算法验证）。

### 9.8 闭环 demo

- [ ] `Tools/ClosedLoopDemo.cs` 定义 `internal static class ClosedLoopDemo` + `RunAsync(ct)` 方法。
- [ ] 覆盖 5 个场景：正常 write / 正常 read / 正常 edit（唯一匹配）/ edit 歧义失败 / 未注册工具。
- [ ] 不抛异常完成运行。
- [ ] 输出格式清晰（成功 / 失败状态明显，含 Spectre 着色）。
- [ ] 不依赖 LLM——`ToolCall` 全部手工构造。

### 9.9 schema 转换

- [ ] `ReadFileTool.ToOpenAiSchema()` 返回含 `type=function` / `function.name="read_file"` / `function.parameters.properties.path` 的 JSON。
- [ ] `WriteFileTool.ToOpenAiSchema()` 的 `required` 数组含 `path` 与 `content`。
- [ ] `EditFileTool.ToOpenAiSchema()` 的 `required` 数组含 3 个参数。
- [ ] `ToAnthropicSchema()` 返回含 `name` / `description` / `input_schema` 字段的 JSON。
- [ ] `ToolRegistry.ToOpenAiSchemas()` 返回 JSON 数组，元素数 = 已注册工具数。
- [ ] 空 `ToolRegistry.ToOpenAiSchemas()` 返回空数组 `[]`。

### 9.10 异常与边界

- [ ] 工具执行中任何异常（IO / 权限 / 参数）都被 ToolExecutor 捕获，转为 `ToolResult.Fail`。
- [ ] 工具执行超时（默认 30s 或自定义）返回 `ToolResult.Fail("工具执行超时...")`。
- [ ] 外部 `cancellationToken` 取消时 ToolExecutor 透传 `OperationCanceledException`。
- [ ] 工具的 `ExecuteAsync` 不直接抛异常给调用方（ToolExecutor 兜底捕获）。
- [ ] `ToolCall.Input` 为 `{}`（无参数）时，工具返回"缺少必需参数"错误，不崩溃。

### 9.11 敏感信息

- [ ] 工具执行日志不出现 ApiKey 等敏感信息。
- [ ] `ToolResult.Content` / `Error` 不包含 ApiKey（即使读到的文件含 key，也是文件内容而非日志泄露）。
- [ ] 文件路径不写日志（避免泄露项目结构）—— ToolExecutor 只记 `工具 {Name} 执行完成` 而非 `工具 {Name}({path}) ...`。

### 9.12 跨平台

- [ ] Windows 上 `dotnet test` 全绿。
- [ ] macOS / Linux 上 `dotnet test` 全绿（注意路径分隔符——工具内统一用 `Path` API 而非硬编码 `\`）。
- [ ] 文件编码 UTF-8 在三平台一致（不依赖 BOM）。
- [ ] 文件权限差异（Unix 的 `chmod` / Windows 的 ACL）由 OS 报错，工具返回 `UnauthorizedAccessException` → `ToolResult.Fail`。

### 9.13 迁移与回归

- [ ] `IBaseProvider` 接口**不变**（迭代 4 的 `ChatAsync` + `ChatStreamAsync`）。
- [ ] `Message` / `MessageRole` / `ToolCall` 类型**不变**（迭代 2a 已定义，本迭代启用）。
- [ ] `ConversationHistory` **不变**（迭代 4 行为保持，`AddTool` 仍预留）。
- [ ] `App.cs` / `Program.cs` **不变**（工具系统未接入主循环）。
- [ ] `OpenAIProvider` / `MockProvider` **不变**。
- [ ] `Config/` 模块**不变**。
- [ ] 迭代 1-4 的所有测试**全绿**（无回归）。
- [ ] `dotnet run`（mock）多轮对话行为与迭代 4 一致。

## 十、进阶练习（可选，不计入验收）

1. **`RunCommandTool`**：执行 shell 命令的工具。参数 `command` + `args` + `cwd` + `timeout`。本迭代不接入安全层，先跑通——`Process.Start` + 重定向 stdout/stderr。注意：plan.md 把这个工具列入迭代 6 的影响文件，可提前实现工具本身，集成在迭代 6。

2. **工具结果截断**：`ReadFileTool` 读超大文件（如 1MB 日志）时返回完整内容。在工具内加 50K 字符截断（保留头部 2K + 尾部 2K + "...（已截断 N 字符）..."），返回截断后内容。这是迭代 9 Truncator 的工具内版本——可作为本迭代的可选优化。

3. **`GlobTool` + `GrepTool`**：文件查找工具。`GlobTool(pattern)` 返回匹配文件路径列表；`GrepTool(pattern, path)` 返回匹配行列表。这两个工具在 plan.md 列入迭代 6 影响文件，可提前实现工具本身。

4. **工具超时可配置化**：在 `example.parrotcode.yaml` 加 `tools.timeout: 60` 字段，由 ConfigLoader 加载到 `AppConfig.Tools.Timeout`，ToolExecutor 构造时传入。体会"配置驱动工具行为"的模式。

5. **工具结果元数据**：在 `ToolResult` 加 `Metadata` 字段（`Dictionary<string, object>`），工具可附加上下文信息（如 `ReadFileTool` 附加 `FileSize` / `Encoding` / `LineCount`）。AgentLoop 可把这些元数据记入日志或传给上下文管理器。

6. **`ToAnthropicSchema` 实测**：实现一个 `AnthropicSchemaValidator`，用 `JsonSchema.Net` 库校验生成的 `input_schema` 符合 JSON Schema 规范。本迭代只提供接口实现，端到端验证在 Anthropic Provider 接入时。

7. **工具调用计数与限流**：在 `ToolExecutor` 加 `MaxCallsPerSession` 配置（如 100 次），超过抛错或返回 `ToolResult.Fail`。防止 Agent 死循环调用工具。

## 十一、风险与注意事项

| 风险 | 缓解 |
| --- | --- |
| 工具执行超时后任务仍在后台跑（无法强杀） | `CancellationToken` 传递给工具，工具内 IO 接受 ct 则会响应取消。文件 IO 通常响应；CPU 密集型工具（本迭代无）可能泄漏。日志记录超时事件便于追踪 |
| `ReadFileTool` 读超大文件 OOM | 本迭代不限制大小，文档明确"接受任意大小"。迭代 9 Truncator 在工具结果入历史时截断；本迭代单测不构造超大文件 |
| `EditFileTool` 重叠匹配歧义 | 用非重叠匹配（`pos = idx + needle.Length`），与 Python str.count 一致。单测覆盖 `"aaa"` 查 `"aa"` 的边界（只匹配 1 处） |
| `EditFileTool` 大小写敏感导致 LLM 困惑 | 文档与 Description 明确"区分大小写、保留空白"。LLM 应提供精确 old_text，这是强约束的设计意图 |
| 工具内参数校验错误格式不统一 | `ToolBase.GetRequiredString` / `GetOptionalString` 统一返回 `(Value, Error)` 元组，错误信息格式一致 |
| `JsonSerializer` 默认属性命名与协议不符 | 手写匿名对象时属性名用 camelCase（`type` / `function` / `input_schema`），与 OpenAI / Anthropic wire format 完全一致。避免全局 `JsonSerializerOptions` 配置 |
| `ToAnthropicSchema` 未经端到端验证 | 代码按 Anthropic 官方文档实现，待 Anthropic Provider 接入时回归。单测验证字段结构（`name` / `description` / `input_schema` 存在） |
| 工具名 LLM 大小写混淆 | `StringComparer.Ordinal` 大小写敏感；Description 明示工具名。ToolExecutor 找不到工具时返回 `ToolResult.Fail("未注册工具：...")`，错误信息含期望的工具名 |
| 闭环 demo 在 Program.cs 中如何调用 | 本迭代不在 Program.cs 加调用（避免影响 iter 4 行为）。集成测试 `ToolClosedLoopTests` 直接验证；手测时临时在 Program.cs 加一行 `await ClosedLoopDemo.RunAsync(ct); return;` |
| 工具系统未接入 App 导致"看不到效果" | 闭环 demo 与集成测试是验收物。完整 LLM 集成在迭代 6——届时用户能"让 AI 读 README 并总结"看到端到端效果 |
| `Directory.CreateDirectory` 权限不足 | 工具内 try/catch `UnauthorizedAccessException` 转友好错误；ToolExecutor 兜底捕获其他异常 |
| 文件路径含中文 / 空格 / 特殊字符 | .NET `File` API 原生支持 Unicode 路径；测试覆盖中文路径用例 |
| 测试中临时文件未清理 | 测试用 `Path.GetTempFileName()` + `try/finally` 清理，或用 `IDisposable` fixture 在 Dispose 时删 |
| `JsonElement` 生命周期 | `JsonDocument.Parse` 返回的 `JsonElement` 在 `JsonDocument` Dispose 后失效。ClosedLoopDemo 用 `JsonDocument.Parse(...).RootElement` 不持有 `JsonDocument` 引用——`JsonElement` 是 struct 拷贝，但底层 buffer 在 Dispose 后释放。修复：用 `using var doc = JsonDocument.Parse(...)` 持有 doc 生命周期，或用 `JsonSerializer.SerializeToElement` 直接构造（无需 JsonDocument 中转） |

## 十二、交付清单

- [ ] `ParrotCode.Net/Tools/IBaseTool.cs`（新增：工具抽象接口）
- [ ] `ParrotCode.Net/Tools/ToolBase.cs`（新增：抽象基类，schema 转换 + 参数提取辅助）
- [ ] `ParrotCode.Net/Tools/ToolResult.cs`（新增：record ToolResult + Ok/Fail 工厂）
- [ ] `ParrotCode.Net/Tools/ToolCategory.cs`（新增：enum { Read, Write }）
- [ ] `ParrotCode.Net/Tools/ToolParameter.cs`（新增：record ToolParameter）
- [ ] `ParrotCode.Net/Tools/ToolRegistry.cs`（新增：注册中心 + 批量 schema 转换）
- [ ] `ParrotCode.Net/Tools/ToolExecutor.cs`（新增：单次执行 + 超时 + 异常捕获）
- [ ] `ParrotCode.Net/Tools/ReadFileTool.cs`（新增：读文件工具）
- [ ] `ParrotCode.Net/Tools/WriteFileTool.cs`（新增：写文件工具）
- [ ] `ParrotCode.Net/Tools/EditFileTool.cs`（新增：精确匹配替换工具）
- [ ] `ParrotCode.Net/Tools/ClosedLoopDemo.cs`（新增：不接 LLM 的闭环演示）
- [ ] `ParrotCode.Net-xUnit/ReadFileToolTests.cs`（新增）
- [ ] `ParrotCode.Net-xUnit/WriteFileToolTests.cs`（新增）
- [ ] `ParrotCode.Net-xUnit/EditFileToolTests.cs`（新增）
- [ ] `ParrotCode.Net-xUnit/ToolRegistryTests.cs`（新增）
- [ ] `ParrotCode.Net-xUnit/ToolExecutorTests.cs`（新增）
- [ ] `ParrotCode.Net-xUnit/ToolSchemaTests.cs`（新增）
- [ ] `ParrotCode.Net-xUnit/ToolClosedLoopTests.cs`（新增：集成测试）
- [ ] 演示：在 Program.cs 临时加 `await ClosedLoopDemo.RunAsync(ct); return;` 手测 + `dotnet test` 全绿截图
- [ ] 本文档状态改为 `[已完成]`
