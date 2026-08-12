# Parrot Code

基于 .NET 8 的终端 AI 编程助手（仿 Claude Code）。编写这个项目的根本目的是学习 Agent 底层原理与工程实现，而不是交付生产级的产品：通过 15+ 个循序渐进的迭代，从零亲手实现 ReAct 循环、工具系统、HITL、安全防御、上下文压缩、MCP、Skill、SubAgent 等 Agent 核心机制，把"Agent 到底是怎么跑起来的"这件事彻底搞清楚。

## 功能特性

当前已实现的能力（对应迭代 1~14）：

- 多 Provider：OpenAI 兼容协议（以 DeepSeek 为主要联调对象）与 Mock Provider，配置文件三级发现
- ReAct Agent 循环：事件流架构，AgentLoop 通过 Channel 产出事件、UI 侧消费，生产与展示解耦；读工具并发、写工具串行，带最大轮次保护
- 流式 TUI：基于 Terminal.Gui v2 的全屏界面，token 级流式渲染、状态栏、Spinner、Tab 补全
- HITL 人在回路：写操作执行前弹窗确认，支持 Allow once / Session / Permanent / Deny 四种决定
- 安全纵深防御：危险命令黑名单（所有档位下始终生效）+ 路径沙箱 + Strict / Normal / Permissive 三档权限，拒绝原因回灌 LLM 让其调整策略
- 上下文管理：超长工具结果截断写盘并保留预览、结构化摘要压缩历史、摘要连续失败自动熔断
- 工具系统：read_file / write_file / edit_file / run_command / glob / grep，参数用 JSON Schema 描述，edit_file 要求原文唯一匹配替换
- 斜杠命令：/help /clear /mode /status /session /skill /commit /compress /exit
- 会话持久化：JSONL 追加写入，支持崩溃恢复与损坏行跳过，/session 可加载历史会话
- 项目指令：项目根目录的 PARROTCODE.md 自动注入对话，支持 @include 嵌套（限 3 层）
- MCP 客户端：JSON-RPC 2.0 协议，Stdio 子进程与 Streamable HTTP 两种传输，MCP 工具名加 `{server}/{tool}` 前缀防冲突
- Skill 系统：目录化 SKILL.md + scripts / references / assets 三层按需加载，SOP 两阶段注入，内置 commit / review / test 三个 Skill
- SubAgent 系统：SubAgent Runner + 角色 + 三层工具过滤 + sub_agent 工具

## 架构与目录结构

一次请求的核心链路：

```
用户输入 → AgentLoop → LLM 流式响应 ──→ 文本增量 → TUI 实时渲染
              ↑              ↓ tool_call
              │        SecurityGuard 管线（黑名单 → 沙箱 → 策略 → HITL）
              │              ↓ 放行
              └──── 结果回填 ── 分批执行（读并发 / 写串行）
```

源代码组织（`ParrotCode.Net/` 为主项目，`ParrotCode.Net-xUnit/` 为测试项目）：

```
ParrotCode.Net/
├── Agent/                   # ReAct 循环与事件流
│   ├── AgentLoop.cs         # 核心循环：调 LLM → 解析 tool_call → 执行 → 回填 → 继续
│   ├── AgentEvent.cs        # 事件类型定义
│   ├── BatchToolExecutor.cs # 读并发 / 写串行的分批执行
│   └── ...                  # Channel 事件分发、流式 tool_call 累积等
├── App/                     # 应用装配与启动
├── Commands/                # 斜杠命令系统
│   └── Builtin/             # /help /clear /mode /status /session /skill /commit 等
├── Config/                  # YAML 配置三级发现与校验
├── Conversation/            # 对话历史 + 上下文管理（截断 / 摘要 / 压缩 / 熔断）
├── Instructions/            # 项目指令加载（PARROTCODE.md + @include）
├── Mcp/                     # MCP 协议客户端
│   ├── Protocol/            # JSON-RPC 2.0 编解码
│   └── Transport/           # Stdio 与 Streamable HTTP 传输
├── Providers/               # LLM Provider 抽象（OpenAI 兼容 / Mock）
├── Security/                # 黑名单 + 路径沙箱 + 三档权限 + 安全管线
├── Skills/                  # Skill 系统（两阶段加载）
│   └── Builtin/             # commit / review / test（目录化 SKILL.md）
├── Storage/                 # JSONL 会话持久化
├── SubAgent/                # SubAgent
│   └── Roles/               # SubAgent 的角色
├── Tools/                   # 工具系统（read / write / edit / run / glob / grep）
├── Tui/                     # Terminal.Gui v2 全屏界面与 HITL 弹窗
├── Program.cs               # 入口
└── example.parrotcode.yaml  # 配置模板
```

