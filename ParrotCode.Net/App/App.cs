using Microsoft.Extensions.Logging;

namespace ParrotCode;

/// <summary>
/// 主应用入口：装配 TerminalApp（Terminal.Gui v2）。
/// 迭代 7a/7b：旧 TuiApp（Spectre.Console）已删除。
/// 迭代 7c-1/2/3：TerminalApp 接管全部 UI，HITL 模态对话框 + Spinner + 流式渲染。
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
        // 7b：从 SecurityConfig.Level 解析 SecurityLevel（仅状态栏显示，迭代 8 接入真实拦截）
        var securityLevel = ParseSecurityLevel(_config.Security?.Level);

        // 7c-3：旧 TuiApp 已删除，统一走 TerminalApp
        using var terminalApp = new TerminalApp(_provider,
                                                _providerConfig,
                                                _config.Agent,
                                                tuiConfig,
                                                securityLevel,
                                                _logger,
                                                _ct);
        await terminalApp.RunAsync();
    }

    /// <summary>
    /// 解析安全等级字符串为 SecurityLevel 枚举。
    /// 7b 仅状态栏显示，不做真实拦截（迭代 8 接入）。
    /// 大小写不敏感；未配置或无效值默认 Normal。
    /// </summary>
    private static SecurityLevel ParseSecurityLevel(string? level) => level?.ToLowerInvariant() switch
    {
        "strict" => SecurityLevel.Strict,
        "permissive" or "permisive" => SecurityLevel.Permisive,  // 兼容 7a 拼写
        _ => SecurityLevel.Normal  // 默认 Normal
    };
}
