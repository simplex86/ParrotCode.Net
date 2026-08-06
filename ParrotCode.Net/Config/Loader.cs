using System.Text.RegularExpressions;
using YamlDotNet.Core;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace ParrotCode;

/// <summary>
/// 配置加载器：三级发现 + YamlDotNet 解析 + ${VAR} 展开 + 语义校验。
/// 无任何配置文件时返回默认 mock 配置，保证无配置也能跑。
/// </summary>
public static class ConfigLoader
{
    public const string EnvVar = "PARROTCODE_CONFIG";
    public const string CwdFileName = ".parrotcode.yaml";
    public const string UserDirName = ".parrotcode";
    public const string UserFileName = "config.yaml";

    private static readonly Regex EnvVarPattern = new(@"\$\{([A-Z0-9_]+)\}", RegexOptions.Compiled);

    private static readonly HashSet<string> SupportedProtocols = new(StringComparer.Ordinal)
    {
        "mock", "openai", "anthropic"
    };

    /// <summary>
    /// 按三级发现加载；无任何配置时返回默认 mock 配置。
    /// </summary>
    public static AppConfig Load() => Load(explicitPath: null);

    /// <summary>
    /// explicitPath 优先级最高（用于测试与未来 --config 参数）。
    /// </summary>
    public static AppConfig Load(string? explicitPath)
    {
        var path = ResolvePath(explicitPath);
        if (path is null) return Default();
        var config = Parse(path);
        config = ExpandEnv(config, path);
        return Validate(config, path);
    }

    // —— 三级发现（不合并）——
    private static string? ResolvePath(string? explicitPath)
    {
        // 优先级 0：explicitPath（调用方明确指定，不存在则报错，不静默回退）
        if (explicitPath is not null)
        {
            return File.Exists(explicitPath) ? explicitPath
                                             : throw new ConfigException($"指定的配置文件不存在: {explicitPath}", explicitPath);
        }

        // 优先级 1：环境变量 PARROTCODE_CONFIG（明确意图，不存在则报错）
        var envPath = Environment.GetEnvironmentVariable(EnvVar);
        if (!string.IsNullOrWhiteSpace(envPath))
        {
            return File.Exists(envPath) ? envPath
                                        : throw new ConfigException($"环境变量 {EnvVar} 指向的文件不存在: {envPath}", envPath);
        }

        // 优先级 2：当前工作目录 .parrotcode.yaml
        var cwdPath = Path.Combine(Environment.CurrentDirectory, CwdFileName);
        if (File.Exists(cwdPath)) return cwdPath;

        // 优先级 3：用户目录 ~/.parrotcode/config.yaml
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrEmpty(userProfile))
        {
            var userPath = Path.Combine(userProfile, UserDirName, UserFileName);
            if (File.Exists(userPath)) return userPath;
        }

        // 都没有 → 返回 null，调用方用默认 mock 配置
        return null;
    }

    // —— YAML 解析 + 行号捕获 ——
    private static AppConfig Parse(string path)
    {
        var text = File.ReadAllText(path);
        if (string.IsNullOrWhiteSpace(text)) throw new ConfigException("配置文件为空", path);

        var deserializer = new DeserializerBuilder().WithNamingConvention(UnderscoredNamingConvention.Instance) // snake_case ↔ PascalCase
                                                    .Build();
        try
        {
            return deserializer.Deserialize<AppConfig>(text) ?? throw new ConfigException("配置文件内容为 null", path);
        }
        catch (YamlException ex)
        {
            throw new ConfigException($"YAML 解析失败: {ex.Message}", path, (int)ex.Start.Line, (int)ex.Start.Column, ex);
        }
    }

    // —— ${VAR} 环境变量展开（所有 ProviderConfig 字符串字段）——
    private static AppConfig ExpandEnv(AppConfig config, string sourcePath)
    {
        var expandedProviders = config.Providers.Select((p, i) => new ProviderConfig
        {
            Name = ExpandField(p.Name, $"providers[{i}].name", sourcePath),
            Protocol = ExpandField(p.Protocol, $"providers[{i}].protocol", sourcePath),
            Model = ExpandField(p.Model, $"providers[{i}].model", sourcePath),
            BaseUrl = ExpandField(p.BaseUrl, $"providers[{i}].base_url", sourcePath),
            ApiKey = ExpandField(p.ApiKey, $"providers[{i}].api_key", sourcePath),
        }).ToArray();

        return config with { Providers = expandedProviders };
    }

    private static string ExpandField(string value, string fieldPath, string sourcePath)
    {
        if (string.IsNullOrEmpty(value) || !value.Contains("${")) return value;
        return EnvVarPattern.Replace(value, m =>
        {
            var name = m.Groups[1].Value;
            var v = Environment.GetEnvironmentVariable(name);
            if (string.IsNullOrEmpty(v)) throw new ConfigException($"环境变量 '{name}' 未设置（引用于 {fieldPath}）", sourcePath);
            return v;
        });
    }

    // —— 语义校验 ——
    private static AppConfig Validate(AppConfig config, string? sourcePath)
    {
        if (config.Providers.Count == 0)
            throw new ConfigException("providers 不能为空", sourcePath);

        for (var i = 0; i < config.Providers.Count; i++)
        {
            var p = config.Providers[i];
            var prefix = $"providers[{i}]";

            if (string.IsNullOrWhiteSpace(p.Name))
                throw new ConfigException($"{prefix}.name 不能为空", sourcePath);
            if (string.IsNullOrWhiteSpace(p.Protocol))
                throw new ConfigException($"{prefix}.protocol 不能为空", sourcePath);
            if (!SupportedProtocols.Contains(p.Protocol))
                throw new ConfigException($"{prefix}.protocol '{p.Protocol}' 不支持（允许: mock/openai/anthropic）", sourcePath);
        }

        // name 唯一性（报告第一组重复及其索引）
        var duplicate = config.Providers.Select((p, i) => (p.Name, Index: i))
                                        .GroupBy(x => x.Name, StringComparer.Ordinal)
                                        .FirstOrDefault(g => g.Count() > 1);
        if (duplicate is not null)
        {
            var indices = duplicate.Select(x => x.Index).OrderBy(x => x).ToArray();
            throw new ConfigException($"providers 名称重复: '{duplicate.Key}' (providers[{indices[0]}] 与 providers[{indices[1]}])", sourcePath);
        }

        // active_provider 命中（为 null 时回退 providers[0]，不报错）
        if (config.ActiveProvider is not null && 
            !config.Providers.Any(p => p.Name == config.ActiveProvider))
        {
            throw new ConfigException($"active_provider '{config.ActiveProvider}' 未在 providers 中定义", sourcePath);
        }

        return config;
    }

    private static AppConfig Default() => new()
    {
        ActiveProvider = "mock",
        Providers = new[]
        {
            new ProviderConfig { Name = "mock", Protocol = "mock", Model = "mock-1" }
        }
    };
}
