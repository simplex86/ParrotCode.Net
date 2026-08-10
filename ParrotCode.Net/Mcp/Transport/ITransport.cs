namespace ParrotCode;

/// <summary>
/// MCP 传输层抽象（迭代 11a）。
/// 上层（McpClient）通过此接口收发 JSON-RPC 消息。
/// 职责：序列化/反序列化由 JsonRpc 层负责；Transport 只负责"发送 JSON 字符串 + 接收 JSON 字符串"。
/// </summary>
internal interface ITransport : IAsyncDisposable
{
    /// <summary>
    /// 启动传输（连接 server / 启动子进程）。成功后可 Send/Receive。
    /// </summary>
    Task ConnectAsync(CancellationToken cancellationToken);

    /// <summary>
    /// 发送 JSON-RPC 消息。
    /// </summary>
    Task SendAsync(string json, CancellationToken cancellationToken);

    /// <summary>
    /// 接收一条 JSON-RPC 消息。返回 null 表示连接关闭。
    /// </summary>
    Task<string?> ReceiveAsync(CancellationToken cancellationToken);

    /// <summary>
    /// 关闭传输（发送关闭通知 + 关闭连接/进程）。
    /// </summary>
    Task CloseAsync(CancellationToken cancellationToken);

    /// <summary>
    /// 获取传输层的错误诊断信息（如子进程 stderr 输出）。
    /// 连接失败时上层调用此方法，将诊断信息拼入错误消息展示给用户。
    /// 无诊断信息时返回 null。
    /// </summary>
    string? GetErrorContext();
}
