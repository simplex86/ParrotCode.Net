using System.Text.Json;
using Spectre.Console;

namespace ParrotCode;

/// <summary>
/// 不接 LLM 的工具系统闭环演示。
/// 模拟"LLM 返回 tool_call → 执行 → 拿到结果"的完整流程，
/// 验证 ToolRegistry + ToolExecutor + 三个工具的端到端集成。
/// 调用方：手测时在 Program.cs 临时调用，或集成测试 ToolClosedLoopTests 直接验证。
/// </summary>
internal static class ClosedLoopDemo
{
    public static async Task RunAsync(CancellationToken cancellationToken)
    {
        // 1. 装配 ToolRegistry
        var registry = new ToolRegistry();
        registry.Register(new ReadFileTool());
        registry.Register(new WriteFileTool());
        registry.Register(new EditFileTool());

        // 2. 构造 ToolExecutor
        var executor = new ToolExecutor(registry, timeout: TimeSpan.FromSeconds(10));

        AnsiConsole.MarkupLine("[grey]=== 工具系统闭环 demo ===[/]");

        // 3. 模拟 LLM 返回的 ToolCall：write_file
        await RunOne(executor, "demo-write-1", "write_file",
            """{"path":"demo_output.txt","content":"hello\nworld\n"}""", cancellationToken);

        // 4. 模拟 LLM 返回的 ToolCall：read_file（读回刚写的）
        await RunOne(executor, "demo-read-1", "read_file",
            """{"path":"demo_output.txt"}""", cancellationToken);

        // 5. 模拟 LLM 返回的 ToolCall：edit_file（把 hello 改成 hi）
        await RunOne(executor, "demo-edit-1", "edit_file",
            """{"path":"demo_output.txt","old_text":"hello","new_text":"hi"}""", cancellationToken);

        // 6. 模拟 LLM 返回的 ToolCall：edit_file 唯一性失败（world 在文件中只出现一次但 o 出现多次）
        await RunOne(executor, "demo-edit-2", "edit_file",
            """{"path":"demo_output.txt","old_text":"o","new_text":"0"}""", cancellationToken);

        // 7. 模拟 LLM 返回的 ToolCall：未注册工具
        await RunOne(executor, "demo-unknown-1", "nonexistent_tool",
            "{}", cancellationToken);

        AnsiConsole.MarkupLine("[grey]=== demo 结束 ===[/]");
    }

    /// <summary>
    /// 执行单个 ToolCall 并打印结果。
    /// JsonDocument 用 using 限定在方法作用域内——
    /// JsonElement 借用 doc 的 buffer，doc Dispose 后 JsonElement 失效。
    /// executor.ExecuteAsync 内同步提取 string 参数，await 返回时 JsonElement 已不再被使用。
    /// 返回的 ToolResult 只含 plain string，doc Dispose 后仍可安全访问。
    /// </summary>
    private static async Task RunOne(ToolExecutor executor, string id, string name, string inputJson,
        CancellationToken cancellationToken)
    {
        using var doc = JsonDocument.Parse(inputJson);
        var call = new ToolCall(id, name, doc.RootElement);
        AnsiConsole.MarkupLine($"[cyan]→ tool_call:[/] {call.Name} (id={call.Id})");
        var result = await executor.ExecuteAsync(call, cancellationToken);
        PrintResult(result);
    }

    private static void PrintResult(ToolResult result)
    {
        if (result.Success)
        {
            AnsiConsole.MarkupLine($"[green]✓ 成功:[/] {Markup.Escape(result.Content)}");
        }
        else
        {
            AnsiConsole.MarkupLine($"[red]✗ 失败:[/] {Markup.Escape(result.Error ?? "(无错误信息)")}");
        }
    }
}
