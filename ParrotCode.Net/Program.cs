using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Console;
using ParrotCode;

// Ctrl+C 触发取消令牌，由主循环优雅退出
var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true; // 阻止默认终止
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
    // LogToStandardErrorThreshold 表示 >= 该级别的日志写 stderr，设为 Trace 即全部走 stderr。
    builder.Services.Configure<ConsoleLoggerOptions>(o => o.LogToStandardErrorThreshold = LogLevel.Trace);
    builder.SetMinimumLevel(LogLevel.Information);
});
var logger = loggerFactory.CreateLogger("ParrotCode");

// 迭代 2a：硬编码 ProviderConfig 装配。迭代 2b 改由 ConfigLoader 从 YAML 加载。
var providerConfig = new ProviderConfig
{
    Name = "mock",
    Protocol = "mock",
    Model = "mock-1"
};
var provider = ProviderFactory.Create(providerConfig);
logger.LogInformation("使用 provider={Name} model={Model} protocol={Protocol}",
    providerConfig.Name, providerConfig.Model, providerConfig.Protocol);

var app = new App(provider, providerConfig, logger, cts.Token);
await app.RunAsync();
