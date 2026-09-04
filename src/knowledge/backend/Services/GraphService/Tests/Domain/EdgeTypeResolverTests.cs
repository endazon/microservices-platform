using AwesomeAssertions;
using GraphService.Infrastructure.Persistence;
using GraphService.Domain;

namespace GraphService.Tests.Domain;

// FR-17, ADR-0033 決定 3・8, IADR-0281 (#912): 3 層の既定型への写像（純粋関数）。
//
//   ① 明示型   フロントマターのキー名（**書き手の明示が最も強い**）
//   ② 文脈既定 `#` 付き → cites / `![[` → embeds
//   ③ 既定     それ以外 → related
//
// **未定義型は拒否も破棄もしない。related へ丸め、フォールバックの印を返す**（決定 3）。
[Trait("TestKind", "Unit")]
public class EdgeTypeResolverTests
{
    // 実行時辞書（seed 相当）。**コード定義ではない**ので、テストも「今の辞書」を渡して測る。
    private static readonly IReadOnlySet<string> Seeded = new HashSet<string>(
        ["related", "cites", "supersedes", "derived-from", "embeds", "implements"],
        StringComparer.OrdinalIgnoreCase);

    private static ObsidianLink Link(ObsidianLinkKind kind, string? explicitType = null)
        => new("対象", kind == ObsidianLinkKind.SectionReference ? "第2章" : null, explicitType, kind);

    // ── ① 明示型 ────────────────────────────────────────────────────────────

    [Fact]
    public void フロントマターの明示指定が最優先される()
    {
        var r = EdgeTypeResolver.Resolve(Link(ObsidianLinkKind.Explicit, "supersedes"), Seeded);

        r.Should().NotBeNull();
        r!.Value.TypeName.Should().Be("supersedes");
        r.Value.IsFallback.Should().BeFalse();
    }

    [Fact]
    public void 明示指定は大文字小文字を無視して辞書と突き合わせる()
    {
        var r = EdgeTypeResolver.Resolve(Link(ObsidianLinkKind.Explicit, "Supersedes"), Seeded);

        r!.Value.TypeName.Should().Be("Supersedes", "呼び出し側が辞書を大文字小文字無視で引ける");
        r.Value.IsFallback.Should().BeFalse();
    }

    [Fact]
    public void 明示指定は文脈既定より強い()
    {
        // 見出し指定つき（文脈既定なら cites）だが、明示指定が in する。
        var link = new ObsidianLink("対象", "第2章", "implements", ObsidianLinkKind.Explicit);

        EdgeTypeResolver.Resolve(link, Seeded)!.Value.TypeName.Should().Be("implements");
    }

    // ── ② 文脈既定 ──────────────────────────────────────────────────────────

    [Fact]
    public void 見出し指定つき参照はcitesへ写像される()
    {
        var r = EdgeTypeResolver.Resolve(Link(ObsidianLinkKind.SectionReference), Seeded);

        r!.Value.TypeName.Should().Be("cites");
        r.Value.IsFallback.Should().BeFalse();
    }

    [Fact]
    public void 埋め込みはembedsへ写像される()
    {
        var r = EdgeTypeResolver.Resolve(Link(ObsidianLinkKind.Embed), Seeded);

        r!.Value.TypeName.Should().Be("embeds");
        r.Value.IsFallback.Should().BeFalse();
    }

    // ── ③ 既定 ──────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(ObsidianLinkKind.Reference)]
    [InlineData(ObsidianLinkKind.MarkdownLink)]
    public void 種類を判別できないリンクはrelatedへ写像される(ObsidianLinkKind kind)
    {
        var r = EdgeTypeResolver.Resolve(Link(kind), Seeded);

        r!.Value.TypeName.Should().Be(EdgeTypeResolver.DefaultTypeName);
        r.Value.IsFallback.Should().BeFalse("既定型そのものはフォールバックではない");
    }

    // ── 未定義型のフォールバック（決定 3） ──────────────────────────────────

    [Fact]
    public void 辞書に無い明示指定はrelatedへ丸められフォールバックの印が付く()
    {
        var r = EdgeTypeResolver.Resolve(Link(ObsidianLinkKind.Explicit, "contradicts"), Seeded);

        r!.Value.TypeName.Should().Be(EdgeTypeResolver.DefaultTypeName,
            "拒否すると取り込み全体が落ち、破棄すると辺そのものが失われる（ADR-0033 決定 3）");
        r.Value.IsFallback.Should().BeTrue();
        r.Value.RequestedTypeName.Should().Be("contradicts", "警告とカウンタのために元の名前を残す");
    }

    [Fact]
    public void 辞書からcitesが消えていれば見出し参照もrelatedへ丸められる()
    {
        // SC-09 から `cites` を削除した辞書（削除は参照 0 件なら許される。ADR-0033 決定 9）。
        var withoutCites = new HashSet<string>(["related", "embeds"], StringComparer.OrdinalIgnoreCase);

        var r = EdgeTypeResolver.Resolve(Link(ObsidianLinkKind.SectionReference), withoutCites);

        r!.Value.TypeName.Should().Be(EdgeTypeResolver.DefaultTypeName);
        r.Value.IsFallback.Should().BeTrue();
        r.Value.RequestedTypeName.Should().Be("cites");
    }

    // ── 辞書が空（seed 前）: 辺を作らない側へ倒す ───────────────────────────

    [Fact]
    public void 既定型が辞書に無ければ解決しない_陽性対照つき()
    {
        var empty = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        EdgeTypeResolver.Resolve(Link(ObsidianLinkKind.Reference), empty).Should().BeNull(
            "存在しない型 ID の辺は後から作れない（AiSuggestionGenerator と同じ倒し方）");
        EdgeTypeResolver.Resolve(Link(ObsidianLinkKind.Explicit, "supersedes"), empty).Should().BeNull();
        // 陽性対照: 既定型があれば解決する。
        EdgeTypeResolver.Resolve(Link(ObsidianLinkKind.Reference), Seeded).Should().NotBeNull();
    }

    // ── 既定型の名前は seed と同じ値でなければならない ──────────────────────

    [Fact]
    public void 既定型の名前がseedの既定型と一致する()
    {
        // Domain は Api を参照できないため定数を共有できない。**値が割れたらここで落ちる。**
        EdgeTypeResolver.DefaultTypeName.Should().Be(EdgeTypeSeed.DefaultTypeName);
    }
}
