using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace ParrotCode;

/// <summary>
/// Skill 加载器（迭代 12 + 13a）：三级目录扫描 + YAML frontmatter 解析 + 目录格式支持。
/// 加载顺序（后者覆盖前者同名）：
/// 1. 内置 Skills/Builtin/（随程序发布，AppContext.BaseDirectory 定位）
/// 2. 全局 ~/.parrotcode/skills/
/// 3. 项目 ./.parrotcode/skills/
/// 支持两种格式（迭代 13a）：
/// - 单文件格式：<name>.md（向后兼容）
/// - 目录格式：<name>/SKILL.md + scripts/ + references/ + assets/（Phase 3 按需加载）
/// 同级同名时目录格式优先 + 警告日志。
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
    /// 扫描指定目录下的 Skill：先扫单文件 *.md（向后兼容），再扫目录 */SKILL.md（目录格式）。
    /// 同名时目录格式覆盖单文件版本（+ 警告日志）。
    /// 目录不存在静默跳过；单个 Skill 解析失败记录日志跳过，不中断整体加载。
    /// </summary>
    private void ScanDirectory(string dir, SkillSource source, Dictionary<string, SkillDefinition> byName)
    {
        if (!Directory.Exists(dir)) return;

        // 1. 扫描单文件格式 *.md（向后兼容）
        foreach (var file in Directory.GetFiles(dir, "*.md"))
        {
            // 防御性跳过顶层 SKILL.md（目录格式在子目录内，不会被 GetFiles(dir, "*.md") 扫到）
            if (Path.GetFileName(file) == "SKILL.md") continue;

            try
            {
                var def = ParseFile(file, source);
                if (def is not null)
                {
                    byName[def.Meta.Name] = def;
                    _logger?.LogDebug("已加载单文件 Skill {Name}（{Source}）：{File}", def.Meta.Name, source, file);
                }
            }
            catch (Exception ex)
            {
                _logger?.LogWarning("Skill 文件解析失败 {File}：{Error}", file, ex.Message);
            }
        }

        // 2. 扫描目录格式 <name>/SKILL.md
        foreach (var subDir in Directory.GetDirectories(dir))
        {
            var skillFile = Path.Combine(subDir, "SKILL.md");
            if (!File.Exists(skillFile)) continue;

            try
            {
                var def = ParseDirectory(subDir, skillFile, source);
                if (def is not null)
                {
                    // 同名冲突检测：单文件版本已存在则警告（目录格式优先）
                    if (byName.TryGetValue(def.Meta.Name, out var existing) && existing.Source == source)
                    {
                        _logger?.LogWarning("Skill {Name} 在 {Source} 层级同时存在单文件和目录格式，使用目录格式（{Dir}）",
                            def.Meta.Name, source, subDir);
                    }
                    byName[def.Meta.Name] = def;
                    _logger?.LogDebug("已加载目录 Skill {Name}（{Source}）：{Dir}（{Count} 个资源）",
                        def.Meta.Name, source, subDir, def.Resources.Count);
                }
            }
            catch (Exception ex)
            {
                _logger?.LogWarning("Skill 目录解析失败 {Dir}：{Error}", subDir, ex.Message);
            }
        }
    }

    /// <summary>
    /// 解析目录格式 Skill：先复用 ParseFile 解析 SKILL.md，再扫描子目录资源。
    /// </summary>
    private SkillDefinition? ParseDirectory(string skillDir, string skillFile, SkillSource source)
    {
        var def = ParseFile(skillFile, source);
        if (def is null) return null;

        var resources = new List<SkillResource>();
        ScanResources(skillDir, "scripts", SkillResourceKind.Script, resources);
        ScanResources(skillDir, "references", SkillResourceKind.Reference, resources);
        ScanResources(skillDir, "assets", SkillResourceKind.Asset, resources);

        return def with { SkillDir = skillDir, Resources = resources };
    }

    /// <summary>
    /// 递归扫描子目录下的所有文件（跳过隐藏文件）。
    /// </summary>
    private void ScanResources(string skillDir, string subDirName, SkillResourceKind kind, List<SkillResource> resources)
    {
        var subDir = Path.Combine(skillDir, subDirName);
        if (!Directory.Exists(subDir)) return;

        foreach (var file in Directory.GetFiles(subDir, "*", SearchOption.AllDirectories))
        {
            var fileName = Path.GetFileName(file);
            // 跳过隐藏文件（.DS_Store / .gitkeep 等）
            if (fileName.StartsWith(".")) continue;

            resources.Add(new SkillResource
            {
                Kind = kind,
                RelativePath = Path.GetRelativePath(skillDir, file),
                AbsolutePath = Path.GetFullPath(file)
            });
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
