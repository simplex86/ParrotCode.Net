using ParrotCode;

namespace ParrotCode.xUnit;

/// <summary>
/// TokenEstimator 单元测试：覆盖字符数/3 向上取整公式、空输入、中文、消息列表求和。
/// </summary>
public class TokenEstimatorTests
{
    // ---- Estimate(string) ----

    [Fact]
    public void Estimate_EmptyString_ReturnsZero()
    {
        TokenEstimator.Estimate("").Should().Be(0);
    }

    [Fact]
    public void Estimate_NullString_ReturnsZero()
    {
        TokenEstimator.Estimate((string?)null).Should().Be(0);
    }

    [Fact]
    public void Estimate_SingleChar_ReturnsOne()
    {
        // 1 字符 → 1/3 → 向上取整 → 1
        TokenEstimator.Estimate("a").Should().Be(1);
    }

    [Fact]
    public void Estimate_TwoChars_ReturnsOne()
    {
        // 2 字符 → 2/3 → 向上取整 → 1
        TokenEstimator.Estimate("ab").Should().Be(1);
    }

    [Fact]
    public void Estimate_ThreeChars_ReturnsOne()
    {
        // 3 字符 → 3/3 = 1
        TokenEstimator.Estimate("abc").Should().Be(1);
    }

    [Fact]
    public void Estimate_FourChars_ReturnsTwo()
    {
        // 4 字符 → 4/3 → 向上取整 → 2
        TokenEstimator.Estimate("abcd").Should().Be(2);
    }

    [Fact]
    public void Estimate_SixChars_ReturnsTwo()
    {
        // 6 字符 → 6/3 = 2
        TokenEstimator.Estimate("abcdef").Should().Be(2);
    }

    [Fact]
    public void Estimate_ChineseChars_TwoChars_ReturnsOne()
    {
        // "你好" = 2 字符 → 2/3 → 向上取整 → 1
        TokenEstimator.Estimate("你好").Should().Be(1);
    }

    [Fact]
    public void Estimate_LongChineseText_SixChars_ReturnsTwo()
    {
        // "你好世界测试" = 6 字符 → 6/3 = 2
        TokenEstimator.Estimate("你好世界测试").Should().Be(2);
    }

    [Fact]
    public void Estimate_MixedChineseEnglish_SevenChars_ReturnsThree()
    {
        // "hello你好" = 7 字符 → 7/3 → 向上取整 → 3
        TokenEstimator.Estimate("hello你好").Should().Be(3);
    }

    [Fact]
    public void Estimate_LongText_GrowthPattern()
    {
        // 验证线性增长：3/6/9/12 字符 → 1/2/3/4 tokens
        TokenEstimator.Estimate("abc").Should().Be(1);
        TokenEstimator.Estimate("abcdef").Should().Be(2);
        TokenEstimator.Estimate("abcdefghi").Should().Be(3);
        TokenEstimator.Estimate("abcdefghijkl").Should().Be(4);
    }

    // ---- Estimate(Message) ----

    [Fact]
    public void Estimate_SingleMessage_EqualsContentEstimate()
    {
        var message = new Message(MessageRole.User, "abc");

        TokenEstimator.Estimate(message).Should().Be(TokenEstimator.Estimate("abc"));
    }

    [Fact]
    public void Estimate_MessageWithEmptyContent_ReturnsZero()
    {
        var message = new Message(MessageRole.User, "");

        TokenEstimator.Estimate(message).Should().Be(0);
    }

    [Fact]
    public void Estimate_AssistantMessage_EqualsContentEstimate()
    {
        var message = new Message(MessageRole.Assistant, "abcdef");

        TokenEstimator.Estimate(message).Should().Be(2);
    }

    // ---- Estimate(IReadOnlyList<Message>) ----

    [Fact]
    public void Estimate_MessageList_SumsAll()
    {
        var messages = new Message[]
        {
            new(MessageRole.User, "abc"),       // 1 token
            new(MessageRole.Assistant, "def")   // 1 token
        };

        TokenEstimator.Estimate(messages).Should().Be(2);
    }

    [Fact]
    public void Estimate_EmptyMessageList_ReturnsZero()
    {
        var messages = Array.Empty<Message>();

        TokenEstimator.Estimate(messages).Should().Be(0);
    }

    [Fact]
    public void Estimate_MessageList_WithEmptyContentMessage_ReturnsZero()
    {
        var messages = new Message[]
        {
            new(MessageRole.User, "")
        };

        TokenEstimator.Estimate(messages).Should().Be(0);
    }

    [Fact]
    public void Estimate_MessageList_MultipleMessages_SumsCorrectly()
    {
        var messages = new Message[]
        {
            new(MessageRole.User, "abc"),           // 1
            new(MessageRole.Assistant, "abcdef"),   // 2
            new(MessageRole.User, "a"),             // 1
            new(MessageRole.Assistant, "")          // 0
        };

        TokenEstimator.Estimate(messages).Should().Be(4);
    }
}
