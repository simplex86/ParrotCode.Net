using System.Text.Json;
using ParrotCode;

namespace ParrotCode.xUnit;

/// <summary>
/// NullHitlGate 单元测试。
/// 验证默认放行行为（返回 null + IsAllowedThisSession 恒 false）。
/// </summary>
public class NullHitlGateTests
{
    private static ToolCall MakeCall(string name = "write_file") =>
        new("call_1", name, JsonDocument.Parse("{}").RootElement.Clone());

    [Fact]
    public async Task RequestAsync_ReturnsNull()
    {
        var gate = new NullHitlGate();

        var decision = await gate.RequestAsync(MakeCall(), CancellationToken.None);

        decision.Should().BeNull();
    }

    [Fact]
    public void IsAllowedThisSession_AlwaysFalse()
    {
        var gate = new NullHitlGate();

        gate.IsAllowedThisSession("write_file").Should().BeFalse();
        gate.IsAllowedThisSession("any_tool").Should().BeFalse();
    }
}
