using System.Text.RegularExpressions;

namespace ParrotCode;

/// <summary>
/// 斜杠命令解析器：把用户输入行解析为 (命令名, 参数字符串)。
/// 规则：
/// - 行首必须是 '/' 才视为命令（否则返回 null，走 AI）
/// - 命令名 = '/' 后到第一个空格前的部分（大小写不敏感）
/// - 参数 = 第一个空格后的全部内容（保留原始大小写与空格）
/// </summary>
public static class CommandParser
{
    /// <summary>
    /// 解析输入行。返回 (命令名, 参数)；非命令（不以 / 开头）返回 null。
    /// </summary>
    public static (string Name, string Args)? Parse(string line)
    {
        if (string.IsNullOrEmpty(line) || line[0] != '/')
            return null;

        var body = line[1..];
        var spaceIdx = body.IndexOf(' ');
        if (spaceIdx < 0)
            return (body, string.Empty);

        var name = body[..spaceIdx];
        var args = body[(spaceIdx + 1)..];
        return (name, args);
    }

    /// <summary>
    /// 把参数字符串按空格分割为参数数组（支持引号包裹的含空格参数）。
    /// "/session save my-session" → ["save", "my-session"]
    /// "/mode \"strict mode\"" → ["strict mode"]
    /// </summary>
    public static IReadOnlyList<string> SplitArgs(string args)
    {
        if (string.IsNullOrWhiteSpace(args))
            return Array.Empty<string>();

        var result = new List<string>();
        var matches = Regex.Matches(args, @"(?:""([^""]*)""|(\S+))");
        foreach (Match m in matches)
            result.Add(m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value);
        return result;
    }
}
