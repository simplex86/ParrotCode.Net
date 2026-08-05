# 迭代 1：项目脚手架 + 最小可跑（Hello Agent）— 详细设计

> 状态：[进行中]
> 对应 `plan.md` 第二章「迭代 1」章节，本文档在其基础上补充实现级细节与可执行的验收清单。

## 一、概述

搭建 ParrotCode.Net 的最小可运行骨架，跑通「用户输入 → 调用 Provider → 打印回复」这条最基础的管线。

本迭代**刻意保持最小**：
- **不做**流式输出（迭代 3）
- **不做**工具系统（迭代 5）
- **不做**多轮上下文（迭代 4）
- **不做**正式配置加载（迭代 2），仅放占位 `appsettings.json`

目标是用最少代码把"Agent = LLM + 工具 + 循环"中的 **LLM 调用链**先立起来，为后续迭代提供稳定的接入点。

## 二、学习目标

1. 理解 Agent 的最小骨架：输入 → 调用 → 输出 → 循环。
2. 熟悉 .NET 8 控制台项目结构、`async Task Main`、`HttpClient` 基础调用姿势。
3. 建立「日志走 stderr、用户可见输出走 stdout」的分离习惯，为迭代 7 TUI 接管做准备。
4. 掌握 `CancellationToken` + `Console.CancelKeyPress` 的取消模型。

## 三、范围

### 3.1 本迭代包含（In Scope）

| 项 | 说明 |
| --- | --- |
| 入口程序 | `async Task Main`，读输入 → 调 Provider → 打印 |
| Provider 抽象（最小） | `IChatProvider` 接口，仅一个非流式方法 |
| MockProvider | 返回固定/回显文本，跑通管线 |
| 日志 | `Microsoft.Extensions.Logging.Console`，输出到 stderr |
| 终端着色 | `Spectre.Console` 仅用于着色，不引入全屏 TUI |
| 取消 | Ctrl+C 干净退出 |
| 占位配置 | `appsettings.json`（本迭代不读取，仅占位） |

### 3.2 本迭代不包含（Out of Scope）

- 真实 LLM HTTP 调用（迭代 3 的 `OpenAIProvider`）
- 流式 SSE 解析（迭代 3）
- 配置三级发现与 YAML 加载（迭代 2）
- `IBaseProvider` 完整抽象（含 `ToolCall` / `Message` 类型，迭代 2）
- 多轮历史、工具调用、TUI、安全层

> 注：本迭代引入的 `IChatProvider` 是 **临时最小接口**，迭代 2 会用 `IBaseProvider` 替代/演进它。这是有意的：避免在没跑通管线前就设计过度抽象。

## 四、架构设计

### 4.1 模块结构

```
ParrotCode.Net/
├── Program.cs                 # 入口 + 主循环 + Ctrl+C 处理
├── ParrotCode.Net.csproj      # 引入 Spectre.Console + Logging
├── appsettings.json           # 占位配置（本迭代不读取）
└── Providers/
    ├── IChatProvider.cs       # 最小接口（临时，迭代 2 演进为 IBaseProvider）
    └── MockProvider.cs        # 固定/回显文本，跑通管线
```

> 命名空间约定：受 IDE 自动重构约束（会把 `ParrotCode.Net.*` 折叠为 `ParrotCode`），本迭代源文件统一使用 `namespace ParrotCode`（csproj `RootNamespace` 已设为 `ParrotCode`，`AssemblyName` 仍为 `ParrotCode.Net`）。`Providers/` 仅作文件夹组织。迭代 2 起若要恢复子命名空间，需先禁用该自动重构。

### 4.2 调用流程

```
┌─────────┐   读取一行     ┌──────────┐  ChatAsync(userInput, ct)  ┌──────────────┐
│  用户   │ ────────────▶ │ Program  │ ─────────────────────────▶ │ MockProvider │
└─────────┘                │ (主循环) │ ◀───────────────────────── └──────────────┘
                           └────┬─────┘         string reply
                                │ Spectre.Console 着色输出到 stdout
                                ▼
                           ┌──────────┐
                           │  控制台  │
                           └──────────┘
```

主循环伪代码：

```
while not cancelled:
    line = Console.ReadLine()
    if line is null (EOF) or in {"exit","quit"}: break
    if line is empty: continue
    emit UserEcho(line)          // stdout，着色
    reply = await provider.ChatAsync(line, ct)
    emit AssistantReply(reply)   // stdout，着色
```

### 4.3 关键类型设计

#### 4.3.1 `IChatProvider`（临时最小接口）

```csharp
namespace ParrotCode;

public interface IChatProvider
{
    Task<string> ChatAsync(string userInput, CancellationToken cancellationToken);
}
```

- 仅一个非流式方法，签名带 `CancellationToken`（贯彻「异步全链路 + 取消贯穿」约定）。
- 返回纯 `string`，不引入 `Message`/`ToolCall` 类型——那些属于迭代 2。

#### 4.3.2 `MockProvider`

