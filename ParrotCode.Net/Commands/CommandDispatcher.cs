namespace ParrotCode;

/// <summary>
/// 命令分发器：判断输入是否为命令，若是则查找并执行。
/// "/" 前缀 → 查 Registry → 执行 ICommand.ExecuteAsync
/// 非 "/" 前缀 → 返回 CommandResult.NotHandled（回退到 AI）
/// 命令未找到 → 返回 WithOutput("未知命令: xxx，输入 /help 查看可用命令")
/// 命令抛异常 → 返回 WithOutput("[!] 执行命令失败...")，不崩溃应用
/// </summary>
public sealed class CommandDispatcher
{
    private readonly CommandRegistry _registry;

    public CommandDispatcher(CommandRegistry registry) => _registry = registry ?? throw new ArgumentNullException(nameof(registry));

    public async Task<CommandResult> DispatchAsync(string line, CommandContext context, CancellationToken ct)
    {
        var parsed = CommandParser.Parse(line);
        if (parsed is null)
            return CommandResult.NotHandled;

        var (name, _) = parsed.Value;
        var command = _registry.Find(name);
        if (command is null)
            return CommandResult.WithOutput($"未知命令: /{name}，输入 /help 查看可用命令");

        var ctx = context with { RawInput = line, Ct = ct };

        try
        {
            return await command.ExecuteAsync(ctx);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return CommandResult.WithOutput($"[!] 执行命令 /{name} 失败：{ex.Message}");
        }
    }
}
