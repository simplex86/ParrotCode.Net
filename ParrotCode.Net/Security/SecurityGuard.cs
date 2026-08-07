using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace ParrotCode;

/// <summary>
/// 安全管线编排器（迭代 8b）：黑名单 → 路径沙箱 → 策略。
/// 三层独立，任一拦截即返回 ToolResult.Fail；全放行返回 null。
/// 拦截不弹 HITL（避免打扰已被拦截的操作）；HITL 由 BatchToolExecutor 在 Write 组后续触发。
/// 纯逻辑（无 IO/无 UI），可单测。持有可变 Level 属性支持运行时切换（迭代 10 /mode）。
/// </summary>
public sealed class SecurityGuard
{
    private readonly Blacklist _blacklist;
    private readonly PathSandbox _sandbox;
    private readonly SecurityPolicy _policy;
    private readonly ILogger? _logger;

    /// <summary>当前安全等级（可运行时 set，为迭代 10 /mode 预留）。</summary>
    public SecurityLevel Level { get; set; }

    public SecurityGuard(SecurityContext context, SecurityLevel level, ILogger? logger = null)
    {
        _blacklist = new Blacklist(context?.ExtraBlacklist ?? Array.Empty<string>());
        _sandbox = new PathSandbox(context ?? throw new ArgumentNullException(nameof(context)));
        _policy = new SecurityPolicy(_sandbox);
        Level = level;
        _logger = logger;
    }

    /// <summary>
    /// 检查单个工具调用。null=放行；ToolResult.Fail=拦截（Error 回灌 LLM）。
    /// 三层短路：黑名单命中即返回，沙箱/策略不再调；沙箱命中即返回，策略不再调。
    /// </summary>
    public Task<ToolResult?> CheckAsync(ToolCall call, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ToolResult? blocked = null;

        // ① 黑名单（始终生效，不依赖 Level）
        var (cmd, args) = ExtractCommand(call);
        if (cmd is not null)
        {
            var hit = _blacklist.Match(cmd, args);
            if (hit is not null)
            {
                blocked = ToolResult.Fail($"[黑名单] {hit.Reason}");
                _logger?.LogInformation("黑名单拦截工具 {Name}：{Reason}", call.Name, hit.Reason);
            }
        }

        // ② 路径沙箱（按 Level 收紧）
        if (blocked is null)
        {
            var path = ExtractPath(call);
            if (path is not null)
            {
                var result = _sandbox.Check(path, Level);
                if (!result.IsAllowed)
                {
                    blocked = ToolResult.Fail($"[路径沙箱] {result.Detail}");
                    _logger?.LogInformation("沙箱拦截工具 {Name}：{Detail}", call.Name, result.Detail);
                }
            }
        }

        // ③ 策略（档位相关扩展点，本迭代默认放行）
        blocked ??= _policy.Evaluate(call, Level);

        return Task.FromResult(blocked);
    }

    /// <summary>
    /// 从 ToolCall 提取 run_command 的 command/args。
    /// 非 run_command 工具返回 (null, null)，黑名单层直接跳过。
    /// </summary>
    private static (string? Command, string? Args) ExtractCommand(ToolCall call)
    {
        if (!string.Equals(call.Name, "run_command", StringComparison.Ordinal))
            return (null, null);
        var cmd = TryGetString(call.Input, "command");
        var args = TryGetString(call.Input, "args");
        return (cmd, args);
    }

    /// <summary>
    /// 从 ToolCall 提取 path 或 cwd 参数。
    /// 适用工具：read_file/write_file/edit_file（path）/ glob/grep（cwd）。
    /// 无 path/cwd 参数的工具返回 null，沙箱层跳过。
    /// </summary>
    private static string? ExtractPath(ToolCall call)
    {
        var path = TryGetString(call.Input, "path");
        if (path is not null) return path;
        return TryGetString(call.Input, "cwd");  // glob/grep 的 cwd
    }

    private static string? TryGetString(JsonElement input, string name)
    {
        if (input.ValueKind == JsonValueKind.Object &&
            input.TryGetProperty(name, out var prop) &&
            prop.ValueKind == JsonValueKind.String)
        {
            return prop.GetString();
        }
        return null;
    }
}
