using AwesomeAssertions;
using GraphService.Domain;

namespace GraphService.Tests.Domain;

// FR-10, FR-17, UC-10, ADR-0033 決定 4, [[IADR-0281]], [[IADR-0389]] (#1246):
// リンク先の解決規則そのもの。**辺を張る側と未解決リンク数を数える側が共有する**ため、
// ここが規則の唯一の仕様である。
//
// 🔴 **従前の `LinkEdgeSynchronizer.ResolveTargetsAsync` の意味を保存しているか**を固定する。
// 切り出しで意味が変わると、**辺は張られないのに未解決にも数えられない**リンクが生まれる。
public sealed class LinkTargetMatcherTests
{
    private static readonly Guid A = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid B = Guid.Parse("22222222-2222-2222-2222-222222222222");

    private static List<LinkTargetMatcher.TitleCandidate> Candidates(params (Guid Id, string Title)[] rows)
        => rows.Select(r => new LinkTargetMatcher.TitleCandidate(r.Id, r.Title)).ToList();

    // ── 解決できる（陽性対照） ────────────────────────────────────

    [Fact]
    public void 完全一致が1件なら解決する()
    {
        var m = LinkTargetMatcher.Match("設計メモ", Candidates((A, "設計メモ"), (B, "別の文書")));

        m.IsResolved.Should().BeTrue();
        m.DocumentId.Should().Be(A);
        m.Dimension.Should().BeNull("解決できたものに軸は無い");
    }

    // 大文字小文字だけが違う一致は**一意なら**解決する（従前の [2] 段）。
    [Fact]
    public void 大文字小文字を無視した一致が1件なら解決する()
    {
        var m = LinkTargetMatcher.Match("readme", Candidates((A, "README")));

        m.IsResolved.Should().BeTrue();
        m.DocumentId.Should().Be(A);
    }

    // ── 曖昧（未解決・軸は ambiguous） ────────────────────────────

    [Fact]
    public void 完全一致が複数なら曖昧である()
    {
        var m = LinkTargetMatcher.Match("設計メモ", Candidates((A, "設計メモ"), (B, "設計メモ")));

        m.IsResolved.Should().BeFalse();
        m.Dimension.Should().Be(LinkTargetMatcher.AmbiguousDimension);
    }

    // ⚠️ **ここは「境界」ではない。** 大文字小文字を無視した一致は ordinal 一致の上位集合なので、
    // `exact >= 2` なら `loose >= 2` が必ず成り立つ。実装の短絡（exact が複数なら即 ambiguous）を
    // 消しても結論は変わらない —— **変異試験で実測した**（削っても 21 件すべて緑）。
    // 固定するのは**結論**だけであり、「降りない」という経路の主張はしない
    // （測れない主張をテストの名前に書くと、通っているのに何も守っていない状態になる）。
    [Fact]
    public void 完全一致が複数なら大文字小文字違いが混ざっていても曖昧である()
    {
        var m = LinkTargetMatcher.Match("設計メモ", Candidates(
            (A, "設計メモ"), (B, "設計メモ"), (Guid.NewGuid(), "設計メモ")));

        m.Dimension.Should().Be(LinkTargetMatcher.AmbiguousDimension);
    }

    [Fact]
    public void 大文字小文字を無視した一致が複数なら曖昧である()
    {
        var m = LinkTargetMatcher.Match("readme", Candidates((A, "README"), (B, "ReadMe")));

        m.IsResolved.Should().BeFalse();
        m.Dimension.Should().Be(LinkTargetMatcher.AmbiguousDimension);
    }

    // 🔴 **優先順位の境界。** 完全一致が 1 件あれば、ci でしか一致しない同名が何件あっても解決する。
    [Fact]
    public void 完全一致が1件なら大文字小文字違いの同名が居ても解決する()
    {
        var m = LinkTargetMatcher.Match("README", Candidates(
            (A, "README"), (B, "readme"), (Guid.NewGuid(), "ReadMe")));

        m.IsResolved.Should().BeTrue();
        m.DocumentId.Should().Be(A);
    }

    // ── 不在（未解決・軸は not-found） ────────────────────────────

    [Fact]
    public void どちらの段でも当たらなければ不在である()
    {
        var m = LinkTargetMatcher.Match("存在しない文書", Candidates((A, "設計メモ")));

        m.IsResolved.Should().BeFalse();
        m.Dimension.Should().Be(LinkTargetMatcher.NotFoundDimension);
    }

    [Fact]
    public void 候補が空なら不在である()
    {
        LinkTargetMatcher.Match("何か", Candidates()).Dimension
            .Should().Be(LinkTargetMatcher.NotFoundDimension);
    }

    // 軸の綴りは受け口の内訳に現れる。**実装が閉じた 2 語である**ことを固定する
    // （綴りが揺れると内訳が静かに分裂する）。
    [Fact]
    public void 軸の綴りは2語に閉じている()
    {
        LinkTargetMatcher.NotFoundDimension.Should().Be("not-found");
        LinkTargetMatcher.AmbiguousDimension.Should().Be("ambiguous");
    }
}
