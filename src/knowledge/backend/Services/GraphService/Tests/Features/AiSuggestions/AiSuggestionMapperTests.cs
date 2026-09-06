using AwesomeAssertions;
using GraphService.Domain;
using GraphService.Features.AiSuggestions;
using Knowledge.Contracts.Dtos;

namespace GraphService.Tests.Features.AiSuggestions;

// FR-18, SC-21, 計画 ADR-0030 §決定（マッピング = Riok.Mapperly）/ IADR-0371 決定 3 /
// IADR-0393 / IADR-0406: 手書きの詰め替えを生成マッパへ置き換えた際の**振る舞い同値**を固定する。
//
// 🔴 **生成物を信じるのではなく、写った値を見る。** Mapperly は名前が一致しないプロパティを
// 黙って落とすことがあり、**列が 1 つ抜けても型は通る**。13 列を 1 つずつ見る。
//
// 🔴 **追加引数の 3 列は特に危ない。** `AiSuggestionDto` の末尾 3 列は既定値つきなので、
// 引数名の綴り違いは「既定値のまま出る」形で通ってしまう（RMG082 は既定では警告）。
// `src/Directory.Build.props` の RMG012 / RMG082 の error 化が第一の防御で、
// この試験が第二の防御である。
[Trait("TestKind", "Unit")]
public class AiSuggestionMapperTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 6, 12, 0, 0, TimeSpan.Zero);

    // 陽性: リンク提案の全 13 列が値を保ったまま写る（追加引数の 3 列を含む）。
    [Fact]
    public void ToDto_CopiesEveryProperty()
    {
        var source = Guid.NewGuid();
        var target = Guid.NewGuid();
        var edgeType = Guid.NewGuid();
        var s = AiSuggestion.CreateLink(source, target, edgeType, "本文が 8 割重なる", Now);

        var dto = AiSuggestionMapper.ToDto(s, "起点の文書", "終点の文書", canDecide: true);

        dto.Id.Should().Be(s.Id);
        dto.Kind.Should().Be(SuggestionKind.Link);
        dto.SourceDocumentId.Should().Be(source);
        dto.TargetDocumentId.Should().Be(target);
        dto.EdgeTypeId.Should().Be(edgeType);
        dto.TagValue.Should().BeNull();
        dto.Rationale.Should().Be("本文が 8 割重なる");
        dto.State.Should().Be(SuggestionState.Pending);
        dto.RejectedCount.Should().Be(0);
        dto.ReinstatedReason.Should().BeNull();
        dto.SourceDocumentTitle.Should().Be("起点の文書");
        dto.TargetDocumentTitle.Should().Be("終点の文書");
        dto.CanDecide.Should().BeTrue();
    }

    // 陽性 2: タグ提案（終点を持たない種別）も同じ写像を通る。
    // **種別で写像を分けていない**ことを固定する（DTO を 1 つにした理由そのもの）。
    [Fact]
    public void ToDto_CopiesEveryProperty_ForTagSuggestion()
    {
        var document = Guid.NewGuid();
        var s = AiSuggestion.CreateTag(document, "営業", "本文に頻出", Now);

        var dto = AiSuggestionMapper.ToDto(s, "対象の文書", null);

        dto.Id.Should().Be(s.Id);
        dto.Kind.Should().Be(SuggestionKind.Tag);
        dto.SourceDocumentId.Should().Be(document);
        dto.TargetDocumentId.Should().BeNull();
        dto.EdgeTypeId.Should().BeNull();
        dto.TagValue.Should().Be("営業");
        dto.Rationale.Should().Be("本文に頻出");
        dto.State.Should().Be(SuggestionState.Pending);
        dto.SourceDocumentTitle.Should().Be("対象の文書");
    }

    // 陰性: 任意項目の null は null のまま写る（空文字へ倒れない）。
    // 🔴 **`TargetDocumentTitle` が `""` になると、画面は「名前の無い終点がある」と描ける** ——
    // タグ提案には終点が無いという事実（null）とは別物である。
    [Fact]
    public void ToDto_KeepsNullOptionalFields()
    {
        var s = AiSuggestion.CreateTag(Guid.NewGuid(), "総務", "本文に頻出", Now);

        var dto = AiSuggestionMapper.ToDto(s, "対象の文書", null);

        dto.TargetDocumentId.Should().BeNull();
        dto.EdgeTypeId.Should().BeNull();
        dto.TargetDocumentTitle.Should().BeNull();
        dto.ReinstatedReason.Should().BeNull();
    }

    // 陰性 2: 却下・再提示を経た状態が写り直る（写像が古い値を握らない）。
    [Fact]
    public void ToDto_ReflectsRejectedAndReinstatedState()
    {
        var s = AiSuggestion.CreateLink(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "根拠", Now);
        s.TryReject("src-1", "tgt-1", Now).Should().BeTrue();

        var rejected = AiSuggestionMapper.ToDto(s, "起点", "終点");
        rejected.State.Should().Be(SuggestionState.Rejected);
        rejected.RejectedCount.Should().Be(1);
        rejected.ReinstatedReason.Should().BeNull();

        s.TryReinstate("src-2", "tgt-1", Now.AddDays(1)).Should().BeTrue();

        var reinstated = AiSuggestionMapper.ToDto(s, "起点", "終点");
        reinstated.State.Should().Be(SuggestionState.Pending);
        reinstated.RejectedCount.Should().Be(1);
        reinstated.ReinstatedReason.Should().Be("source");
    }

    // 🔴 **追加引数はそのまま写る（`canDecide` は両方向）。** 既定は deny 側の `false` である。
    // ADR-0063 決定 4「承認と却下は同じ権限に従う」/ IADR-0364 決定 4。
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ToDto_CopiesCanDecideVerbatim(bool canDecide)
    {
        var s = AiSuggestion.CreateTag(Guid.NewGuid(), "経理", "根拠", Now);

        AiSuggestionMapper.ToDto(s, "文書", null, canDecide).CanDecide.Should().Be(canDecide);
    }

    // 🔴 **3 引数で呼ぶと `CanDecide` は false になる**（既定値が partial メソッドの宣言側に在り、
    // 生成側はそれを継承する）。生成（`Generate`）の経路がこの形で呼んでいる ——
    // **載せ忘れた経路は「できない」と描かれる**という deny-by-default をここで固定する。
    [Fact]
    public void ToDto_WithoutCanDecideArgument_DeniesByDefault()
    {
        var s = AiSuggestion.CreateTag(Guid.NewGuid(), "人事", "根拠", Now);

        AiSuggestionMapper.ToDto(s, "文書", null).CanDecide.Should().BeFalse();
    }

    // 🔴 **本文指紋は DTO に載らない**（ADR-0033 決定 10 / `EdgeTypeDictionaryDto` の宣言）。
    // 生成マッパにも `[MapperIgnoreSource]` で明示してある。**DTO 側に欄を足せば
    // RMG012 でビルドが止まる**（`src/Directory.Build.props`）が、この試験は
    // 「今の DTO に指紋の欄が無い」ことそのものを固定する。
    [Fact]
    public void Dto_HasNoFingerprintMembers()
    {
        typeof(AiSuggestionDto).GetProperty("SourceFingerprint").Should().BeNull(
            "本文指紋は公開面へ出さない（ADR-0033 決定 10）");
        typeof(AiSuggestionDto).GetProperty("TargetFingerprint").Should().BeNull(
            "本文指紋は公開面へ出さない（ADR-0033 決定 10）");
    }
}
