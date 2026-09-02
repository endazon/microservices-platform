using DocumentService.Domain;
using AwesomeAssertions;

namespace DocumentService.Tests.Domain;

// FR-05, UC-03, SC-05, IADR-0047, Issue #199: 機密区分検証ヘルパー（単一情報源）の単体テスト。
public class DocumentAttributesTests
{
    [Theory]
    [InlineData("public")]
    [InlineData("internal")]
    [InlineData("confidential")]
    [InlineData("restricted")]
    public void Validate_CanonicalValue_Ok(string value)
    {
        var (ok, error) = DocumentAttributes.ValidateConfidentiality(
            new Dictionary<string, string> { ["confidentiality"] = value });
        ok.Should().BeTrue();
        error.Should().BeNull();
    }

    [Fact]
    public void Validate_Null_Fails()
    {
        var (ok, error) = DocumentAttributes.ValidateConfidentiality(null);
        ok.Should().BeFalse();
        error.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void Validate_MissingKey_Fails()
    {
        var (ok, _) = DocumentAttributes.ValidateConfidentiality(
            new Dictionary<string, string> { ["dept"] = "sales" });
        ok.Should().BeFalse();
    }

    [Theory]
    [InlineData("secret")]
    [InlineData("PUBLIC")]
    [InlineData(" internal ")]
    [InlineData("")]
    public void Validate_UnknownOrBlankValue_Fails(string value)
    {
        var (ok, _) = DocumentAttributes.ValidateConfidentiality(
            new Dictionary<string, string> { ["confidentiality"] = value });
        ok.Should().BeFalse();
    }
}
