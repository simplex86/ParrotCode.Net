using System.Text;
using System.Text.Json;
using ParrotCode;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace ParrotCode.xUnit;

/// <summary>
/// TuiApp 集成测试（端到端，无 HITL）。
/// 用 MockProvider 脚本注入 LLM 响应，TestConsole 捕获输出。
/// useLive=false 避免测试环境 Live 渲染，用降级行模式验证事件流与输出。
/// </summary>
public class TuiAppIntegrationTests
{
    /// <summary>
    /// 测试用 IConsole：队列返回按键，捕获输出（含 IRenderable 渲染为纯文本）。
    /// </summary>
    private sealed class TestConsole : IConsole
    {
        private readonly Queue<ConsoleKeyInfo> _keys = new();
        private readonly StringBuilder _output = new();

        public string Output => _output.ToString();

        public void EnqueueText(string text)
        {
            foreach (var ch in text)
                _keys.Enqueue(new ConsoleKeyInfo(ch, ConsoleKey.None, false, false, false));
        }

        public void EnqueueEnter() => _keys.Enqueue(new ConsoleKeyInfo('\0', ConsoleKey.Enter, false, false, false));
        public void EnqueueExit() => EnqueueText("/exit");

        public ConsoleKeyInfo ReadKey(bool intercept)
        {
            if (_keys.Count == 0)
                throw new InvalidOperationException("TestConsole: 无可用按键");
            return _keys.Dequeue();
        }

        public void Write(string text) => _output.Append(text);
        public void WriteLine() => _output.AppendLine();
        public void WriteMarkup(string markup) => _output.Append(markup);
        public void WriteMarkupLine(string markup) => _output.AppendLine(markup);

        public void Write(IRenderable renderable)
        {
            // 把 IRenderable 渲染为纯文本（去掉 ANSI/Markup 标记）
            using var sw = new StringWriter();
            var console = AnsiConsole.Create(new AnsiConsoleSettings
            {
                Out = new AnsiConsoleOutput(sw),
                ColorSystem = ColorSystemSupport.NoColors,
                Ansi = AnsiSupport.No,
                Interactive = InteractionSupport.No
            });
            console.Write(renderable);
            _output.Append(sw.ToString());
        }
    }

    private static ChatChunk[] TextScript(string text) =>
        new ChatChunk[] { new ChatChunk.TextDelta(text), new ChatChunk.Done() };

    private static ChatChunk[] ToolCallScript(string id, string name, string argsJson) =>
        new ChatChunk[] { new ChatChunk.ToolCallDelta(0, id, name, argsJson), new ChatChunk.Done() };

    /// <summary>创建并运行 TuiApp，返回捕获的输出。</summary>
    private static async Task<string> RunTuiAppAsync(
        MockProvider provider,
        AgentConfig? agentConfig = null,
        Action<TestConsole>? setupKeys = null)
    {
        var console = new TestConsole();
        setupKeys?.Invoke(console);

        var tuiConfig = new TuiConfig { Mode = "console", EnableHitl = false };
        var app = new TuiApp(
            provider,
            new ProviderConfig { Name = "mock", Protocol = "mock", Model = "mock-1" },
            agentConfig ?? new AgentConfig(),
            tuiConfig,
            SecurityLevel.Normal,
            logger: null,
            CancellationToken.None,
            console: console,
            useLive: false);

        await app.RunAsync();
        return console.Output;
    }

    [Fact]
    public async Task EndToEnd_NoTool_RendersText()
    {
        var provider = new MockProvider();
        provider.EnqueueScript(TextScript("hello from mock"));

        var output = await RunTuiAppAsync(
            provider,
            setupKeys: console =>
            {
                console.EnqueueText("hi");
                console.EnqueueEnter();
                console.EnqueueExit();
                console.EnqueueEnter();
            });

        output.Should().Contain("hello from mock");
        output.Should().Contain("你：");
    }

