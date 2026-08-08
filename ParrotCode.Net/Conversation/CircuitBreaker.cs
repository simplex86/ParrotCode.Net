namespace ParrotCode;

/// <summary>
/// 通用熔断器：连续失败 maxFailures 次后打开，停止自动触发。
/// 成功或手动 Reset 后关闭。非线程安全（AgentLoop 单线程驱动）。
/// </summary>
public sealed class CircuitBreaker
{
    private readonly int _maxFailures;
    private int _failureCount;
    private bool _isOpen;

    public CircuitBreaker(int maxFailures = 2)
    {
        if (maxFailures < 1) throw new ArgumentOutOfRangeException(nameof(maxFailures));
        _maxFailures = maxFailures;
    }

    public bool IsOpen => _isOpen;
    public int FailureCount => _failureCount;
    public int MaxFailures => _maxFailures;

    /// <summary>
    /// 记录一次失败。达到阈值时打开熔断器。
    /// </summary>
    public void RecordFailure()
    {
        _failureCount++;
        if (_failureCount >= _maxFailures)
            _isOpen = true;
    }

    /// <summary>
    /// 记录一次成功。清零计数，关闭熔断器。
    /// </summary>
    public void RecordSuccess()
    {
        _failureCount = 0;
        _isOpen = false;
    }

    /// <summary>
    /// 手动重置（如 /compress 命令或程序重启）。
    /// </summary>
    public void Reset()
    {
        _failureCount = 0;
        _isOpen = false;
    }
}