## 开发计划与完成情况

整体规划为 15 个迭代，每个迭代的设计文档包含学习目标、交付物、影响文件、关键设计点与验收标准。部分迭代在实施时进一步拆分为 a/b/c 子迭代。进度以实际代码为准：

| 迭代 | 内容 | 状态 |
| --- | --- | --- |
| [1](.docs/iter-01-design.md) | 项目脚手架 + 最小可跑 | 已完成 |
| [2](.docs/iter-02-design.md) | 配置系统 + Provider 抽象层（拆分为 [2a](.docs/iter-02a-design.md)、[2b](.docs/iter-02b-design.md)） | 已完成 |
| [3](.docs/iter-03-design.md) | 第一个 LLM Provider（OpenAI 兼容）+ 流式输出 | 已完成 |
| [4](.docs/iter-04-design.md) | 对话历史 + 多轮上下文 | 已完成 |
| [5](.docs/iter-05-design.md) | 工具系统骨架 + 三个文件工具 | 已完成 |
| [6](.docs/iter-06-design.md) | ReAct Agent 循环（事件流 + Function Calling 闭环） | 已完成 |
| [7](.docs/iter-07-design.md) | TUI 接入 + HITL（拆分为 [7a](.docs/iter-07a-design.md)、[7b](.docs/iter-07b-design.md)，后经 [7c](.docs/iter-07c-design.md) 迁移至 Terminal.Gui v2） | 已完成 |
| [8](.docs/iter-08-design.md) | 安全纵深防御（拆分为 [8a](.docs/iter-08a-design.md)、[8b](.docs/iter-08b-design.md)、[8c](.docs/iter-08c-design.md)） | 已完成 |
| [9](.docs/iter-09-design.md) | 上下文管理（截断 + 摘要 + 压缩） | 已完成 |
| [10](.docs/iter-10-design.md) | 斜杠命令 + 会话持久化 + 项目指令（拆分为 [10a](.docs/iter-10a-design.md)、[10b](.docs/iter-10b-design.md)、[10c](.docs/iter-10c-design.md)） | 已完成 |
| [11](.docs/iter-11-design.md) | MCP 协议客户端（拆分为 [11a](.docs/iter-11a-design.md)、[11b](.docs/iter-11b-design.md)、[11c](.docs/iter-11c-design.md)） | 已完成 |
| [12](.docs/iter-12-design.md) | Skill 系统 | 已完成 |
| [13](.docs/iter-13-design.md) | Skill 目录化与三层加载 + /skill 命令（拆分为 [13a](.docs/iter-13a-design.md)、[13b](.docs/iter-13b-design.md)） | 已完成 |
| [14](.docs/iter-14-design.md) | 子 Agent（拆分为 [14a](.docs/iter-14a-design.md)、[14b](.docs/iter-14b-design.md)） | 已完成 |
| 15 | Hook 引擎 | 未开始 |

前 15 个迭代跑通后，视情况引入可选扩展。

## 文档导航

所有设计文档位于 `.docs/` 目录。`plan.md` 是总体开发计划，各 `iter-*.md` 是按迭代拆分的详细设计：

