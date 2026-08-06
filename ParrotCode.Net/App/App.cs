using Microsoft.Extensions.Logging;

namespace ParrotCode;

/// <summary>
/// 主应用入口：委托 TuiApp（Live/降级行模式由 TuiApp 内部检测决定）。
/// 迭代 7a：渲染逻辑迁移到 TuiApp + EventRenderer + ConsoleEventRenderer，App 仅做装配。
/// </summary>
internal sealed class App
{
    private readonly IBaseProvider _provider;
    private readonly ProviderConfig _providerConfig;
    private readonly AppConfig _config;
    private readonly ILogger _logger;
    private readonly CancellationToken _ct;

    public App(
        IBaseProvider provider,
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
        var tuiApp = new TuiApp(
            _provider,
            _providerConfig,
            _config.Agent,
            tuiConfig,
            SecurityLevel.Normal,  // 7a 硬编码 Normal，迭代 8 加配置项
            _logger,
            _ct);
        await tuiApp.RunAsync();
    }
}
