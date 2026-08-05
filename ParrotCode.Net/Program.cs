using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Console;
using ParrotCode;
using Spectre.Console;

// Ctrl+C 触发取消令牌，由主循环优雅退出
var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, evt) =>
{
    evt.Cancel = true; // 阻止默认终止
    cts.Cancel();
};

using var loggerFactory = LoggerFactory.Create(builder =>
{
    builder.AddSimpleConsole(options =>
    {
        options.UseUtcTimestamp = true;
        options.SingleLine = true;
        options.TimestampFormat = "HH:mm:ss ";
    });
    // 把所有日志路由到 stderr（Console.Error），与用户可见输出（stdout/Spectre）分离。
    builder.Services.Configure<ConsoleLoggerOptions>(o => o.LogToStandardErrorThreshold = LogLevel.Trace);
    builder.SetMinimumLevel(LogLevel.Information);
});
var logger = loggerFactory.CreateLogger("ParrotCode");

AppConfig config;
try
{
    config = ConfigLoader.Load();
}
catch (ConfigException ex)
{
    AnsiConsole.MarkupLine($"[red]配置错误：[/]{Markup.Escape(ex.Message)}");
    if (ex.SourcePath is not null)
        AnsiConsole.MarkupLine($"[grey]  文件：{Markup.Escape(ex.SourcePath)}[/]");
    if (ex.Line is not null)
        AnsiConsole.MarkupLine($"[grey]  行：{ex.Line}{(ex.Column is null ? "" : $"，列：{ex.Column}")}[/]");
    return 1;
}

ProviderConfig activeConfig;
IBaseProvider provider;
try
{
    provider = ProviderFactory.CreateActive(config);
    // CreateActive 返回 IBaseProvider 但不返回选中的 ProviderConfig；App 启动横幅需要它，外部解析。
    var activeName = config.ActiveProvider ?? config.Providers[0].Name;
    activeConfig = config.Providers.First(p => p.Name == activeName);
}
catch (ProviderNotImplementedException ex)
{
    AnsiConsole.MarkupLine($"[yellow]提示：[/]{Markup.Escape(ex.Message)}");
    return 1;
}
catch (ConfigException ex)
{
    AnsiConsole.MarkupLine($"[red]配置错误：[/]{Markup.Escape(ex.Message)}");
    return 1;
}
catch (ArgumentException ex)
{
    AnsiConsole.MarkupLine($"[red]配置错误：[/]{Markup.Escape(ex.Message)}");
    return 1;
}

logger.LogInformation("使用 provider={Name} model={Model} protocol={Protocol}",
                      activeConfig.Name, 
                      activeConfig.Model, 
                      activeConfig.Protocol);  // 注意：不记 ApiKey

var app = new App(provider, activeConfig, logger, cts.Token);
await app.RunAsync();

return 0;