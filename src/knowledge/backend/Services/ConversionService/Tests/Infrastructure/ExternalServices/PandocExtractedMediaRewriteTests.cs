using AwesomeAssertions;
using ConversionService.Domain;
using ConversionService.Domain.Ports;
using ConversionService.Infrastructure.ExternalServices;

namespace ConversionService.Tests.Infrastructure.ExternalServices;

// FR-12, UC-06, SC-07, ADR-0012, IADR-0352 (#1120):
// `--extract-media` が本文へ書き込んだ一時パスを、図の目印へ書き換える部分の単体テスト。
//
// 🔴 **不変条件は「一時パスを 1 件も残さない」である。** pandoc は本文中の画像参照を
// `--extract-media` の一時ディレクトリの絶対パスへ書き換えるが、そのディレクトリは変換直後に
// `ConvertAsync` の `finally` が消す。書き換えを怠ると、**保管された時点で既に存在しない参照**が
// 索引本文にも Wiki ページにも入る（#1097 で pandoc を実走させて初めて観測された）。
//
// 🔴 **入力は実 pandoc（3.1.3）の出力である。** ローカルにも CI にも pandoc は無いが、
// 稼働 k3s の conversion-service pod で実変換して採取した綴りをそのまま固定入力にしてある
// （2026-09-03 実測）。「変換器がこう出すであろう」と人が想像した綴りではない —— 想像で書くと
// **属性が改行をまたぐ `<img>`** のような実際の形を取りこぼす。
public class PandocExtractedMediaRewriteTests
{
    // 本番と同じ形の一時ディレクトリ（`Path.GetTempPath()` + `conv-<乱数>`）。
    private const string MediaDir = "/tmp/conv-50s34roo";

    private static PandocConversionService.ExtractedMedia Media(string path, string figureId) =>
        new(path, new ExtractedFigure(figureId, "image/png", [1, 2, 3]));

    private static string Rewrite(string markdown,
        params PandocConversionService.ExtractedMedia[] media) =>
        PandocConversionService.RewriteExtractedMediaReferences(markdown, MediaDir, media);

    // T-23: docx 由来の HTML 形（属性つき・**属性が改行をまたぐ**）。
    // pod での実測出力そのままである（`pandoc -f docx -t gfm --extract-media`）。
    [Fact]
    public void Rewrites_html_img_tag_emitted_for_docx_sources()
    {
        const string body = """
            # 四半期レポート

            本文の段落である。**強調**と`コード`を含む。

            <img src="/tmp/conv-50s34roo/media/rId20.png"
            style="width:4.16667in;height:2.77778in" alt="構成図" />

            構成図

            ## まとめ
            """;

        var rewritten = Rewrite(body, Media("/tmp/conv-50s34roo/media/rId20.png", "fig-1"));

        rewritten.Should().NotContain("/tmp/", "一時ディレクトリは変換直後に消える");
        rewritten.Should().NotContain("<img", "src 属性だけ差し替えると人手補正の目印と一致しない");
        rewritten.Should().Contain(FigureMarkdown.PlaceholderEmbed("fig-1"));
        // 図の**元の位置**に目印が入る（末尾へ寄らない）。
        rewritten.Should().Contain("本文の段落である。**強調**と`コード`を含む。\n\n"
            + FigureMarkdown.PlaceholderEmbed("fig-1") + "\n\n構成図");
    }

    // T-24: html / gfm 由来の Markdown 画像形。pod での実測出力である。
    [Fact]
    public void Rewrites_markdown_image_form_emitted_for_html_sources()
    {
        const string body = """
            # H

            para

            ![構成図](/tmp/conv-50s34roo/fig.png)

            tail
            """;

        var rewritten = Rewrite(body, Media("/tmp/conv-50s34roo/fig.png", "fig-1"));

        rewritten.Should().NotContain("/tmp/");
        rewritten.Should().Contain("para\n\n" + FigureMarkdown.PlaceholderEmbed("fig-1") + "\n\ntail");
    }

    // T-25: 図として採らなかった媒体（画像でない拡張子）への参照は**落とす**。
    // `ExtractMedia` は画像以外を図にしないので、この参照には対応する figureId が無い。
    // 残すと消えたディレクトリを指したままになる（IADR-0352 決定 4）。
    [Fact]
    public void Drops_references_to_extracted_media_that_is_not_a_figure()
    {
        const string body = """
            para

            <img src="/tmp/conv-50s34roo/media/embedded.bin" alt="x" />

            tail
            """;

        // 図は 1 件も渡さない（＝この媒体は図として採られなかった）。
        var rewritten = Rewrite(body);

        rewritten.Should().NotContain("/tmp/");
        rewritten.Should().NotContain("figure:", "対応する図が無いのだから目印も作らない");
        rewritten.Should().Contain("para").And.Contain("tail");
    }

