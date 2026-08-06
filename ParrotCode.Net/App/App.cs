using Microsoft.Extensions.Logging;

namespace ParrotCode;

/// <summary>
/// 主应用入口：委托 TuiApp（Live/降级行模式由 TuiApp 内部检测决定）。
/// 迭代 7a：渲染逻辑迁移到 TuiApp + EventRenderer + ConsoleEventRenderer，App 仅做装配。
/// 迭代 7b：从 SecurityConfig.Level 解析 SecurityLevel 传给 TuiApp（仅状态栏显示，不拦截）。
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
        var tuiApp = new TuiApp(_provider,
                                _providerConfig,
                                _config.Agent,
                                tuiConfig,
                                securityLevel,
                                _logger,
                                _ct);
        await tuiApp.RunAsync();
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
