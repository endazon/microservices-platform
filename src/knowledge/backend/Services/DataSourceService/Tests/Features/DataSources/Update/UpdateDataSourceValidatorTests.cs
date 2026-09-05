using AwesomeAssertions;
using DataSourceService.Features.DataSources.Update;

namespace DataSourceService.Tests.Features.DataSources.Update;

// FR-01, UC-04, SC-06, 計画 ADR-0030 §決定（検証 = FluentValidation）/ IADR-0371 決定 2 /
// IADR-0395: 全置換（PUT）の入力検証を手書きガード節から AbstractValidator へ移した際の
// **振る舞い同値**を固定する。
//
// 🔴 **固定するのは「落ちること」だけではない。** 移送前の応答本文（`{ "error": "..." }` の
// 文字列）まで同じであることを見る —— 案内文だけ変わる退行は状態コードでは捕まらない
// （この本文は「どう直せばよいか」を運用者へ伝える唯一の手段である）。
[Trait("TestKind", "Unit")]
public class UpdateDataSourceValidatorTests
{
    private readonly UpdateDataSourceValidator _validator = new();

    private static UpdateDataSourceRequest Request(
        Dictionary<string, string>? config, Dictionary<string, string>? defaultAttributes)
        => new("name", "filesystem", "smb://share", config, defaultAttributes);

    // 陽性対照: 両方が明示されていれば通る（空辞書は「消す」という明示である）。
    // **これが無いと「常に落ちる検証器」でも陰性側のテストが全部緑になる。**
    [Fact]
    public void BothProvided_Passes()
    {
        var result = _validator.Validate(Request([], []));

        result.IsValid.Should().BeTrue();
        result.Errors.Should().BeEmpty();
    }

    // 陽性対照 2: 中身のある辞書でも通る。
    [Fact]
    public void BothProvidedWithEntries_Passes()
    {
        var result = _validator.Validate(Request(
            new Dictionary<string, string> { ["apiToken"] = "t" },
            new Dictionary<string, string> { ["confidentiality"] = "internal" }));

        result.IsValid.Should().BeTrue();
    }

    // 陰性: 片方でも省略があれば落ち、**移送前と同じ本文**になる。
    // 🔴 **両方省略も 1 件の違反である**（元は 1 本の `||`）。2 本の規則へ割ると件数が変わる。
    [Fact]
    public void MissingConfig_FailsWithOriginalMessage()
    {
        var result = _validator.Validate(Request(null, []));

        result.IsValid.Should().BeFalse();
        result.Errors.Should().HaveCount(1);
        result.Errors[0].ErrorMessage.Should()
            .Be(UpdateDataSourceValidator.FullReplacementRequiredMessage);
    }

    [Fact]
    public void MissingDefaultAttributes_FailsWithOriginalMessage()
    {
        var result = _validator.Validate(Request([], null));

        result.IsValid.Should().BeFalse();
        result.Errors.Should().HaveCount(1);
        result.Errors[0].ErrorMessage.Should()
            .Be(UpdateDataSourceValidator.FullReplacementRequiredMessage);
    }

    [Fact]
    public void MissingBoth_FailsOnceWithOriginalMessage()
    {
        var result = _validator.Validate(Request(null, null));

        result.IsValid.Should().BeFalse();
        result.Errors.Should().HaveCount(1, "元は 1 本の `||` であり、違反の件数も移送前と同じである");
        result.Errors[0].ErrorMessage.Should()
            .Be(UpdateDataSourceValidator.FullReplacementRequiredMessage);
    }

    // **定数とリテラルの両方に当てる。** 定数だけを見る試験は、定数ごと書き換わったときに
    // 緑のまま通ってしまう。
    [Fact]
    public void Message_IsTheOriginalLiteral()
        => UpdateDataSourceValidator.FullReplacementRequiredMessage.Should().Be(
            "PUT は全置換です。config と defaultAttributes を明示してください"
            + "（消す場合は {} を送る）。一部だけ変更するなら PATCH を使ってください。");

    // 🔴 **`ownerMappings` の省略は落ちない**（ADR-0074 決定 1: 後から足した項目を必須にすると
    // 既存の PUT クライアントが一斉に 400 になる）。規則を足したらここで止まる。
    [Fact]
    public void MissingOwnerMappings_Passes()
    {
        var result = _validator.Validate(
            new UpdateDataSourceRequest("name", "filesystem", "smb://share", [], [], null));

        result.IsValid.Should().BeTrue();
    }
}
