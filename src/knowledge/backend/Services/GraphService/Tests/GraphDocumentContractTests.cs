using AwesomeAssertions;
using GraphService.Domain;

namespace GraphService.Tests;

// FR-17, ADR-0033 決定 2, IADR-0242 決定 12: **属性の複製の鮮度契約を固定する。**
//
// 消費側（DocumentUpdated の購読）は #911 が足すが、**順序ガードは契約であって実装詳細ではない**
// ため、契約を定義した本 issue で試験する。ここが緩むと、再配信や追い越しで
// 「厳格化したのに緩和が復活する」＝**権限外文書が見えるようになる**。
public class GraphDocumentContractTests
{
    private static GraphDocument At(string ts, string confidentiality = "internal", string? hash = null)
        => GraphDocument.Create(
            Guid.NewGuid(), "t",
            new Dictionary<string, string> { ["confidentiality"] = confidentiality },
            hash, DateTimeOffset.Parse(ts));

    // IADR-0242 決定 12-4: 新しいイベントは適用する。
    [Fact]
    public void TryApply_applies_a_newer_event()
    {
        var doc = At("2026-08-22T00:00:00Z", "internal");

        var applied = doc.TryApply("new title",
            new Dictionary<string, string> { ["confidentiality"] = "restricted" },
            "hash-2", DateTimeOffset.Parse("2026-08-22T01:00:00Z"));

        applied.Should().BeTrue();
        doc.Title.Should().Be("new title");
        doc.Attributes["confidentiality"].Should().Be("restricted");
        doc.BodyHash.Should().Be("hash-2");
    }

    // 🔴 IADR-0242 決定 12-4: **古いイベントは適用しない。**
    // ここが逆だと、厳格化イベントの後に遅れて届いた緩和イベントが権限を戻してしまう。
    [Fact]
    public void TryApply_rejects_a_stale_event_and_keeps_the_stricter_state()
    {
        var doc = At("2026-08-22T00:00:00Z", "internal");
        doc.TryApply("strict", new Dictionary<string, string> { ["confidentiality"] = "restricted" },
            null, DateTimeOffset.Parse("2026-08-22T02:00:00Z"));

        // 追い越しで遅れて届いた「緩和」イベント（古い時刻）。
        var applied = doc.TryApply("stale", new Dictionary<string, string> { ["confidentiality"] = "public" },
            null, DateTimeOffset.Parse("2026-08-22T01:00:00Z"));

        applied.Should().BeFalse();
        doc.Attributes["confidentiality"].Should().Be("restricted",
            "古いイベントを適用すると、厳格化を巻き戻して権限外文書が見えるようになる");
        doc.Title.Should().Be("strict");
    }

    // 同一時刻の再配信は適用してよい（冪等。結果は変わらない）。
    [Fact]
    public void TryApply_accepts_a_redelivery_with_the_same_timestamp()
    {
        var ts = DateTimeOffset.Parse("2026-08-22T00:00:00Z");
        var doc = At("2026-08-22T00:00:00Z", "internal");

        var applied = doc.TryApply("t", new Dictionary<string, string> { ["confidentiality"] = "internal" }, null, ts);

        applied.Should().BeTrue();
        doc.Attributes["confidentiality"].Should().Be("internal");
    }

    // UC-10: 探索がフロンティアの反対側の端点を引くのに使う（#909 が使用）。
    [Fact]
    public void OtherEnd_returns_the_far_endpoint_from_either_side()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var edge = Edge.Create(a, b, Guid.NewGuid(), isSymmetric: false, EdgeProvenance.Auto);

        edge.OtherEnd(a).Should().Be(b);
        edge.OtherEnd(b).Should().Be(a);
    }

    // ADR-0033 決定 4: 出所の値域。
    [Theory]
    [InlineData("auto", true)]
    [InlineData("user", true)]
    [InlineData("ai-approved", true)]
    [InlineData("pending", false)]
    [InlineData("", false)]
    public void EdgeProvenance_value_domain_is_closed(string value, bool expected)
        => EdgeProvenance.IsValid(value).Should().Be(expected);

    // ADR-0033 決定 3・8: 層の値域。
    [Theory]
    [InlineData("core", true)]
    [InlineData("recommended", true)]
    [InlineData("future", true)]
    [InlineData("misc", false)]
    public void EdgeTypeLayer_value_domain_is_closed(string value, bool expected)
        => EdgeTypeLayer.IsValid(value).Should().Be(expected);
}
