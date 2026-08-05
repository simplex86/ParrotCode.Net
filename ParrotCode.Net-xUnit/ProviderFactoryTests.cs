using ParrotCode;

namespace ParrotCode.xUnit;

/// <summary>
/// ProviderFactory 单元测试：覆盖协议路由、未实现协议异常、未知协议异常、null 入参。
/// 迭代 2a：仅 mock 协议返回实例；openai/anthropic 抛 ProviderNotImplementedException。
/// </summary>
public class ProviderFactoryTests
{
    [Fact]
    public void Create_WithMockProtocol_ReturnsMockProvider()
    {
        var config = new ProviderConfig { Name = "mock", Protocol = "mock", Model = "mock-1" };

        var provider = ProviderFactory.Create(config);

        provider.Should().BeOfType<MockProvider>();
    }

    [Fact]
    public void Create_WithOpenAiProtocol_ThrowsProviderNotImplemented()
    {
        var config = new ProviderConfig { Name = "openai", Protocol = "openai", Model = "gpt-4o-mini" };

        var act = () => ProviderFactory.Create(config);

        var ex = act.Should().Throw<ProviderNotImplementedException>().Which;
        ex.Message.Should().Contain("迭代 3");
        ex.Message.Should().Contain("openai");
    }

    [Fact]
    public void Create_WithAnthropicProtocol_ThrowsProviderNotImplemented()
    {
        var config = new ProviderConfig { Name = "claude", Protocol = "anthropic", Model = "claude-3" };

        var act = () => ProviderFactory.Create(config);

        act.Should().Throw<ProviderNotImplementedException>();
    }

    [Fact]
    public void Create_WithUnknownProtocol_ThrowsArgumentWithMessage()
    {
        var config = new ProviderConfig { Name = "x", Protocol = "foo", Model = "m" };

        var act = () => ProviderFactory.Create(config);

        var ex = act.Should().Throw<ArgumentException>().Which;
        ex.Message.Should().Contain("不支持的协议");
        ex.Message.Should().Contain("foo");
    }

    [Fact]
    public void Create_WithEmptyProtocol_ThrowsArgument()
    {
        // 空串走 switch default 分支，与未知协议同处理
        var config = new ProviderConfig { Name = "x", Protocol = "", Model = "m" };

        var act = () => ProviderFactory.Create(config);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Create_WithNullConfig_ThrowsArgumentNull()
    {
        var act = () => ProviderFactory.Create(null!);

        act.Should().Throw<ArgumentNullException>();
    }
}
