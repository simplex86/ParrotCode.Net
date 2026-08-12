using System.Text.Json;
using ParrotCode;

namespace ParrotCode.xUnit;

/// <summary>
/// Fork 模式历史清理测试（bug 修复：HTTP 400 "tool_calls must be followed by tool messages"）。
///
/// 主 Agent 调用 sub_agent 时，AgentLoop 已把 assistant(tool_calls) 入历史，
/// 但 tool 结果还没入（工具正在执行中）。Fork 模式直接复制会导致 OpenAI API 400。
/// <see cref="SubAgentRunner.TrimIncompleteToolCalls"/> 负责截断末尾未完成的 assistant(tool_calls)。
/// </summary>
public class SubAgentForkHistoryTests
{
    private static readonly JsonElement EmptyInput = JsonDocument.Parse("{}").RootElement.Clone();

    private static Message UserMsg(string text) => new(MessageRole.User, text);
    private static Message AssistantMsg(string text) => new(MessageRole.Assistant, text);

    private static Message AssistantWithCalls(string text, params ToolCall[] calls) =>
        new(MessageRole.Assistant, text) { ToolCalls = calls };

    private static Message ToolMsg(string content, string toolCallId) =>
        new(MessageRole.Tool, content) { ToolCallId = toolCallId };

    private static ToolCall Call(string id, string name = "sub_agent") =>
        new(id, name, EmptyInput);

    [Fact]
    public void Trim_EmptyHistory_ReturnsEmpty()
    {
        var result = SubAgentRunner.TrimIncompleteToolCalls(Array.Empty<Message>());
        result.Should().BeEmpty();
    }

    [Fact]
    public void Trim_NoToolCalls_ReturnsUnchanged()
    {
        var messages = new[]
        {
            UserMsg("你好"),
            AssistantMsg("你好（mock）")
        };

        var result = SubAgentRunner.TrimIncompleteToolCalls(messages);
        result.Should().HaveCount(2);
        result[1].Role.Should().Be(MessageRole.Assistant);
        result[1].ToolCalls.Should().BeNull();
    }

    [Fact]
    public void Trim_TrailingAssistantWithToolCallsNoResponse_Truncates()
    {
        // 模拟主 Agent 调用 sub_agent 时的历史状态：
        // assistant(tool_calls: [sub_agent]) 在末尾，没有对应的 tool 消息
        var messages = new[]
        {
            UserMsg("总结对话"),
            AssistantWithCalls("", Call("call-1", "sub_agent"))
        };

        var result = SubAgentRunner.TrimIncompleteToolCalls(messages);
        result.Should().HaveCount(1);
        result[0].Role.Should().Be(MessageRole.User);
    }

    [Fact]
    public void Trim_TrailingAssistantWithPartialResponse_Truncates()
    {
        // assistant 调用了两个工具，但只有一个 tool 响应 → 不完整，截断到 assistant 之前
        var messages = new[]
        {
            UserMsg("读取并总结"),
            AssistantWithCalls("", Call("call-1", "read_file"), Call("call-2", "sub_agent")),
            ToolMsg("文件内容", "call-1")
            // call-2 没有响应
        };

        var result = SubAgentRunner.TrimIncompleteToolCalls(messages);
        result.Should().HaveCount(1);
        result[0].Role.Should().Be(MessageRole.User);
    }

    [Fact]
    public void Trim_TrailingAssistantWithCompleteResponse_ReturnsUnchanged()
    {
        // assistant 调用了两个工具，都有响应 → 完整，不截断
        var messages = new[]
        {
            UserMsg("读取并总结"),
            AssistantWithCalls("", Call("call-1", "read_file"), Call("call-2", "grep")),
            ToolMsg("文件内容", "call-1"),
            ToolMsg("搜索结果", "call-2")
        };

        var result = SubAgentRunner.TrimIncompleteToolCalls(messages);
        result.Should().HaveCount(4);
    }

    [Fact]
    public void Trim_CompleteHistoryWithTrailingIncomplete_TruncatesOnlyTail()
    {
        // 完整对话 + 末尾未完成的 assistant(tool_calls)
        // 模拟真实场景：主 Agent 已完成几轮对话，最后调用 sub_agent
        var messages = new[]
        {
            UserMsg("帮我分析代码"),
            AssistantWithCalls("", Call("call-1", "read_file")),
            ToolMsg("代码内容", "call-1"),
            AssistantMsg("分析完成，现在委派子任务"),
            AssistantWithCalls("", Call("call-2", "sub_agent"))  // 未完成
        };

        var result = SubAgentRunner.TrimIncompleteToolCalls(messages);
        result.Should().HaveCount(4);
        result[3].Role.Should().Be(MessageRole.Assistant);
        result[3].ToolCalls.Should().BeNull();
    }

    [Fact]
    public void Trim_TrailingUserMessage_ReturnsUnchanged()
    {
        var messages = new[]
        {
            UserMsg("你好"),
            AssistantMsg("你好（mock）"),
            UserMsg("总结对话")
        };

        var result = SubAgentRunner.TrimIncompleteToolCalls(messages);
        result.Should().HaveCount(3);
    }

    [Fact]
    public void Trim_TrailingToolMessage_ReturnsUnchanged()
    {
        // 末尾是 tool 消息（完整轮次），不截断
        var messages = new[]
        {
            UserMsg("读取文件"),
            AssistantWithCalls("", Call("call-1", "read_file")),
            ToolMsg("内容", "call-1")
        };

        var result = SubAgentRunner.TrimIncompleteToolCalls(messages);
        result.Should().HaveCount(3);
    }
}
