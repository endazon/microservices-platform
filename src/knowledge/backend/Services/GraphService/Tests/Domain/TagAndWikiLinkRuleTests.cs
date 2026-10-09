using AwesomeAssertions;
using GraphService.Domain;

namespace GraphService.Tests.Domain;

// FR-17, ADR-0033 決定 4・8, ADR-0035 決定 1, [[IADR-0522]] (#1396): 共有タグの辺と Wiki のリンクの**純粋な規則**。
// DB も購読も要らない（受け口を通す測定は `TagEdgeSyncTests` / `LinkRelinkTests`）。
public sealed class TagAndWikiLinkRuleTests
{
    private static readonly Guid A = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid B = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid C = Guid.Parse("33333333-3333-3333-3333-333333333333");

    // ── タグの正規化 ─────────────────────────────────────────────

    [Fact]
    public void タグは前後の空白を落として小文字化し_空は捨て_長すぎるものは切る()
    {
        var keys = GraphDocumentTag.Normalize([" Ops ", "OPS", "設計", "", "   ", new string('x', 250)]);

        keys.Should().BeEquivalentTo(["ops", "設計", new string('x', GraphDocumentTag.MaxTagLength)]);
        GraphDocumentTag.Normalize(null).Should().BeEmpty();
    }

    // ── ハブの判定と相手の集合 ─────────────────────────────────────

    [Theory]
    [InlineData(50, 50, false)]
    [InlineData(51, 50, true)]
    [InlineData(2, 2, false)]
    [InlineData(3, 2, true)]
    public void 文書数が上限を超えたタグだけがハブ(int count, int max, bool hub)
        => TagEdgeRule.IsHub(count, max).Should().Be(hub, "上限ちょうどは結ぶ（超えたら結ばない）");

    [Fact]
    public void 相手はハブでないタグの所属から自分を除いたもの()
    {
        var members = new Dictionary<string, IReadOnlyCollection<Guid>>
        {
            ["小"] = [A, B],
            ["大"] = [A, B, C],
        };

        var partners = TagEdgeRule.DesiredPartners(
            A, ["小", "大"], t => members[t].Count, t => members[t], maxDocumentsPerTag: 2);

        partners.Should().BeEquivalentTo([B], "「大」は 3 文書でハブ。自分（A）は含めない");

        TagEdgeRule.DesiredPartners(A, ["小", "大"], t => members[t].Count, t => members[t], 3)
            .Should().BeEquivalentTo([B, C], "陽性対照: 上限 3 なら「大」でも結ぶ");
    }

    // ── Wiki のリンク ───────────────────────────────────────────

    [Theory]
    [InlineData("[設計](/doc/{0})")]
    [InlineData("[設計](/en/doc/{0})")]
    [InlineData("[設計](doc/{0})")]
    [InlineData("[設計](/DOC/{0}#見出し)")]
    [InlineData("[設計](/doc/{0}?version=3)")]
    [InlineData("[設計](https://wiki.example/ja/doc/{0})")]
    [InlineData("[設計](http://wiki.example/doc/{0})")]
    public void Wikiの文書ページへのリンクは文書IDの名前になる(string template)
    {
        var links = ObsidianLinkParser.Parse(string.Format(template, A.ToString().ToUpperInvariant()));

        var link = links.Should().ContainSingle().Subject;
        link.Target.Should().Be($"doc/{A:D}", "小文字の正規形（`WikiDocumentPath.Format`）");
        link.Kind.Should().Be(ObsidianLinkKind.MarkdownLink);
        WikiDocumentPath.TryParse(link.Target, out var id).Should().BeTrue();
        id.Should().Be(A);
    }

    [Theory]
    [InlineData("[外](https://example.com/page)")]
    [InlineData("[外](https://example.com/doc/not-a-guid)")]
    [InlineData("[外](ftp://wiki.example/doc/11111111-1111-1111-1111-111111111111)")]
    [InlineData("[外](mailto:someone@example.com)")]
    public void Wikiの形でない絶対URLは従前どおり辺にしない(string markdown)
        => ObsidianLinkParser.Parse(markdown).Should().BeEmpty();