    [Fact]
    public async Task EndToEnd_SlashClear_ClearsHistory()
    {
        var provider = new MockProvider();
        provider.EnqueueScript(TextScript("response"));

        var output = await RunTuiAppAsync(
            provider,
            setupKeys: console =>
            {
                console.EnqueueText("test");
                console.EnqueueEnter();
                console.EnqueueText("/clear");
                console.EnqueueEnter();
                console.EnqueueExit();
                console.EnqueueEnter();
            });

        output.Should().Contain("已清空对话历史");
    }

    [Fact]
    public async Task EndToEnd_SlashStatus_PrintsStatusBar()
    {
        var output = await RunTuiAppAsync(
            new MockProvider(),
            setupKeys: console =>
            {
                console.EnqueueText("/status");
                console.EnqueueEnter();
                console.EnqueueExit();
                console.EnqueueEnter();
            });

        // 状态栏应含 provider/model/security/ctx/round/tools 字段
        output.Should().Contain("provider=");
        output.Should().Contain("model=");
        output.Should().Contain("security=");
        output.Should().Contain("ctx=");
        output.Should().Contain("round=");
        output.Should().Contain("tools=");
    }

    [Fact]
    public async Task EndToEnd_SlashHelp_PrintsCommands()
    {
        var output = await RunTuiAppAsync(
            new MockProvider(),
            setupKeys: console =>
            {
                console.EnqueueText("/help");
                console.EnqueueEnter();
                console.EnqueueExit();
                console.EnqueueEnter();
            });

        output.Should().Contain("可用命令");
        output.Should().Contain("/clear");
        output.Should().Contain("/status");
        output.Should().Contain("/help");
        output.Should().Contain("/exit");
    }

    [Fact]
    public async Task EndToEnd_MultiRound_RendersBothRounds()
    {
        var provider = new MockProvider();
        // 第一轮：工具调用 → 第二轮
        provider.EnqueueScript(ToolCallScript("call_1", "nonexistent_tool", "{}"));
        // 第二轮：文本回复 → AgentDone
        provider.EnqueueScript(TextScript("done after tool"));

        var output = await RunTuiAppAsync(
            provider,
            setupKeys: console =>
            {
                console.EnqueueText("do something");
                console.EnqueueEnter();
                console.EnqueueExit();
                console.EnqueueEnter();
            });

        // 应含工具调用渲染和最终文本
        output.Should().Contain("nonexistent_tool");
        output.Should().Contain("done after tool");
    }

    [Fact]
    public async Task EndToEnd_MaxRounds_RendersWarning()
    {
        var provider = new MockProvider();
        // 两轮都返回工具调用 → 达到最大轮次
        provider.EnqueueScript(ToolCallScript("call_1", "nonexistent_tool", "{}"));
        provider.EnqueueScript(ToolCallScript("call_2", "nonexistent_tool", "{}"));

        var agentConfig = new AgentConfig { MaxRounds = 2 };

        var output = await RunTuiAppAsync(
            provider,
            agentConfig: agentConfig,
            setupKeys: console =>
            {
                console.EnqueueText("loop");
                console.EnqueueEnter();
                console.EnqueueExit();
                console.EnqueueEnter();
            });

        output.Should().Contain("最大轮次");
        output.Should().Contain("2");
    }

    [Fact]
    public async Task EndToEnd_Cancelled_ExitsCleanly()
    {
        // 预取消的 token 应让 TuiApp 立即退出不崩溃
        var console = new TestConsole();
        var provider = new MockProvider();

        var cts = new CancellationTokenSource();
        cts.Cancel();

        var app = new TuiApp(
            provider,
            new ProviderConfig { Name = "mock", Protocol = "mock", Model = "mock-1" },
            new AgentConfig(),
            new TuiConfig { Mode = "console" },
            SecurityLevel.Normal,
            logger: null,
            cts.Token,
            console: console,
            useLive: false);

        // 应不抛异常地退出
        await app.RunAsync();
    }

    [Fact]
    public async Task EndToEnd_Banner_PrintsModeAndToolCount()
    {
        var output = await RunTuiAppAsync(
            new MockProvider(),
            setupKeys: console =>
            {
                console.EnqueueExit();
                console.EnqueueEnter();
            });

        // 启动横幅应含模式标记和工具数
        output.Should().Contain("TUI 模式");
        output.Should().Contain("tools=");
    }
}
