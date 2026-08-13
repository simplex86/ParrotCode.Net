using ParrotCode;
using FluentAssertions;
using Xunit;
using Microsoft.Extensions.Logging.Abstractions;

namespace ParrotCode.xUnit;

public class HookLoaderTests
{
    private readonly NullLogger<HookLoader> _logger = new();

    private static string WriteTempYaml(string content)
    {
        var dir = Path.Combine(Path.GetTempPath(), "hook-tests-" + Guid.NewGuid().ToString("N")[..8]);
        var parrotDir = Path.Combine(dir, ".parrotcode");
        Directory.CreateDirectory(parrotDir);
        var file = Path.Combine(parrotDir, "hooks.yaml");
        File.WriteAllText(file, content);
        return dir;
    }

    private HookLoader CreateLoader(string projectRoot) => new(projectRoot: projectRoot, userHome: "/nonexistent", logger: _logger);

    [Fact]
    public void File_NotExists_Returns_Empty()
    {
        var loader = CreateLoader("/nonexistent");
        loader.Load().Should().BeEmpty();
    }

    [Fact]
    public void Empty_File_Returns_Empty()
    {
        var dir = WriteTempYaml("");
        CreateLoader(dir).Load().Should().BeEmpty();
        Directory.Delete(dir, true);
    }

    [Fact]
    public void No_Hooks_Field_Returns_Empty()
    {
        var dir = WriteTempYaml("other: value\n");
        CreateLoader(dir).Load().Should().BeEmpty();
        Directory.Delete(dir, true);
    }

    [Fact]
    public void Single_Rule_Loads_Success()
    {
        var dir = WriteTempYaml("""
            hooks:
              - name: test
                event: round_start
                actions:
                  - type: shell
                    command: "echo hello"
            """);
        var rules = CreateLoader(dir).Load();
        rules.Should().HaveCount(1);
        rules[0].EventType.Should().Be(HookEvent.RoundStart);
        rules[0].Actions[0].ActionType.Should().Be(HookActionType.Shell);
        Directory.Delete(dir, true);
    }

    [Fact]
    public void Multiple_Rules_Loads_Success()
    {
        var dir = WriteTempYaml("""
            hooks:
              - name: r1
                event: round_start
                actions:
                  - type: shell
                    command: "echo 1"
              - name: r2
                event: round_end
                actions:
                  - type: prompt_inject
                    text: "done"
              - name: r3
                event: tool_post_exec
                actions:
                  - type: http
                    url: "http://example.com"
            """);
        var rules = CreateLoader(dir).Load();
        rules.Should().HaveCount(3);
        Directory.Delete(dir, true);
    }

