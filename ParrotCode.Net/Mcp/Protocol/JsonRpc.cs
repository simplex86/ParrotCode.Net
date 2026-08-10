using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace ParrotCode;

/// <summary>
/// JSON-RPC 2.0 编解码 + Future 匹配（迭代 11a）。
/// 每个请求带自增 id，响应按 id 匹配到 TaskCompletionSource&lt;JsonElement&gt;。
/// 线程安全：ConcurrentDictionary + Interlocked.Increment。
/// </summary>
internal sealed class JsonRpc
{
    private int _nextId = 0;
    private readonly ConcurrentDictionary<int, TaskCompletionSource<JsonElement>> _pending = new();
    private readonly ILogger? _logger;
    private static readonly JsonSerializerOptions s_camelCase = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public JsonRpc(ILogger? logger = null) => _logger = logger;

    /// <summary>
    /// 创建带自增 id 的请求，返回 (JSON 字符串, 等待响应的 Task)。
    /// 调用方发送 JSON 字符串后 await Task 等待响应。
    /// </summary>
    public (string Json, Task<JsonElement> ResponseTask) CreateRequest(string method, object? @params = null)
    {
        var id = Interlocked.Increment(ref _nextId);
        var tcs = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = tcs;

        var request = new
        {
            jsonrpc = "2.0",
            id,
            method,
            @params
        };
        var json = JsonSerializer.Serialize(request, s_camelCase);

        return (json, tcs.Task);
    }

    /// <summary>创建通知（无 id，不期望响应）。</summary>
    public string CreateNotification(string method, object? @params = null)
    {
        var notification = new
        {
            jsonrpc = "2.0",
            method,
            @params
        };
        return JsonSerializer.Serialize(notification, s_camelCase);
    }

    /// <summary>
    /// 处理从 transport 收到的 JSON 消息，按类型分发。
    /// - 有 id 且有 result/error → 响应，匹配到 pending TCS
    /// - 有 method 无 id → 通知（本迭代不处理，记日志）
    /// </summary>
    public void HandleMessage(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        // 有 id 且有 result/error → 响应
        if (root.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.Number)
        {
            var id = idEl.GetInt32();
            if (_pending.TryRemove(id, out var tcs))
            {
                if (root.TryGetProperty("error", out var error))
                {
                    var message = error.TryGetProperty("message", out var msgEl) ? msgEl.GetString() ?? "Unknown error" 
                                                                                 : "Unknown error";
                    var code = error.TryGetProperty("code", out var codeEl) ? codeEl.GetInt32() : -1;
                    tcs.SetException(new JsonRpcException(code, message));
                }
                else if (root.TryGetProperty("result", out var result))
                {
                    tcs.SetResult(result.Clone());
                }
                else
                {
                    tcs.SetException(new JsonRpcException(-1, "响应缺少 result 和 error 字段"));
                }
            }
            else
            {
                _logger?.LogWarning("收到未知 id={Id} 的 JSON-RPC 响应", id);
            }
        }
        // 有 method 无 id → 通知
        else if (root.TryGetProperty("method", out _))
        {
            // 本迭代不处理 Server → Client 通知（如 tools/list_changed）
            _logger?.LogDebug("收到 JSON-RPC 通知，暂不处理：{Json}", json);
        }
    }

    /// <summary>
    /// 取消所有等待中的请求（transport 关闭时调用）。
    /// </summary>
    public void CancelAllPending()
    {
        foreach (var kvp in _pending)
        {
            kvp.Value.TrySetException(new JsonRpcException(-1, "连接已关闭"));
        }
        _pending.Clear();
    }
}

/// <summary>
/// JSON-RPC 错误异常。
/// </summary>
public sealed class JsonRpcException : Exception
{
    public int Code { get; }
    public JsonRpcException(int code, string message) : base($"JSON-RPC 错误 [{code}]: {message}") => Code = code;
}
