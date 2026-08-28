using AwesomeAssertions;
using GraphService.Domain;

namespace GraphService.Tests;

// FR-17, ADR-0033 決定 8, IADR-0281 (#912): Obsidian リンク構文の抽出（純粋パーサ）。
//
// **3 意味層の別を落とさないことを固定する** —— ADR-0033 決定 8 の理由が「すべて related に丸めると
// 書き手が既に表明していた意味を捨てることになる」であり、抽出の時点で層が潰れると後段の
// EdgeTypeResolver は何も分けられない。
//
// 🔴 **否定形テストには必ず陽性対照を対で置く**（GraphTraversalTests と同じ作法）。
public class ObsidianLinkParserTests
{
    // ── 3 意味層の写像（ADR-0033 決定 8 の表） ─────────────────────────────────

    [Fact]
    public void 一般参照は種類を判別できない参照として抽出される()
    {
        var links = ObsidianLinkParser.Parse("本文に [[設計メモ]] がある。");

        links.Should().ContainSingle();
        links[0].Should().Be(new ObsidianLink("設計メモ", null, null, ObsidianLinkKind.Reference));
    }

    [Fact]
    public void 別名つき参照は別名を捨ててリンク先だけを見る()
    {
        var links = ObsidianLinkParser.Parse("[[設計メモ|こちら]] を参照。");

        links.Should().ContainSingle();
        links[0].Target.Should().Be("設計メモ");
        links[0].Kind.Should().Be(ObsidianLinkKind.Reference, "別名は表示のためのもので、意味は変えない");
    }

    [Fact]
    public void 見出し指定つき参照はアンカーを記録した特定箇所参照になる()
    {
        var links = ObsidianLinkParser.Parse("[[設計メモ#第2章]] を見よ。");

        links.Should().ContainSingle();
        links[0].Target.Should().Be("設計メモ");
        links[0].Anchor.Should().Be("第2章", "見出し指定は to_anchor へ記録する（ADR-0033 決定 5）");
        links[0].Kind.Should().Be(ObsidianLinkKind.SectionReference);
    }

    [Fact]
    public void ブロック参照も特定箇所参照として扱われる()
    {
        var links = ObsidianLinkParser.Parse("[[設計メモ#^abc123]]");

        links.Should().ContainSingle();
        links[0].Anchor.Should().Be("^abc123");
        links[0].Kind.Should().Be(ObsidianLinkKind.SectionReference);
    }

    [Fact]
    public void 埋め込みは埋め込みとして抽出される_見出しつきでも埋め込みのまま()
    {
        var plain = ObsidianLinkParser.Parse("![[用語集]]");
        var section = ObsidianLinkParser.Parse("![[用語集#略語]]");

        plain.Should().ContainSingle();
        plain[0].Kind.Should().Be(ObsidianLinkKind.Embed);
        plain[0].Anchor.Should().BeNull();

        section.Should().ContainSingle();
        section[0].Kind.Should().Be(ObsidianLinkKind.Embed, "見出しがあっても埋め込みは埋め込みである");
        section[0].Anchor.Should().Be("略語");
    }

    [Fact]
    public void 標準Markdownリンクは種類を判別できないリンクとして抽出される()
    {
        var links = ObsidianLinkParser.Parse("詳細は [設計メモ](docs/設計メモ.md) にある。");

        links.Should().ContainSingle();
        links[0].Target.Should().Be("設計メモ", "最終セグメントを取り .md を落とす");
        links[0].Kind.Should().Be(ObsidianLinkKind.MarkdownLink);
    }

    [Fact]
    public void 複数のリンクが出現順に抽出される()
    {
        var links = ObsidianLinkParser.Parse("[[A]] と ![[B]] と [[C#章]] と [表示名](D.md)。");

        links.Select(l => l.Target).Should().Equal("A", "B", "C", "D");
        links.Select(l => l.Kind).Should().Equal(
            ObsidianLinkKind.Reference,
            ObsidianLinkKind.Embed,
            ObsidianLinkKind.SectionReference,
            ObsidianLinkKind.MarkdownLink);
    }

    // ── フロントマターの明示指定（書き手の明示が最も強い） ─────────────────────