    [Fact]
    public void Global_And_Project_Merge()
    {
        var globalDir = Path.Combine(Path.GetTempPath(), "hook-global-" + Guid.NewGuid().ToString("N")[..8]);
        var projectDir = Path.Combine(Path.GetTempPath(), "hook-project-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(Path.Combine(globalDir, ".parrotcode"));
        Directory.CreateDirectory(Path.Combine(projectDir, ".parrotcode"));
        File.WriteAllText(Path.Combine(globalDir, ".parrotcode", "hooks.yaml"), """
            hooks:
              - name: global-rule
                event: round_start
                actions:
                  - type: shell
                    command: "echo global"
            """);
        File.WriteAllText(Path.Combine(projectDir, ".parrotcode", "hooks.yaml"), """
            hooks:
              - name: project-rule
                event: round_end
                actions:
                  - type: shell
                    command: "echo project"
            """);

        var loader = new HookLoader(projectRoot: projectDir, userHome: globalDir, logger: _logger);
        var rules = loader.Load();
        rules.Should().HaveCount(2);
        rules.Should().Contain(r => r.Name == "global-rule");
        rules.Should().Contain(r => r.Name == "project-rule");

        Directory.Delete(globalDir, true);
        Directory.Delete(projectDir, true);
    }

    [Fact]
    public void Invalid_YAML_Returns_Empty()
    {
        var dir = WriteTempYaml("hooks: [invalid yaml {{{");
        CreateLoader(dir).Load().Should().BeEmpty();
        Directory.Delete(dir, true);
    }

    [Fact]
    public void Missing_Event_Skips_Rule()
    {
        var dir = WriteTempYaml("""
            hooks:
              - name: no-event
                actions:
                  - type: shell
                    command: "echo hi"
            """);
        CreateLoader(dir).Load().Should().BeEmpty();
        Directory.Delete(dir, true);
    }

    [Fact]
    public void Invalid_Event_Skips_Rule()
    {
        var dir = WriteTempYaml("""
            hooks:
              - name: bad-event
                event: unknown_event
                actions:
                  - type: shell
                    command: "echo hi"
            """);
        CreateLoader(dir).Load().Should().BeEmpty();
        Directory.Delete(dir, true);
    }

    [Fact]
    public void Missing_Actions_Skips_Rule()
    {
        var dir = WriteTempYaml("""
            hooks:
              - name: no-actions
                event: round_start
            """);
        CreateLoader(dir).Load().Should().BeEmpty();
        Directory.Delete(dir, true);
    }

    [Fact]
    public void Invalid_ActionType_Skips_Rule()
    {
        var dir = WriteTempYaml("""
            hooks:
              - name: bad-type
                event: round_start
                actions:
                  - type: unknown_type
                    command: "echo hi"
            """);
        CreateLoader(dir).Load().Should().BeEmpty();
        Directory.Delete(dir, true);
    }

    [Fact]
    public void Shell_Missing_Command_Skips_Rule()
    {
        var dir = WriteTempYaml("""
            hooks:
              - name: no-cmd
                event: round_start
                actions:
                  - type: shell
            """);
        CreateLoader(dir).Load().Should().BeEmpty();
        Directory.Delete(dir, true);
    }

    [Fact]
    public void PromptInject_Missing_Text_Skips_Rule()
    {
        var dir = WriteTempYaml("""
            hooks:
              - name: no-text
                event: round_start
                actions:
                  - type: prompt_inject
            """);
        CreateLoader(dir).Load().Should().BeEmpty();
        Directory.Delete(dir, true);
    }

    [Fact]
    public void Http_Missing_Url_Skips_Rule()
    {
        var dir = WriteTempYaml("""
            hooks:
              - name: no-url
                event: round_start
                actions:
                  - type: http
            """);
        CreateLoader(dir).Load().Should().BeEmpty();
        Directory.Delete(dir, true);
    }

    [Fact]
    public void SubAgent_Missing_Task_Skips_Rule()
    {
        var dir = WriteTempYaml("""
            hooks:
              - name: no-task
                event: round_start
                actions:
                  - type: sub_agent
            """);
        CreateLoader(dir).Load().Should().BeEmpty();
        Directory.Delete(dir, true);
    }

    [Fact]
    public void InterceptEvent_WithAsync_Skips_Rule()
    {
        var dir = WriteTempYaml("""
            hooks:
              - name: intercept-async
                event: tool_pre_exec
                actions:
                  - type: prompt_inject
                    text: "blocked"
                control:
                  async: true
            """);
        CreateLoader(dir).Load().Should().BeEmpty();
        Directory.Delete(dir, true);
    }

    [Fact]
    public void Missing_Name_AutoGenerated()
    {
        var dir = WriteTempYaml("""
            hooks:
              - event: round_start
                actions:
                  - type: shell
                    command: "echo hi"
            """);
        var rules = CreateLoader(dir).Load();
        rules.Should().HaveCount(1);
        rules[0].Name.Should().Be("rule-0");
        Directory.Delete(dir, true);
    }

    [Fact]
    public void Missing_Control_Uses_Defaults()
    {
        var dir = WriteTempYaml("""
            hooks:
              - name: defaults
                event: round_start
                actions:
                  - type: shell
                    command: "echo hi"
            """);
        var rules = CreateLoader(dir).Load();
        rules[0].Control.Once.Should().BeFalse();
        rules[0].Control.Async.Should().BeFalse();
        rules[0].Control.Timeout.Should().Be(30.0);
        Directory.Delete(dir, true);
    }

    [Fact]
    public void Missing_Condition_Is_Null()
    {
        var dir = WriteTempYaml("""
            hooks:
              - name: no-cond
                event: round_start
                actions:
                  - type: shell
                    command: "echo hi"
            """);
        var rules = CreateLoader(dir).Load();
        rules[0].Condition.Should().BeNull();
        Directory.Delete(dir, true);
    }

    [Fact]
    public void Invalid_Rule_DoesNot_Affect_Others()
    {
        var dir = WriteTempYaml("""
            hooks:
              - name: invalid
                event: unknown_event
                actions:
                  - type: shell
                    command: "echo hi"
              - name: valid
                event: round_start
                actions:
                  - type: shell
                    command: "echo ok"
            """);
        var rules = CreateLoader(dir).Load();
        rules.Should().HaveCount(1);
        rules[0].Name.Should().Be("valid");
        Directory.Delete(dir, true);
    }

    [Fact]
    public void SnakeCase_Event_Parses_To_Enum()
    {
        var dir = WriteTempYaml("""
            hooks:
              - name: snake
                event: tool_pre_exec
                actions:
                  - type: prompt_inject
                    text: "blocked"
            """);
        var rules = CreateLoader(dir).Load();
        rules.Should().HaveCount(1);
        rules[0].EventType.Should().Be(HookEvent.ToolPreExec);
        Directory.Delete(dir, true);
    }

    [Fact]
    public void Condition_Operator_Parses_To_Enum()
    {
        var dir = WriteTempYaml("""
            hooks:
              - name: cond
                event: round_start
                condition:
                  match: ALL
                  rules:
                    - field: tool_name
                      operator: glob
                      value: "*_file"
                actions:
                  - type: shell
                    command: "echo hi"
            """);
        var rules = CreateLoader(dir).Load();
        rules.Should().HaveCount(1);
        rules[0].Condition!.MatchMode.Should().Be(HookMatchMode.All);
        rules[0].Condition!.Rules[0].OperatorEnum.Should().Be(HookOperator.Glob);
        Directory.Delete(dir, true);
    }
}
