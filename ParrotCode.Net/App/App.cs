using System.IO;
using Microsoft.Extensions.Logging;

namespace ParrotCode;

/// <summary>
/// 主应用入口：装配 TerminalApp（Terminal.Gui v2）。
/// 迭代 7a/7b：旧 TuiApp（Spectre.Console）已删除。
/// 迭代 7c-1/2/3：TerminalApp 接管全部 UI，HITL 模态对话框 + Spinner + 流式渲染。
/// 迭代 8c：构造 SecurityContext + SecurityGuard，传入 TerminalApp 装配 SecureBatchToolExecutor。
/// </summary>
internal sealed class App
{
    private readonly IBaseProvider _provider;
    private readonly ProviderConfig _providerConfig;
    private readonly AppConfig _config;
    private readonly ILogger _logger;
    private readonly CancellationToken _ct;

    public App(IBaseProvider provider,
               ProviderConfig providerConfig,
               AppConfig config,
               ILogger logger,
               CancellationToken ct)
    {
        _provider = provider ?? throw new ArgumentNullException(nameof(provider));
        _providerConfig = providerConfig ?? throw new ArgumentNullException(nameof(providerConfig));
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _logger = logger;
        _ct = ct;
    }

    public async Task RunAsync()
    {
        var tuiConfig = _config.Tui ?? new TuiConfig();
        var securityLevel = ParseSecurityLevel(_config.Security?.Level);

        // 【迭代 8c】构造 SecurityContext + SecurityGuard，传入 TerminalApp
        // 项目根 = 当前工作目录（由调用方/用户在启动时确定）
        var projectRoot = Directory.GetCurrentDirectory();
        var secCtx = new SecurityContext
        {
            ProjectRoot = projectRoot,
            AllowPaths = NormalizePaths(_config.Security?.AllowPaths, projectRoot),
            DenyPaths = NormalizePaths(_config.Security?.DenyPaths, projectRoot),
            // IList<string> → IReadOnlyList<string>：ToReadOnly 通过数组转换
            ExtraBlacklist = (_config.Security?.ExtraBlacklist ?? Array.Empty<string>()).ToArray()
        };
        // SecurityGuard 始终构造（即使无 security 配置，也用 Normal + 空白名单 + 硬编码黑名单）
        // 保证黑名单层至少生效（防 rm -rf /）
        var securityGuard = new SecurityGuard(secCtx, securityLevel, _logger);

        // 【迭代 9】构造 ContextCompressor（上下文管理：层 1 截断 + 层 2 摘要）
        // 始终创建——层 1 截断始终生效；enable_auto_compress: false 仅禁用层 2 摘要
        var contextConfig = _config.Context ?? new ContextConfig();
        var contextWindow = contextConfig.ContextWindowTokens ?? tuiConfig.ContextWindowTokens ?? 64000;
        var truncateConfig = new TruncateConfig
        {
            PerResultThreshold = contextConfig.PerResultThreshold ?? 50_000,
            RoundTotalThreshold = contextConfig.RoundTotalThreshold ?? 200_000,
            PreviewLength = contextConfig.PreviewLength ?? 2_000,
        };
        var compressor = new ContextCompressor(_provider, 
                                               contextWindow,
                                               truncateConfig,
                                               contextConfig.WarningFraction ?? 0.7,
                                               contextConfig.TriggerFraction ?? 0.9,
                                               contextConfig.KeepRecentMessages ?? 4,
                                               contextConfig.MaxCircuitFailures ?? 2,
                                               enableAutoCompress: contextConfig.EnableAutoCompress ?? true,
                                               projectRoot: projectRoot,
                                               _logger);

        // 【迭代 10b】构造 SessionStore（会话持久化）
        // enable: false 时不构造（/session 命令返回"未启用"）
        var sessionConfig = _config.Session ?? new SessionConfig();
        SessionStore? sessionStore = null;
        if (sessionConfig.Enable ?? true)
        {
            var storageDir = string.IsNullOrWhiteSpace(sessionConfig.StorageDir) ? Path.Combine(projectRoot, ".parrotcode", "sessions")
                                                                                 : sessionConfig.StorageDir;
            sessionStore = new SessionStore(storageDir, _logger);
        }

        // 【迭代 10c】加载项目指令（三级目录扫描 + @include 展开）
        // enable: false 时不加载（TerminalApp 用空 InstructionResult）
        var instructionsConfig = _config.Instructions ?? new InstructionsConfig();
        InstructionResult instructions = new();
        if (instructionsConfig.Enable ?? true)
        {
            var loader = new InstructionLoader(projectRoot: projectRoot,
                                               maxIncludeDepth: instructionsConfig.MaxIncludeDepth ?? 3,
                                               projectInstructionsPath: instructionsConfig.ProjectInstructionsPath,
                                               logger: _logger);
            instructions = loader.Load();
            if (instructions.HasInstructions)
                _logger.LogInformation("已加载项目指令：{Count} 个文件", instructions.Sources.Count);
        }

        // 【迭代 11c】构造 McpConnectionManager（MCP 客户端）
        // enable: false 时不构造（TerminalApp 用 null）
        // TUI 模式 logger 传 null（硬约束：避免 stderr 日志在 Terminal.Gui 初始化前闪现到终端）
        // 连接结果通过 mcpManager.ConnectionResults 在 TUI 中显示
        var mcpConfig = _config.Mcp ?? new McpConfig();
        McpConnectionManager? mcpManager = null;
        if (mcpConfig.Enable ?? true)
        {
            mcpManager = new McpConnectionManager((mcpConfig.Servers ?? Array.Empty<McpServerConfig>()).ToArray(), logger: null);
            // 并行连接所有 MCP server，工具适配器收集到 mcpManager.Adapters
            // TerminalApp.RunAsync 中统一注册到主 ToolRegistry
            await mcpManager.ConnectAllAsync(_ct);
        }

        // 【迭代 12】构造 Skill 系统（Loader 三级扫描 + Registry + Executor）
        // enable: false 时不构造（/commit 返回"未启用"，skill_loader 不注册）
        var skillConfig = _config.Skills ?? new SkillConfig();
        SkillRegistry? skillRegistry = null;
        SkillExecutor? skillExecutor = null;
        if (skillConfig.Enable ?? true)
        {
            var skillLoader = new SkillLoader(projectRoot: projectRoot, logger: _logger);
            var skills = skillLoader.Load();
            skillRegistry = new SkillRegistry(skills,
                                              maxActive: skillConfig.MaxActiveSkills ?? 3,
                                              logger: _logger);
            skillExecutor = new SkillExecutor(skillRegistry);
            if (skills.Count > 0)
                _logger.LogInformation("已加载 {Count} 个 Skill", skills.Count);
        }

        // 【迭代 14】构造子 Agent 角色系统（Loader 三级扫描 + Registry）
        // enable: false 时不构造（sub_agent 工具不注册）
        var subAgentConfig = _config.SubAgent ?? new SubAgentConfig();
        RoleRegistry? roleRegistry = null;
        if (subAgentConfig.Enable ?? true)
        {
            var roleLoader = new RoleLoader(projectRoot: projectRoot, logger: _logger);
            var roles = roleLoader.Load();
            roleRegistry = new RoleRegistry(roles);
            if (roles.Count > 0)
                _logger.LogInformation("已加载 {Count} 个子 Agent 角色", roles.Count);
        }

        using var terminalApp = new TerminalApp(_provider,
                                                _providerConfig,
                                                _config.Agent,
                                                tuiConfig,
                                                securityLevel,
                                                securityGuard,
                                                compressor,
                                                sessionStore,
                                                instructions,         // 10c 新增
                                                mcpManager,           // 11c 新增
                                                skillRegistry,        // 迭代 12 新增
                                                skillExecutor,        // 迭代 12 新增
                                                roleRegistry,         // 迭代 14 新增
                                                subAgentConfig,       // 迭代 14 新增
                                                _logger,
                                                _ct);
        await terminalApp.RunAsync();

        // 程序退出时关闭 MCP 连接
        if (mcpManager is not null)
            await mcpManager.CloseAllAsync(_ct);
    }

    /// <summary>
    /// 解析安全等级字符串为 SecurityLevel 枚举。
    /// 委托 SecurityLevelParser.Parse（迭代 8a 抽取，便于单测）。
    /// </summary>
    private static SecurityLevel ParseSecurityLevel(string? level) => SecurityLevelParser.Parse(level);

    /// <summary>
    /// 规范化路径列表（相对→绝对，基于 projectRoot）。
    /// 非法路径（含非法字符等）忽略，不抛异常（避免启动失败）。
    /// internal 便于单测验证（08c-05 / 08c-10）。
    /// </summary>
    internal static IReadOnlyList<string> NormalizePaths(IList<string>? paths, string projectRoot)
    {
        if (paths is null || paths.Count == 0) return Array.Empty<string>();
        var result = new List<string>(paths.Count);
        foreach (var p in paths)
        {
            if (string.IsNullOrWhiteSpace(p)) continue;
            try
            {
                result.Add(Path.GetFullPath(p, projectRoot));
            }
            catch
            {
                // 非法路径忽略（避免启动失败）。生产环境可记 warning 日志。
            }
        }
        return result;
    }
}
