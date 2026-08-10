using System.IO;
using ParrotCode;

namespace ParrotCode.xUnit;

/// <summary>
/// InstructionsConfig 配置解析单元测试（迭代 10c）。
/// 覆盖 YAML 解析、默认值、enable=false、自定义 max_include_depth / project_instructions_path。
/// </summary>
public class InstructionsConfigTests
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
    public void Load_WithInstructionsSection_ParsesConfig()
    {
        using var dir = new TestDir();
        var yaml = BaseYaml + "\n" + """
            instructions:
              enable: true
              max_include_depth: 5
              project_instructions_path: ./MY_RULES.md
            """;
        var path = dir.WriteFile(".parrotcode.yaml", yaml);

        var config = ConfigLoader.Load(path);

        config.Instructions.Should().NotBeNull();
        config.Instructions!.Enable.Should().BeTrue();
        config.Instructions.MaxIncludeDepth.Should().Be(5);
        config.Instructions.ProjectInstructionsPath.Should().Be("./MY_RULES.md");
    }

    [Fact]
    public void Load_WithoutInstructionsSection_InstructionsIsNull()
    {
        using var dir = new TestDir();
        var path = dir.WriteFile(".parrotcode.yaml", BaseYaml);

        var config = ConfigLoader.Load(path);

        config.Instructions.Should().BeNull();
    }

    [Fact]
    public void Load_InstructionsEnableFalse_ParsesCorrectly()
    {
        using var dir = new TestDir();
        var yaml = BaseYaml + "\n" + """
            instructions:
              enable: false
            """;
        var path = dir.WriteFile(".parrotcode.yaml", yaml);

        var config = ConfigLoader.Load(path);

        config.Instructions.Should().NotBeNull();
        config.Instructions!.Enable.Should().BeFalse();
    }

    [Fact]
    public void Load_InstructionsMaxIncludeDepthOnly_ParsesCorrectly()
    {
        using var dir = new TestDir();
        var yaml = BaseYaml + "\n" + """
            instructions:
              max_include_depth: 10
            """;
        var path = dir.WriteFile(".parrotcode.yaml", yaml);

        var config = ConfigLoader.Load(path);

        config.Instructions.Should().NotBeNull();
        config.Instructions!.MaxIncludeDepth.Should().Be(10);
        config.Instructions.Enable.Should().BeNull();  // 未设置
    }

    [Fact]
    public void InstructionsConfig_Defaults_AllNull()
    {
        var cfg = new InstructionsConfig();

        cfg.Enable.Should().BeNull();
        cfg.MaxIncludeDepth.Should().BeNull();
        cfg.ProjectInstructionsPath.Should().BeNull();
    }
}
