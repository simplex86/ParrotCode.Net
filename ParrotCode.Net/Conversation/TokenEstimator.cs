namespace ParrotCode;

/// <summary>
/// 粗略 token 估算器：字符数 / 3（向上取整）。
/// 英文约 4 char/token，中文约 1-2 char/token，取 3 是跨语言折中近似。
/// 仅供上下文占比估算与日志展示，不用于计费或精确截断（迭代 9 可能升级为分词器）。
/// </summary>
public static class TokenEstimator
{
    private const double CharsPerToken = 3.0;

    /// <summary>
    /// 估算纯文本的 token 数。空字符串返回 0。
    /// </summary>
    public static int Estimate(string? text)
    {
        if (string.IsNullOrEmpty(text)) return 0;
        // 向上取整：1-3 字符算 1 token，4-6 字符算 2 tokens...
        // 比向下取整更保守（高估），利于上下文窗口管理。
        return (int)Math.Ceiling(text.Length / CharsPerToken);
    }

    /// <summary>
    /// 估算单条消息的 token 数（仅 Content，不含 role 开销）。
    /// </summary>
    public static int Estimate(Message message)
    {
        return Estimate(message.Content);
    }

    /// <summary>
    /// 估算消息列表的总 token 数（仅 Content 之和，不含 role / 格式开销）。
    /// </summary>
    public static int Estimate(IReadOnlyList<Message> messages)
    {
        var total = 0;
        foreach (var msg in messages)
        {
            total += Estimate(msg);
        }
        return total;
    }
}
