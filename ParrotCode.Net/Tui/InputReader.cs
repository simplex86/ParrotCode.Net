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
                    var oldScreenLen = buf.Length + 2;  // 旧 buffer + "> "
                    buf.Clear();
                    buf.Append(matches[0]);
                    RedrawLine(oldScreenLen, buf);
                }
                else if (matches.Length > 1)
                {
                    // 多匹配——列选项（不填充），重绘当前行
                    _console.WriteLine();
                    _console.WriteMarkupLine(string.Join("  ", matches.Select(m => $"[cyan]{Markup.Escape(m)}[/]")));
                    RedrawLine(buf.Length + 2, buf);
                }
                // 无匹配——不做任何事
                continue;
            }
            if (key.Key == ConsoleKey.Backspace && buf.Length > 0)
            {
                // 删除前屏幕长度 = "> " (2) + buf.Length（含将被删除的字符）
                // RedrawLine 会先回退 oldScreenLen 个字符再重绘 "> " + buf（删除后）
                var oldScreenLen = buf.Length + 2;  // 旧 buffer（含将删除的字符） + "> "
                buf.Remove(buf.Length - 1, 1);
                RedrawLine(oldScreenLen, buf);
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
    /// 用 \r 回到行首 + 输出与旧内容等长的空格覆盖旧字符 + 再 \r 回到行首 + 重绘。
    /// 不用 "\b \b" 循环——中文字符在终端占 2 列但 \b 只退 1 列，
    /// 退格中文会留下半字符残骸或提示符残留。
    /// oldScreenLen 是旧屏幕长度（字符数），用于计算覆盖空格数；
    /// 对宽字符场景会多覆盖几列空格，无害（行首对齐后多余空格被新内容覆盖或行尾）。
    /// </summary>
    private void RedrawLine(int oldScreenLen, StringBuilder buf)
    {
        // 回到行首，用空格覆盖旧内容（多覆盖一些防宽字符残留），再回到行首重绘
        _console.Write("\r");
        _console.Write(new string(' ', oldScreenLen + 4));  // +4 余量防宽字符残留
        _console.Write("\r");
        var color = buf.Length > 0 && buf[0] == '/' ? "cyan" : "white";
        _console.WriteMarkup($"[bold blue]> [/][{color}]{Markup.Escape(buf.ToString())}[/]");
    }
}
