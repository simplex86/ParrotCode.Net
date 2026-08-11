using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace ParrotCode;

/// <summary>
/// Skill 加载器（迭代 12）：三级目录扫描 + YAML frontmatter 解析。
/// 加载顺序（后者覆盖前者同名）：
/// 1. 内置 Skills/Builtin/*.md（随程序发布，AppContext.BaseDirectory 定位）
/// 2. 全局 ~/.parrotcode/skills/*.md
/// 3. 项目 ./.parrotcode/skills/*.md
/// 复用迭代 10c InstructionLoader 的三级扫描模式，区别在扫描多文件 + 同名覆盖。
/// </summary>
public sealed class SkillLoader
{
    private readonly string _projectRoot;
    private readonly string _userHome;
    private readonly string _builtinDir;
    private readonly ILogger? _logger;

    /// <summary>
    /// 构造加载器。所有路径参数可选（用于测试注入），默认跨平台推导。
    /// </summary>
    public SkillLoader(string? projectRoot = null,
                       string? userHome = null,
                       string? builtinDir = null,
                       ILogger? logger = null)
    {
        _projectRoot = projectRoot ?? Directory.GetCurrentDirectory();
        _userHome = userHome ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        _builtinDir = builtinDir ?? Path.Combine(AppContext.BaseDirectory, "Skills", "Builtin");
        _logger = logger;
    }

    /// <summary>
    /// 加载所有 Skill。同名按 项目 > 全局 > 内置 覆盖。
    /// 返回按 SkillMeta.Name 索引的字典。
    /// </summary>
    public IReadOnlyDictionary<string, SkillDefinition> Load()
    {
        var byName = new Dictionary<string, SkillDefinition>(StringComparer.Ordinal);

        // 1. 内置（兜底默认）
        ScanDirectory(_builtinDir, SkillSource.Builtin, byName);
        // 2. 全局
        var globalDir = Path.Combine(_userHome, ".parrotcode", "skills");
        if (!string.IsNullOrEmpty(_userHome))
            ScanDirectory(globalDir, SkillSource.Global, byName);
        // 3. 项目
        var projectDir = Path.Combine(_projectRoot, ".parrotcode", "skills");
        ScanDirectory(projectDir, SkillSource.Project, byName);

        return byName;
    }

    /// <summary>
    /// 扫描指定目录下的所有 *.md，解析后加入 byName（后者覆盖前者）。
    /// 目录不存在静默跳过；单个文件解析失败记录日志跳过，不中断整体加载。
    /// </summary>
    private void ScanDirectory(string dir, SkillSource source, Dictionary<string, SkillDefinition> byName)
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
                    _logger?.LogDebug("已加载 Skill {Name}（{Source}）：{File}", def.Meta.Name, source, file);
                }
            }
            catch (Exception ex)
            {
                _logger?.LogWarning("Skill 文件解析失败 {File}：{Error}", file, ex.Message);
            }
        }
    }

    /// <summary>
    /// 解析单个 Skill 文件：分离 frontmatter（YAML）+ 正文（Markdown）。
    /// frontmatter 不存在、name 为空、YAML 语法错时返回 null（跳过）。
    /// </summary>
    private SkillDefinition? ParseFile(string path, SkillSource source)
    {
        var raw = File.ReadAllText(path);
        var match = FrontmatterRegex.Match(raw);
        if (!match.Success)
        {
            _logger?.LogWarning("Skill 文件缺少 frontmatter：{File}", path);
            return null;
        }

        var yaml = match.Groups[1].Value;
        var body = match.Groups[2].Value.TrimStart('\r', '\n');

        var deserializer = new DeserializerBuilder()
            .WithNamingConvention(UnderscoredNamingConvention.Instance)  // snake_case ↔ PascalCase
            .Build();
        var meta = deserializer.Deserialize<SkillMeta>(yaml);

        if (meta is null || string.IsNullOrWhiteSpace(meta.Name))
        {
            _logger?.LogWarning("Skill 文件缺少 name 字段：{File}", path);
            return null;
        }

        return new SkillDefinition
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
