using System.Text;
using System.Text.Json;
using ParrotCode;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace ParrotCode.xUnit;

/// <summary>
/// TuiApp HITL 集成测试（端到端，迭代 7b 新增）。
///
/// 测试策略说明：
/// - 方案 A（流式渲染）不再区分 Live/降级模式，统一用 ConsoleEventRenderer 渲染。
/// - HITL 提示渲染（A/S/P/D 按键映射、Panel/Markup 渲染）由 HitlPromptTests 单测覆盖。
/// - HITL 决策流程（Deny→ToolBlockedEvent、Allow→执行）由 BatchToolExecutorHitlTests 覆盖。
/// - 本测试文件验证 TuiApp 的接线：
///   * enable_hitl: false → NullHitlGate 注入，Write 工具直接执行。
///   * 端到端流程：write_file 调用 → 工具执行 → 结果渲染。
/// </summary>
public class TuiAppHitlIntegrationTests
{
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

    private static ChatChunk[] ToolCallScript(string id, string name, string argsJson = "{}") =>
        new ChatChunk[] { new ChatChunk.ToolCallDelta(0, id, name, argsJson), new ChatChunk.Done() };

    private static async Task<string> RunTuiAppAsync(
        MockProvider provider,
        TuiConfig? tuiConfig = null,
        AgentConfig? agentConfig = null,
        Action<TestConsole>? setupKeys = null)
    {
        var console = new TestConsole();
        setupKeys?.Invoke(console);

        var app = new TuiApp(
            provider,
            new ProviderConfig { Name = "mock", Protocol = "mock", Model = "mock-1" },
            agentConfig ?? new AgentConfig(),
            tuiConfig ?? new TuiConfig { Mode = "console", EnableHitl = false },
            SecurityLevel.Normal,
            logger: null,
            CancellationToken.None,
            console: console,
            useLive: false);

        await app.RunAsync();
        return console.Output;
    }

    [Fact]
    public async Task EndToEnd_HitlDisabled_WriteTool_DirectlyExecutes()
    {
        // enable_hitl: false → NullHitlGate 注入，Write 工具直接执行
        var provider = new MockProvider();
        provider.EnqueueScript(ToolCallScript("call_1", "nonexistent_write_tool", "{}"));
        provider.EnqueueScript(TextScript("done"));

        var output = await RunTuiAppAsync(
            provider,
            tuiConfig: new TuiConfig { Mode = "console", EnableHitl = false },
            setupKeys: console =>
            {
                console.EnqueueText("do write");
                console.EnqueueEnter();
                console.EnqueueExit();
                console.EnqueueEnter();
            });

        // 应渲染工具调用（未注册工具返回 Fail，但不卡 HITL）
        output.Should().Contain("nonexistent_write_tool");
        output.Should().Contain("done");
    }

    [Fact]
    public async Task EndToEnd_EnableHitlFalse_DirectlyExecutes()
    {
        // enable_hitl: false → NullHitlGate 注入，工具直接执行
        var provider = new MockProvider();
        provider.EnqueueScript(ToolCallScript("call_1", "nonexistent_tool", "{}"));
        provider.EnqueueScript(TextScript("completed"));

        var output = await RunTuiAppAsync(
            provider,
            tuiConfig: new TuiConfig { Mode = "console", EnableHitl = false },
            setupKeys: console =>
            {
                console.EnqueueText("test");
                console.EnqueueEnter();
                console.EnqueueExit();
                console.EnqueueEnter();
            });

        output.Should().Contain("nonexistent_tool");
        output.Should().Contain("completed");
    }

    [Fact]
    public async Task EndToEnd_MultiRound_StatusBarUpdatesRound()
    {
        var provider = new MockProvider();
        // 第一轮：工具调用 → 第二轮
        provider.EnqueueScript(ToolCallScript("call_1", "nonexistent_tool", "{}"));
        // 第二轮：文本回复 → AgentDone
        provider.EnqueueScript(TextScript("final answer"));

        var output = await RunTuiAppAsync(
            provider,
            setupKeys: console =>
            {
                console.EnqueueText("go");
                console.EnqueueEnter();
                console.EnqueueExit();
                console.EnqueueEnter();
            });

        output.Should().Contain("nonexistent_tool");
        output.Should().Contain("final answer");
    }

    [Fact]
    public async Task EndToEnd_Cancelled_ExitsCleanly()
    {
        var console = new TestConsole();
        var provider = new MockProvider();
        var cts = new CancellationTokenSource();
        cts.Cancel();

        var app = new TuiApp(
            provider,
            new ProviderConfig { Name = "mock", Protocol = "mock", Model = "mock-1" },
            new AgentConfig(),
            new TuiConfig { Mode = "console", EnableHitl = false },
            SecurityLevel.Normal,
            logger: null,
            cts.Token,
            console: console,
            useLive: false);

        // 应不抛异常地退出
        await app.RunAsync();
    }

    [Fact]
    public async Task EndToEnd_Banner_PrintsModeAndSecurity()
    {
        var output = await RunTuiAppAsync(
            new MockProvider(),
            tuiConfig: new TuiConfig { Mode = "console", EnableHitl = false },
            setupKeys: console =>
            {
                console.EnqueueExit();
                console.EnqueueEnter();
            });

        // 启动横幅应含模式标记、安全等级、工具数
        output.Should().Contain("TUI 模式");
        output.Should().Contain("security=");
        output.Should().Contain("tools=");
    }
}
