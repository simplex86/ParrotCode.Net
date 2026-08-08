using ParrotCode;

namespace ParrotCode.xUnit;

/// <summary>
/// ConversationHistory 单元测试：覆盖 Add 方法、顺序保持、快照语义、Clear、token 估算。
/// </summary>
public class ConversationHistoryTests
{
    // ---- Add 方法 + Count ----

    [Fact]
    public void AddUser_IncrementsCount()
    {
        var history = new ConversationHistory();

        history.AddUser("hello");

        history.Count.Should().Be(1);
    }

    [Fact]
    public void AddAssistant_IncrementsCount()
    {
        var history = new ConversationHistory();

        history.AddAssistant("hi");

        history.Count.Should().Be(1);
    }

    [Fact]
    public void AddTool_IncrementsCount()
    {
        var history = new ConversationHistory();

        history.AddTool("result");

        history.Count.Should().Be(1);
    }

    // ---- 存储内容验证 ----

    [Fact]
    public void AddUser_StoresUserMessage()
    {
        var history = new ConversationHistory();

        history.AddUser("hello");
        var messages = history.ToProviderMessages();

        messages.Should().HaveCount(1);
        messages[0].Role.Should().Be(MessageRole.User);
        messages[0].Content.Should().Be("hello");
    }

    [Fact]
    public void AddAssistant_StoresAssistantMessage()
    {
        var history = new ConversationHistory();

        history.AddAssistant("hi");
        var messages = history.ToProviderMessages();

        messages.Should().HaveCount(1);
        messages[0].Role.Should().Be(MessageRole.Assistant);
        messages[0].Content.Should().Be("hi");
    }

    [Fact]
    public void AddTool_StoresToolMessage()
    {
        var history = new ConversationHistory();

        history.AddTool("result");
        var messages = history.ToProviderMessages();

        messages.Should().HaveCount(1);
        messages[0].Role.Should().Be(MessageRole.Tool);
        messages[0].Content.Should().Be("result");
    }

    // ---- 顺序保持 ----

    [Fact]
    public void MultipleAdds_MaintainOrder()
    {
        var history = new ConversationHistory();

        history.AddUser("第一");
        history.AddAssistant("第二");
        history.AddUser("第三");
        var messages = history.ToProviderMessages();

        messages.Should().HaveCount(3);
        messages[0].Role.Should().Be(MessageRole.User);
        messages[0].Content.Should().Be("第一");
        messages[1].Role.Should().Be(MessageRole.Assistant);
        messages[1].Content.Should().Be("第二");
        messages[2].Role.Should().Be(MessageRole.User);
        messages[2].Content.Should().Be("第三");
    }

    [Fact]
    public void RepeatedAdds_AccumulateCorrectly()
    {
        var history = new ConversationHistory();

        history.AddUser("u1");
        history.AddUser("u2");
        history.AddUser("u3");
        history.AddAssistant("a1");
        history.AddAssistant("a2");

        history.Count.Should().Be(5);
    }

    // ---- Clear ----

    [Fact]
    public void Clear_EmptiesHistory()
    {
        var history = new ConversationHistory();
        history.AddUser("hello");
        history.AddAssistant("hi");

        history.Clear();

        history.Count.Should().Be(0);
        history.ToProviderMessages().Should().BeEmpty();
    }

    [Fact]
    public void Clear_OnEmptyHistory_NoOp()
    {
        var history = new ConversationHistory();

        var act = () => history.Clear();

        act.Should().NotThrow();
        history.Count.Should().Be(0);
    }

    // ---- ToProviderMessages 快照语义 ----

    [Fact]
    public void ToProviderMessages_ReturnsSnapshot()
    {
        var history = new ConversationHistory();
        history.AddUser("first");

        var snapshot = history.ToProviderMessages();

        // 取快照后再 Add，快照内容不变
        history.AddAssistant("second");

        snapshot.Should().HaveCount(1);
        snapshot[0].Content.Should().Be("first");
        history.Count.Should().Be(2);  // 但 History 内部已更新
    }

    [Fact]
    public void ToProviderMessages_ModifyingReturnDoesNotAffectHistory()
    {
        var history = new ConversationHistory();
        history.AddUser("hello");

        var snapshot = history.ToProviderMessages();
        // 尝试修改返回的数组（强制转型为数组）
        if (snapshot is Message[] array)
        {
            array[0] = new Message(MessageRole.Tool, "tampered");
        }

        // History 内部不受影响
        history.Count.Should().Be(1);
        var fresh = history.ToProviderMessages();
        fresh[0].Role.Should().Be(MessageRole.User);
        fresh[0].Content.Should().Be("hello");
    }