    [Fact]
    public void フロントマターの明示指定はキー名を型名として抽出される()
    {
        const string body = """
            ---
            title: 新方式
            supersedes: [[旧方式]]
            ---

            本文。
            """;

        var links = ObsidianLinkParser.Parse(body);

        links.Should().ContainSingle();
        links[0].ExplicitTypeName.Should().Be("supersedes");
        links[0].Target.Should().Be("旧方式");
        links[0].Kind.Should().Be(ObsidianLinkKind.Explicit);
    }

    [Fact]
    public void フロントマターのリスト形も直前のキーに属する()
    {
        const string body = """
            ---
            derived-from:
              - [[原案A]]
              - [[原案B]]
            ---

            本文。
            """;

        var links = ObsidianLinkParser.Parse(body);

        links.Should().HaveCount(2);
        links.Select(l => l.ExplicitTypeName).Should().AllBe("derived-from");
        links.Select(l => l.Target).Should().Equal("原案A", "原案B");
    }

    [Fact]
    public void フロントマターと本文のリンクが両方抽出される()
    {
        const string body = """
            ---
            supersedes: [[旧方式]]
            ---

            詳細は [[設計メモ]]。
            """;

        var links = ObsidianLinkParser.Parse(body);

        links.Should().HaveCount(2);
        links[0].ExplicitTypeName.Should().Be("supersedes");
        links[1].ExplicitTypeName.Should().BeNull("本文中のリンクは明示指定ではない");
    }

    [Fact]
    public void 閉じのないフロントマターは本文として扱われる()
    {
        // 陽性対照つき: 閉じがあれば明示指定になる（上のテスト）。閉じが無いと本文である。
        var links = ObsidianLinkParser.Parse("---\nsupersedes: [[旧方式]]\n\n本文。");

        links.Should().ContainSingle();
        links[0].ExplicitTypeName.Should().BeNull();
        links[0].Kind.Should().Be(ObsidianLinkKind.Reference);
    }

    // ── 抽出しないもの（いずれも陽性対照つき） ────────────────────────────────

    [Fact]
    public void コードフェンスの中のリンクは抽出されない_外は抽出される()
    {
        const string body = """
            外の [[外側]]。

            ```markdown
            [[中身]] と ![[埋め込み]]
            ```

            ~~~
            [[チルダ]]
            ~~~
            """;

        var links = ObsidianLinkParser.Parse(body);

        // 陽性対照（外側）が取れているので、中身が落ちたのはフェンスの効果である。
        links.Select(l => l.Target).Should().Equal(["外側"]);
    }

    [Fact]
    public void 外部URLと画像は辺にしない_相対リンクは抽出される()
    {
        const string body = """
            [外部](https://example.com/page) と
            ![図](assets/diagram.png) と
            [社内](docs/社内資料.md)。
            """;

        var links = ObsidianLinkParser.Parse(body);

        // 外部 URL に対応する文書は存在せず、画像は辺にしない（陽性対照は相対リンク）。
        links.Select(l => l.Target).Should().Equal(["社内資料"]);
    }

    [Fact]
    public void 文書内アンカーだけのリンクは抽出されない()
    {
        var links = ObsidianLinkParser.Parse("[章へ](#第2章) と [他文書](other.md)。");

        links.Select(l => l.Target).Should().Equal("other");
    }

    [Fact]
    public void 空のリンク先は抽出されない()
    {
        ObsidianLinkParser.Parse("[[#見出しだけ]] と [[]] と [[ ]]").Should().BeEmpty();
    }

    [Fact]
    public void 本文が空なら何も抽出されない()
    {
        ObsidianLinkParser.Parse(null).Should().BeEmpty();
        ObsidianLinkParser.Parse("").Should().BeEmpty();
        ObsidianLinkParser.Parse("   \n  ").Should().BeEmpty();
    }

    [Fact]
    public void URLエンコードされた相対リンクは復号してから解決名にする()
    {
        var links = ObsidianLinkParser.Parse("[メモ](docs/%E8%A8%AD%E8%A8%88%E3%83%A1%E3%83%A2.md#章)");

        links.Should().ContainSingle();
        links[0].Target.Should().Be("設計メモ");
    }

    [Fact]
    public void 埋め込み記法が標準Markdownリンクとして二重に数えられない()
    {
        var links = ObsidianLinkParser.Parse("![[用語集]]");

        links.Should().ContainSingle("`![[x]]` は 1 本の埋め込みであって画像リンクではない");
        links[0].Kind.Should().Be(ObsidianLinkKind.Embed);
    }
}
