using ParrotCode;

namespace ParrotCode.xUnit;

/// <summary>
/// HitlDecision + HitlChoice 单元测试。
/// 验证枚举值、派生属性（IsAllowed/ShouldCache）、静态工厂。
/// </summary>
public class HitlDecisionTests
{
    [Fact]
    public void AllowOnce_IsAllowed_True_ShouldCache_False()
    {
        var decision = HitlDecision.AllowOnce;

        decision.Choice.Should().Be(HitlChoice.AllowOnce);
        decision.IsAllowed.Should().BeTrue();
        decision.ShouldCache.Should().BeFalse();
        decision.Reason.Should().BeNull();
    }

    [Fact]
    public void AllowSession_IsAllowedAndShouldCache_True()
    {
        var decision = new HitlDecision(HitlChoice.AllowSession);

        decision.Choice.Should().Be(HitlChoice.AllowSession);
        decision.IsAllowed.Should().BeTrue();
        decision.ShouldCache.Should().BeTrue();
        decision.Reason.Should().BeNull();
    }

    [Fact]
    public void AllowPermanent_ShouldCache_True()
    {
        var decision = new HitlDecision(HitlChoice.AllowPermanent);

        decision.Choice.Should().Be(HitlChoice.AllowPermanent);
        decision.IsAllowed.Should().BeTrue();
        decision.ShouldCache.Should().BeTrue();
    }

    [Fact]
    public void Deny_IsAllowed_False_ReasonSet()
    {
        var decision = HitlDecision.Deny("测试原因");

        decision.Choice.Should().Be(HitlChoice.Deny);
        decision.IsAllowed.Should().BeFalse();
        decision.ShouldCache.Should().BeFalse();
        decision.Reason.Should().Be("测试原因");
    }

    [Fact]
    public void Deny_StaticFactory_SetsChoiceDeny()
    {
        var decision = HitlDecision.Deny("拒绝");

        decision.Choice.Should().Be(HitlChoice.Deny);
        decision.IsAllowed.Should().BeFalse();
    }
}
