using System.Text.Json;
using ParrotCode;

namespace ParrotCode.xUnit;

/// <summary>
/// ConversationHistory 工具调用重载 + MessageExtensions.ToOpenAiWire 单元测试。
/// 覆盖 AddAssistant(content, toolCalls) / AddTool(content, toolCallId) 存储与参数校验，
/// 以及 ToOpenAiWire 在 assistant+ToolCalls / tool+ToolCallId / 普通消息下的 wire 序列化。
/// </summary>
public class HistoryToolCallTests
{
    private static ToolCall MakeToolCall(string id, string name, string argsJson = "{}")
    {
        using var doc = JsonDocument.Parse(argsJson);
        return new ToolCall(id, name, doc.RootElement.Clone());
    }

    // ---- AddAssistant(content, toolCalls) ----

    [Fact]
    public void AddAssistant_WithToolCalls_StoresToolCalls()
    {
        var history = new ConversationHistory();
        var toolCalls = new[] { MakeToolCall("call_1", "get_weather", "{\"city\":\"北京\"}") };

        history.AddAssistant("", toolCalls);
        var messages = history.ToProviderMessages();

        messages.Should().HaveCount(1);
        messages[0].Role.Should().Be(MessageRole.Assistant);
        messages[0].ToolCalls.Should().NotBeNull();
        messages[0].ToolCalls.Should().HaveCount(1);
        messages[0].ToolCalls![0].Id.Should().Be("call_1");
        messages[0].ToolCalls![0].Name.Should().Be("get_weather");
    }

    [Fact]
    public void AddAssistant_WithEmptyToolCalls_Throws()
    {
        var history = new ConversationHistory();

        var act = () => history.AddAssistant("", Array.Empty<ToolCall>());

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void AddAssistant_WithNullToolCalls_Throws()
    {
        var history = new ConversationHistory();

        var act = () => history.AddAssistant("", null!);

        act.Should().Throw<ArgumentNullException>();
    }

    // ---- AddTool(content, toolCallId) ----

    [Fact]
    public void AddTool_WithToolCallId_StoresToolCallId()
    {
        var history = new ConversationHistory();

        history.AddTool("result", "call_1");
        var messages = history.ToProviderMessages();

        messages.Should().HaveCount(1);
        messages[0].Role.Should().Be(MessageRole.Tool);
        messages[0].Content.Should().Be("result");
        messages[0].ToolCallId.Should().Be("call_1");
    }

    [Fact]
    public void AddTool_WithNullToolCallId_Throws()
    {
        var history = new ConversationHistory();

        var act = () => history.AddTool("result", null!);

        act.Should().Throw<ArgumentNullException>();
    }

    // ---- ToOpenAiWire ----

    [Fact]
    public void ToOpenAiWire_AssistantWithToolCalls_ProducesToolCallsArray()
    {
        var message = new Message(MessageRole.Assistant, "")
        {
            ToolCalls = new[] { MakeToolCall("call_1", "get_weather", "{\"city\":\"北京\"}") }
        };

        var json = JsonSerializer.Serialize(message.ToOpenAiWire());

        json.Should().Contain("tool_calls");
        json.Should().Contain("function");
        json.Should().Contain("get_weather");
    }

    [Fact]
    public void ToOpenAiWire_ToolWithToolCallId_ProducesToolCallId()
    {
        var message = new Message(MessageRole.Tool, "result") { ToolCallId = "call_1" };

        var json = JsonSerializer.Serialize(message.ToOpenAiWire());

        json.Should().Contain("tool_call_id");
        json.Should().Contain("call_1");
    }

    [Fact]
    public void ToOpenAiWire_PlainUser_ProducesRoleAndContent()
    {
        var message = new Message(MessageRole.User, "hello");

        var json = JsonSerializer.Serialize(message.ToOpenAiWire());

        json.Should().Contain("\"role\":\"user\"");
        json.Should().Contain("\"content\":\"hello\"");
        json.Should().NotContain("tool_calls");
        json.Should().NotContain("tool_call_id");
    }
}
