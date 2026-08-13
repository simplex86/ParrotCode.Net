using System.Net;
using System.Text;

namespace ParrotCode.xUnit;

/// <summary>
/// 单测用的 mock HttpMessageHandler——拦截 HTTP 请求返回预设响应。
/// </summary>
public class MockHttpHandler : HttpMessageHandler
{
    private readonly string _responseBody;
    private readonly HttpStatusCode _statusCode;
    private readonly TimeSpan? _delay;

    public HttpRequestMessage? LastRequest { get; private set; }
    public string? LastRequestBody { get; private set; }

    public MockHttpHandler(string responseBody, HttpStatusCode statusCode = HttpStatusCode.OK, TimeSpan? delay = null)
    {
        _responseBody = responseBody;
        _statusCode = statusCode;
        _delay = delay;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        LastRequest = request;
        if (request.Content is not null)
            LastRequestBody = await request.Content.ReadAsStringAsync(ct);

        if (_delay is not null)
            await Task.Delay(_delay.Value, ct);

        var resp = new HttpResponseMessage(_statusCode)
        {
            Content = new StringContent(_responseBody, Encoding.UTF8, "application/json")
        };
        return resp;
    }
}
