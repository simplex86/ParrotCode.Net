using System.Threading.Channels;

namespace ParrotCode;

/// <summary>
/// 内存传输 mock：写入的消息排队，可预设响应（迭代 11a）。
/// 供 11b/11c 的 McpClient / McpConnectionManager 测试用，不启动真实进程/网络。
///
/// 用法：
///   var mock = new MockTransport();
///   mock.EnqueueResponse(@"{""jsonrpc"":""2.0"",""id"":1,""result"":{...}}");
///   await mock.SendAsync(requestJson, ct);  // Client 发送
///   var sent = await mock.GetLastSentAsync(ct);  // 测试验证发送内容
///   var received = await mock.ReceiveAsync(ct);  // Client 接收预设响应
/// </summary>
internal sealed class MockTransport : ITransport
{
    private readonly Channel<string> _sendChannel = Channel.CreateUnbounded<string>();
    private readonly Channel<string> _receiveChannel = Channel.CreateUnbounded<string>();

    public Task ConnectAsync(CancellationToken ct) => Task.CompletedTask;

    public async Task SendAsync(string json, CancellationToken ct) => await _sendChannel.Writer.WriteAsync(json, ct);

    public async Task<string?> ReceiveAsync(CancellationToken ct)
    {
        if (await _receiveChannel.Reader.WaitToReadAsync(ct) && _receiveChannel.Reader.TryRead(out var msg))
            return msg;
        return null;
    }

    public Task CloseAsync(CancellationToken ct)
    {
        _sendChannel.Writer.TryComplete();
        _receiveChannel.Writer.TryComplete();
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        _sendChannel.Writer.TryComplete();
        _receiveChannel.Writer.TryComplete();
        return ValueTask.CompletedTask;
    }

    // ===== 测试辅助方法 =====

    /// <summary>
    /// 获取 Client 发送的最近一条消息（阻塞等待）。
    /// </summary>
    public async Task<string> GetLastSentAsync(CancellationToken ct) => await _sendChannel.Reader.ReadAsync(ct);

    /// <summary>
    /// 预设响应（Client ReceiveAsync 会读到）。
    /// </summary>
    public void EnqueueResponse(string json) => _receiveChannel.Writer.TryWrite(json);

    /// <summary>
    /// 关闭接收通道，模拟连接断开（ReceiveAsync 返回 null）。
    /// </summary>
    public void SimulateDisconnect() => _receiveChannel.Writer.TryComplete();
}
