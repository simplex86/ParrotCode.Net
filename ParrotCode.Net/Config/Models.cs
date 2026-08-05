namespace ParrotCode;

/// <summary>
/// 单个 Provider 配置。Protocol 决定由哪个 Provider 实现处理。
/// 迭代 2a：作为工厂入参载体，由 Program 硬编码传入；BaseUrl/ApiKey 暂未使用。
/// 迭代 2b：由 ConfigLoader 从 YAML 加载，BaseUrl/ApiKey 启用。
/// </summary>
public sealed record ProviderConfig
{
    public string Name { get; init; } = string.Empty;
    public string Protocol { get; init; } = string.Empty;   // mock | openai | anthropic
    public string Model { get; init; } = string.Empty;
    public string BaseUrl { get; init; } = string.Empty;    // 2a 未用，2b 启用
    public string ApiKey { get; init; } = string.Empty;     // 2a 未用，2b 启用
}
