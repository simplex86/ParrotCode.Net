using System.Text.RegularExpressions;

namespace ParrotCode;

/// <summary>
/// 危险命令黑名单（迭代 8a）。
/// 硬编码核心规则 + 配置扩展（ExtraBlacklist）。
/// 匹配对象：run_command 工具的 command+args 拼接后的规范化字符串。
/// 非命令工具（read_file/write_file 等）直接由调用方判断（command 为 null 不调本类）。
/// 始终生效，不依赖 SecurityLevel（防 Permissive 下 rm -rf /）。
/// </summary>
public sealed class Blacklist
{
    /// <summary>硬编码黑名单规则。Reason 会回灌给 LLM。</summary>
    private static readonly BlacklistRule[] BuiltInRules =
    {
        // 递归删除根目录（rm -rf /，/ 后是空格或行尾）
        new(new(@"\brm\s+-[a-zA-Z]*r[a-zA-Z]*f?\s+/(?:\s|$)", RegexOptions.Compiled | RegexOptions.IgnoreCase),
            "递归删除根目录（rm -rf /）"),

        // 递归删除系统关键目录（/boot /etc /usr /var /bin /sbin /root /home /tmp）
        // 边界 (?:\s|$)：只匹配目录本身，不拦 /tmp/foo 等子路径
        new(new(@"\brm\s+-[a-zA-Z]*r[a-zA-Z]*f?\s+/(?:boot|etc|usr|var|bin|sbin|root|home|tmp)(?:\s|$)",
                RegexOptions.Compiled | RegexOptions.IgnoreCase),
            "递归删除系统目录"),

        // 远程脚本执行（curl/wget 管道到 shell）
        new(new(@"\b(curl|wget)\b[^|]*\|\s*(?:sh|bash|zsh|fish)\b",
                RegexOptions.Compiled | RegexOptions.IgnoreCase),
            "远程脚本执行（curl|sh）"),

        // 下载写入块设备（curl/wget > /dev/sd*）
        new(new(@"\b(curl|wget)\b[^>]*>\s*/dev/(?:sd[a-z]+|nvme\d+n\d+|disk\d+)",
                RegexOptions.Compiled | RegexOptions.IgnoreCase),
            "下载写入块设备"),

        // fork bomb（:(){ :|:& };: 及空格变体）
        new(new(@":\s*\(\)\s*\{\s*:\|:&\s*\}\s*;\s*:",
                RegexOptions.Compiled | RegexOptions.IgnoreCase),
            "fork bomb"),

        // 写块设备（dd of=/dev/sd*）
        new(new(@"\bdd\b.*\bof=/dev/(?:sd[a-z]+|nvme\d+n\d+|disk\d+)",
                RegexOptions.Compiled | RegexOptions.IgnoreCase),
            "写块设备（dd）"),

        // 格式化块设备（mkfs / mkfs.ext4）
        new(new(@"\bmkfs(?:\.\w+)?\s+/dev/",
                RegexOptions.Compiled | RegexOptions.IgnoreCase),
            "格式化块设备（mkfs）"),
    };

    private readonly Regex[] _extraRules;

    public Blacklist(IReadOnlyList<string> extraPatterns)
    {
        _extraRules = (extraPatterns ?? Array.Empty<string>())
            .Select(p => new Regex(p, RegexOptions.Compiled | RegexOptions.IgnoreCase))
            .ToArray();
    }

    /// <summary>
    /// 检查命令是否命中黑名单。
    /// command/args 取自 run_command 的参数；调用方对非命令工具传 null。
    /// 命中返回 BlacklistHit（含 Reason）；未命中返回 null。
    /// </summary>
    public BlacklistHit? Match(string? command, string? args)
    {
        if (string.IsNullOrWhiteSpace(command)) return null;

        // 规范化：合并多余空白，便于正则匹配（防 "rm  -rf  /" 绕过）
        var full = string.IsNullOrEmpty(args) ? command! : $"{command} {args}";
        var normalized = Regex.Replace(full, @"\s+", " ").Trim();

        foreach (var rule in BuiltInRules)
        {
            if (rule.Pattern.IsMatch(normalized))
                return new BlacklistHit(rule.Reason);
        }
        foreach (var rule in _extraRules)
        {
            if (rule.IsMatch(normalized))
                return new BlacklistHit($"自定义黑名单规则命中：{rule}");
        }
        return null;
    }
}
