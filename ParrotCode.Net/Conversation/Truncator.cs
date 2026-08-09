using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace ParrotCode;

/// <summary>
/// 截断配置。所有阈值可经 ContextConfig 覆盖。
/// </summary>
public sealed record TruncateConfig
{
    /// <summary>
    /// 单条工具结果截断阈值（字符数）。默认 50_000。
    /// </summary>
    public int PerResultThreshold { get; init; } = 50_000;

    /// <summary>
    /// 一轮内所有工具结果合计截断阈值（字符数）。默认 200_000。
    /// </summary>
    public int RoundTotalThreshold { get; init; } = 200_000;

    /// <summary>
    /// 截断后保留的预览长度（字符数）。默认 2_000。
    /// </summary>
    public int PreviewLength { get; init; } = 2_000;

    /// <summary>
    /// 截断文件存储目录。默认 ".parrotcode/truncated"（项目根下）。
    /// </summary>
    public string StorageDir { get; init; } = ".parrotcode/truncated";
}

/// <summary>
/// 单条截断结果信息。
/// </summary>
public sealed record TruncationInfo(
    int Index,
    string ToolName,
    int OriginalChars,
    string? FilePath);

/// <summary>
/// 层 1：工具结果截断器。轻量、无 LLM 调用。
/// 单条 > PerResultThreshold → 写盘留 PreviewLength 预览。
/// 一轮合计 > RoundTotalThreshold → 从最大开始截断。
/// 在工具结果入历史前执行，历史始终保持紧凑。
/// </summary>
public sealed class ToolResultTruncator
{
    private readonly TruncateConfig _config;
    private readonly string _storageDir;

    public ToolResultTruncator(TruncateConfig? config = null, string? projectRoot = null)
    {
        _config = config ?? new TruncateConfig();
        var root = projectRoot ?? Directory.GetCurrentDirectory();
        _storageDir = Path.IsPathRooted(_config.StorageDir)
            ? _config.StorageDir
            : Path.GetFullPath(_config.StorageDir, root);
    }

    /// <summary>
    /// 存储目录（绝对路径）。
    /// </summary>
    public string StorageDir => _storageDir;

    /// <summary>
    /// 批量截断工具结果。返回 (截断后内容数组, 被截断的 TruncationInfo 列表)。
    /// 先处理单条超长，再处理合计超长。
    /// </summary>
    public (string[] TruncatedContents, IReadOnlyList<TruncationInfo> Infos)
        TruncateBatch(IReadOnlyList<string> contents, IReadOnlyList<string> toolNames)
    {
        if (contents.Count != toolNames.Count)
            throw new ArgumentException("contents 和 toolNames 长度不一致");

        var result = new string[contents.Count];
        var infos = new List<TruncationInfo>();

        // Pass 1：单条截断
        var sizes = new int[contents.Count];
        for (var i = 0; i < contents.Count; i++)
        {
            var content = contents[i];
            sizes[i] = content.Length;
            if (content.Length > _config.PerResultThreshold)
            {
                var (truncated, filePath) = TruncateToDisk(content, toolNames[i]);
                result[i] = truncated;
                infos.Add(new TruncationInfo(i, toolNames[i], content.Length, filePath));
                sizes[i] = truncated.Length;
            }
            else
            {
                result[i] = content;
            }
        }

        // Pass 2：合计截断（从最大开始截断直到合计 < 阈值）
        var total = sizes.Sum();
        if (total <= _config.RoundTotalThreshold)
            return (result, infos);

        var truncatedIndices = infos.Select(info => info.Index).ToHashSet();
        var candidates = Enumerable.Range(0, contents.Count)
            .Where(i => !truncatedIndices.Contains(i))
            .OrderByDescending(i => sizes[i])
            .ToList();

        foreach (var i in candidates)
        {
            if (total <= _config.RoundTotalThreshold)
                break;
            if (result[i].Length <= _config.PreviewLength + 200)
                continue;

            var original = result[i];
            var (truncated, filePath) = TruncateToDisk(original, toolNames[i]);
            total -= original.Length - truncated.Length;
            result[i] = truncated;
            infos.Add(new TruncationInfo(i, toolNames[i], original.Length, filePath));
        }

        return (result, infos);
    }

    /// <summary>
    /// 截换单条内容到磁盘，返回 (预览文本, 文件路径)。
    /// </summary>
    private (string Preview, string? FilePath) TruncateToDisk(string content, string toolName)
    {
        var previewLen = Math.Min(_config.PreviewLength, content.Length);
        var preview = content[..previewLen];
        var omitted = content.Length - _config.PreviewLength;

        try
        {
            Directory.CreateDirectory(_storageDir);
            var ts = DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
            var safeName = Regex.Replace(toolName, @"[^a-zA-Z0-9_-]", "_");
            var filePath = Path.Combine(_storageDir, $"{ts}_{safeName}.txt");
            File.WriteAllText(filePath, content, Encoding.UTF8);

            var text = new StringBuilder()
                .AppendLine("[工具结果过大，完整内容已保存到磁盘]")
                .AppendLine($"文件: {filePath}")
                .AppendLine($"预览（前 {_config.PreviewLength} 字符）:")
                .AppendLine(preview)
                .Append($"...（省略 {omitted} 字符）")
                .ToString();
            return (text, filePath);
        }
        catch (Exception)
        {
            // 写盘失败：降级为仅截断不写盘
            var text = new StringBuilder()
                .AppendLine("[工具结果过大（写盘失败，未保存完整内容）]")
                .AppendLine($"预览（前 {_config.PreviewLength} 字符）:")
                .AppendLine(preview)
                .Append($"...（省略 {omitted} 字符）")
                .ToString();
            return (text, null);
        }
    }
}
