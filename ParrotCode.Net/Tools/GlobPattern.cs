using System.Text;
using System.Text.RegularExpressions;

namespace ParrotCode;

/// <summary>
/// glob 模式转正则的工具类。供 GlobTool / GrepTool 共享。
/// 支持 * / ** / ?：
/// - ** 匹配任意层级目录（含路径分隔符）
/// - * 匹配除路径分隔符外的任意字符
/// - ? 匹配除路径分隔符外的单个字符
/// </summary>
internal static class GlobPattern
{
    /// <summary>
    /// 把 glob 模式转成正则。返回已编译的 Regex，锚定 ^...$。
    /// </summary>
    public static Regex ToRegex(string pattern)
    {
        var sb = new StringBuilder("^");
        var i = 0;
        while (i < pattern.Length)
        {
            if (pattern[i] == '*' && i + 1 < pattern.Length && pattern[i + 1] == '*')
            {
                sb.Append(".*");
                i += 2;
                if (i < pattern.Length && pattern[i] == '/') i++; // 吃掉 **/
            }
            else if (pattern[i] == '*') { sb.Append("[^/]*"); i++; }
            else if (pattern[i] == '?') { sb.Append("[^/]"); i++; }
            else if ("+()|^$.{}\\".IndexOf(pattern[i]) >= 0) { sb.Append('\\').Append(pattern[i]); i++; }
            else { sb.Append(pattern[i]); i++; }
        }
        sb.Append('$');
        return new Regex(sb.ToString(), RegexOptions.Compiled);
    }
}