    [Fact]
    public void 先頭スラッシュのサイト内パスは捨てずに最終セグメントで解決する()
    {
        // 🔴 Unix では "/folder/note" が file:///folder/note として絶対 URI に化け、外部 URL として捨てられていた。
        var links = ObsidianLinkParser.Parse("[n](/folder/設計メモ.md)");

        links.Should().ContainSingle().Which.Target.Should().Be("設計メモ");
    }

    [Fact]
    public void Wikiの名前は文書IDで解決し題名とは突き合わせない()
    {
        var target = WikiDocumentPath.Format(A);

        LinkTargetMatcher.Match(target, [new(A, "設計書"), new(B, "別")]).Should()
            .Be(new LinkTargetMatcher.LinkTargetMatch(LinkTargetMatcher.LinkTargetOutcome.Resolved, A));

        var missing = LinkTargetMatcher.Match(target, [new(B, target)]);
        missing.IsResolved.Should().BeFalse("題名が偶然 `doc/<ID>` の文書へは解決させない");
        missing.Dimension.Should().Be(LinkTargetMatcher.NotFoundDimension);
    }

    // ── 保存したリンクの復元 ─────────────────────────────────────

    [Fact]
    public void 保存したリンクは構文の別_明示型_アンカーごと復元できる_移行前の行は復元しない()
    {
        var link = new ObsidianLink("B", "手順", "supersedes", ObsidianLinkKind.Explicit);

        DocumentLinkTarget.Create(A, link, DateTimeOffset.UnixEpoch).ToLink().Should().Be(link);
        DocumentLinkTarget.Create(A, "B", DateTimeOffset.UnixEpoch).ToLink()
            .Should().BeNull("移行前の行（名前だけ）は型が分からない");
    }

    // ── 辺の内訳の遷移 ───────────────────────────────────────────

    [Fact]
    public void 自動抽出の辺は内訳を持ち_利用者付与は持たない()
    {
        Edge.Create(A, B, Guid.NewGuid(), true, EdgeProvenance.Auto).AutoSource
            .Should().Be(EdgeAutoSource.Link, "従前の自動抽出は本文のリンクだけ");
        Edge.Create(A, B, Guid.NewGuid(), true, EdgeProvenance.User, autoSource: EdgeAutoSource.Tag).AutoSource
            .Should().BeNull("自動抽出以外は内訳を持たない");
    }

    [Fact]
    public void 共有タグの辺だけが引き取りの対象になる()
    {
        var tag = Edge.Create(A, B, Guid.NewGuid(), true, EdgeProvenance.Auto, autoSource: EdgeAutoSource.Tag);
        var link = Edge.Create(A, B, Guid.NewGuid(), true, EdgeProvenance.Auto, extractedFrom: A);

        tag.ClaimAsLink(A);
        tag.AutoSource.Should().Be(EdgeAutoSource.Link);
        tag.ExtractedFrom.Should().Be(A);

        FluentActions.Invoking(() => link.AdoptAs(EdgeProvenance.User))
            .Should().Throw<InvalidOperationException>("本文のリンクの辺は引き取らない（従前どおり）");
        FluentActions.Invoking(() => link.ClaimAsLink(A)).Should().Throw<InvalidOperationException>();

        link.ConvertToTagDerived();
        link.IsTagDerived.Should().BeTrue();
        link.ExtractedFrom.Should().BeNull();
        FluentActions.Invoking(() => link.AdoptAs(EdgeProvenance.Auto)).Should().Throw<ArgumentException>();
        link.AdoptAs(EdgeProvenance.User);
        link.Provenance.Should().Be(EdgeProvenance.User);
        link.AutoSource.Should().BeNull();
    }
}
