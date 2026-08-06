using Microsoft.Extensions.Logging;
using Spectre.Console;

namespace ParrotCode
{
    /// <summary>
    /// 主循环：读输入 → 调 Provider（流式）→ 逐字打印回复。
    /// 承载在独立类中，便于后续迭代替换 Provider / TUI 实现而不动 Main 的装配代码。
    /// </summary>
    internal sealed class App(IBaseProvider provider, ProviderConfig providerConfig,
        ILogger logger, CancellationToken ct)
    {
        public async Task RunAsync()
        {
            AnsiConsole.MarkupLine(
                $"[grey]ParrotCode.Net[/] {(providerConfig.Protocol == "mock"
                    ? "[green]mock 模式[/]"
                    : "[green]stream 模式[/]")} | " +
                $"provider=[cyan]{Markup.Escape(providerConfig.Name)}[/] " +
                $"model=[cyan]{Markup.Escape(providerConfig.Model)}[/] " +
                $"protocol=[cyan]{Markup.Escape(providerConfig.Protocol)}[/]");

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
                logger.LogInformation("调用 provider（流式），输入长度 {Len}", line.Length);

                try
                {
                    var messages = new[] { new Message(MessageRole.User, line) };
                    AnsiConsole.Markup("[green]AI：[/]");
                    // 流式逐字输出：Console.Write 直接写 stdout，不经过 Spectre 缓冲
                    await foreach (var token in provider.ChatStreamAsync(messages, ct))
                    {
                        Console.Write(token);
                    }
                    Console.WriteLine();  // 回复结束换行
                }
                catch (OperationCanceledException)
                {
                    AnsiConsole.MarkupLine("\n[grey]已取消。[/]");
                    break;
                }
                catch (ProviderAuthException ex)
                {
                    AnsiConsole.MarkupLine($"[red]认证失败：[/]{Markup.Escape(ex.Message)}");
                    AnsiConsole.MarkupLine("[grey]请检查 api_key 配置。[/]");
                }
                catch (ProviderRateLimitException ex)
                {
                    AnsiConsole.MarkupLine($"[yellow]请求过快：[/]{Markup.Escape(ex.Message)}");
                    AnsiConsole.MarkupLine("[grey]请稍后重试。[/]");
                }
                catch (ProviderException ex)
                {
                    AnsiConsole.MarkupLine($"[red]Provider 错误：[/]{Markup.Escape(ex.Message)}");
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
