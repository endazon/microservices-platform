using AwesomeAssertions;
using Knowledge.Contracts.Indexing;
using Xunit;

namespace Knowledge.Contracts.Tests;

// FR-03, UC-01, ADR-0009, #1118, [[IADR-0339]] 決定 1:
// **取り込み側と検索側が共有する 2-gram 変換を固定する。**
//
// 🔴 ここが黙って変わると、取り込み済みの `text_ngram` とクエリの変換が食い違い、
// 検索は 200 を返したまま日本語だけが 0 件へ落ちる（#1116 と同じ「静かな壊れ方」）。
// fixture は稼働 Qdrant から scroll した**実配備チャンクの実文字列**を使う（起票の
// 「短い人工の日本語文で測って当たったとしない」に従う）。
public class CjkBigramPayloadTests
{
    // 実配備チャンク（稼働 k3s の knowledge_chunks_deterministic_v1・chunk_index=1）の本文そのもの
    // （2026-09-02 に scroll で取得）。日本語＋識別子＋記号の長文で、現行の `multilingual` では
    // `本文` `文書` `索引` `早期` `捨てる` のどれも当たらなかった。
    private const string LiveChunk =
        "なぜ本文が要るのか\n\nIngestionService の `DocumentUpdatedConsumer` は `MarkdownUri` が null の文書を早期 return で捨てる。\n本文を持たない文書をいくら作っても、索引には一度も入らない。";

    // #1118 受け入れ基準 1: 実配備チャンクから作る 2-gram 列。**この文字列と同じものを使い捨て
    // コレクションへ入れ、同じ変換のクエリで 25/25 語・176/176 の 2-gram が当たることを実機で測った。**
    [Fact]
    public void Encode_LiveChunk_ProducesBigramsPerCjkRun()
    {
        var encoded = CjkBigramPayload.Encode(LiveChunk);

        encoded.Should().Be(
            "なぜ ぜ本 本文 文が が要 要る るの のか の は が の文 文書 書を を早 早期 で捨 捨て てる "
            + "本文 文を を持 持た たな ない い文 文書 書を をい いく くら ら作 作っ って ても "
            + "索引 引に には は一 一度 度も も入 入ら らな ない");
    }

    // CJK の連なりは記号・英数字・改行で切れる（連なりを跨いだ 2-gram は作らない）。
    [Fact]
    public void Encode_DoesNotBridgeRunsAcrossNonCjk()
    {
        CjkBigramPayload.Encode("検索（parse）導線").Should().Be("検索 導線");
    }

    // 1 文字の連なりは 1-gram のまま残す（落とすと「本」のような 1 文字の語が索引に入らない）。
    [Fact]
    public void Encode_KeepsSingleCharacterRunAsUnigram()
    {
        CjkBigramPayload.Encode("a本b").Should().Be("本");
    }

    // カタカナの長音・踊り字は語の一部として扱う。
    [Theory]
    [InlineData("ストレージ", "スト トレ レー ージ")]
    [InlineData("佐々木", "佐々 々木")]
    public void Encode_TreatsProlongedSoundAndIterationMarksAsCjk(string text, string expected) =>
        CjkBigramPayload.Encode(text).Should().Be(expected);

    // CJK を含まない本文は空（`text` の索引だけが引く）。
    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("IngestionService RX-7800X3D ABAC")]
    public void Encode_ReturnsEmpty_WhenNoCjk(string? text) =>
        CjkBigramPayload.Encode(text).Should().BeEmpty();

    // 検索側: 識別子は `text` へ、日本語は `text_ngram` へ。**識別子の系統は #1117 のまま**（受け入れ基準 3）。
    [Fact]
    public void SplitQuery_SeparatesIdentifiersFromCjk()
    {
        var (nonCjk, ngram) = CjkBigramPayload.SplitQuery("msp-searchseed-tanpopo 検索導線 IngestionService");

        nonCjk.Should().Be("msp-searchseed-tanpopo IngestionService");
        ngram.Should().Be("検索 索導 導線");
    }

    [Fact]
    public void SplitQuery_CjkOnly_LeavesNonCjkEmpty()
    {
        var (nonCjk, ngram) = CjkBigramPayload.SplitQuery("横断検索");

        nonCjk.Should().BeEmpty();
        ngram.Should().Be("横断 断検 検索");
    }

    [Fact]
    public void SplitQuery_IdentifierOnly_LeavesNgramEmpty()
    {
        var (nonCjk, ngram) = CjkBigramPayload.SplitQuery("tanpopo searchseed msp");

        nonCjk.Should().Be("tanpopo searchseed msp");
        ngram.Should().BeEmpty();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void SplitQuery_Blank_ReturnsBothEmpty(string? query)
    {
        var (nonCjk, ngram) = CjkBigramPayload.SplitQuery(query);

        nonCjk.Should().BeEmpty();
        ngram.Should().BeEmpty();
    }

    // 全角英数は CJK ではない（識別子の系統へ回す）。
    [Fact]
    public void IsCjk_ExcludesFullWidthAlphanumerics()
    {
        CjkBigramPayload.IsCjk(new System.Text.Rune('Ａ')).Should().BeFalse();
        CjkBigramPayload.IsCjk(new System.Text.Rune('漢')).Should().BeTrue();
    }
}