```csharp
namespace ParrotCode;

public sealed class MockProvider : IChatProvider
{
    public Task<string> ChatAsync(string userInput, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        // 固定回显格式，便于验收时一眼区分 mock 与真实 LLM
        return Task.FromResult($"{userInput}（mock）");
    }
}
```

- 返回 `{输入}（mock）`，与 `plan.md` 验收标准「输出『你好（mock）』」对齐。
- 开头先检查取消令牌，体现取消纪律。

#### 4.3.3 `Program.cs` 入口

```csharp
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Console;
using ParrotCode;
using Spectre.Console;

var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;          // 阻止默认终止，让我们自己优雅退出
    cts.Cancel();
};

using var loggerFactory = LoggerFactory.Create(builder =>
{
    builder.AddSimpleConsole(o =>
    {
        o.UseUtcTimestamp = true;
        o.SingleLine = true;
        o.TimestampFormat = "HH:mm:ss ";
    });
    // 关键：console logger 默认写 stdout，必须显式把日志路由到 stderr（见 §4.4）
    builder.Services.Configure<ConsoleLoggerOptions>(o => o.LogToStandardErrorThreshold = LogLevel.Trace);
    builder.SetMinimumLevel(LogLevel.Information);
});
var logger = loggerFactory.CreateLogger("ParrotCode");

IChatProvider provider = new MockProvider();
var app = new App(provider, logger, cts.Token);
await app.RunAsync();
```

> `App` 类承载主循环，便于后续迭代替换实现而不动 `Main` 的装配代码。

#### 4.3.4 主循环（`App` 类，可与 Program.cs 同文件或独立）

```csharp
internal sealed class App(IChatProvider provider, ILogger logger, CancellationToken ct)
{
    public async Task RunAsync()
    {
        AnsiConsole.MarkupLine("[grey]ParrotCode.Net[/] [green]mock 模式[/]。输入 exit 退出。");
        while (!ct.IsCancellationRequested)
        {
            AnsiConsole.Markup("[bold blue]> [/]");
            var line = Console.ReadLine();
            if (line is null) break;                 // EOF（Ctrl+Z / 管道关闭）
            if (line is "exit" or "quit") break;
            if (string.IsNullOrWhiteSpace(line)) continue;

            AnsiConsole.MarkupLine($"[grey]你：[/]{Markup.Escape(line)}");
            logger.LogInformation("调用 provider，输入长度 {Len}", line.Length);

            try
            {
                var reply = await provider.ChatAsync(line, ct);
                AnsiConsole.MarkupLine($"[green]AI：[/]{Markup.Escape(reply)}");
            }
            catch (OperationCanceledException)
            {
                AnsiConsole.MarkupLine("\n[grey]已取消。[/]");
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "provider 调用失败");
                AnsiConsole.MarkupLine($"[red]错误：[/]{Markup.Escape(ex.Message)}");
            }
        }

        logger.LogInformation("程序退出");
    }
}
```

### 4.4 日志与输出分离

- **stdout**：用户可见内容（提示、用户回显、AI 回复）经 `Spectre.Console`（`AnsiConsole.MarkupLine`）输出。
- **stderr**：日志经 `Microsoft.Extensions.Logging.Console` 输出。**注意该 logger 默认写 stdout（`Console.Out`）**，需显式 `builder.Services.Configure<ConsoleLoggerOptions>(o => o.LogToStandardErrorThreshold = LogLevel.Trace)` 把 >= Trace 的日志路由到 `Console.Error`，否则会污染 stdout。
- 验证方式：用构建产物 `ParrotCode.Net\bin\Debug\net8.0\ParrotCode.Net.exe`（或 `dotnet ParrotCode.Net.dll`）做重定向；**避免 `dotnet run > out 2> err`**——`dotnet run` 在 PowerShell 重定向下会丢失部分 stdout 内容，干扰判断。

> 实测：`ParrotCode.Net.exe > out.txt 2> err.txt`，`out.txt` 只含 `AI：你好（mock）` 等用户输出，`err.txt` 只含 `调用 provider` / `程序退出` 日志，分离干净。

### 4.5 取消与退出语义

| 触发 | 行为 |
| --- | --- |
| 输入 `exit` / `quit` | 正常退出循环 |
| `Ctrl+Z` + 回车（Windows）/ `Ctrl+D`（Unix）→ `ReadLine` 返回 null | 正常退出 |
| `Ctrl+C` | `CancelKeyPress` 置 `e.Cancel=true` 并 `cts.Cancel()`；若正在 `await`，`OperationCanceledException` 被捕获后优雅退出 |
| 空行 | `continue`，不调用 Provider |

**干净退出**的定义：
- 不残留前台进程（`dotnet run` 立即返回 shell）。
- 不抛未处理异常导致非零退出码（`Ctrl+C` 退出码应为 0 或 130，本迭代要求不报未处理异常即可）。

## 五、依赖变更

`ParrotCode.Net.csproj` 新增：

```xml
<ItemGroup>
  <PackageReference Include="Spectre.Console" Version="0.*" />
  <PackageReference Include="Microsoft.Extensions.Logging.Console" Version="8.*" />
</ItemGroup>
```

