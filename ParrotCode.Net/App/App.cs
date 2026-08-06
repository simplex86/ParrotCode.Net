using System.Text;
using Microsoft.Extensions.Logging;
using Spectre.Console;

namespace ParrotCode;

/// <summary>
/// 主循环：读输入 → 调 Provider（流式）→ 逐字打印回复。
/// 维护 ConversationHistory 实现多轮上下文；/clear 清空历史。
/// 承载在独立类中，便于后续迭代替换 Provider / TUI 实现而不动 Main 的装配代码。
/// </summary>
internal sealed class App(IBaseProvider provider, ProviderConfig providerConfig, ILogger logger, CancellationToken ct)
{
    public async Task RunAsync()
    {
        var history = new ConversationHistory();

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

            // /clear：清空对话历史（最小实现，完整命令系统在迭代 10）
            if (line is "/clear")
            {
                history.Clear();
                AnsiConsole.MarkupLine("[grey]已清空对话历史。[/]");
                continue;
            }

            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            AnsiConsole.MarkupLine($"[grey]你：[/]{Markup.Escape(line)}");
            logger.LogInformation("调用 provider（流式），输入长度 {Len}", line.Length);

            // 先追加 user 消息到历史，再发送完整历史给 Provider
            history.AddUser(line);
            var messages = history.ToProviderMessages();

            try
            {
                AnsiConsole.Markup("[green]AI：[/]");
                // 流式逐字输出：Console.Write 直接写 stdout，不经过 Spectre 缓冲
                // 同时用 StringBuilder 收集完整回复，结束后追加到历史
                var replyBuilder = new StringBuilder();
                await foreach (var token in provider.ChatStreamAsync(messages, ct))
                {
                    Console.Write(token);
                    replyBuilder.Append(token);
                }
                Console.WriteLine();  // 回复结束换行

                // 流式正常结束后，追加完整 assistant 回复到历史
                history.AddAssistant(replyBuilder.ToString());
                logger.LogInformation("本轮结束，历史 {Count} 条消息，约 {Tokens} tokens", history.Count, history.EstimatedTokens);
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
                // user 消息已在历史中，但无 assistant 回复——语义正确（问了但没答上）
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
