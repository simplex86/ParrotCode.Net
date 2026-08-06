using ParrotCode;

namespace ParrotCode.xUnit;

/// <summary>
/// ConfigLoader 单元测试：覆盖三级发现、YAML 解析、${VAR} 展开、语义校验。
/// 隔离策略：用 explicitPath 注入临时文件；涉及 env/cwd 的用临时目录与 EnvScope 还原；
/// 不写真实用户目录（home 发现由 cwd 用例同构覆盖 File.Exists 命中逻辑）。
/// </summary>
public class ConfigLoaderTests
{
    /// <summary>创建临时目录并在其中写文件，用完自动清理。</summary>
    private sealed class TestDir : IDisposable
    {
        public string Dir { get; } = Directory.CreateTempSubdirectory("parrotcode-test-").FullName;

        public string WriteFile(string name, string content)
        {
            var path = Path.Combine(Dir, name);
            File.WriteAllText(path, content);
            return path;
        }

        public void Dispose()
        {
            try { Directory.Delete(Dir, recursive: true); } catch { }
        }
    }

    /// <summary>临时设置环境变量与（可选）当前工作目录，用完还原。</summary>
    private sealed class Scope : IDisposable
    {
        private readonly string? _var;
        private readonly string? _oldVar;
        private readonly string _oldCwd;

        public Scope(string? envVar = null, string? envValue = null, string? tempCwd = null)
        {
            _oldCwd = Environment.CurrentDirectory;
            if (envVar is not null)
            {
                _var = envVar;
                _oldVar = Environment.GetEnvironmentVariable(envVar);
                Environment.SetEnvironmentVariable(envVar, envValue);
            }
            if (tempCwd is not null)
                Environment.CurrentDirectory = tempCwd;
        }

        public void Dispose()
        {
            Environment.CurrentDirectory = _oldCwd;
            if (_var is not null)
                Environment.SetEnvironmentVariable(_var, _oldVar);
        }
    }

    private const string EnvVar = "PARROTCODE_CONFIG";

    private const string ValidMockYaml = """
        active_provider: mock
        providers:
          - name: mock
            protocol: mock
            model: mock-1
        """;

    // —— explicitPath（优先级 0）——

    [Fact]
    public void Load_WithExplicitPath_Valid_LoadsFile()
    {
        using var dir = new TestDir();
        var path = dir.WriteFile(".parrotcode.yaml", ValidMockYaml);

        var config = ConfigLoader.Load(path);

        config.ActiveProvider.Should().Be("mock");
        config.Providers.Should().HaveCount(1);
        config.Providers[0].Name.Should().Be("mock");
        config.Providers[0].Model.Should().Be("mock-1");
    }

    [Fact]
    public void Load_WithExplicitPath_NotExist_ThrowsWithPath()
    {
        var missing = Path.Combine(Path.GetTempPath(), $"not-exist-{Guid.NewGuid():N}.yaml");

        var act = () => ConfigLoader.Load(missing);

        var ex = act.Should().Throw<ConfigException>().Which;
        ex.Message.Should().Contain("指定的配置文件不存在");
        ex.SourcePath.Should().Be(missing);
    }

    // —— 环境变量（优先级 1）——

    [Fact]
    public void Load_WithEnvVar_Valid_LoadsFile()
    {
        using var dir = new TestDir();
        var path = dir.WriteFile("env.yaml", ValidMockYaml);
        // 临时改 cwd 到空目录，避免 cwd 的 .parrotcode.yaml 干扰
        using var scope = new Scope(EnvVar, path, tempCwd: dir.Dir);

        var config = ConfigLoader.Load();

        config.ActiveProvider.Should().Be("mock");
    }

    [Fact]
    public void Load_WithEnvVar_NotExist_Throws()
    {
        var missing = Path.Combine(Path.GetTempPath(), $"env-miss-{Guid.NewGuid():N}.yaml");
        using var dir = new TestDir();
        using var scope = new Scope(EnvVar, missing, tempCwd: dir.Dir);

        var act = () => ConfigLoader.Load();

        var ex = act.Should().Throw<ConfigException>().Which;
        ex.Message.Should().Contain("环境变量");
        ex.Message.Should().Contain(EnvVar);
    }

    [Fact]
    public void Load_EnvVar_TakesPriorityOverCwd()
    {
        using var dir = new TestDir();
        var envPath = dir.WriteFile("env.yaml", "active_provider: env-mock\nproviders:\n  - name: env-mock\n    protocol: mock\n    model: m-env\n");
        dir.WriteFile(".parrotcode.yaml", "active_provider: cwd-mock\nproviders:\n  - name: cwd-mock\n    protocol: mock\n    model: m-cwd\n");
        using var scope = new Scope(EnvVar, envPath, tempCwd: dir.Dir);

        var config = ConfigLoader.Load();

        config.ActiveProvider.Should().Be("env-mock", "环境变量优先级高于 cwd");
    }

    // —— 项目目录（优先级 2）——

    [Fact]
    public void Load_CwdFile_LoadsWhenNoEnvNoExplicit()
    {
        using var dir = new TestDir();
        dir.WriteFile(".parrotcode.yaml", ValidMockYaml);
        using var scope = new Scope(EnvVar, envValue: null, tempCwd: dir.Dir);

        var config = ConfigLoader.Load();

        config.ActiveProvider.Should().Be("mock");
    }

    // —— 无配置（默认 mock）——

    [Fact]
    public void Load_NoConfig_ReturnsDefaultMock()
    {
        // 注：依赖运行环境无 ~/.parrotcode/config.yaml（CI 与干净环境通过）；
        // 若本机用户目录有该文件，此用例会读到它而非默认 mock，需清理后重跑。
        using var dir = new TestDir();
        using var scope = new Scope(EnvVar, envValue: null, tempCwd: dir.Dir);

        var config = ConfigLoader.Load();

        config.ActiveProvider.Should().Be("mock");
        config.Providers.Should().HaveCount(1);
        config.Providers[0].Protocol.Should().Be("mock");
    }

