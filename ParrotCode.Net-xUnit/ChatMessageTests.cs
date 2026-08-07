using Terminal.Gui;

namespace ParrotCode.xUnit;

/// <summary>
/// ChatMessage 单元测试（迭代 7c-2）。
/// 纯 C# 测试，不依赖 Terminal.Gui 初始化。
/// </summary>
public class ChatMessageTests
{
    [Fact]
    public void Format_User_ReturnsWithArrowPrefix()
    {
        var msg = new ChatMessage(MessageType.User, "hello");
        msg.Format().Should().Be("❯ hello");
    }

    [Fact]
    public void Format_Assistant_ReturnsWithCirclePrefix()
    {
        var msg = new ChatMessage(MessageType.Assistant, "writing code");
        msg.Format().Should().Be("⏺ writing code");
    }

    [Fact]
    public void Format_ToolCall_ReturnsContentAsIs()
    {
        var msg = new ChatMessage(MessageType.ToolCall, "  ⎿ → read_file({\"path\":\"a.txt\"})");
        msg.Format().Should().Be("  ⎿ → read_file({\"path\":\"a.txt\"})");
    }

    [Fact]
    public void GetColor_Assistant_ReturnsBrightCyan()
    {
        var msg = new ChatMessage(MessageType.Assistant, "text");
        // Color.BrightCyan 是 ColorName16 枚举字段，需显式转为 Color 结构体
        msg.GetColor().Should().Be((Terminal.Gui.Color)Terminal.Gui.Color.BrightCyan);
    }

    [Fact]
    public void GetColor_Error_ReturnsRed()
    {
        var msg = new ChatMessage(MessageType.Error, "failed");
        msg.GetColor().Should().Be((Terminal.Gui.Color)Terminal.Gui.Color.Red);
    }
}
