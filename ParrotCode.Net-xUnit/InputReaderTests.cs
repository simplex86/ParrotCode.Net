using System.Text;
using ParrotCode;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace ParrotCode.xUnit;

/// <summary>
/// InputReader 单元测试。
/// 用 FakeConsole 注入按键序列与捕获输出，验证 Tab 补全、Backspace、Enter/Esc、取消等行为。
/// </summary>
public class InputReaderTests
{
    /// <summary>
    /// 假控制台：按队列返回 ConsoleKeyInfo，捕获所有输出到 OutputBuilder。
    /// </summary>
    private sealed class FakeConsole : IConsole
    {
        private readonly Queue<ConsoleKeyInfo> _keys = new();
        private readonly StringBuilder _output = new();

        public string Output => _output.ToString();

        public void EnqueueKey(char ch)
        {
            _keys.Enqueue(new ConsoleKeyInfo(ch, ConsoleKey.None, false, false, false));
        }

        public void EnqueueKey(ConsoleKey key)
        {
            _keys.Enqueue(new ConsoleKeyInfo('\0', key, false, false, false));
        }

        public ConsoleKeyInfo ReadKey(bool intercept)
        {
            if (_keys.Count == 0)
                throw new InvalidOperationException("FakeConsole: 无可用按键（测试未注入足够输入）");
            return _keys.Dequeue();
        }

        public void Write(string text) => _output.Append(text);
        public void WriteLine() => _output.AppendLine();
        public void WriteMarkup(string markup) => _output.Append(markup);
        public void WriteMarkupLine(string markup) => _output.AppendLine(markup);
        public void Write(IRenderable renderable) => _output.Append(renderable.ToString() ?? string.Empty);
    }

    private static InputReader CreateReader(FakeConsole console, string[]? commands = null) =>
        new(console, commands);

    /// <summary>注入字符序列 + Enter，返回结果。</summary>
    private static async Task<string?> ReadAsync(FakeConsole console, InputReader reader)
    {
        return await reader.ReadLineWithCompletionAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Complete_UniquePrefix_FillsFull()
    {
        var console = new FakeConsole();
        // 输入 /cl + Tab + Enter
        console.EnqueueKey('/');
        console.EnqueueKey('c');
        console.EnqueueKey('l');
        console.EnqueueKey(ConsoleKey.Tab);
        console.EnqueueKey(ConsoleKey.Enter);

        var reader = CreateReader(console);
        var result = await ReadAsync(console, reader);

        result.Should().Be("/clear");
    }

    [Fact]
    public async Task Complete_MultipleMatches_ListsOptions()
    {
        var console = new FakeConsole();
        // 输入 / + Tab（匹配 /clear /exit /quit /help /status，5 个都是多匹配）
        console.EnqueueKey('/');
        console.EnqueueKey(ConsoleKey.Tab);
        console.EnqueueKey(ConsoleKey.Enter);

        var reader = CreateReader(console);
        var result = await ReadAsync(console, reader);

        // 列出的选项含 /clear /exit 等
        console.Output.Should().Contain("/clear");
        console.Output.Should().Contain("/exit");
        // buf 仍是 "/"（不填充）
        result.Should().Be("/");
    }

    [Fact]
    public async Task Complete_NoSlashPrefix_NoCompletion()
    {
        var console = new FakeConsole();
        // 输入 foo + Tab + Enter（非 / 开头，Tab 无效）
        console.EnqueueKey('f');
        console.EnqueueKey('o');
        console.EnqueueKey('o');
        console.EnqueueKey(ConsoleKey.Tab);
        console.EnqueueKey(ConsoleKey.Enter);

        var reader = CreateReader(console);
        var result = await ReadAsync(console, reader);

        result.Should().Be("foo");
    }

    [Fact]
    public async Task Complete_NonExistentCommand_NoMatch()
    {
        var console = new FakeConsole();
        // 输入 /xyz + Tab + Enter（无匹配命令）
        console.EnqueueKey('/');
        console.EnqueueKey('x');
        console.EnqueueKey('y');
        console.EnqueueKey('z');
        console.EnqueueKey(ConsoleKey.Tab);
        console.EnqueueKey(ConsoleKey.Enter);

        var reader = CreateReader(console);
        var result = await ReadAsync(console, reader);

        result.Should().Be("/xyz");
    }

    [Fact]
    public async Task Backspace_RemovesLastChar()
    {
        var console = new FakeConsole();
        // 输入 /cle + Backspace + Enter → buf == "/cl"
        console.EnqueueKey('/');
        console.EnqueueKey('c');
        console.EnqueueKey('l');
        console.EnqueueKey('e');
        console.EnqueueKey(ConsoleKey.Backspace);
        console.EnqueueKey(ConsoleKey.Enter);

        var reader = CreateReader(console);
        var result = await ReadAsync(console, reader);

        result.Should().Be("/cl");
    }

    [Fact]
    public async Task Enter_ReturnsBuffer()
    {
        var console = new FakeConsole();
        console.EnqueueKey('/');
        console.EnqueueKey('c');
        console.EnqueueKey('l');
        console.EnqueueKey('e');
        console.EnqueueKey('a');
        console.EnqueueKey('r');
        console.EnqueueKey(ConsoleKey.Enter);

        var reader = CreateReader(console);
        var result = await ReadAsync(console, reader);

        result.Should().Be("/clear");
    }

    [Fact]
    public async Task Escape_ReturnsNull()
    {
        var console = new FakeConsole();
        console.EnqueueKey('/');
        console.EnqueueKey('c');
        console.EnqueueKey(ConsoleKey.Escape);

        var reader = CreateReader(console);
        var result = await ReadAsync(console, reader);

        result.Should().BeNull();
    }

    [Fact]
    public async Task CancelledToken_ReturnsNull()
    {
        var console = new FakeConsole();
        var reader = CreateReader(console);

        var cts = new CancellationTokenSource();
        cts.Cancel();

        var result = await reader.ReadLineWithCompletionAsync(cts.Token);

        result.Should().BeNull();
    }

    [Fact]
    public async Task Backspace_OnEmptyBuffer_DoesNothing()
    {
        var console = new FakeConsole();
        // Backspace + Enter（空 buffer 时 Backspace 无效）
        console.EnqueueKey(ConsoleKey.Backspace);
        console.EnqueueKey(ConsoleKey.Enter);

        var reader = CreateReader(console);
        var result = await ReadAsync(console, reader);

        result.Should().Be("");
    }

    [Fact]
    public async Task PlainText_EnteredCorrectly()
    {
        var console = new FakeConsole();
        // 输入普通文本 "hello world"
        foreach (var ch in "hello world")
            console.EnqueueKey(ch);
        console.EnqueueKey(ConsoleKey.Enter);

        var reader = CreateReader(console);
        var result = await ReadAsync(console, reader);

        result.Should().Be("hello world");
    }

    [Fact]
    public async Task Complete_ExitCommand_FillsFull()
    {
        var console = new FakeConsole();
        // 输入 /ex + Tab → /exit + Enter
        console.EnqueueKey('/');
        console.EnqueueKey('e');
        console.EnqueueKey('x');
        console.EnqueueKey(ConsoleKey.Tab);
        console.EnqueueKey(ConsoleKey.Enter);

        var reader = CreateReader(console);
        var result = await ReadAsync(console, reader);

        result.Should().Be("/exit");
    }
}