    // —— YAML 语法错误（带行号）——

    [Fact]
    public void Load_YamlSyntaxError_ThrowsWithLine()
    {
        // providers: 下项不缩进，触发语法错误
        var badYaml = "active_provider: mock\nproviders:\n- name: mock\n  protocol: mock\n  model: m\n";
        // 上面其实合法；改用真正非法的：tab 缩进或重复键
        badYaml = "active_provider: mock\nactive_provider: mock2\nproviders: [}\n";
        using var dir = new TestDir();
        var path = dir.WriteFile(".parrotcode.yaml", badYaml);

        var act = () => ConfigLoader.Load(path);

        var ex = act.Should().Throw<ConfigException>().Which;
        ex.Line.Should().NotBeNull("YAML 语法错误应带行号");
        ex.SourcePath.Should().Be(path);
    }

    [Fact]
    public void Load_EmptyFile_Throws()
    {
        using var dir = new TestDir();
        var path = dir.WriteFile(".parrotcode.yaml", "   \n  \n");

        var act = () => ConfigLoader.Load(path);

        var ex = act.Should().Throw<ConfigException>().Which;
        ex.Message.Should().Contain("配置文件为空");
    }

    // —— 语义校验 ——

    [Fact]
    public void Load_MissingNameField_Throws()
    {
        var yaml = """
            providers:
              - protocol: mock
                model: m
            """;
        using var dir = new TestDir();
        var path = dir.WriteFile(".parrotcode.yaml", yaml);

        var act = () => ConfigLoader.Load(path);

        var ex = act.Should().Throw<ConfigException>().Which;
        ex.Message.Should().Contain("providers[0].name");
    }

    [Fact]
    public void Load_DuplicateNames_Throws()
    {
        var yaml = """
            providers:
              - name: dup
                protocol: mock
                model: m1
              - name: dup
                protocol: mock
                model: m2
            """;
        using var dir = new TestDir();
        var path = dir.WriteFile(".parrotcode.yaml", yaml);

        var act = () => ConfigLoader.Load(path);

        var ex = act.Should().Throw<ConfigException>().Which;
        ex.Message.Should().Contain("dup");
        ex.Message.Should().Contain("重复");
    }

    [Fact]
    public void Load_ActiveProviderNotDefined_Throws()
    {
        var yaml = """
            active_provider: foo
            providers:
              - name: mock
                protocol: mock
                model: m
            """;
        using var dir = new TestDir();
        var path = dir.WriteFile(".parrotcode.yaml", yaml);

        var act = () => ConfigLoader.Load(path);

        var ex = act.Should().Throw<ConfigException>().Which;
        ex.Message.Should().Contain("active_provider 'foo'");
    }

    [Fact]
    public void Load_ActiveProviderNull_FallsBackToFirst()
    {
        var yaml = """
            providers:
              - name: first
                protocol: mock
                model: m1
              - name: second
                protocol: mock
                model: m2
            """;
        using var dir = new TestDir();
        var path = dir.WriteFile(".parrotcode.yaml", yaml);

        var config = ConfigLoader.Load(path);

        config.ActiveProvider.Should().BeNull();
        // 回退逻辑在 CreateActive 验证；此处仅确认加载成功且未报错
        config.Providers.Should().HaveCount(2);
    }

    [Fact]
    public void Load_UnsupportedProtocol_Throws()
    {
        var yaml = """
            providers:
              - name: x
                protocol: foo
                model: m
            """;
        using var dir = new TestDir();
        var path = dir.WriteFile(".parrotcode.yaml", yaml);

        var act = () => ConfigLoader.Load(path);

        var ex = act.Should().Throw<ConfigException>().Which;
        ex.Message.Should().Contain("foo");
        ex.Message.Should().Contain("mock/openai/anthropic");
    }

    // —— ${VAR} 环境变量展开 ——

    [Fact]
    public void Load_EnvVarExpansion_Set_ReturnsValue()
    {
        const string varName = "PARROTCODE_TEST_KEY_12345";
        try
        {
            Environment.SetEnvironmentVariable(varName, "secret123");
            var yaml =
                "providers:\n" +
                "  - name: ds\n" +
                "    protocol: openai\n" +
                "    model: deepseek-chat\n" +
                "    base_url: https://api.deepseek.com/v1\n" +
                "    api_key: ${" + varName + "}\n";
            using var dir = new TestDir();
            var path = dir.WriteFile(".parrotcode.yaml", yaml);

            var config = ConfigLoader.Load(path);

            config.Providers[0].ApiKey.Should().Be("secret123");
        }
        finally
        {
            Environment.SetEnvironmentVariable(varName, null);
        }
    }

    [Fact]
    public void Load_EnvVarExpansion_NotSet_Throws()
    {
        const string varName = "PARROTCODE_TEST_KEY_NOT_SET_99999";
        Environment.SetEnvironmentVariable(varName, null);
        var yaml =
            "providers:\n" +
            "  - name: ds\n" +
            "    protocol: openai\n" +
            "    model: deepseek-chat\n" +
            "    base_url: https://api.deepseek.com/v1\n" +
            "    api_key: ${" + varName + "}\n";
        using var dir = new TestDir();
        var path = dir.WriteFile(".parrotcode.yaml", yaml);

        var act = () => ConfigLoader.Load(path);

        var ex = act.Should().Throw<ConfigException>().Which;
        ex.Message.Should().Contain(varName);
        ex.Message.Should().Contain("providers[0].api_key");
    }
}
