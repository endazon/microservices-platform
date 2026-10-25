using AwesomeAssertions;
using Knowledge.Contracts.Dtos;
using Xunit;

namespace Knowledge.Contracts.Tests;

// FR-06, 計画 ADR-0141 決定 2・3, [[IADR-0551]] 決定 1 (#1961): **アーカイブの判定は台帳の状態だけを見る 1 つの純関数である。**
//
// 🔴 本ファイルが守るのは「アーカイブ済みだけが外れ、他の状態は外れない」こと —— 述語が広がると公開済みの文書が
// 検索・グラフから一斉に消え、狭まるとアーカイブ済みが残る。
public class DocumentArchivalTests
{
    [Fact]
    public void アーカイブ済みの状態だけが真である()
    {
        DocumentArchival.IsArchived(DocumentArchival.ArchivedStatus).Should().BeTrue();
        DocumentArchival.ArchivedStatus.Should().Be("archived",
            "台帳・イベント・SC-05 の状態の値域（`draft` / `normalized` / `published` / `archived`）と同じ綴りである");
    }

    // 陽性対照: アーカイブしていない状態は外さない（取り込み・グラフ・MCP の一覧に載る）。
    [Theory]
    [InlineData("draft")]
    [InlineData("normalized")]
    [InlineData("published")]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("Archived")]
    [InlineData("archived ")]
    public void アーカイブ以外の状態は偽である(string? status)
        => DocumentArchival.IsArchived(status).Should().BeFalse(
            "状態は発行側の定数だけが書く。未知・空・null をアーカイブとして扱わない（公開済みを静かに落とさない）");

    // 🔴 ABAC 属性 `lifecycle` は判定に使わない（IADR-0551 決定 1）。述語は状態だけを受け取る —— 属性を渡す口を持たない。
    [Fact]
    public void 述語は状態の文字列だけを受け取る()
    {
        var method = typeof(DocumentArchival).GetMethod(nameof(DocumentArchival.IsArchived))!;
        method.GetParameters().Should().ContainSingle()
            .Which.ParameterType.Should().Be<string>("`lifecycle` 属性（属性辞書）を判定の根拠にしない");
    }
}
