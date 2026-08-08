using ParrotCode;

namespace ParrotCode.xUnit;

/// <summary>
/// CircuitBreaker 单元测试：记录失败/成功、打开/关闭/重置、阈值边界。
/// </summary>
public class CircuitBreakerTests
{
    [Fact]
    public void NewBreaker_IsClosed()
    {
        var cb = new CircuitBreaker(2);

        cb.IsOpen.Should().BeFalse();
        cb.FailureCount.Should().Be(0);
    }

    [Fact]
    public void RecordFailure_IncrementsCount()
    {
        var cb = new CircuitBreaker(3);

        cb.RecordFailure();

        cb.FailureCount.Should().Be(1);
        cb.IsOpen.Should().BeFalse();
    }

    [Fact]
    public void RecordFailure_ReachesMax_OpensBreaker()
    {
        var cb = new CircuitBreaker(2);

        cb.RecordFailure();
        cb.RecordFailure();

        cb.IsOpen.Should().BeTrue();
        cb.FailureCount.Should().Be(2);
    }

    [Fact]
    public void RecordSuccess_ClearsCountAndCloses()
    {
        var cb = new CircuitBreaker(2);
        cb.RecordFailure();
        cb.RecordFailure();
        cb.IsOpen.Should().BeTrue();

        cb.RecordSuccess();

        cb.IsOpen.Should().BeFalse();
        cb.FailureCount.Should().Be(0);
    }

    [Fact]
    public void Reset_ClearsCountAndCloses()
    {
        var cb = new CircuitBreaker(2);
        cb.RecordFailure();
        cb.RecordFailure();
        cb.IsOpen.Should().BeTrue();

        cb.Reset();

        cb.IsOpen.Should().BeFalse();
        cb.FailureCount.Should().Be(0);
    }

    [Fact]
    public void MaxFailures_One_OpensOnFirstFailure()
    {
        var cb = new CircuitBreaker(1);

        cb.RecordFailure();

        cb.IsOpen.Should().BeTrue();
    }

    [Fact]
    public void Constructor_InvalidMax_Throws()
    {
        var act = () => new CircuitBreaker(0);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void RecordFailure_AfterOpen_StillCounts()
    {
        var cb = new CircuitBreaker(2);
        cb.RecordFailure();
        cb.RecordFailure();
        cb.IsOpen.Should().BeTrue();

        cb.RecordFailure();

        cb.FailureCount.Should().Be(3);
        cb.IsOpen.Should().BeTrue();
    }
}
