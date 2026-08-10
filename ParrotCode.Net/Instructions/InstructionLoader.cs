using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace ParrotCode;

/// <summary>
/// 项目指令加载器（迭代 10c）：三级目录扫描 + @include 嵌套（限 3 层）。
/// 加载顺序：
/// 1. ~/.parrotcode/instructions.md（全局用户指令）
/// 2. ./PARROTCODE.md（项目根指令）
/// 3. ./.parrotcode/instructions.md（项目本地指令）
/// 每个文件支持 @include path/to/file.md 嵌套引用，限 maxIncludeDepth 层防无限递归。
/// </summary>
public sealed class InstructionLoader
{
    private readonly string _projectRoot;
    private readonly string _userHome;
    private readonly int _maxIncludeDepth;
    private readonly string _projectInstructionsPath;
    private readonly ILogger? _logger;

    // @include path/to/file.md 或 @include "path with spaces.md"
    private static readonly Regex IncludeRegex = new(@"@include\s+(?:""([^""]+)""|(\S+))",
                                                     RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public InstructionLoader(
        string? projectRoot = null,
        string? userHome = null,
        int maxIncludeDepth = 3,
        string? projectInstructionsPath = null,
        ILogger? logger = null)
    {
        _projectRoot = projectRoot ?? Directory.GetCurrentDirectory();
        _userHome = userHome ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        _maxIncludeDepth = maxIncludeDepth;
        _projectInstructionsPath = projectInstructionsPath ?? "PARROTCODE.md";
        _logger = logger;
    }

    /// <summary>
    /// 加载所有指令（三级目录扫描 + @include 展开）。
    /// </summary>
    public InstructionResult Load()
    {
        var sources = new List<string>();
        var sections = new List<string>();

        // 1. 全局用户指令
        var globalPath = Path.Combine(_userHome, ".parrotcode", "instructions.md");
        var globalContent = TryReadWithIncludes(globalPath, depth: 0);
        if (globalContent is not null)
        {
            sections.Add("## 全局指令\n" + globalContent.Value.Content);
            sources.AddRange(globalContent.Value.Sources);
        }

        // 2. 项目根指令
        var projectPath = Path.IsPathRooted(_projectInstructionsPath) ? _projectInstructionsPath
                                                                      : Path.Combine(_projectRoot, _projectInstructionsPath);
        var projectContent = TryReadWithIncludes(projectPath, depth: 0);
        if (projectContent is not null)
        {
            sections.Add("## 项目指令\n" + projectContent.Value.Content);
            sources.AddRange(projectContent.Value.Sources);
        }

        // 3. 项目本地指令
        var localPath = Path.Combine(_projectRoot, ".parrotcode", "instructions.md");
        var localContent = TryReadWithIncludes(localPath, depth: 0);
        if (localContent is not null)
        {
            sections.Add("## 本地指令\n" + localContent.Value.Content);
            sources.AddRange(localContent.Value.Sources);
        }

        return new InstructionResult
        {
            Content = string.Join("\n\n", sections),
            Sources = sources.Distinct().ToList()
        };
    }

    /// <summary>
    /// 读取文件并展开 @include 指令（递归，限 maxIncludeDepth 层）。
    /// 返回 (展开后内容, 来源文件列表)；文件不存在返回 null。
    /// </summary>
    private (string Content, List<string> Sources)? TryReadWithIncludes(string filePath, int depth)
    {
        if (!File.Exists(filePath))
            return null;

        if (depth > _maxIncludeDepth)
        {
            _logger?.LogWarning("@include 嵌套超过 {Max} 层，跳过 {File}", _maxIncludeDepth, filePath);
            return null;
        }

        string raw;
        try
        {
            raw = File.ReadAllText(filePath);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning("读取指令文件失败 {File}：{Error}", filePath, ex.Message);
            return null;
        }

        var sources = new List<string> { filePath };
        var content = new StringBuilder(raw);

        // 查找所有 @include 指令
        var matches = IncludeRegex.Matches(raw);
        // 从后往前替换（避免索引偏移）
        for (var i = matches.Count - 1; i >= 0; i--)
        {
            var match = matches[i];
            var includePath = match.Groups[1].Success ? match.Groups[1].Value 
                                                      : match.Groups[2].Value;

            // 解析路径（相对当前文件所在目录）
            var basePath = Path.GetDirectoryName(filePath) ?? _projectRoot;
            var resolvedPath = Path.IsPathRooted(includePath) ? includePath
                                                              : Path.GetFullPath(includePath, basePath);

            var included = TryReadWithIncludes(resolvedPath, depth + 1);
            if (included is not null)
            {
                content.Remove(match.Index, match.Length);
                content.Insert(match.Index, included.Value.Content);
                sources.AddRange(included.Value.Sources);
            }
            else
            {
                // @include 文件不存在——替换为提示
                var warning = $"[指令引用失败：{includePath}]";
                content.Remove(match.Index, match.Length);
                content.Insert(match.Index, warning);
                _logger?.LogWarning("@include 文件不存在：{Path}（引用自 {File}）", resolvedPath, filePath);
            }
        }

        return (content.ToString(), sources);
    }

    /// <summary>生成指令加载概要（/status 用）。</summary>
    public static string GetSummary(InstructionResult result)
    {
        if (!result.HasInstructions) return "未加载";
        return $"{result.Sources.Count} 个文件：{string.Join(", ", result.Sources.Select(Path.GetFileName))}";
    }
}