    [Fact]
    public void ToProviderMessages_OnEmptyHistory_ReturnsEmpty()
    {
        var history = new ConversationHistory();

        var messages = history.ToProviderMessages();

        messages.Should().BeEmpty();
    }

    // ---- EstimatedTokens ----

    [Fact]
    public void EstimatedTokens_ZeroOnEmpty()
    {
        var history = new ConversationHistory();

        history.EstimatedTokens.Should().Be(0);
    }

    [Fact]
    public void EstimatedTokens_IncreasesWithMessages()
    {
        var history = new ConversationHistory();
        history.AddUser("abc");  // 3 字符 → 1 token
        var tokensAfterFirst = history.EstimatedTokens;

        history.AddAssistant("defghi");  // 6 字符 → 2 tokens
        var tokensAfterSecond = history.EstimatedTokens;

        tokensAfterSecond.Should().BeGreaterThan(tokensAfterFirst);
    }

    [Fact]
    public void EstimatedTokens_AfterClear_ReturnsZero()
    {
        var history = new ConversationHistory();
        history.AddUser("some content");

        history.Clear();

        history.EstimatedTokens.Should().Be(0);
    }

    // ---- 边界 ----

    [Fact]
    public void AddUser_EmptyContent_StoresEmptyMessage()
    {
        var history = new ConversationHistory();

        history.AddUser("");

        history.Count.Should().Be(1);
        history.ToProviderMessages()[0].Content.Should().Be("");
    }

    [Fact]
    public void AddUser_NullContent_ThrowsArgumentNullException()
    {
        var history = new ConversationHistory();

        var act = () => history.AddUser(null!);

        act.Should().Throw<ArgumentNullException>();
    }

    // ---- AddSystem（迭代 9 新增）----

    [Fact]
    public void AddSystem_StoresSystemMessage()
    {
        var history = new ConversationHistory();

        history.AddSystem("摘要内容");

        history.Count.Should().Be(1);
        var msg = history.ToProviderMessages()[0];
        msg.Role.Should().Be(MessageRole.System);
        msg.Content.Should().Be("摘要内容");
    }

    [Fact]
    public void AddSystem_NullContent_ThrowsArgumentNullException()
    {
        var history = new ConversationHistory();

        var act = () => history.AddSystem(null!);

        act.Should().Throw<ArgumentNullException>();
    }

    // ---- ReplaceMessages（迭代 9 新增）----

    [Fact]
    public void ReplaceMessages_ReplacesAllMessages()
    {
        var history = new ConversationHistory();
        history.AddUser("旧消息1");
        history.AddAssistant("旧消息2");

        var newMessages = new List<Message>
        {
            new(MessageRole.System, "摘要"),
            new(MessageRole.User, "保留的消息")
        };
        history.ReplaceMessages(newMessages);

        history.Count.Should().Be(2);
        var msgs = history.ToProviderMessages();
        msgs[0].Role.Should().Be(MessageRole.System);
        msgs[0].Content.Should().Be("摘要");
        msgs[1].Role.Should().Be(MessageRole.User);
        msgs[1].Content.Should().Be("保留的消息");
    }

    [Fact]
    public void ReplaceMessages_NullArgument_ThrowsArgumentNullException()
    {
        var history = new ConversationHistory();

        var act = () => history.ReplaceMessages(null!);

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void ReplaceMessages_EmptyList_ClearsHistory()
    {
        var history = new ConversationHistory();
        history.AddUser("old");

        history.ReplaceMessages(Array.Empty<Message>());

        history.Count.Should().Be(0);
    }

    [Fact]
    public void ReplaceMessages_EstimatedTokens_ReflectsNewMessages()
    {
        var history = new ConversationHistory();
        history.AddUser(new string('a', 300));  // ceil(300/3) = 100 tokens
        history.EstimatedTokens.Should().Be(100);

        history.ReplaceMessages(new[] { new Message(MessageRole.System, "short") });  // ceil(5/3) = 2 tokens

        history.EstimatedTokens.Should().Be(2);
    }
}
