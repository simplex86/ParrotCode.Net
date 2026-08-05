using Microsoft.Extensions.Logging;
using Spectre.Console;

namespace ParrotCode
{
    /// <summary>
    /// 主循环：读输入 → 调 Provider → 打印回复。承载在独立类中，
    /// 便于后续迭代替换 Provider / TUI 实现而不动 Main 的装配代码。
    /// </summary>
    internal sealed class App(IChatProvider provider, ILogger logger, CancellationToken ct)
    {
        public async Task RunAsync()
        {
            AnsiConsole.MarkupLine("[grey]ParrotCode.Net[/] [green]mock 模式[/]。输入 exit 退出。");

            while (!ct.IsCancellationRequested)
            {
                AnsiConsole.Markup("[bold blue]> [/]");
                var line = Console.ReadLine();
                if (line is null)
                {
                    break; // EOF（Ctrl+Z / 管道关闭）
                }

                if (line is "exit" or "quit")
                {
                    break;
                }

                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                AnsiConsole.MarkupLine($"[grey]你：[/]{Markup.Escape(line)}");
                logger.LogInformation("调用 provider，输入长度 {Len}", line.Length);

                try
                {
                    var reply = await provider.ChatAsync(line, ct);
                    AnsiConsole.MarkupLine($"[green]AI：[/]{Markup.Escape(reply)}");
                }
                catch (OperationCanceledException)
                {
                    AnsiConsole.MarkupLine("\n[grey]已取消。[/]");
                    break;
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "provider 调用失败");
                    AnsiConsole.MarkupLine($"[red]错误：[/]{Markup.Escape(ex.Message)}");
                }
            }

            logger.LogInformation("程序退出");
        }
    }
}