```
.docs/
└── plan.md                          # 总体开发计划：技术栈选型、15 个迭代路线图、跨迭代约定
    ├── iter-01-design.md            #   迭代 1：项目脚手架 + 最小可跑
    ├── iter-02-design.md            #   迭代 2：配置系统 + Provider 抽象层（总览）
    │   ├── iter-02a-design.md       #     2a：Provider 抽象层
    │   └── iter-02b-design.md       #     2b：配置系统
    ├── iter-03-design.md            #   迭代 3：第一个 LLM Provider + 流式输出
    ├── iter-04-design.md            #   迭代 4：对话历史 + 多轮上下文
    ├── iter-05-design.md            #   迭代 5：工具系统骨架 + 三个文件工具
    ├── iter-06-design.md            #   迭代 6：ReAct Agent 循环
    ├── iter-07-design.md            #   迭代 7：TUI 接入（总览）
    │   ├── iter-07a-design.md       #     7a：TUI 展示层（Spectre.Console Live + 状态栏 + Tab 补全）    
    │   ├── iter-07b-design.md       #     7b：HITL 交互层
    │   └── iter-07c-design.md       #     7c：TUI 库迁移（Spectre.Console → Terminal.Gui v2）
    │       ├── iter-07c-1-design.md #       7c-1：Terminal.Gui 基础设施 + 三段式静态布局
    │       ├── iter-07c-2-design.md #       7c-2：事件流接入 + 流式渲染
    │       └── iter-07c-3-design.md #       7c-3：HITL 模态对话框 + Spinner + 收尾
    ├── iter-08-design.md            #   迭代 8：安全纵深防御（总览）
    │   ├── iter-08a-design.md       #     8a：安全核心（SecurityLevel + Blacklist + PathSandbox）
    │   ├── iter-08b-design.md       #     8b：安全管线编排 + Agent 集成
    │   └── iter-08c-design.md       #     8c：配置扩展 + 装配 + 端到端验收
    ├── iter-09-design.md            #   迭代 9：上下文管理（截断 + 摘要 + 压缩）
    ├── iter-10-design.md            #   迭代 10：斜杠命令 + 会话持久化 + 项目指令（总览）
    │   ├── iter-10a-design.md       #     10a：斜杠命令系统骨架
    │   ├── iter-10b-design.md       #     10b：JSONL 会话持久化
    │   └── iter-10c-design.md       #     10c：项目指令
    ├── iter-11-design.md            #   迭代 11：MCP 协议客户端（总览）
    │   ├── iter-11a-design.md       #     11a：JSON-RPC 协议层 + Stdio 传输
    │   ├── iter-11b-design.md       #     11b：MCP 客户端 + 工具适配器
    │   └── iter-11c-design.md       #     11c：HTTP SSE 传输 + 连接管理器
    ├── iter-12-design.md            #   迭代 12：Skill 系统
    ├── iter-13-design.md            #   迭代 13：Skill 目录化与三层加载 + /skill 管理命令（总览）
    │   ├── iter-13a-design.md       #     13a：Skill 目录化与三层加载
    │   └── iter-13b-design.md       #     13b：/skill 管理命令
    └── iter-14-design.md            #   迭代 14：子 Agent（总览）
        ├── iter-14a-design.md       #     14a：角色系统与三层工具过滤
        └── iter-14b-design.md       #     14b：SubAgentRunner + sub_agent 工具 + 装配
```

## 快速开始

前置条件：.NET 8 SDK。

```powershell
git clone https://github.com/simplex86/ParrotCode.Net.git
cd ParrotCode.Net

# 准备配置（.parrotcode.yaml 已被 .gitignore 忽略，不会提交）
copy ParrotCode.Net\example.parrotcode.yaml ParrotCode.Net\.parrotcode.yaml

# 配置 API Key（默认以 DeepSeek 为激活 Provider）
$env:DEEPSEEK_API_KEY = "sk-..."

dotnet run --project ParrotCode.Net
```

配置文件按以下顺序发现：`PARROTCODE_CONFIG` 环境变量 → 当前工作目录的 `.parrotcode.yaml` → 用户目录的 `~/.parrotcode/config.yaml`。没有真实 API Key 时，把 `active_provider` 改为 `mock` 即可离线体验完整交互流程。

运行测试：

```powershell
dotnet test
```

## 技术栈与测试

| 能力 | 实现 |
| --- | --- |
| 目标框架 | .NET 8（`net8.0`，跨平台） |
| 异步与流式 | async/await + HttpClient 手写 SSE 解析 + System.Threading.Channels |
| TUI | Terminal.Gui v2（迭代 7c 从 Spectre.Console 迁入） |
| 配置 | YamlDotNet + record |
| JSON | System.Text.Json |
| 会话存储 | JSONL 追加写 |
| MCP 传输 | System.Diagnostics.Process（Stdio）/ HttpClient（Streamable HTTP） |
| 日志 | Microsoft.Extensions.Logging |
| 测试 | xUnit + FluentAssertions + coverlet |

测试项目 `ParrotCode.Net-xUnit/` 覆盖全部核心模块，每个迭代以 `dotnet test` 全绿作为完成标准。

## 学习背景与致谢

本项目参考了 Python 项目 MewCode（11 个阶段的 Claude Code 仿制实现），在其基础上做了两处调整：把 asyncio / Prompt Toolkit / PyYAML 等技术栈逐项映射到 .NET 生态，并把"核心循环 + 工具系统"进一步拆细，让每个迭代都能跑起来、看得见效果。交互形态与功能划分对标 Claude Code，MCP 客户端遵循 Model Context Protocol 规范。感谢这些项目与规范的作者，让"从零手写一个 Agent"成为一条可跟随的学习路径。

## License

[MIT](LICENSE)
