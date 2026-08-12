using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace ParrotCode;

/// <summary>
/// 角色加载器（迭代 14a）：三级目录扫描 + YAML frontmatter 解析。
/// 是 <see cref="SkillLoader"/> 的简化版——单文件格式、三级扫描、同名覆盖，无目录化、无资源。
/// 加载顺序（后者覆盖前者同名）：
/// <list type="number">
///   <item>内置 SubAgent/Roles/Builtin/*.md（随程序发布，AppContext.BaseDirectory 定位）</item>
///   <item>全局 ~/.parrotcode/roles/*.md</item>
///   <item>项目 ./.parrotcode/roles/*.md</item>
/// </list>
/// </summary>
public sealed class RoleLoader
{
    private readonly string _projectRoot;
    private readonly string _userHome;
    private readonly string _builtinDir;
    private readonly ILogger? _logger;

    /// <summary>
    /// 构造加载器。所有路径参数可选（用于测试注入），默认跨平台推导。
    /// </summary>
    public RoleLoader(string? projectRoot = null,
                      string? userHome = null,
                      string? builtinDir = null,
                      ILogger? logger = null)
    {
        _projectRoot = projectRoot ?? Directory.GetCurrentDirectory();
        _userHome = userHome ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        _builtinDir = builtinDir ?? Path.Combine(AppContext.BaseDirectory, "SubAgent", "Roles", "Builtin");
        _logger = logger;
    }

    /// <summary>
    /// 加载所有角色。同名按 项目 > 全局 > 内置 覆盖。
    /// 返回按 <see cref="RoleMeta.Name"/> 索引的字典。
    /// </summary>
    public IReadOnlyDictionary<string, RoleDefinition> Load()
    {
        var byName = new Dictionary<string, RoleDefinition>(StringComparer.Ordinal);

        // 1. 内置（兜底默认）
        ScanDirectory(_builtinDir, RoleSource.Builtin, byName);
        // 2. 全局
        var globalDir = Path.Combine(_userHome, ".parrotcode", "roles");
        if (!string.IsNullOrEmpty(_userHome))
            ScanDirectory(globalDir, RoleSource.Global, byName);
        // 3. 项目
        var projectDir = Path.Combine(_projectRoot, ".parrotcode", "roles");
        ScanDirectory(projectDir, RoleSource.Project, byName);

        return byName;
    }

    /// <summary>
    /// 扫描指定目录下的角色文件 *.md。
    /// 目录不存在静默跳过；单个角色解析失败记录日志跳过，不中断整体加载。
    /// </summary>
    private void ScanDirectory(string dir, RoleSource source, Dictionary<string, RoleDefinition> byName)
    {
        if (!Directory.Exists(dir)) return;

        foreach (var file in Directory.GetFiles(dir, "*.md"))
        {
            try
            {
                var def = ParseFile(file, source);
                if (def is not null)
                {
                    byName[def.Meta.Name] = def;  // 后者覆盖前者
                    _logger?.LogDebug("已加载角色 {Name}（{Source}）：{File}", def.Meta.Name, source, file);
                }
            }
            catch (Exception ex)
            {
                _logger?.LogWarning("角色文件解析失败 {File}：{Error}", file, ex.Message);
            }
        }
    }

    /// <summary>
    /// 解析单个角色文件：分离 frontmatter（YAML）+ 正文（Markdown）。
    /// frontmatter 不存在、name 为空、YAML 语法错时返回 null（跳过）。
    /// </summary>
    private RoleDefinition? ParseFile(string path, RoleSource source)
    {
        var raw = File.ReadAllText(path);
        var match = FrontmatterRegex.Match(raw);
        if (!match.Success)
        {
            _logger?.LogWarning("角色文件缺少 frontmatter：{File}", path);
            return null;
        }

        var yaml = match.Groups[1].Value;
        var body = match.Groups[2].Value.TrimStart('\r', '\n');

        var deserializer = new DeserializerBuilder()
            .WithNamingConvention(UnderscoredNamingConvention.Instance)  // snake_case ↔ PascalCase
            .Build();
        var meta = deserializer.Deserialize<RoleMeta>(yaml);

        if (meta is null || string.IsNullOrWhiteSpace(meta.Name))
        {
            _logger?.LogWarning("角色文件缺少 name 字段：{File}", path);
            return null;
        }

        return new RoleDefinition
        {
            Meta = meta,
            Body = body,
            SourcePath = path,
            Source = source
        };
    }

    /// <summary>
    /// frontmatter 分离正则：以 --- 包围的 YAML + 其后的正文。
    /// Singleline 让 . 匹配换行；\r?\n 兼容 Windows/Unix 换行。
    /// </summary>
    private static readonly Regex FrontmatterRegex = new(@"^---\s*\r?\n(.*?)\r?\n---\s*\r?\n(.*)$", RegexOptions.Singleline | RegexOptions.Compiled);
}

/// <summary>
/// 角色注册表（迭代 14a）：管理已加载角色。无激活状态（角色是定义，不是运行时状态）。
/// 比 <see cref="SkillRegistry"/> 简单——无 maxActive / 无 Activate / 无 Deactivate。
/// </summary>
public sealed class RoleRegistry
{
    private readonly Dictionary<string, RoleDefinition> _roles;

    public RoleRegistry(IReadOnlyDictionary<string, RoleDefinition> roles)
    {
        _roles = new Dictionary<string, RoleDefinition>(roles, StringComparer.Ordinal);
    }

    /// <summary>
    /// 是否含任何角色。
    /// </summary>
    public bool HasRoles => _roles.Count > 0;

    /// <summary>
    /// 获取角色（不存在返回 null）。
    /// </summary>
    public RoleDefinition? Get(string name)
    {
        _roles.TryGetValue(name, out var def);
        return def;
    }

    /// <summary>
    /// 所有已加载角色的快照。
    /// </summary>
    public IReadOnlyCollection<RoleDefinition> GetAll() => _roles.Values.ToList().AsReadOnly();
}
