using AwesomeAssertions;
using IngestionService.Domain;

namespace IngestionService.Tests.Domain;

// FR-02, FR-03, ADR-0070 決定 4, #1193, [[IADR-0358]] 決定 2, #1253, [[IADR-0381]] 決定 4:
// 本文なしの文書の索引テキスト（題名・タグ・原本の所在・データソース名）を作る純関数。
[Trait("TestKind", "Unit")]
public class MetadataIndexTextTests
{
    [Fact]
    public void Build_ShouldJoinTitleAndTags()
    {
        MetadataIndexText.Build("2026 年度 経費精算マニュアル", ["経理", "規程"])
            .Should().Be("2026 年度 経費精算マニュアル 経理 規程");
    }

    [Fact]
    public void Build_ShouldIgnoreEmptyTagsAndTrim()
    {
        MetadataIndexText.Build("  設計書  ", ["", "  ", "図面"])
            .Should().Be("設計書 図面");
    }

    // 題名もタグも無ければ空。呼び出し側は**当たりようがない点を作らない**（`DocumentUpdatedConsumer`）。
    [Fact]
    public void Build_ShouldBeEmpty_WhenNothingToIndex()
    {
        MetadataIndexText.Build("   ", []).Should().BeEmpty();
        MetadataIndexText.Build(null, null).Should().BeEmpty();
    }

    // ── #1253 / [[IADR-0381]] 決定 4: 所在とデータソース名も索引テキストへ ──────────────

    // A-1 / A-2 **陽性**: 所在の各要素とデータソース名が語として並ぶ。
    // 🔴 **区切りを開くのが要点である。** 開かないと `/共有/経理/2026年度経費.pdf` は
    // 1 つの長い語になり、「経理」でも「共有」でも当たらない。
    [Fact]
    public void Build_ShouldIncludePathSegmentsAndDataSourceName()
    {
        var text = MetadataIndexText.Build(
            "2026年度経費", ["規程"],
            originalPath: "/共有/経理/2026年度経費.pdf",
            dataSourceName: "本社ファイルサーバー");

        text.Should().Be("2026年度経費 規程 共有 経理 本社ファイルサーバー");
    }

    // Windows 形式の区切りでも同じ（コネクタによって混ざる）。
    [Fact]
    public void Build_ShouldSplitWindowsSeparators()
    {
        MetadataIndexText.Build("報告書", null, @"\fileserver\部門共有\総務\報告書.docx", "総務共有")
            .Should().Be("報告書 fileserver 部門共有 総務 総務共有");
    }

    // A-3 **陽性対照**: 所在を足しても題名で当たる（#1193 の獲得物を壊していない）。
    [Fact]
    public void Build_ShouldStillContainTitle_WhenPathIsAdded()
    {
        MetadataIndexText.Build("経費精算マニュアル", ["経理"], "/共有/経理/経費精算マニュアル.pdf", "FS")
            .Should().Contain("経費精算マニュアル").And.Contain("経理");
    }

    // A-4 **陰性対照**: 与えていない語は入らない（「何を入れても当たる」実装で緑にならない）。
    [Fact]
    public void Build_ShouldNotContainWordsThatWereNotGiven()
    {
        var text = MetadataIndexText.Build("経費精算マニュアル", ["経理"], "/共有/経理/経費精算マニュアル.pdf", "FS");

        text.Should().NotContain("人事").And.NotContain("見積書").And.NotContain("pdf");
    }

    // 拡張子は語にしない（`pdf` で全 PDF が並ぶのは絞り込みの役に立たない）。
    // 途中のフォルダ名にドットが在っても壊さない。
    [Fact]
    public void Build_ShouldDropExtension_ButKeepDottedFolderNames()
    {
        MetadataIndexText.Build(null, null, "/v1.2.仕様/設計書.md", null)
            .Should().Be("v1.2.仕様 設計書");
    }

    // A-6: 旧発行者（所在もデータソース名も運ばない）でも従来どおり題名・タグだけで作る。
    [Fact]
    public void Build_ShouldFallBackToTitleAndTags_WhenOriginIsUnknown()
    {
        MetadataIndexText.Build("設計書", ["図面"], null, null).Should().Be("設計書 図面");
        MetadataIndexText.Build("設計書", ["図面"], "   ", "  ").Should().Be("設計書 図面");
    }

    // 題名は原本のファイル名（拡張子なし）なので所在の最終要素と必ず重なる。**2 度並べない。**
    [Fact]
    public void Build_ShouldNotRepeatTheTitleThatAlsoAppearsInThePath()
    {
        MetadataIndexText.Build("設計書", null, "/共有/設計書.md", null).Should().Be("設計書 共有");
    }
}
