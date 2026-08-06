using ParrotCode;

namespace ParrotCode.xUnit;

/// <summary>
/// MessageExtensions 单元测试：覆盖 ToOpenAiRoleString 角色映射 + EstimateTokens 便捷扩展。
/// </summary>
public class MessageExtensionsTests
{
    // ---- ToOpenAiRoleString ----

    [Fact]
    public void ToOpenAiRoleString_System_ReturnsSystem()
    {
        MessageRole.System.ToOpenAiRoleString().Should().Be("system");
    }

    [Fact]
    public void ToOpenAiRoleString_User_ReturnsUser()
    {
        MessageRole.User.ToOpenAiRoleString().Should().Be("user");
    }

    [Fact]
    public void ToOpenAiRoleString_Assistant_ReturnsAssistant()
    {
        MessageRole.Assistant.ToOpenAiRoleString().Should().Be("assistant");
    }

    [Fact]
    public void ToOpenAiRoleString_Tool_ReturnsTool()
    {
        MessageRole.Tool.ToOpenAiRoleString().Should().Be("tool");
    }

    [Fact]
    public void ToOpenAiRoleString_UnknownEnum_FallsBackToUser()
    {
        var unknownRole = (MessageRole)999;

        unknownRole.ToOpenAiRoleString().Should().Be("user");
    }

    // ---- EstimateTokens(this Message) ----

    [Fact]
    public void EstimateTokens_OnMessage_DelegatesToEstimator()
    {
        var message = new Message(MessageRole.User, "abc");

        message.EstimateTokens().Should().Be(TokenEstimator.Estimate(message));
    }

    [Fact]
    public void EstimateTokens_OnMessage_WithEmptyContent_ReturnsZero()
    {
        var message = new Message(MessageRole.User, "");

        message.EstimateTokens().Should().Be(0);
    }

    [Fact]
    public void EstimateTokens_OnMessage_FourChars_ReturnsTwo()
    {
        var message = new Message(MessageRole.Assistant, "abcd");

        message.EstimateTokens().Should().Be(2);
    }

    // ---- EstimateTokens(this IReadOnlyList<Message>) ----

    [Fact]
    public void EstimateTokens_OnMessageList_DelegatesToEstimator()
    {
        var messages = new Message[]
        {
            new(MessageRole.User, "abc"),       // 1
            new(MessageRole.Assistant, "def")   // 1
        };

        messages.EstimateTokens().Should().Be(2);
    }

    [Fact]
    public void EstimateTokens_OnEmptyMessageList_ReturnsZero()
    {
        var messages = Array.Empty<Message>();

        messages.EstimateTokens().Should().Be(0);
    }
}
