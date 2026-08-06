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

/// <summary>
/// 顶层配置，对应 .parrotcode.yaml 的根结构。
/// </summary>
public sealed record AppConfig
{
    /// <summary>
    /// 当前激活的 Provider 名称；为 null 时回退到 providers[0].name。
    /// </summary>
    public string? ActiveProvider { get; init; }

    /// <summary>
    /// Provider 列表。无配置文件时由 Loader 提供默认 mock 项。
    /// 用 IList 而非 IReadOnlyList：YamlDotNet 需要可变集合来填充（消费方仍按只读语义使用）。
    /// </summary>
    public IList<ProviderConfig> Providers { get; init; } = Array.Empty<ProviderConfig>();
}
