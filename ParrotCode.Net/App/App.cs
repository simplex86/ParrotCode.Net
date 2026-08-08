using System.IO;
using Microsoft.Extensions.Logging;

namespace ParrotCode;

/// <summary>
/// 主应用入口：装配 TerminalApp（Terminal.Gui v2）。
/// 迭代 7a/7b：旧 TuiApp（Spectre.Console）已删除。
/// 迭代 7c-1/2/3：TerminalApp 接管全部 UI，HITL 模态对话框 + Spinner + 流式渲染。
/// 迭代 8c：构造 SecurityContext + SecurityGuard，传入 TerminalApp 装配 SecureBatchToolExecutor。
/// </summary>
internal sealed class App
{
    private readonly IBaseProvider _provider;
    private readonly ProviderConfig _providerConfig;
    private readonly AppConfig _config;
    private readonly ILogger _logger;
    private readonly CancellationToken _ct;

    public App(IBaseProvider provider,
               ProviderConfig providerConfig,
               AppConfig config,
               ILogger logger,
               CancellationToken ct)
    {
        _provider = provider ?? throw new ArgumentNullException(nameof(provider));
        _providerConfig = providerConfig ?? throw new ArgumentNullException(nameof(providerConfig));
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _logger = logger;
        _ct = ct;
    }

    public async Task RunAsync()
    {
        var tuiConfig = _config.Tui ?? new TuiConfig();
        var securityLevel = ParseSecurityLevel(_config.Security?.Level);

        // 【迭代 8c】构造 SecurityContext + SecurityGuard，传入 TerminalApp
        // 项目根 = 当前工作目录（由调用方/用户在启动时确定）
        var projectRoot = Directory.GetCurrentDirectory();
        var secCtx = new SecurityContext
        {
            ProjectRoot = projectRoot,
            AllowPaths = NormalizePaths(_config.Security?.AllowPaths, projectRoot),
            DenyPaths = NormalizePaths(_config.Security?.DenyPaths, projectRoot),
            // IList<string> → IReadOnlyList<string>：ToReadOnly 通过数组转换
            ExtraBlacklist = (_config.Security?.ExtraBlacklist ?? Array.Empty<string>()).ToArray()
        };
        // SecurityGuard 始终构造（即使无 security 配置，也用 Normal + 空白名单 + 硬编码黑名单）
        // 保证黑名单层至少生效（防 rm -rf /）
        var securityGuard = new SecurityGuard(secCtx, securityLevel, _logger);

        using var terminalApp = new TerminalApp(_provider,
                                                _providerConfig,
                                                _config.Agent,
                                                tuiConfig,
                                                securityLevel,
                                                securityGuard,
                                                _logger,
                                                _ct);
        await terminalApp.RunAsync();
    }

    /// <summary>
    /// 解析安全等级字符串为 SecurityLevel 枚举。
    /// 委托 SecurityLevelParser.Parse（迭代 8a 抽取，便于单测）。
    /// </summary>
    private static SecurityLevel ParseSecurityLevel(string? level) => SecurityLevelParser.Parse(level);

    /// <summary>
    /// 规范化路径列表（相对→绝对，基于 projectRoot）。
    /// 非法路径（含非法字符等）忽略，不抛异常（避免启动失败）。
    /// internal 便于单测验证（08c-05 / 08c-10）。
    /// </summary>
    internal static IReadOnlyList<string> NormalizePaths(IList<string>? paths, string projectRoot)
    {
        if (paths is null || paths.Count == 0) return Array.Empty<string>();
        var result = new List<string>(paths.Count);
        foreach (var p in paths)
        {
            if (string.IsNullOrWhiteSpace(p)) continue;
            try
            {
                result.Add(Path.GetFullPath(p, projectRoot));
            }
            catch
            {
                // 非法路径忽略（避免启动失败）。生产环境可记 warning 日志。
            }
        }
        return result;
    }
}
