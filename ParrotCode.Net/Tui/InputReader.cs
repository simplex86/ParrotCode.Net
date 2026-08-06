using System.Text;
using Spectre.Console;

namespace ParrotCode;

/// <summary>
/// 带 Tab 补全的输入读取器。
/// 遇 / 开头按 Tab 补全硬编码命令列表。Enter 提交，Esc 取消，Backspace 删除。
/// 迭代 10 命令系统完善后，命令列表来自 Registry。
///
/// 注意：此方法不在 Live 期间调用（Live 与 Console.ReadKey 互斥）。
/// TuiApp 在 Live 结束后（IsCompletingEvent 提交后）调用此方法读输入。
///
/// 依赖 IConsole 抽象（非静态 Console）便于单元测试。
/// </summary>
public sealed class InputReader
{
    private readonly IConsole _console;
    private readonly string[] _commands;

    public InputReader(IConsole? console = null, string[]? commands = null)
    {
        _console = console ?? new SystemConsole();
        _commands = commands ?? new[] { "/clear", "/exit", "/quit", "/help", "/status" };
    }

    /// <summary>
    /// 读取一行输入，支持 Tab 补全。
    /// 返回用户输入的字符串（Enter 提交）；Esc 取消或 CancellationToken 取消时返回 null。
    /// </summary>
    public async Task<string?> ReadLineWithCompletionAsync(CancellationToken ct)
    {
        if (ct.IsCancellationRequested) return null;

        var buf = new StringBuilder();
        _console.WriteMarkup("[bold blue]> [/]");

        while (!ct.IsCancellationRequested)
        {
            ConsoleKeyInfo key;
            try
            {
                // Console.ReadKey 是同步阻塞调用，包成 Task 让 await 能响应 CancellationToken。
                key = await Task.Run(() => _console.ReadKey(true), ct);
            }
            catch (OperationCanceledException)
            {
                return null;
            }

            if (key.Key == ConsoleKey.Enter)
            {
                _console.WriteLine();
                return buf.ToString();
            }
            if (key.Key == ConsoleKey.Escape)
            {
                _console.WriteLine();
                return null;  // 取消
            }
            if (key.Key == ConsoleKey.Tab && buf.Length > 0 && buf[0] == '/')
            {
                var prefix = buf.ToString();
                var matches = _commands
                    .Where(c => c.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    .ToArray();
                if (matches.Length == 1)
                {
                    // 唯一匹配——填充（清行重写）
                    buf.Clear();
                    buf.Append(matches[0]);
                    RedrawLine(buf);
                }
                else if (matches.Length > 1)
                {
                    // 多匹配——列选项（不填充），重绘当前行
                    _console.WriteLine();
                    _console.WriteMarkupLine(string.Join("  ", matches.Select(m => $"[cyan]{Markup.Escape(m)}[/]")));
                    RedrawLine(buf);
                }
                // 无匹配——不做任何事
                continue;
            }
            if (key.Key == ConsoleKey.Backspace && buf.Length > 0)
            {
                buf.Remove(buf.Length - 1, 1);
                RedrawLine(buf);
                continue;
            }
            if (!char.IsControl(key.KeyChar))
            {
                buf.Append(key.KeyChar);
                var color = buf[0] == '/' ? "cyan" : "white";
                _console.WriteMarkup($"[{color}]{Markup.Escape(key.KeyChar.ToString())}[/]");
            }
        }
        return null;
    }

    /// <summary>
    /// 清除当前行并重绘 prompt + buf。
    /// 用 ANSI \x1b[2K（清整行）+ \r（回到行首）+ 重绘。
    /// 不用空格覆盖——宽字符（中文/emoji）占多列，字符数≠列数，空格数难精确计算。
    /// \x1b[2K 清除整行所有内容（不管列数），Spectre.Console 已依赖 ANSI，终端必支持。
    /// </summary>
    private void RedrawLine(StringBuilder buf)
    {
        _console.Write("\r\x1b[2K");
        var color = buf.Length > 0 && buf[0] == '/' ? "cyan" : "white";
        _console.WriteMarkup($"[bold blue]> [/][{color}]{Markup.Escape(buf.ToString())}[/]");
    }
}