> 版本号在实现时锁定为当时最新稳定版，此处仅示意范围。`appsettings.json` 设为 `CopyToOutputDirectory`（迭代 2 才真正读取，本迭代仅占位）。

## 六、占位配置

`appsettings.json`：

```json
{
  "Logging": {
    "LogLevel": {
      "Default": "Information"
    }
  },
  "ParrotCode": {
    "Provider": "mock"
  }
}
```

本迭代**不**读取该文件（配置加载在迭代 2）。占位目的：
1. 让项目结构看起来完整，降低后续迭代改动落差。
2. 锁定 `CopyToOutputDirectory`，避免迭代 2 再补。

## 七、验收标准

> 全部满足方可标记 `[已完成]`。每条尽量可被命令或肉眼验证。

### 7.1 构建与运行

- [ ] `dotnet build` 无 error、无 warning（ nullable / 异步相关）。
- [ ] `dotnet run` 能启动，显示欢迎提示与 `> ` 输入符。

### 7.2 基本对话（mock）

- [ ] 输入 `你好`，输出包含 `你好（mock）`，且以 `AI：` 前缀着色显示。
- [ ] 输入 `今天天气如何`，输出 `今天天气如何（mock）`。
- [ ] 连续输入两轮（虽无历史，但循环本身要能连续工作），第二轮仍正常返回。

### 7.3 退出

- [ ] 输入 `exit` 后程序立即退出，shell 回到提示符。
- [ ] 输入 `quit` 同样退出。
- [ ] `Ctrl+Z`+回车（Windows）或 `Ctrl+D`（Unix）能退出。
- [ ] `Ctrl+C` 能在 1 秒内退出，**不**抛未处理的 `OperationCanceledException` 堆栈，退出码可接受。

### 7.4 输入边界

- [ ] 直接回车（空行）不调用 Provider，重新显示 `> ` 提示符。
- [ ] 输入含中文、空格、标点均能正确回显与返回。

### 7.5 日志/输出分离

- [ ] `ParrotCode.Net\bin\Debug\net8.0\ParrotCode.Net.exe > out.txt 2> err.txt`（或 `dotnet ParrotCode.Net.dll ...`），输入 `你好` 后：
  - `out.txt` 包含 `AI：你好（mock）`，**不**包含 `调用 provider` 日志。
  - `err.txt` 包含 `调用 provider，输入长度` 日志，**不**包含 AI 回复正文。
- [ ] `err.txt` 中**不**出现 ApiKey 或敏感信息（本迭代无 key，作为习惯校验）。

### 7.6 健壮性

- [ ] 模拟 Provider 抛异常（临时改 `MockProvider` 抛 `throw new Exception("boom")`），程序打印 `错误：boom` 并**继续**循环，可再次输入。
- [ ] 日志记录该异常的堆栈到 stderr。

### 7.7 跨平台

- [ ] Windows 上 `dotnet run` 正常（Ctrl+C 退出）。
- [ ] macOS / Linux 上 `dotnet run` 正常（Ctrl+C 退出，`Ctrl+D` 退出）。

## 八、进阶练习（可选，不计入验收）

1. 用 `CancellationToken` 实现「输入期间按 `Esc` 取消当前轮」（提示：`Console.ReadKey` 非阻塞轮询 + 单独读取线程，或迭代 7 用 Spectre 的输入控件）。
2. 让 `MockProvider` 支持通过环境变量 `PARROTCODE_MOCK_PREFIX` 自定义前缀，预热迭代 2 的配置发现。
3. 加一个 `-v/--verbose` 参数切换 `LogLevel` 到 `Debug`，观察日志变化。

## 九、风险与注意事项

| 风险 | 缓解 |
| --- | --- |
| 过早抽象：把迭代 2 的 `IBaseProvider`/`Message`/`ToolCall` 提前引入 | 严格限定 `IChatProvider` 仅 1 个方法；类型清单见 §3.1 |
| Spectre.Console 的 ANSI 在重定向管道下产生乱码 | 验收 §7.5 用重定向验证；Spectre 默认在非 TTY 下会降级为纯文本，无需额外处理 |
| `Console.ReadLine` 在 `Ctrl+C` 时行为平台差异 | 用 `CancelKeyPress` + `cts.Cancel()` 兜底；若 `ReadLine` 阻塞，可在迭代 7 改用 Spectre 输入控件 |
| 退出码非零被误判为失败 | §7.3 要求「不抛未处理异常」即达标，不强制退出码 0 |

## 十、交付清单

- [ ] `ParrotCode.Net/Program.cs`（入口 + `App` 主循环）
- [ ] `ParrotCode.Net/Providers/IChatProvider.cs`
- [ ] `ParrotCode.Net/Providers/MockProvider.cs`
- [ ] `ParrotCode.Net/ParrotCode.Net.csproj`（新增依赖）
- [ ] `ParrotCode.Net/appsettings.json`（占位）
- [ ] 演示：`dotnet run` 一段交互截图/录屏
- [ ] 本文档状态改为 `[已完成]`
