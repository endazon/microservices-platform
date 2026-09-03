using AwesomeAssertions;
using IngestionService.Domain;

namespace IngestionService.Tests.Domain;

// FR-02, FR-03, ADR-0070 決定 4, #1193, [[IADR-0354]] 決定 2:
// 本文なしの文書の索引テキスト（題名・タグ）を作る純関数。
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
}