    // T-25b: 構文として認識できなかった参照の**安全網**（IADR-0352 決定 4）。
    // `<embed>` のように画像構文でない形で一時パスが出ても、一時パスは 1 件も残さない。
    [Fact]
    public void Scrubs_residual_temp_paths_left_by_unrecognized_syntax()
    {
        const string body = """
            para

            <embed src="/tmp/conv-50s34roo/media/rId20.png" />

            tail
            """;

        var rewritten = Rewrite(body, Media("/tmp/conv-50s34roo/media/rId20.png", "fig-1"));

        rewritten.Should().NotContain("/tmp/", "安全網が無ければ一時パスがそのまま保管物へ残る");
        rewritten.Should().Contain(FigureMarkdown.PlaceholderUri("fig-1"));
    }

    // T-26: 原本が目印の綴りを含んでいたら**落とす**（IADR-0352 決定 5）。
    // 残すと正規化側が無関係の図をそこへ差し込むか、解決できない参照が保管物へ残る。
    [Fact]
    public void Drops_source_authored_figure_placeholders()
    {
        const string body = """
            para

            ![fig-1](figure:fig-1)

            tail
            """;

        // 図は 1 件も抽出されていない（原本が勝手に目印の綴りを書いていただけ）。
        var rewritten = Rewrite(body);

        rewritten.Should().NotContain("figure:");
        rewritten.Should().Contain("para").And.Contain("tail");
    }

    // T-27: 同じ媒体を 2 度参照する原本では、目印も 2 つ置く（IADR-0352 決定 7）。
    // 片方だけにすると、置換されない `figure:` 参照が本文へ残る。
    [Fact]
    public void Places_one_placeholder_per_reference_when_the_same_media_is_used_twice()
    {
        const string body = """
            A

            <img src="/tmp/conv-50s34roo/media/rId20.png" alt="one" />

            B

            <img src="/tmp/conv-50s34roo/media/rId20.png" alt="again" />

            C
            """;

        var rewritten = Rewrite(body, Media("/tmp/conv-50s34roo/media/rId20.png", "fig-1"));

        rewritten.Should().NotContain("/tmp/");
        rewritten.Split(FigureMarkdown.PlaceholderEmbed("fig-1")).Should().HaveCount(3,
            "参照 2 件に対して目印も 2 件（分割片は n+1 個）");
    }

    // T-27b: 図が複数あるとき、目印は**パスで対応付ける**（本文の出現順ではない）。
    // 採番は媒体ファイル名の序数順であり、必ずしも本文の出現順と一致しない。
    [Fact]
    public void Maps_each_reference_to_the_figure_extracted_from_that_exact_path()
    {
        const string body = """
            A

            <img src="/tmp/conv-50s34roo/media/rId23.png" alt="two" />

            B

            <img src="/tmp/conv-50s34roo/media/rId20.png" alt="one" />

            C
            """;

        var rewritten = Rewrite(body,
            Media("/tmp/conv-50s34roo/media/rId20.png", "fig-1"),
            Media("/tmp/conv-50s34roo/media/rId23.png", "fig-2"));

        rewritten.Should().Contain("A\n\n" + FigureMarkdown.PlaceholderEmbed("fig-2"));
        rewritten.Should().Contain("B\n\n" + FigureMarkdown.PlaceholderEmbed("fig-1"));
    }

    // T-28: 陽性対照の対 —— **媒体外の参照は触らない。**
    // これが無いと「一時パスが 0 件」は「画像参照を全部消した」でも達成できてしまう。
    [Fact]
    public void Leaves_references_outside_the_media_directory_untouched()
    {
        const string body = """
            <img src="https://example.test/logo.png" alt="logo" />

            ![local](./assets/diagram.svg)

            <img src="/tmp/conv-50s34roo/media/rId20.png" alt="extracted" />
            """;

        var rewritten = Rewrite(body, Media("/tmp/conv-50s34roo/media/rId20.png", "fig-1"));

        rewritten.Should().Contain("<img src=\"https://example.test/logo.png\" alt=\"logo\" />");
        rewritten.Should().Contain("![local](./assets/diagram.svg)");
        rewritten.Should().Contain(FigureMarkdown.PlaceholderEmbed("fig-1"));
        rewritten.Should().NotContain("/tmp/");
    }

    // 図を含まない本文は素通しである（回帰）。
    [Fact]
    public void Passes_through_bodies_without_extracted_media()
    {
        const string body = "# タイトル\n\n本文。\n";

        Rewrite(body).Should().Be(body);
    }
}
