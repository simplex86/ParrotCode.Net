using Spectre.Console;
using Spectre.Console.Rendering;

namespace ParrotCode;

/// <summary>
/// 控制台 I/O 抽象接口（迭代 7a 为 InputReader 可测试性引入）。
/// 让 InputReader 依赖抽象而非静态 Console/AnsiConsole，便于单元测试注入假实现。
/// SystemConsole 是生产实现，测试用 FakeConsole（在测试项目实现）。
/// </summary>
public interface IConsole
{
    /// <summary>读取一次按键（intercept=true 不回显）。</summary>
    ConsoleKeyInfo ReadKey(bool intercept);

    /// <summary>写入原始文本（不做 Markup 转义）。</summary>
    void Write(string text);

    /// <summary>写入换行。</summary>
    void WriteLine();

    /// <summary>写入 Spectre Markup 文本（支持颜色标记）。</summary>
    void WriteMarkup(string markup);

    /// <summary>写入 Spectre Markup 文本并换行。</summary>
    void WriteMarkupLine(string markup);

    /// <summary>写入 Spectre IRenderable（如 Panel/Rows/Markup 等）。</summary>
    void Write(IRenderable renderable);
}

/// <summary>
/// IConsole 的生产实现：委托静态 Console 与 AnsiConsole。
/// </summary>
internal sealed class SystemConsole : IConsole
{
    public ConsoleKeyInfo ReadKey(bool intercept) => Console.ReadKey(intercept);
    public void Write(string text) => Console.Write(text);
    public void WriteLine() => Console.WriteLine();
    public void WriteMarkup(string markup) => AnsiConsole.Markup(markup);
    public void WriteMarkupLine(string markup) => AnsiConsole.MarkupLine(markup);
    public void Write(IRenderable renderable) => AnsiConsole.Write(renderable);
}
