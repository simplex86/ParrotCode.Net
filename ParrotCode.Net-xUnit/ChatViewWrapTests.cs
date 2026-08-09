using System.Text;
using ParrotCode;

namespace ParrotCode.xUnit;

/// <summary>
/// 诊断测试：验证 ChatView.WrapText 对 emoji 代理对的处理。
/// 核心：代理对（emoji）不应在换行时被拆分到两行。
/// </summary>
public class ChatViewWrapTests
{
    private static IEnumerable<string> InvokeWrapText(string text, int maxWidth)
    {
        var method = typeof(ChatView).GetMethod("WrapText",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        return (IEnumerable<string>)method!.Invoke(null, new object[] { text, maxWidth })!;
    }

    /// <summary>检查字符串是否包含孤立代理（高代理后无低代理，或低代理前无高代理）。</summary>
    private static bool HasIsolatedSurrogate(string s)
    {
        for (int i = 0; i < s.Length; i++)
        {
            if (char.IsHighSurrogate(s, i))
            {
                if (i + 1 >= s.Length || !char.IsLowSurrogate(s, i + 1))
                    return true;  // 高代理后无低代理
                i++;  // 跳过低代理
            }
            else if (char.IsLowSurrogate(s, i))
            {
                return true;  // 低代理前无高代理
            }
        }
        return false;
    }

    [Fact]
    public void WrapText_EmojoAtLineBoundary_NotSplit()
    {
        // 🎉 = U+1F389, 代理对 (0xD83C, 0xDF89)
        // 9 个 A（各 1 列）+ emoji（2 列）= 11 列，maxWidth=10 → emoji 应换到下一行
        var text = new string('A', 9) + "🎉B";
        var lines = InvokeWrapText(text, 10).ToList();

        for (int i = 0; i < lines.Count; i++)
        {
            var codes = lines[i].Select(c => $"U+{(int)c:X4}").ToArray();
            Console.WriteLine($"Line {i}: [{lines[i]}] => {string.Join(" ", codes)}");
            HasIsolatedSurrogate(lines[i]).Should().BeFalse(
                $"第 {i} 行不应包含孤立代理字符（代理对应完整在同一行）");
        }
    }

    [Fact]
    public void WrapText_EmojoWidth_Correct()
    {
        // 单个 emoji 应完整保留
        var lines = InvokeWrapText("🎉", 10).ToList();
        lines.Should().HaveCount(1);
        lines[0].Should().Be("🎉");

        // emoji 宽度 2：A(1) + emoji(2) = 3 > maxWidth 2 → emoji 换行
        lines = InvokeWrapText("A🎉B", 2).ToList();
        for (int i = 0; i < lines.Count; i++)
            Console.WriteLine($"  Line {i}: [{lines[i]}]");
        // 每行都不应有孤立代理
        foreach (var line in lines)
            HasIsolatedSurrogate(line).Should().BeFalse();
    }

    [Fact]
    public void WrapText_MultipleEmojis_NotSplit()
    {
        var text = "你好🎉世界🤖测试";
        var lines = InvokeWrapText(text, 6).ToList();

        Console.WriteLine($"Text: {text} (width 6)");
        for (int i = 0; i < lines.Count; i++)
        {
            var codes = lines[i].Select(c => $"U+{(int)c:X4}").ToArray();
            Console.WriteLine($"  Line {i}: [{lines[i]}] => {string.Join(" ", codes)}");
            HasIsolatedSurrogate(lines[i]).Should().BeFalse(
                $"第 {i} 行不应包含孤立代理字符");
        }
    }

    [Fact]
    public void WrapText_EmojoExactlyAtBoundary_NotSplit()
    {
        // 8 个 A + emoji(2列) = 10 = maxWidth → emoji 刚好放得下
        var text = new string('A', 8) + "🎉";
        var lines = InvokeWrapText(text, 10).ToList();

        foreach (var line in lines)
            HasIsolatedSurrogate(line).Should().BeFalse();
    }

    [Fact]
    public void WrapText_EmojoOverflow_NotSplit()
    {
        // 10 个 A + emoji → emoji 放不下，应完整换到下一行
        var text = new string('A', 10) + "🎉";
        var lines = InvokeWrapText(text, 10).ToList();

        for (int i = 0; i < lines.Count; i++)
            Console.WriteLine($"  Line {i}: [{lines[i]}]");
        lines.Should().HaveCount(2);
        HasIsolatedSurrogate(lines[0]).Should().BeFalse();
        HasIsolatedSurrogate(lines[1]).Should().BeFalse();
    }

    [Fact]
    public void WrapText_CjkStillWrapsCorrectly()
    {
        // 回归：CJK 全角字符换行不受影响
        var text = "你好世界测试中文";
        var lines = InvokeWrapText(text, 6).ToList();

        lines.Should().HaveCount(3);  // 6 个汉字 / 每行 3 个
        lines[0].Should().Be("你好世");
        lines[1].Should().Be("界测试");
        lines[2].Should().Be("中文");
    }
}
