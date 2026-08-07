using Terminal.Gui;

namespace ParrotCode.xUnit;

/// <summary>
/// Terminal.Gui 共享测试 Fixture。
/// 确保 Application.Init/Shutdown 在整个测试集合中只调用一次。
/// </summary>
public class TerminalGuiFixture : IDisposable
{
    public TerminalGuiFixture()
    {
        Application.Init();
    }

    public void Dispose()
    {
        Application.Shutdown();
        GC.SuppressFinalize(this);
    }
}

/// <summary>
/// Terminal.Gui 测试集合定义——所有依赖 Terminal.Gui Application 的测试共享此集合。
/// xUnit 不会并行执行同一集合内的测试，避免 Application 状态竞争。
/// </summary>
[CollectionDefinition("Terminal.Gui")]
public class TerminalGuiCollection : ICollectionFixture<TerminalGuiFixture> { }
