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

    // ===== 诊断测试：验证孤立代理对的行为 =====

    [Fact]
    public void Diagnostic_EnumerateRunes_IsolatedHighSurrogate_ProducesReplacementChar()
    {
        // 🎉 = U+1F389 = 代理对 (\uD83C, \uDF89)
        // 模拟流式 chunk 边界截断：只有高代理 \uD83C 到达
        var truncated = "Hello \uD83C";

        // 直接验证 .NET 的 EnumerateRunes 行为（这是 .NET 的固有行为，无法改变）
        var runes = truncated.EnumerateRunes().ToList();
        foreach (var r in runes)
            Console.WriteLine($"  Rune: U+{r.Value:X4} = {r}");

        // .NET 的 EnumerateRunes 将孤立高代理替换为 U+FFFD——这是根因
        runes.Last().Value.Should().Be(0xFFFD,
            "孤立高代理在 EnumerateRunes 下会被替换为 U+FFFD——这是 .NET 的固有行为");
    }

    [Fact]
    public void Diagnostic_WrapText_TruncatedSurrogate_NoReplacementChar_AfterFix()
    {
        // 模拟流式 chunk 1：只有高代理
        var chunk1 = "Hello \uD83C";
        var lines1 = InvokeWrapText(chunk1, 20).ToList();
        var output1 = string.Join("", lines1);

        Console.WriteLine($"  Chunk1 output: [{output1}]");
        Console.WriteLine($"  Contains U+FFFD: {output1.Contains('\uFFFD')}");

        // 修复后：输出不应包含 U+FFFD（末尾孤立高代理被跳过）
        output1.Contains('\uFFFD').Should().BeFalse(
            "修复后 WrapText 应跳过末尾孤立高代理，不产生 U+FFFD");

        // 输出应为 "Hello "（末尾空格保留，高代理跳过）
        output1.Should().Be("Hello ");
    }

    [Fact]
    public void Diagnostic_WrapText_CompleteSurrogatePair_NoReplacementChar()
    {
        // 模拟流式 chunk 1 + chunk 2：完整代理对
        var complete = "Hello \uD83C\uDF89!";  // "Hello 🎉!"
        var lines = InvokeWrapText(complete, 20).ToList();
        var output = string.Join("", lines);

        Console.WriteLine($"  Complete output: [{output}]");
        Console.WriteLine($"  Contains U+FFFD: {output.Contains('\uFFFD')}");

        output.Contains('\uFFFD').Should().BeFalse(
            "完整代理对不应产生 U+FFFD");
    }

    [Fact]
    public void Diagnostic_WrapText_StreamingChunks_SeamlessTransition()
    {
        // 模拟完整的流式过程：chunk 1（截断）→ chunk 2（补全）
        // chunk 1: "Hello \uD83C"（只有高代理）
        var chunk1 = "Hello \uD83C";
        var lines1 = InvokeWrapText(chunk1, 20).ToList();
        var output1 = string.Join("", lines1);
        Console.WriteLine($"  Chunk1: [{output1}] FFFD={output1.Contains('\uFFFD')}");

        // chunk 2: "\uDF89!"（低代理 + 感叹号），追加到 chunk 1 后
        var complete = "Hello \uD83C\uDF89!";
        var lines2 = InvokeWrapText(complete, 20).ToList();
        var output2 = string.Join("", lines2);
        Console.WriteLine($"  Complete: [{output2}] FFFD={output2.Contains('\uFFFD')}");

        // 两个阶段都不应产生 U+FFFD
        output1.Contains('\uFFFD').Should().BeFalse("chunk 1 不应有 U+FFFD");
        output2.Contains('\uFFFD').Should().BeFalse("完整后不应有 U+FFFD");
    }

    [Fact]
    public void Diagnostic_WrapText_OnlyHighSurrogate_ProducesEmpty()
    {
        // 极端情况：文本只有一个高代理字符
        var onlyHigh = "\uD83C";
        var lines = InvokeWrapText(onlyHigh, 20).ToList();
        var output = string.Join("", lines);

        Console.WriteLine($"  Output: [{output}]");
        output.Contains('\uFFFD').Should().BeFalse("孤立高代理应被跳过，不产生 U+FFFD");
    }
}
