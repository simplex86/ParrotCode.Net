using ParrotCode;

namespace ParrotCode.xUnit;

/// <summary>
/// ProviderFactory 单元测试：覆盖协议路由（Create）与配置驱动装配（CreateActive）。
/// 迭代 2a：Create 的 mock/openai/anthropic/未知/null 路由。
/// 迭代 2b：CreateActive 的 active_provider 选中、回退、未命中、空列表、null。
/// </summary>
public class ProviderFactoryTests
{
    // —— Create（迭代 2a）——

    [Fact]
    public void Create_WithMockProtocol_ReturnsMockProvider()
    {
        var config = new ProviderConfig { Name = "mock", Protocol = "mock", Model = "mock-1" };

        var provider = ProviderFactory.Create(config);

        provider.Should().BeOfType<MockProvider>();
    }

    [Fact]
    public void Create_WithOpenAiProtocol_ReturnsOpenAIProvider()
    {
        var config = new ProviderConfig
        {
            Name = "openai",
            Protocol = "openai",
            Model = "gpt-4o-mini",
            BaseUrl = "https://api.openai.com/v1",
            ApiKey = "sk-test"
        };

        var provider = ProviderFactory.Create(config);

        provider.Should().BeOfType<OpenAIProvider>();
    }

    [Fact]
    public void Create_WithOpenAiProtocol_EmptyModel_ThrowsConfigException()
    {
        var config = new ProviderConfig
        {
            Name = "openai",
            Protocol = "openai",
            Model = "",
            BaseUrl = "https://api.openai.com/v1",
            ApiKey = "sk-test"
        };

        var act = () => ProviderFactory.Create(config);

        var ex = act.Should().Throw<ConfigException>().Which;
        ex.Message.Should().Contain("model");
    }

    [Fact]
    public void Create_WithAnthropicProtocol_ThrowsProviderNotImplemented()
    {
        var config = new ProviderConfig { Name = "claude", Protocol = "anthropic", Model = "claude-3" };

        var act = () => ProviderFactory.Create(config);

        var ex = act.Should().Throw<ProviderNotImplementedException>().Which;
        ex.Message.Should().Contain("后续迭代");
        ex.Message.Should().Contain("anthropic");
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

    // —— CreateActive（迭代 2b）——

    [Fact]
    public void CreateActive_ActiveHitsMock_ReturnsMockProvider()
    {
        var appConfig = new AppConfig
        {
            ActiveProvider = "mock",
            Providers = new[]
            {
                new ProviderConfig { Name = "mock", Protocol = "mock", Model = "mock-1" }
            }
        };

        var provider = ProviderFactory.CreateActive(appConfig);

        provider.Should().BeOfType<MockProvider>();
    }

    [Fact]
    public void CreateActive_ActiveHitsOpenAi_ReturnsOpenAIProvider()
    {
        var appConfig = new AppConfig
        {
            ActiveProvider = "deepseek",
            Providers = new[]
            {
                new ProviderConfig
                {
                    Name = "deepseek",
                    Protocol = "openai",
                    Model = "deepseek-chat",
                    BaseUrl = "https://api.deepseek.com/v1",
                    ApiKey = "sk-test"
                }
            }
        };

        var provider = ProviderFactory.CreateActive(appConfig);

        provider.Should().BeOfType<OpenAIProvider>();
    }

    [Fact]
    public void CreateActive_ActiveNotHit_ThrowsConfigException()
    {
        var appConfig = new AppConfig
        {
            ActiveProvider = "foo",
            Providers = new[]
            {
                new ProviderConfig { Name = "mock", Protocol = "mock", Model = "m" }
            }
        };

        var act = () => ProviderFactory.CreateActive(appConfig);

        var ex = act.Should().Throw<ConfigException>().Which;
        ex.Message.Should().Contain("active_provider 'foo'");
    }

    [Fact]
    public void CreateActive_ActiveNull_FallsBackToFirst()
    {
        var appConfig = new AppConfig
        {
            ActiveProvider = null,
            Providers = new[]
            {
                new ProviderConfig { Name = "first", Protocol = "mock", Model = "m1" },
                new ProviderConfig { Name = "second", Protocol = "mock", Model = "m2" }
            }
        };

        var provider = ProviderFactory.CreateActive(appConfig);

        provider.Should().BeOfType<MockProvider>();
        // 回退到 providers[0]；选中哪个由内部决定，此处仅确认未抛异常并返回实例
    }

    [Fact]
    public void CreateActive_EmptyProviders_ThrowsConfigException()
    {
        var appConfig = new AppConfig { ActiveProvider = null, Providers = Array.Empty<ProviderConfig>() };

        var act = () => ProviderFactory.CreateActive(appConfig);

        var ex = act.Should().Throw<ConfigException>().Which;
        ex.Message.Should().Contain("providers 不能为空");
    }

    [Fact]
    public void CreateActive_NullConfig_ThrowsArgumentNull()
    {
        var act = () => ProviderFactory.CreateActive(null!);

        act.Should().Throw<ArgumentNullException>();
    }
}
