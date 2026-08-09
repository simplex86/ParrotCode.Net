using System.IO;
using ParrotCode;

namespace ParrotCode.xUnit;

/// <summary>
/// SessionConfig 配置解析单元测试（迭代 10b）。
/// 覆盖 YAML 解析、默认值、enable=false、自定义 storage_dir。
/// </summary>
public class SessionConfigTests
{
    private sealed class TestDir : IDisposable
    {
        public string Dir { get; } = Directory.CreateTempSubdirectory("parrotcode-cfg-").FullName;
        public string WriteFile(string name, string content)
        {
            var path = Path.Combine(Dir, name);
            File.WriteAllText(path, content);
            return path;
        }
        public void Dispose() { try { Directory.Delete(Dir, recursive: true); } catch { } }
    }

    private const string BaseYaml = """
        active_provider: mock
        providers:
          - name: mock
            protocol: mock
            model: mock-1
        """;

    [Fact]
    public void Load_WithSessionSection_ParsesSessionConfig()
    {
        using var dir = new TestDir();
        var yaml = BaseYaml + "\n" + """
            session:
              enable: true
              storage_dir: /tmp/my-sessions
            """;
        var path = dir.WriteFile(".parrotcode.yaml", yaml);

        var config = ConfigLoader.Load(path);

        config.Session.Should().NotBeNull();
        config.Session!.Enable.Should().BeTrue();
        config.Session.StorageDir.Should().Be("/tmp/my-sessions");
    }

    [Fact]
    public void Load_WithoutSessionSection_SessionIsNull()
    {
        using var dir = new TestDir();
        var path = dir.WriteFile(".parrotcode.yaml", BaseYaml);

        var config = ConfigLoader.Load(path);

        config.Session.Should().BeNull();
    }

    [Fact]
    public void Load_SessionEnableFalse_ParsesCorrectly()
    {
        using var dir = new TestDir();
        var yaml = BaseYaml + "\n" + """
            session:
              enable: false
            """;
        var path = dir.WriteFile(".parrotcode.yaml", yaml);

        var config = ConfigLoader.Load(path);

        config.Session.Should().NotBeNull();
        config.Session!.Enable.Should().BeFalse();
    }

    [Fact]
    public void Load_SessionStorageDirOnly_ParsesCorrectly()
    {
        using var dir = new TestDir();
        var yaml = BaseYaml + "\n" + """
            session:
              storage_dir: ./custom/path
            """;
        var path = dir.WriteFile(".parrotcode.yaml", yaml);

        var config = ConfigLoader.Load(path);

        config.Session.Should().NotBeNull();
        config.Session!.StorageDir.Should().Be("./custom/path");
        config.Session.Enable.Should().BeNull();  // 未设置，默认 null → App 中 ?? true
    }

    [Fact]
    public void SessionConfig_Defaults_AllNull()
    {
        var cfg = new SessionConfig();

        cfg.Enable.Should().BeNull();
        cfg.StorageDir.Should().BeNull();
    }
}
