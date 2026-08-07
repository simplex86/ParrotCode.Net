using ParrotCode;

namespace ParrotCode.xUnit;

/// <summary>
/// SecurityLevelParser 单元测试（迭代 8a）。
/// 覆盖验收标准 08a-07 / 08a-08（解析兼容性）。
/// 08a-09（全局无 Permisive 字面量残留）通过 Grep 手动验证。
/// </summary>
public class SecurityLevelTests
{
    [Fact]
    public void Parse_Permisive_LegacyTypo_ReturnsPermissive()
    {
        // 08a-07 旧拼法 "permisive" 兼容 → Permissive
        SecurityLevelParser.Parse("permisive").Should().Be(SecurityLevel.Permissive);
    }

    [Fact]
    public void Parse_Permissive_ReturnsPermissive()
    {
        // 08a-08 正确拼法 "permissive" → Permissive
        SecurityLevelParser.Parse("permissive").Should().Be(SecurityLevel.Permissive);
    }

    [Fact]
    public void Parse_Strict_ReturnsStrict()
    {
        SecurityLevelParser.Parse("strict").Should().Be(SecurityLevel.Strict);
    }

    [Fact]
    public void Parse_Normal_ReturnsNormal()
    {
        SecurityLevelParser.Parse("normal").Should().Be(SecurityLevel.Normal);
    }

    [Theory]
    [InlineData("STRICT")]
    [InlineData("Strict")]
    [InlineData("PERMISSIVE")]
    [InlineData("Normal")]
    public void Parse_CaseInsensitive(string input)
    {
        // 大小写不敏感
        var expected = input.ToLowerInvariant() switch
        {
            "strict" => SecurityLevel.Strict,
            "permissive" => SecurityLevel.Permissive,
            "normal" => SecurityLevel.Normal,
            _ => throw new InvalidOperationException()
        };
        SecurityLevelParser.Parse(input).Should().Be(expected);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("invalid")]
    [InlineData("unknown")]
    public void Parse_InvalidOrNull_DefaultsToNormal(string? input)
    {
        // 未配置或无效值默认 Normal
        SecurityLevelParser.Parse(input).Should().Be(SecurityLevel.Normal);
    }
}
