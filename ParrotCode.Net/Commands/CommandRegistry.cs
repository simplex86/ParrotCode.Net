using System.Reflection;
using Microsoft.Extensions.Logging;

namespace ParrotCode;

/// <summary>
/// 命令注册中心：管理所有已注册的 ICommand。
/// 支持手动注册 + 反射自动扫描程序集中所有 ICommand 实现类。
/// 别名冲突检测：注册时检查 Name 和所有 Aliases 是否已被占用。
/// </summary>
public sealed class CommandRegistry
{
    private readonly Dictionary<string, ICommand> _commands = new(StringComparer.OrdinalIgnoreCase);
    private readonly ILogger? _logger;

    public CommandRegistry(ILogger? logger = null) => _logger = logger;

    /// <summary>
    /// 已注册的命令数（含别名不重复计算）。
    /// </summary>
    public int Count => _commands.Values.Distinct().Count();

    /// <summary>
    /// 手动注册命令。Name 和所有 Aliases 必须唯一，冲突抛 InvalidOperationException。
    /// </summary>
    public void Register(ICommand command)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (_commands.ContainsKey(command.Name))
        {
            var existing = _commands[command.Name];
            throw new InvalidOperationException($"命令名 '{command.Name}' 冲突：已由 {existing.GetType().Name} 注册");
        }

        foreach (var alias in command.Aliases)
        {
            if (_commands.ContainsKey(alias))
            {
                var existing = _commands[alias];
                throw new InvalidOperationException($"别名 '{alias}' 冲突：已由 {existing.GetType().Name} 注册");
            }
        }

        _commands[command.Name] = command;
        foreach (var alias in command.Aliases)
            _commands[alias] = command;
    }

    /// <summary>
    /// 反射自动扫描程序集中所有 ICommand 实现类并注册。
    /// 跳过接口和抽象类，用无参构造函数实例化。
    /// 已注册的（按 Name 判断）跳过——支持"手动注册后再自动扫描"模式。
    /// </summary>
    public void AutoRegisterFromAssembly(Assembly? assembly = null)
    {
        assembly ??= Assembly.GetExecutingAssembly();
        var commandTypes = assembly.GetTypes()
                                   .Where(t => typeof(ICommand).IsAssignableFrom(t) && 
                                               t is { IsInterface: false, IsAbstract: false } && 
                                               t.GetConstructor(Type.EmptyTypes) is not null);

        foreach (var type in commandTypes)
        {
            try
            {
                var command = (ICommand)Activator.CreateInstance(type)!;
                // 已手动注册的跳过（如 HelpCommand）
                if (_commands.ContainsKey(command.Name))
                {
                    _logger?.LogDebug("命令 {Name} 已手动注册，跳过自动扫描", command.Name);
                    continue;
                }
                Register(command);
                _logger?.LogDebug("自动注册命令 {Name} ({Type})", command.Name, type.Name);
            }
            catch (InvalidOperationException ex)
            {
                _logger?.LogWarning(ex, "自动注册命令 {Type} 失败（可能已手动注册）", type.Name);
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "自动注册命令 {Type} 失败", type.Name);
            }
        }
    }

    public ICommand? Find(string nameOrAlias) => _commands.TryGetValue(nameOrAlias, out var cmd) ? cmd : null;

    public IReadOnlyList<ICommand> GetAll() => _commands.Values.Distinct().ToList();

    /// <summary>
    /// 获取所有命令名（含别名），供 Tab 补全用。
    /// </summary>
    public IReadOnlyList<string> GetAllNamesWithAliases() => _commands.Keys.ToList();

    public IReadOnlyList<ICommand> GetVisibleCommands() => GetAll().Where(c => c.Type == CommandType.System).ToList();
}
