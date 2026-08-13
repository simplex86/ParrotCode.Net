using Microsoft.Extensions.Logging;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace ParrotCode;

/// <summary>
/// Hook 规则加载器：两级 YAML 加载 + 集中校验 + 枚举字符串解析。
/// 加载顺序（两者合并，都执行——project 不覆盖 global）：
/// 1. 全局 ~/.parrotcode/hooks.yaml
/// 2. 项目 ./.parrotcode/hooks.yaml
///
/// 配置文件格式（YAML，枚举值用 snake_case）：
/// hooks:
///   - name: git-stash-before-write
///     event: tool_pre_exec          # snake_case 字符串，Loader 解析为 HookEvent.ToolPreExec
///     condition:
///       match: ALL                  # 字符串，Loader 解析为 HookMatchMode.All
///       rules:
///         - field: tool_name
///           operator: exact         # 字符串，Loader 解析为 HookOperator.Exact
///           value: write_file
///     actions:
///       - type: shell               # 字符串，Loader 解析为 HookActionType.Shell
///         command: "git stash"
///     control:
///       once: false
///       async: false
///       timeout: 30
/// </summary>
public sealed class HookLoader
{
    private readonly string _projectRoot;
    private readonly string _userHome;
    private readonly ILogger? _logger;

    public HookLoader(string? projectRoot = null,
                      string? userHome = null,
                      ILogger? logger = null)
    {
        _projectRoot = projectRoot ?? Directory.GetCurrentDirectory();
        _userHome = userHome ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        _logger = logger;
    }

    /// <summary>
    /// 加载所有 Hook 规则（全局 + 项目合并）。
    /// 单个文件解析失败记录日志跳过，不中断整体加载。
    /// </summary>
    public IReadOnlyList<HookRule> Load()
    {
        var rules = new List<HookRule>();

        var globalPath = Path.Combine(_userHome, ".parrotcode", "hooks.yaml");
        var projectPath = Path.Combine(_projectRoot, ".parrotcode", "hooks.yaml");

        rules.AddRange(LoadFile(globalPath, "global"));
        rules.AddRange(LoadFile(projectPath, "project"));

        if (rules.Count > 0)
            _logger?.LogInformation("已加载 {Count} 条 Hook 规则", rules.Count);

        return rules;
    }

    private IReadOnlyList<HookRule> LoadFile(string path, string source)
    {
        if (!File.Exists(path))
            return Array.Empty<HookRule>();

        try
        {
            var raw = File.ReadAllText(path);
            var deserializer = new DeserializerBuilder()
                .WithNamingConvention(UnderscoredNamingConvention.Instance)
                .Build();

            var doc = deserializer.Deserialize<HookConfigFile>(raw);
            if (doc?.Hooks is null || doc.Hooks.Count == 0)
                return Array.Empty<HookRule>();

            var rules = new List<HookRule>(doc.Hooks.Count);
            for (var i = 0; i < doc.Hooks.Count; i++)
            {
                try
                {
                    var rule = ValidateAndNormalize(doc.Hooks[i], i);
                    rules.Add(rule);
                }
                catch (HookConfigException ex)
                {
                    _logger?.LogWarning("Hook 规则 [{Source}#{Index}] 无效：{Error}", source, i, ex.Message);
                }
            }

            return rules;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning("Hook 文件 [{Path}] 解析失败：{Error}", path, ex.Message);
            return Array.Empty<HookRule>();
        }
    }

    /// <summary>
    /// 校验并规范化单条规则。
    ///
    /// 校验项：
    /// - event 必填且有效（snake_case → PascalCase → 枚举）
    /// - actions 至少一个，每个 action 的 type 必须有效
    /// - shell 动作必须有 command
    /// - prompt_inject 动作必须有 text
    /// - http 动作必须有 url
    /// - sub_agent 动作必须有 task
    /// - 拦截事件不允许 async=true
    /// - name 为空时自动生成 "rule-{index}"
    /// - condition 中的 match / operator 字符串解析为枚举
    /// </summary>
    private static HookRule ValidateAndNormalize(HookRule rule, int index)
    {
        // name 默认值
        if (string.IsNullOrWhiteSpace(rule.Name))
            rule.Name = $"rule-{index}";

        // event 解析（snake_case → PascalCase → 枚举）
        if (string.IsNullOrWhiteSpace(rule.Event))
            throw new HookConfigException("event 字段不能为空");

        if (!Enum.TryParse<HookEvent>(SnakeToPascal(rule.Event), out var evt))
            throw new HookConfigException($"无效的 event 值: '{rule.Event}'");
        rule.EventType = evt;

        // actions 校验 + type 解析
        if (rule.Actions.Count == 0)
            throw new HookConfigException("至少需要一个 action");

        foreach (var action in rule.Actions)
        {
            if (!Enum.TryParse<HookActionType>(SnakeToPascal(action.Type), out var at))
                throw new HookConfigException($"无效的 action type: '{action.Type}'");
            action.ActionType = at;

            switch (action.ActionType)
            {
                case HookActionType.Shell when string.IsNullOrWhiteSpace(action.Command):
                    throw new HookConfigException($"action type 'shell' 缺少 'command' 字段");
                case HookActionType.PromptInject when string.IsNullOrWhiteSpace(action.Text):
                    throw new HookConfigException($"action type 'prompt_inject' 缺少 'text' 字段");
                case HookActionType.Http when string.IsNullOrWhiteSpace(action.Url):
                    throw new HookConfigException($"action type 'http' 缺少 'url' 字段");
                case HookActionType.SubAgent when string.IsNullOrWhiteSpace(action.Task):
                    throw new HookConfigException($"action type 'sub_agent' 缺少 'task' 字段");
            }
        }

        // 拦截事件不允许 async
        if (rule.IsIntercept && rule.Control.Async)
            throw new HookConfigException($"拦截事件 '{rule.Event}' 不允许 async=true");

        // condition 中的 match / operator 解析
        if (rule.Condition is not null)
        {
            if (!Enum.TryParse<HookMatchMode>(SnakeToPascal(rule.Condition.Match), out var mm))
                throw new HookConfigException($"无效的 match 值: '{rule.Condition.Match}'");
            rule.Condition.MatchMode = mm;

            foreach (var cr in rule.Condition.Rules)
            {
                if (!Enum.TryParse<HookOperator>(SnakeToPascal(cr.Operator), out var op))
                    throw new HookConfigException($"无效的 operator 值: '{cr.Operator}'");
                cr.OperatorEnum = op;
            }
        }

        return rule;
    }

    /// <summary>
    /// snake_case → PascalCase 转换。
    /// tool_pre_exec → ToolPreExec
    /// exact → Exact
    /// prompt_inject → PromptInject
    /// </summary>
    private static string SnakeToPascal(string snake)
    {
        if (string.IsNullOrEmpty(snake)) return snake;
        var parts = snake.Split('_');
        return string.Concat(parts.Select(p => p.Length == 0 ? "" : char.ToUpperInvariant(p[0]) + p[1..].ToLowerInvariant()));
    }

    private sealed class HookConfigFile
    {
        public List<HookRule>? Hooks { get; set; }
    }
}
