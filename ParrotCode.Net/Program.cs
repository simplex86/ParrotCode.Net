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

var provider = new MockProvider();
var app = new App(provider, logger, cts.Token);
await app.RunAsync();
