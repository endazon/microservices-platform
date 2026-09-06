using DocumentService.Domain;
using AwesomeAssertions;

namespace DocumentService.Tests.Domain;

// FR-05, UC-03, SC-05, IADR-0047, Issue #199: 機密区分検証ヘルパー（単一情報源）の単体テスト。
[Trait("TestKind", "Unit")]
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

    // ── FR-06, FR-16, AST/ADR-0032 決定 2, [[IADR-0405]] 決定 2 (#1233): 制限 project の保持 ──
    //
    // 🔴 **単調非減少である。等値ではない。** 「後からの付与」と「制限外の値の付け替え」は
    // 通さなければならない —— 通らない実装（`project` の不変化・必須化）は下の陽性対照が落とす。

    private const string AstProject = "ai-stock-trading";

    private static Dictionary<string, string> Attrs(string? project) =>
        project is null
            ? new Dictionary<string, string> { ["confidentiality"] = "internal" }
            : new Dictionary<string, string>
            {
                ["confidentiality"] = "internal",
                ["project"] = project,
            };

    [Fact]
    public void RestrictedProjectRetained_DroppingRestrictedValue_Fails()
    {
        var (ok, error) = DocumentAttributes.ValidateRestrictedProjectRetained(
            Attrs(null), Attrs(AstProject));

        ok.Should().BeFalse();
        error.Should().Contain(AstProject, "どの値が外れたかを丸めない");
    }

    [Fact]
    public void RestrictedProjectRetained_ReplacingWithAnotherValue_Fails()
    {
        var (ok, _) = DocumentAttributes.ValidateRestrictedProjectRetained(
            Attrs("knowledge-base"), Attrs(AstProject));

        ok.Should().BeFalse();
    }

    [Fact]
    public void RestrictedProjectRetained_NullIncoming_Fails()
    {
        var (ok, _) = DocumentAttributes.ValidateRestrictedProjectRetained(
            null, Attrs(AstProject));

        ok.Should().BeFalse("属性を送らない要求は全置換で空になるのと同じ結果になる");
    }

    // 綴りの揺れ（大文字小文字）でも保持と判定する（`RestrictedProject.Values` は序数無視比較）。
    [Theory]
    [InlineData("ai-stock-trading")]
    [InlineData("AI-Stock-Trading")]
    [InlineData("AI-STOCK-TRADING")]
    public void RestrictedProjectRetained_SameValueDifferentCasing_Ok(string incoming)
    {
        var (ok, error) = DocumentAttributes.ValidateRestrictedProjectRetained(
            Attrs(incoming), Attrs(AstProject));

        ok.Should().BeTrue();
        error.Should().BeNull();
    }

    // 🔴 陽性対照 1: **制限外の値は落としてよい**（`project` を不変にしていない証明）。
    [Fact]
    public void RestrictedProjectRetained_DroppingUnrestrictedValue_Ok()
    {
        var (ok, _) = DocumentAttributes.ValidateRestrictedProjectRetained(
            Attrs(null), Attrs("knowledge-base"));

        ok.Should().BeTrue();
    }

    // 🔴 陽性対照 2: **`project` を持たない文書は 1 件も拒否しない**（必須化していない証明）。
    [Fact]
    public void RestrictedProjectRetained_NoProjectOnEitherSide_Ok()
    {
        var (ok, _) = DocumentAttributes.ValidateRestrictedProjectRetained(
            Attrs(null), Attrs(null));

        ok.Should().BeTrue();
    }

    // 🔴 陽性対照 3: **後からの付与は通る**（統制を強める向きである）。
    [Fact]
    public void RestrictedProjectRetained_AddingRestrictedValueLater_Ok()
    {
        var (ok, _) = DocumentAttributes.ValidateRestrictedProjectRetained(
            Attrs(AstProject), Attrs(null));

        ok.Should().BeTrue();
    }

    // 🔴 陽性対照 4: **現在値が null（＝作成経路）でも拒否しない。**
    [Fact]
    public void RestrictedProjectRetained_NullCurrent_Ok()
    {
        var (ok, _) = DocumentAttributes.ValidateRestrictedProjectRetained(
            Attrs(AstProject), null);

        ok.Should().BeTrue();
    }

    // 🔴 主体側の複数形（`projects`）は文書の帰属として読まない —— 除外
    // （`RestrictedProject.IsRestricted`）と母集合を揃える。
    [Fact]
    public void RestrictedProjectRetained_SubjectSpellingOnDocument_IsNotGuarded()
    {
        var current = new Dictionary<string, string>
        {
            ["confidentiality"] = "internal",
            ["projects"] = AstProject,
        };

        var (ok, _) = DocumentAttributes.ValidateRestrictedProjectRetained(Attrs(null), current);

        ok.Should().BeTrue();
    }
}
