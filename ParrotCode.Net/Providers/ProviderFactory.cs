namespace ParrotCode;

/// <summary>
/// 按 ProviderConfig.Protocol 路由到具体 IBaseProvider 实现。
/// 迭代 2a：仅 mock 实现；openai/anthropic 显式抛 ProviderNotImplementedException。
/// </summary>
public static class ProviderFactory
{
    public static IBaseProvider Create(ProviderConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        return config.Protocol switch
        {
            "mock" => new MockProvider(),
            // openai 协议覆盖 OpenAI 官方与 DeepSeek 等 OpenAI 兼容服务（由 BaseUrl 区分端点）。
            "openai" => new OpenAIProvider(config),
            // anthropic 协议暂未实现（可选进阶练习）
            "anthropic" => throw new ProviderNotImplementedException(config),
            _ => throw new ArgumentException($"不支持的协议: {config.Protocol} (provider={config.Name})")
        };
    }

    /// <summary>
    /// 按 active_provider（回退 providers[0]）选中并创建。
    /// </summary>
    public static IBaseProvider CreateActive(AppConfig appConfig)
    {
        ArgumentNullException.ThrowIfNull(appConfig);
        if (appConfig.Providers.Count == 0) throw new ConfigException("providers 不能为空");

        var name = appConfig.ActiveProvider ?? appConfig.Providers[0].Name;
        var pc = appConfig.Providers.FirstOrDefault(p => p.Name == name) ?? throw new ConfigException($"active_provider '{name}' 未在 providers 中定义");

        return Create(pc);
    }
}

/// <summary>
/// 协议已识别但尚未实现（openai/anthropic）。迭代 3 接入真实 LLM 后消除。
/// </summary>
public sealed class ProviderNotImplementedException : NotSupportedException
{
    public ProviderNotImplementedException(ProviderConfig config)
        : base($"Provider '{config.Name}' (protocol={config.Protocol}) 将在后续迭代实现，本迭代支持 mock/openai。")
    {
    
    }
}
